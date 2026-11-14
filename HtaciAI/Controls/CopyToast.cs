using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using HtaciAI.Services;
using Avalonia.Threading;

namespace HtaciAI.Controls;

/// <summary>
/// 复制成功的轻提示：在<b>当前窗口</b>的覆盖层上浮一个小胶囊，约 1.3 秒后自动淡出。
///
/// <b>为什么不用 Window / Popup</b>：
/// <list type="bullet">
///   <item>独立窗口会激活自己，把焦点从输入框抢走 —— 复制完接着打字就断在��里；</item>
///   <item>Popup 在部分平台上也是独立原生窗口，同样有抢激活的风险，而且位置跟随后要处理锚点重算。</item>
/// </list>
/// 放在 <see cref="OverlayLayer"/> 里就是一个普通控件：它跟着所属窗口走（任务栏里不会多一个条目、
/// 切到别的程序时一起被盖住），设置 <see cref="InputElement.IsHitTestVisible"/> 为 false 之后
/// 它对鼠标完全透明，不会挡住用户的下一次点击，也不参与焦点。
///
/// <b>线程</b>：只能在 UI 线程调用。
/// </summary>
public static class CopyToast
{
    /// <summary>Fluent System Icons：checkmark。</summary>
    private const string GlyphCheck = "\uF295";

    /// <summary>主窗口自绘标题栏的高度：提示条要落在它下面。</summary>
    private const double TitleBarHeight = 40;

    /// <summary>停留时间（淡入完成后开始计时）。</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1200);

    /// <summary>淡入 / 淡出时长。</summary>
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(140);

    private static Border? _current;
    private static OverlayLayer? _layer;
    private static DispatcherTimer? _easeTimer;

    /// <summary>
    /// 在 <paramref name="anchor"/> 所在窗口弹出「已复制」。
    /// <paramref name="anchor"/> 只需要用来定位（通常传刚被复制的那块内容）。
    /// </summary>
    public static void Show(Visual? anchor, string text = "已复制")
    {
        try
        {
            ShowCore(anchor, text);
        }
        catch
        {
            // 提示是锦上添花的东西：拿不到覆盖层、窗口正在关闭，都不该影响复制本身
        }
    }

    private static void ShowCore(Visual? anchor, string text)
    {
        if (anchor is null) return;

        var top = TopLevel.GetTopLevel(anchor);
        if (top is null) return;

        var layer = OverlayLayer.GetOverlayLayer(top);
        if (layer is null) return;

        // 连点几次只留一个：旧提示先撤掉，避免叠成一片
        Remove();

        var toast = new Border
        {
            // 不拦鼠标、不接焦点：这一层盖在正文上，但点下去要落到下面的内容上
            IsHitTestVisible = false,
            Focusable = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x24, 0x30)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 12, 6),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 12,
                OffsetY = 2,
                Color = Color.FromArgb(0x33, 0, 0, 0),
            }),
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = Fade, Easing = new CubicEaseOut() },
            },
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = GlyphCheck,
                        FontFamily = AppFonts.Icons,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#7DD3A0")),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        Foreground = Brushes.White,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                },
            },
        };

        layer.Children.Add(toast);
        _current = toast;
        _layer = layer;

        // 定位与淡入都推迟一拍再做：这一次调用里覆盖层可能还没完成布局
        // （Bounds 是 0），拿它算出来的位置会被钳到左上角 —— 第一次复制弹在角落、
        // 第二次才对，正是这个原因。顺带，同一次调用里改 Opacity 也触发不了转场。
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_current, toast)) return;

            Place(toast, layer);

            toast.Opacity = 1;

            _easeTimer ??= new DispatcherTimer();
            _easeTimer.Stop();
            _easeTimer.Tick -= OnHoldElapsed;
            _easeTimer.Tick += OnHoldElapsed;
            _easeTimer.Interval = Hold;
            _easeTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 摆在<b>整个窗口</b>的顶部居中。
    ///
    /// <b>为什么是整窗而不是内容区</b>：内容区的边界要去可视树里猜（滚动视口有多宽、
    /// 侧栏算不算在内），猜错一次就偏了；而覆盖层的 Bounds 就是窗口客户区，
    /// 每次都是同一个矩形 —— 位置可预期，代码也没有分支。
    ///
    /// <b>为什么要让开顶部 40px</b>：主窗口是自绘标题栏，那一条上有标签页、窗口按钮，
    /// 提示压上去会挡住用户正在点的东西。
    /// </summary>
    private static void Place(Border toast, OverlayLayer layer)
    {
        // 量一次：宽度取决于文字，硬编码会在换文案时错位
        toast.Measure(Size.Infinity);
        var size = toast.DesiredSize;

        var width = layer.Bounds.Width;
        var height = layer.Bounds.Height;

        // 布局还没跑完（Bounds 为 0）时退回窗口尺寸，别把提示甩到左上角
        if (width <= 0 || height <= 0)
        {
            if (TopLevel.GetTopLevel(layer) is { } top)
            {
                width = top.Bounds.Width;
                height = top.Bounds.Height;
            }
        }

        var left = Math.Max(8, (width - size.Width) / 2);
        var offsetY = Math.Clamp(TitleBarHeight + 12, 8, Math.Max(8, height - size.Height - 8));

        toast.Margin = new Thickness(left, offsetY, 0, 0);
    }

    private static void OnHoldElapsed(object? sender, EventArgs e)
    {
        _easeTimer?.Stop();

        if (_current is null)
        {
            Remove();
            return;
        }

        _current.Opacity = 0;

        // 等淡出走完再摘掉：提前移除就看不到淡出，只能看到硬消失
        _easeTimer!.Tick -= OnHoldElapsed;
        _easeTimer.Tick += OnFadeElapsed;
        _easeTimer.Interval = Fade + TimeSpan.FromMilliseconds(30);
        _easeTimer.Start();
    }

    private static void OnFadeElapsed(object? sender, EventArgs e) => Remove();

    private static void Remove()
    {
        _easeTimer?.Stop();
        if (_easeTimer is not null)
        {
            _easeTimer.Tick -= OnHoldElapsed;
            _easeTimer.Tick -= OnFadeElapsed;
        }

        try { _layer?.Children.Remove(_current!); } catch { /* 窗口已关，随它去 */ }

        _current = null;
        _layer = null;
    }
}
