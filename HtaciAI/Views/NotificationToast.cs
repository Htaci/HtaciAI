using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Services;
using HtaciAI.Services.Notifications;

namespace HtaciAI.Views;

/// <summary>
/// 系统消息提醒的浮窗：贴屏幕右下角弹出，<b>常驻不自动消失</b>，由右上角的关闭按钮收起。
/// 点正文跳到对应会话；审批类消息还带「拒绝 / 同意 / 全部同意」三个按钮，可以直接作答。
///
/// 为什么用独立窗口而不是往主窗口里叠一层：提醒的意义就在于<b>主窗口不在前面时也能看见</b>——
/// 最小化或压在别的程序后面时，主窗口里的任何控件都露不出来。所以它必须是个 <see cref="Window"/>：
/// <list type="bullet">
///   <item><see cref="Window.ShowActivated"/> = false：弹出时<b>不抢焦点</b>，
///     否则用户正在别处打字会被打断（也会把自己判成「应用已聚焦」而影响后续判定）；</item>
///   <item><see cref="Window.Topmost"/> = true：压在其它窗口之上；</item>
///   <item><see cref="Window.ShowInTaskbar"/> = false：不进任务栏、alt-tab 列表。</item>
/// </list>
///
/// 背景走系统毛玻璃（Windows 11 上是 Mica）。窗口本身设成透明，圆角由
/// <c>Win32Properties.WindowCornerPreference</c> 让 DWM 把<b>窗口</b>裁圆 ——
/// 只给 Border 画圆角是不够的：毛玻璃铺满整个窗口矩形，四角会露出方的底。
///
/// 多条同时来就向上堆叠，最多留 <see cref="MaxVisible"/> 条，多出来的挤掉最旧的。
/// </summary>
public sealed class NotificationToast : Window
{
    private const double ToastWidth = 380;
    private const double Gap = 8;
    private const double EdgeMargin = 16;

    /// <summary>还没测量出真实高度时的占位值，只影响首帧定位。</summary>
    private const double FallbackHeight = 130;

    private const int MaxVisible = 4;

    private static readonly FontFamily IconFont = AppFonts.Icons;

    /// <summary>当前在屏上的提醒（列表靠后的更新，堆叠时位置更高）。</summary>
    private static readonly List<NotificationToast> Visible = new();

    /// <summary>应用小图标。加载失败时为 null，标题退化成纯文字。</summary>
    private static IImage? _appIcon;
    private static bool _appIconTried;

    private readonly Border _card;
    private readonly SessionNotice _notice;

    private NotificationToast(SessionNotice notice)
    {
        _notice = notice;

        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;

        // 透明不可用时的融合底色。默认就是白色，这里写出来是为了「提醒绝不会变成一块黑」这件事有个明确依据。
        TransparencyBackgroundFallback = Brushes.White;

        // 优先级列表：Win11 走 Mica，退一步亚克力/模糊，再退纯透明。
        // 末尾一定要带 None：全都不可用时 Avalonia 会回落到平台默认值，那时靠上面的 fallback 兜底。
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None,
        };

        _card = BuildCard(notice);
        Content = _card;

        // 高度是按内容自适应出来的，首帧布局前拿不到真值，位置只能先按占位值摆；
        // 真实尺寸定下来后尺寸会变，这里跟着重摆一次，避免像「比预想矮却按高值算」那样压到任务栏上。
        SizeChanged += (_, _) => Place();
    }

    /// <summary>句柄这时才建出来，让 DWM 把窗口裁圆（Mica 的四角才是圆的）。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Win32WindowCorners.Apply(this);
    }

    /// <summary>弹一条提醒。可从任意线程调用。</summary>
    public static void Show(SessionNotice notice)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(notice));
            return;
        }

        var toast = new NotificationToast(notice);

        Visible.Add(toast);
        while (Visible.Count > MaxVisible)
        {
            var oldest = Visible[0];
            Visible.RemoveAt(0);
            oldest.Close();
        }

        toast.Show();

        // 定位与底色都要等首帧布局跑完才准：SizeToContent 撑出来的高度这时才有值，
        // ActualTransparencyLevel 也才被平台回填成真实级别。
        Dispatcher.UIThread.Post(
            () => { toast.ApplySurface(); Place(); },
            DispatcherPriority.Loaded);
    }

    /// <summary>收起（关闭按钮、动作按钮、被挤掉时都走这里）。</summary>
    private void Dismiss()
    {
        Visible.Remove(this);
        Place();   // 剩下的往上挪一格
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Visible.Remove(this);
        base.OnClosed(e);
    }

    // ============================================================
    // 内容
    // ============================================================

    private Border BuildCard(SessionNotice notice)
    {
        var rows = new StackPanel();
        rows.Children.Add(BuildHeaderRow());
        rows.Children.Add(BuildBodyRow(notice));

        if (notice.Kind == SessionNoticeKind.Approval && notice.OnAction is not null)
            rows.Children.Add(BuildActions(notice.OnAction));

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)),
            ClipToBounds = true,
            Child = rows,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // 整张卡片都能点（不只是文字），点了跳到对应会话。
        // handledEventsToo 是必须的：Button 会把自己的指针事件标记为已处理，
        // 不开这个就收不到，也就无从判断「这次点击落在按钮上」。判定见 OnCardPressed / OnCardReleased。
        card.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        card.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        return card;
    }

    // ---- 整卡的「点一下跳会话」 ----

    private Point _pressLocal;
    private PixelPoint _pressWindow;
    private bool _pressOnCard;

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        // 落在按钮上的按下不算（关闭 / 拒绝 / 同意 / 全部同意），免得点按钮顺带把会话也打开
        _pressOnCard = !IsInsideButton(e.Source as Visual);
        _pressLocal = e.GetPosition(this);
        _pressWindow = Position;
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressOnCard || IsInsideButton(e.Source as Visual)) return;

        // 拖过窗口（标题行拖拽）或指针自己移开，都算拖，不算点
        if (Position != _pressWindow) return;
        var moved = e.GetPosition(this) - _pressLocal;
        if (Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4) return;

        Jump(_notice.SessionId);
    }

    /// <summary>事件源或其祖先里有没有按钮。</summary>
    private bool IsInsideButton(Visual? source)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, _card); v = v.GetVisualParent())
            if (v is Button) return true;

        return false;
    }

    /// <summary>标题行：应用小图标 + 应用名 + 关闭按钮。按住这一行可以拖动整条提醒。</summary>
    private Control BuildHeaderRow()
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(14, 10, 8, 6),
        };

        if (AppIcon() is { } icon)
        {
            row.Children.Add(new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var appName = new TextBlock
        {
            Text = "HtaciAI",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(appName, 1);
        row.Children.Add(appName);

        // 关闭按钮的 × 是画出来的，不用图标字体：字形带自己的基线，在不同字体/缩放下
        // 容易在按钮里偏到一角（之前那个偏左上就是这么来的），画出来的两条线则永远居中。
        var cross = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L9,9 M9,0 L0,9"),
            Stroke = new SolidColorBrush(Color.Parse("#6B7280")),
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = cross,
            Width = 26,
            Height = 26,
            MinWidth = 0,       // Fluent 的 Button 自带 MinWidth/MinHeight，不清掉就把 26 顶回去了
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "关闭");
        close.Click += (_, _) => Dismiss();
        close.PointerEntered += (_, _) => cross.Stroke = new SolidColorBrush(Color.Parse("#1F2328"));
        close.PointerExited += (_, _) => cross.Stroke = new SolidColorBrush(Color.Parse("#6B7280"));
        Grid.SetColumn(close, 3);
        row.Children.Add(close);

        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        return row;
    }

    /// <summary>
    /// 正文：标题（工作空间 · 会话）+ 说明。
    ///
    /// 类型标记放在说明<b>末尾</b>（一个小圆点），不放前面：放前面会顶在句首抢视线，
    /// 把「一句话说明」读成「一个图标 + 一句话」。
    /// 点击交给外层卡片统一处理（见 <see cref="OnCardReleased"/>），这里不再单独挂。
    /// </summary>
    private static Control BuildBodyRow(SessionNotice notice)
    {
        var body = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(14, 2, 14, 12),
            Children =
            {
                new TextBlock
                {
                    Text = ComposeTitle(notice),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                },
                BuildBodyText(notice),
            },
        };

        return body;
    }

    /// <summary>说明文字，末尾跟一个按类型着色的小圆点。</summary>
    private static TextBlock BuildBodyText(SessionNotice notice)
    {
        var block = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
        };

        var inlines = new InlineCollection { new Run(notice.Body) };
        if (DotColorFor(notice.Kind) is { } color)
        {
            inlines.Add(new Run(" "));
            inlines.Add(new Run("●")
            {
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse(color)),
            });
        }
        block.Inlines = inlines;

        return block;
    }

    /// <summary>审批类消息的三个动作按钮。</summary>
    private Control BuildActions(Func<NoticeAction, Task> onAction)
    {
        var deny = CreateActionButton("拒绝", "#F1F3F5", "#374151", NoticeAction.Deny, onAction);
        var allow = CreateActionButton("同意", "#4A90D9", "#FFFFFF", NoticeAction.Allow, onAction);
        var allowAll = CreateActionButton("全部同意", "#EEF4FB", "#2F6FB5", NoticeAction.AllowAll, onAction);
        ToolTip.SetTip(allowAll, "允许这次调用，并把本会话的权限改为「自由」——之后所有工具都不再询问");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            // 上边距归零：说明文字自己已经留了 12 的下边距，这里再留就成双倍间距了
            Margin = new Thickness(14, 0, 14, 12),
            Children = { deny, allow, allowAll },
        };
    }

    private Button CreateActionButton(
        string text, string bg, string fg, NoticeAction action, Func<NoticeAction, Task> onAction)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(fg)),
            },
            Height = 28,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse(bg)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        btn.Click += async (_, _) =>
        {
            // 先收起：这条提醒的使命就是这一次作答，点完即结束。
            // 动作本身可能要落库（比如改权限档位），不该让窗口留在屏上等它。
            Dismiss();

            try { await onAction(action); }
            catch { /* 作答失败不该再弹一层错误 —— 卡片还在会话里，用户可以直接在那儿点 */ }
        };

        return btn;
    }

    /// <summary>标题 = 工作空间名 · 会话名（普通会话没有前半段）。</summary>
    private static string ComposeTitle(SessionNotice notice)
        => string.IsNullOrEmpty(notice.WorkspaceName)
            ? notice.Title
            : $"{notice.WorkspaceName} · {notice.Title}";

    /// <summary>说明文字末尾那个小圆点的颜色（按消息类型区分）。</summary>
    private static string? DotColorFor(SessionNoticeKind kind) => kind switch
    {
        SessionNoticeKind.Approval => "#D97706",
        SessionNoticeKind.Question => "#4A90D9",
        SessionNoticeKind.Failed => "#DC2626",
        _ => "#16A34A",
    };

    /// <summary>应用小图标（懒加载一次；拿不到就返回 null，标题退化成纯文字）。</summary>
    private static IImage? AppIcon()
    {
        if (_appIconTried) return _appIcon;
        _appIconTried = true;

        try
        {
            // 用各窗口标题栏同一个图标。注意别用 Icon.png —— 那个是模板里的蓝色 Avalonia 标。
            using var stream = AssetLoader.Open(new Uri("avares://HtaciAI/Assets/AppIcon.png"));
            _appIcon = new Bitmap(stream);
        }
        catch
        {
            // 资源拿不到就算了，不值得为一个小图标让提醒弹不出来
        }

        return _appIcon;
    }

    private void Jump(string sessionId)
    {
        Dismiss();
        SessionNotifier.OpenSession?.Invoke(sessionId);
    }

    // ============================================================
    // 底色与定位
    // ============================================================

    /// <summary>
    /// 按平台实际给到的透明级别选底色。
    ///
    /// <see cref="Window.TransparencyLevelHint"/> 只是优先级列表，平台可能一个都满足不了；
    /// 那种情况由 <see cref="Window.TransparencyBackgroundFallback"/>（白色）兜住，不必在这里处理。
    /// 这里要区分的是另外两种情况：
    /// <list type="bullet">
    ///   <item>拿到了毛玻璃（Mica / 亚克力 / 模糊）→ 留 85% 不透明度，让底衬透一点出来；</item>
    ///   <item>只拿到纯透明（能透明但没有模糊）→ 几乎填满：文字是压在桌面上看的，太透会看不清。</item>
    /// </list>
    /// </summary>
    private void ApplySurface()
        => _card.Background = ActualTransparencyLevel == WindowTransparencyLevel.Transparent
            ? new SolidColorBrush(Color.Parse("#F5FFFFFF"))
            : new SolidColorBrush(Color.Parse("#D9FFFFFF"));

    /// <summary>把所有在屏提醒重新摆到右下角（从下往上堆）。</summary>
    private static void Place()
    {
        for (var i = 0; i < Visible.Count; i++)
            Visible[i].MoveToSlot(i);
    }

    /// <summary>
    /// 摆到右下角第 <paramref name="fromBottom"/> 格（0 = 最靠下）。
    ///
    /// 坐标全是<b>物理像素</b>：<see cref="Screen.WorkingArea"/> 与 <see cref="Window.Position"/> 都是物理像素，
    /// 而宽高是逻辑像素，所以要乘 <see cref="Screen.Scaling"/>。
    /// 高度按内容自适应，所以从最下面那条往上累加各自的实际高度（还没测量过的那几条用占位值）。
    /// 拿不到屏幕信息时保持平台默认位置 —— 宁可位置不对，也不要把窗口摆到看不见的地方去。
    /// </summary>
    private void MoveToSlot(int fromBottom)
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) return;

            var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var area = screen.WorkingArea;

            // 自己下方那几条占掉的高度。列表尾部是最靠下的，所以从尾部往前数。
            var below = 0.0;
            for (var i = 0; i < fromBottom; i++)
                below += HeightOf(Visible[Visible.Count - 1 - i]) + Gap;

            var x = area.X + area.Width - (int)Math.Round((ToastWidth + EdgeMargin) * scale);
            var y = area.Y + area.Height - (int)Math.Round((HeightOf(this) + EdgeMargin) * scale)
                    - (int)Math.Round(below * scale);

            Position = new PixelPoint(x, y);
        }
        catch
        {
            // 平台不支持定位（或屏幕信息拿不到）：退回默认位置
        }
    }

    /// <summary>逻辑高度：布局还没跑完时用占位值，免得首帧把窗口摆到屏幕外面。</summary>
    private static double HeightOf(NotificationToast toast)
        => toast.Bounds.Height > 1 ? toast.Bounds.Height : FallbackHeight;
}
