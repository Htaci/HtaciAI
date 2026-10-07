using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 上下文压缩在消息流里的呈现：一条「———————— 已压缩上下文 ——————————」分割线，
/// 点它展开一张卡片，里面是压缩出来的摘要全文与一个删除入口。
///
/// 刻意做成「分割线 + 可展开卡片」而不是一张常驻的大卡：它平时只占一行，
/// 不抢正文的位置；需要核对摘要内容时才展开。
///
/// 删除本身不在这里做（这里只负责发事件）：删除要二次确认、要重载消息列表，
/// 都是视图层的活。
/// </summary>
public partial class CompressedContextCard : UserControl
{
    /// <summary>展开后卡片的最大高度。取够大的值即可 —— MaxHeight 只封顶，内容矮时不会撑开。</summary>
    private const double ExpandedMaxHeight = 420;

    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    private static readonly IBrush DividerIdle = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush DividerHover = new SolidColorBrush(Color.Parse("#4B5563"));

    private readonly RotateTransform _chevron;
    private bool _expanded;

    /// <summary>用户点了删除。由宿主负责确认与落库。</summary>
    public event Action? DeleteRequested;

    public CompressedContextCard()
    {
        InitializeComponent();

        _chevron = new RotateTransform();
        Chevron!.RenderTransform = _chevron;
        Chevron.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _chevron.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = RotateTransform.AngleProperty,
                Duration = AnimDuration,
                Easing = new CubicEaseInOut(),
            },
        };

        Card!.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Layoutable.MaxHeightProperty,
                Duration = AnimDuration,
                Easing = new CubicEaseInOut(),
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = AnimDuration,
                Easing = new CubicEaseInOut(),
            },
        };

        DividerRow!.PointerEntered += (_, _) => DividerText!.Foreground = DividerHover;
        DividerRow.PointerExited += (_, _) => DividerText!.Foreground = DividerIdle;
        DividerRow.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(DividerRow).Properties.IsLeftButtonPressed)
            {
                Toggle();
                e.Handled = true;
            }
        };
        ToolTip.SetTip(DividerRow, "这是上下文压缩的位置：此前的消息不再发给模型，只发这段摘要。点击查看摘要。");

        DeleteBtn!.PointerEntered += (_, _) => DeleteBtn.Background = new SolidColorBrush(Color.Parse("#14DC2626"));
        DeleteBtn.PointerExited += (_, _) => DeleteBtn.Background = Brushes.Transparent;
        DeleteBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;   // 别冒泡到分割线上变成「收起卡片」
            DeleteRequested?.Invoke();
        };
    }

    /// <summary>
    /// 填入内容。<paramref name="info"/> 是摘要上方那行小字（覆盖了多少条、什么时候压的、哪个模型）。
    /// </summary>
    public void SetContent(string summary, string info)
    {
        SummaryText!.Text = summary;
        InfoText!.Text = info;
    }

    public bool IsExpanded => _expanded;

    public void Expand()
    {
        if (!_expanded) Toggle();
    }

    public void Toggle()
    {
        _expanded = !_expanded;

        Card!.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
        Card.Opacity = _expanded ? 1 : 0;
        _chevron.Angle = _expanded ? 90 : 0;

        // 摘要长的时候，展开的瞬间就把滚动条摆到顶，别让人看到半截
        if (_expanded) ContentScroll?.ScrollToHome();
    }
}
