using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HtaciAI.Models;

namespace HtaciAI.Controls;

/// <summary>
/// 附件 chip 的悬停预览：图片文件先给一张缩略图，下面是文件的真实路径。
///
/// <b>为什么用 Popup 而不是把预览画在卡片旁边</b>：卡片在两种宿主里都可能被裁
/// —— 消息气泡里的 <c>InlineUIContainer</c> 和输入框里的 <c>InlineObjectElement</c>
/// 都按行高裁剪，画在旁边的预览会被切成一条。Popup 是独立窗口，不受宿主裁剪，
/// 位置由 <see cref="Popup.PlacementTarget"/> 跟着卡片走。
///
/// <b>只有图片会有缩略图</b>：其余文件（文本 / Word / 文件夹…）的预览形式还没定，
/// 先只显示路径 —— 这一层缺什么就少显示什么，不要用图标凑成「假预览」。
///
/// <b>所有磁盘操作都在悬停这一刻做</b>：卡片是随历史消息成批建出来的，
/// 构造时每条都碰一次 <see cref="File.Exists"/> / 解码图片，长会话里就是成百上千次白干活。
/// </summary>
internal sealed class AttachmentPreviewPopup : Popup
{
    /// <summary>解码宽度取显示宽度的两倍：150% / 200% 缩放下不至于糊。</summary>
    private const int DecodeWidth = 640;

    /// <summary>解码高度上限。超过说明是「窄而极长」的长截图，见 <see cref="LoadThumbnail"/>。</summary>
    private const int DecodeMaxHeight = 960;

    private const double MaxPreviewWidth = 320;
    private const double MaxPreviewHeight = 240;

    private readonly Image _image;
    private readonly Border _frame;
    private readonly TextBlock _note;
    private readonly TextBlock _path;

    /// <summary>第几次加载。解码回来时对不上就丢掉结果，避免上一张图落在这一张的位置上。</summary>
    private int _version;

    public AttachmentPreviewPopup()
    {
        Placement = PlacementMode.Bottom;

        // 只用来「看」：不参与点击，也就不能让 Popup 自己管关闭 ——
        // 关闭的时机跟着鼠标进出来，见 <see cref="Hide"/>。
        IsLightDismissEnabled = false;
        WindowManagerAddShadowHint = false;

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = MaxPreviewWidth,
            MaxHeight = MaxPreviewHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _frame = new Border
        {
            Child = _image,
            CornerRadius = new CornerRadius(6),
            // 图片自己就是圆的角会溢出：边框只管裁剪，着色交给里面的 Image
            ClipToBounds = true,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _note = new TextBlock
        {
            Text = "文件缺失（已被删除或移走）",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#B45309")),
            IsVisible = false,
        };

        _path = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            MaxWidth = MaxPreviewWidth,
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_frame);
        stack.Children.Add(_note);
        stack.Children.Add(_path);

        Child = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E1E5EA")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            // Popup 的窗口按内容大小裁，四周不留出这圈外边距，阴影就只剩一条硬边
            Margin = new Thickness(10),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 3,
                Blur = 14,
                Color = Color.FromArgb(38, 0, 0, 0),
            }),
            // 鼠标一路扫过卡片时，预览不该在它下面接住指针 —— 否则卡片会收到 PointerExited，
            // 预览自己把自己关掉。
            IsHitTestVisible = false,
        };
    }

    /// <summary>在 <paramref name="anchor"/> 下方显示 <paramref name="item"/> 的预览。可重复调用（换一张卡片）。</summary>
    public void Show(Control anchor, ChatAttachment item)
    {
        PlacementTarget = anchor;

        var path = item.Path ?? "";
        var exists = Exists(path);

        _path.Text = path;
        _note.IsVisible = !exists;

        // 先开窗再解码：路径立刻可见，图片解好之后自己填进来
        IsOpen = true;

        if (exists && item.Kind == "image") _ = LoadAsync(path);
        else SetImage(null);
    }

    /// <summary>关掉预览，并让还没回来的解码结果作废。</summary>
    public void Hide()
    {
        _version++;
        IsOpen = false;
        SetImage(null);
    }

    private async Task LoadAsync(string path)
    {
        var version = ++_version;

        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() => LoadThumbnail(path));
        }
        catch
        {
            // 解不开（svg / tiff / 损坏文件）就只留路径：预览是锦上添花，不该弹错误
        }

        if (version != _version)
        {
            // 期间换了卡片，或者已经关掉了：这份结果没用了
            bitmap?.Dispose();
            return;
        }

        SetImage(bitmap);
    }

    private void SetImage(Bitmap? bitmap)
    {
        var old = _image.Source as Bitmap;
        _image.Source = bitmap;
        _frame.IsVisible = bitmap is not null;

        if (old is not null && !ReferenceEquals(old, bitmap)) old.Dispose();
    }

    /// <summary>
    /// 按显示尺寸的倍数解码，而不是 <c>new Bitmap(path)</c>：原图可能是几千万像素的相机照片，
    /// 为了 320 宽的预览去解一张全尺寸位图，内存和时间都不划算。
    /// </summary>
    private static Bitmap? LoadThumbnail(string path)
    {
        using var stream = File.OpenRead(path);

        var bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
        if (bitmap is null) return null;

        // 「窄而极长」的长截图按宽度解会得到一张很高的位图（640 × 几千），
        // 看着只是一张小预览，内存却是几十 MB。超阈值就丢掉，改成按高度解一次。
        if (bitmap.PixelSize.Height <= DecodeMaxHeight) return bitmap;

        bitmap.Dispose();
        stream.Position = 0;
        return Bitmap.DecodeToHeight(stream, (int)(MaxPreviewHeight * 2));
    }

    /// <summary>
    /// 文件是否还在。路径非法（含非法字符）或无权访问，一律按「找不到」处理 ——
    /// 对用户来说结果一样：这份文件此刻打不开。
    ///
    /// <b>目录也要算「在」</b>：助手回复里的卡片可能是文件夹，只查 <see cref="File.Exists"/>
    /// 会把每一个文件夹都标成「文件缺失」。
    /// </summary>
    private static bool Exists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }
}
