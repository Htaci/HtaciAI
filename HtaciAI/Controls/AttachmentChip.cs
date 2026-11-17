using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;

using HtaciAI.Services;

namespace HtaciAI.Controls;

/// <summary>
/// 附件 chip：图标 + 文字标签（图片 / 文本 / Word / 文件夹 / 后缀名…）。
///
/// 三种用法共用这一个控件：
/// <list type="bullet">
///   <item>输入框正文内联：<c>compact</c> 且 <c>onRemove</c> 非空（右侧 × 移除待发送项），
///         点击定位文件 —— 由 <see cref="ChatInputEditor"/> 当内联控件挂在正文里；</item>
///   <item>用户气泡内联：<c>compact</c> 且 <c>onRemove</c> 为 null，点击定位文件；</item>
///   <item>助手回复里的可点击卡片：<c>compact</c>、无 ×，<c>openOnClick</c> 让点击直接打开
///         文件 / 文件夹（见 <see cref="AttachmentStore.OpenOrReveal"/>）。</item>
/// </list>
/// </summary>
public sealed class AttachmentChip : Border
{
    private static readonly IBrush IdleBg = new SolidColorBrush(Color.Parse("#F1F3F6"));
    private static readonly IBrush HoverBg = new SolidColorBrush(Color.Parse("#E7EBF0"));

    /// <summary>这张卡片描述的那一份文件。悬停预览要用它，所以留着。</summary>
    private readonly ChatAttachment _item;

    /// <summary>
    /// <paramref name="compact"/> 用于消息气泡里的内联卡片：尺寸压到能塞进一行文字而不撑高行距。
    /// <paramref name="openOnClick"/> 为 true 时点击<b>直接打开</b>该文件 / 文件夹
    /// （可执行文件仍只定位，理由见 <see cref="AttachmentStore.IsExecutable"/>）。
    /// </summary>
    public AttachmentChip(ChatAttachment item, Action? onRemove, bool compact = false, bool openOnClick = false)
    {
        _item = item;

        Height = compact ? 19 : 26;
        CornerRadius = new CornerRadius(compact ? 5 : 6);
        Padding = new Thickness(compact ? 6 : 8, 0, compact ? 5 : 6, 0);
        Background = IdleBg;
        BorderBrush = new SolidColorBrush(Color.Parse("#E1E5EA"));
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text = GlyphFor(item.Kind),
            FontFamily = AppFonts.Icons,
            FontSize = compact ? 11 : 12,
            Foreground = new SolidColorBrush(Color.Parse("#5B6472")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = compact ? 11 : 12,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (onRemove is not null)
            row.Children.Add(BuildRemoveButton(onRemove));

        Child = row;

        // 界面上不显示路径，悬停时弹一张预览（图片给缩略图 + 路径，其余只给路径）
        PointerEntered += (_, _) =>
        {
            Background = HoverBg;
            _previewTimer.Start();
        };

        PointerExited += (_, _) =>
        {
            Background = IdleBg;
            HidePreview();
        };

        // 悬停着滚列表时预览会跟着卡片一路飘，滚一下就先收掉（停住不动再弹）。
        // 不标记 Handled：滚轮该照常滚列表。
        PointerWheelChanged += (_, _) => HidePreview();

        // 鼠标扫过一排卡片时不该一路弹个不停：停够一小会儿才弹（时长见 _previewTimer）
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _preview ??= new AttachmentPreviewPopup();
            _preview.Show(this, _item);
        };

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            // 点下去要么开文件、要么点 ×：预览这时该消失了，不然它会飘在资源管理器旁边
            HidePreview();

            if (openOnClick) AttachmentStore.OpenOrReveal(item.Path);
            else AttachmentStore.RevealInExplorer(item.Path);

            e.Handled = true;
        };
    }

    /// <summary>悬停延迟：比系统 ToolTip 短，但足够滤掉「一路扫过去」。</summary>
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>悬停预览。第一次真正悬停时才建 —— 绝大多数卡片一辈子不会被悬停。</summary>
    private AttachmentPreviewPopup? _preview;

    private void HidePreview()
    {
        _previewTimer.Stop();
        _preview?.Hide();
    }

    /// <summary>卡片被移除（点 × 、清空输入框）时把预览一并收掉。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        HidePreview();
        base.OnDetachedFromVisualTree(e);
    }

    private static TextBlock BuildRemoveButton(Action onRemove)
    {
        var close = new TextBlock
        {
            Text = "\uF36A",   // Cancel（×）
            FontFamily = AppFonts.Icons,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#9AA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        close.PointerPressed += (_, e) =>
        {
            // 先于 chip 自身的「定位文件」处理，避免点 × 时把资源管理器打开
            e.Handled = true;
            onRemove();
        };
        close.PointerEntered += (_, _) => close.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        close.PointerExited += (_, _) => close.Foreground = new SolidColorBrush(Color.Parse("#9AA3AF"));
        return close;
    }

    /// <summary>种类图标（Fluent System Icons）。码位对照见 <c>docs/icon-glyphs.md</c>。</summary>
    private static string GlyphFor(string kind) => kind switch
    {
        "image" => "\uF489",     // Picture
        "folder" => "\uF419",    // Folder
        "office" or "text" => "\uF379",   // Document
        _ => "\uF379",           // Page
    };
}
