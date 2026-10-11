using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 一轮 AI 回复的内容宿主：持有内容区容器与蓝框，并提供「显示全部原文」开关。
///
/// 结构：Border（蓝框）→ Grid → [ ContentPanel（聚合卡 + 各正文块）, AllRawBlock（全部原文） ]
/// 切换只改变可见性与文本内容，不重建任何东西。
/// 轮内的跨块拖选由 <see cref="Selector"/> 负责，与原文显示互不干扰（渲染态下就能拖选）。
/// </summary>
public sealed class TurnSelectionHost
{
    /// <summary>「显示全部原文」按钮的字形（Segoe Fluent Icons）。</summary>
    private const string RawIcon = "\uE8AC";

    /// <summary>
    /// 全部原文的最大高度。原文未经渲染、没有折叠，一轮长任务下来能有几千像素，
    /// 不限高就会把整条对话撑开。超出的部分交给滚动条。
    /// </summary>
    private const double RawMaxHeight = 400;

    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush ActiveBgBrush = new SolidColorBrush(Color.Parse("#1A4A90D9"));
    private static readonly IBrush IdleFgBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush HoverFgBrush = new SolidColorBrush(Color.Parse("#374151"));

    private readonly SelectableTextBlock _allRaw;
    private readonly SmoothScrollViewer _allRawScroll;
    private readonly TextBlock _allIcon;
    private readonly Button _allBtn;
    private TurnActivityCard? _card;

    /// <summary>蓝色模式边框（内容区外框）。</summary>
    public Border Border { get; }

    /// <summary>本轮内的跨块拖选（渲染态即可用，与原文显示互不干扰）。</summary>
    public TurnTextSelector Selector { get; } = new();

    /// <summary>内容容器：依次放入聚合卡与各正文块。</summary>
    public StackPanel ContentPanel { get; }

    /// <summary>是否正在显示全部原文。</summary>
    public bool ShowingFullRaw { get; private set; }

    /// <summary>「全部原文」文本的构造回调（由视图按 turn 的原始消息拼装）。</summary>
    public Func<string>? BuildFullRawText { get; set; }

    public TurnSelectionHost()
    {
        ContentPanel = new StackPanel { Spacing = 10 };

        _allRaw = new SelectableTextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            IsVisible = false,
        };

        var stack = new Grid();
        stack.Children.Add(ContentPanel);
        // 限高 + 滚动条：原文不限高会把整条对话一次性撑得很长
        _allRawScroll = new SmoothScrollViewer
        {
            Content = _allRaw,
            MaxHeight = RawMaxHeight,
            IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 原文里按下选字不该把这个框自己滚滚（原因见 DisableFocusBringIntoView）
        SmoothScrollViewer.DisableFocusBringIntoView(_allRawScroll);
        stack.Children.Add(_allRawScroll);

        Border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = Brushes.Transparent,
            BorderThickness = default,
            Padding = default,
            Child = stack,
            // 透明背景让内容区的空白（段间、块间）也能命中本容器：
            // 否则「点空白取消选中」这类操作根本收不到指针事件。
            Background = Brushes.Transparent,
        };

        Selector.AttachHost(Border);

        _allIcon = new TextBlock
        {
            Text = RawIcon,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = IdleFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _allBtn = new Button
        {
            Content = _allIcon,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_allBtn, "显示全部原文");
        _allBtn.Click += (_, _) => SetShowingFullRaw(!ShowingFullRaw);
        _allBtn.PointerEntered += (_, _) => { if (!ShowingFullRaw) _allIcon.Foreground = HoverFgBrush; };
        _allBtn.PointerExited += (_, _) => { if (!ShowingFullRaw) _allIcon.Foreground = IdleFgBrush; };

        ApplyVisual();
    }

    /// <summary>登记一个正文块（按顺序）。</summary>
    public void AddBody(TurnBodyView body) => ContentPanel.Children.Add(body);

    /// <summary>登记聚合卡（置顶，卡片应在所有正文块之上）。</summary>
    public void SetCard(TurnActivityCard card)
    {
        _card = card;
        ContentPanel.Children.Insert(0, card);
    }

    /// <summary>
    /// 登记一个正文块并直接放进聚合卡：它只是过程叙述（后面还有工具调用或正文），
    /// 整轮只把最后那段正文留在卡外。
    /// </summary>
    public void AddNarration(TurnBodyView body) => PutInCard(body);

    /// <summary>
    /// 把已经排在正文流里的正文块挪进聚合卡 —— 后面又出现了工具调用，
    /// 说明它只是过程叙述。挪的时机就是「下一张卡出现之前」，顺序自然是对的。
    /// </summary>
    public void MoveBodyIntoCard(TurnBodyView body)
    {
        ContentPanel.Children.Remove(body);
        PutInCard(body);
    }

    /// <summary>把正文块放进聚合卡的步骤序列里（按调用顺序追加）。</summary>
    private void PutInCard(TurnBodyView body) => _card?.Steps.Children.Add(body);

    /// <summary>操作栏里的「显示全部原文」按钮（放蓝框外）。</summary>
    public Control CreateModeButtons() => _allBtn;

    /// <summary>切换「显示全部原文」。</summary>
    public void SetShowingFullRaw(bool show)
    {
        // 切换呈现方式时先清掉拖选高亮：原文态下这些块已经不可见，留着会变成「退回来又冒出来」
        Selector.ClearSelection();

        // 全部原文：内容区整体隐藏，改由单个原生文本框承载（文本按需即时拼装）
        if (show && BuildFullRawText is not null)
            _allRaw.Text = BuildFullRawText();
        ShowingFullRaw = show;
        // 每次展开都从顶部看起，不要沿用上次的滚动位置
        if (show) _allRawScroll.Offset = default;
        ApplyVisual();
    }

    private void ApplyVisual()
    {
        _allRawScroll.IsVisible = ShowingFullRaw;
        ContentPanel.IsVisible = !ShowingFullRaw;

        // 蓝框标识：显示原文才占位，退出后完全还原布局
        Border.BorderThickness = ShowingFullRaw ? new Thickness(1.5) : default;
        Border.Padding = ShowingFullRaw ? new Thickness(10) : default;
        Border.BorderBrush = ShowingFullRaw ? AccentBrush : Brushes.Transparent;

        _allIcon.Foreground = ShowingFullRaw ? AccentBrush : IdleFgBrush;
        _allBtn.Background = ShowingFullRaw ? ActiveBgBrush : Brushes.Transparent;
    }
}
