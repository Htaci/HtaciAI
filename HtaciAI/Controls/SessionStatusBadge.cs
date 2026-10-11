using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Controls;

/// <summary>
/// 会话列表项右侧的状态标记（占时间那一格的位置）：
/// 请求进行中转圈、等作答 / 授权时黄胶囊、本轮结束未看时绿点、空闲不显示。
/// </summary>
public sealed class SessionStatusBadge : UserControl
{
    private const string WaitingFg = "#B45309";
    private const string WaitingBg = "#FEF3C7";
    private const string DoneHex = "#22C55E";

    private readonly LoadingRing _ring = new();
    private readonly Border _waiting;
    private readonly Border _done;

    public SessionStatusBadge()
    {
        _waiting = new Border
        {
            Background = new SolidColorBrush(Color.Parse(WaitingBg)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = "等待确认",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse(WaitingFg)),
            },
        };
        ToolTip.SetTip(_waiting, "有提问或工具授权等待你处理");

        _done = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(DoneHex)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 3, 0),
        };
        ToolTip.SetTip(_done, "已回复");

        Content = new Grid
        {
            Height = 24,
            Children = { _ring, _waiting, _done },
        };
        Set(SessionActivity.Idle);
    }

    /// <summary>按状态切换显示；空闲时整个控件隐藏（让位给时间）。</summary>
    public void Set(SessionActivity activity)
    {
        IsVisible = activity != SessionActivity.Idle;

        // 同时只能有一个可见：转圈只在「已挂到视觉树上且可见」时才转
        _ring.IsVisible = activity == SessionActivity.Running;
        _waiting.IsVisible = activity == SessionActivity.WaitingConfirm;
        _done.IsVisible = activity == SessionActivity.Done;
    }
}
