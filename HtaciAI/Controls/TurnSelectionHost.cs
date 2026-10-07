using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>一轮回复的文字选择模式。</summary>
public enum TurnSelectionMode
{
    /// <summary>普通渲染态：不显示选择相关 UI，内容不可选中。</summary>
    None,

    /// <summary>正文原文：按块切换——正文显示 Markdown 源码，卡片内文本控件变为原生可选中，卡片外观保留。</summary>
    BodyRaw,

    /// <summary>全部原文：整轮内容（思考 / 工具 / 正文）合并为单个原生可选文本框。</summary>
    AllRaw,
}

/// <summary>
/// 一轮 AI 回复的「文字选择模式」宿主：持有内容区容器与蓝色模式边框，
/// 提供「显示正文原文 / 显示全部原文」两个互斥切换按钮。
///
/// 结构：Border（蓝框）→ Grid → [ ContentPanel（卡片 + 各正文块）, AllRawBlock（全部原文） ]
/// 进入选择模式时只切换可见性与文本控件类型，不重建任何内容。
/// </summary>
public sealed class TurnSelectionHost
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush ActiveBgBrush = new SolidColorBrush(Color.Parse("#1A4A90D9"));
    private static readonly IBrush IdleFgBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush HoverFgBrush = new SolidColorBrush(Color.Parse("#374151"));

    private readonly SelectableTextBlock _allRaw;
    private readonly List<TurnBodyView> _bodies = new();
    private readonly Button _bodyBtn;
    private readonly Button _allBtn;
    private TurnActivityCard? _card;

    /// <summary>蓝色模式边框（内容区外框）。</summary>
    public Border Border { get; }

    /// <summary>内容容器：依次放入聚合卡与各正文块。</summary>
    public StackPanel ContentPanel { get; }

    /// <summary>当前模式。</summary>
    public TurnSelectionMode Mode { get; private set; } = TurnSelectionMode.None;

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
        stack.Children.Add(_allRaw);

        Border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = Brushes.Transparent,
            BorderThickness = default,
            Padding = default,
            Child = stack,
        };

        _bodyBtn = CreateToggleButton("显示正文原文", TurnSelectionMode.BodyRaw);
        _allBtn = CreateToggleButton("显示全部原文", TurnSelectionMode.AllRaw);
        UpdateButtonVisual(_bodyBtn, false);
        UpdateButtonVisual(_allBtn, false);
    }

    /// <summary>登记一个正文块（按顺序）。</summary>
    public void AddBody(TurnBodyView body)
    {
        _bodies.Add(body);
        ContentPanel.Children.Add(body);
    }

    /// <summary>登记聚合卡（置顶，卡片应在所有正文块之上）。</summary>
    public void SetCard(TurnActivityCard card)
    {
        _card = card;
        ContentPanel.Children.Insert(0, card);
    }

    /// <summary>操作栏里的两个模式按钮（放蓝框外）。</summary>
    public Control CreateModeButtons()
        => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 0, 0, 0),
            Children = { _bodyBtn, _allBtn },
        };

    /// <summary>切换模式（传当前模式即退出到 None）。</summary>
    public void Toggle(TurnSelectionMode mode)
        => SetMode(Mode == mode ? TurnSelectionMode.None : mode);

    public void SetMode(TurnSelectionMode mode)
    {
        Mode = mode;

        var bodyRaw = mode == TurnSelectionMode.BodyRaw;
        var allRaw = mode == TurnSelectionMode.AllRaw;

        // 全部原文：内容区整体隐藏，改由单个原生文本框承载（文本按需即时拼装）
        if (allRaw && BuildFullRawText is not null)
            _allRaw.Text = BuildFullRawText();
        _allRaw.IsVisible = allRaw;
        ContentPanel.IsVisible = !allRaw;

        // 每块一个：正文切源码、卡片内文本控件转原生可选
        foreach (var body in _bodies)
            body.SetRawMode(bodyRaw);
        _card?.SetSelectionMode(bodyRaw);

        // 蓝框标识：进入选择模式才占位，退出后完全还原布局
        var active = mode != TurnSelectionMode.None;
        Border.BorderThickness = active ? new Thickness(1.5) : default;
        Border.Padding = active ? new Thickness(10) : default;
        Border.BorderBrush = active ? AccentBrush : Brushes.Transparent;

        UpdateButtonVisual(_bodyBtn, bodyRaw);
        UpdateButtonVisual(_allBtn, allRaw);
    }

    // ---- 按钮 ----

    private Button CreateToggleButton(string text, TurnSelectionMode mode)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = block,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = default,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => Toggle(mode);
        btn.PointerEntered += (_, _) => { if (Mode != mode) block.Foreground = HoverFgBrush; };
        btn.PointerExited += (_, _) => { if (Mode != mode) block.Foreground = IdleFgBrush; };
        return btn;
    }

    private static void UpdateButtonVisual(Button btn, bool active)
    {
        if (btn.Content is not TextBlock block) return;
        block.Foreground = active ? AccentBrush : IdleFgBrush;
        btn.Background = active ? ActiveBgBrush : Brushes.Transparent;
    }
}

/// <summary>
/// 可被 <see cref="TurnActivityCard.SetSelectionMode"/> 递归发现的折叠容器（思考卡 / 工具卡）：
/// 进入原文模式时自动展开以便选中内容，退出时恢复原本的展开状态。
/// </summary>
public sealed class SelectionModeHost : Border
{
    private bool _autoExpanded;

    /// <summary>查询当前是否展开。</summary>
    public Func<bool>? GetExpanded { get; set; }

    /// <summary>展开。</summary>
    public Action? Expand { get; set; }

    /// <summary>收起。</summary>
    public Action? Collapse { get; set; }

    public void SetSelectionMode(bool on)
    {
        if (on)
        {
            if (GetExpanded?.Invoke() == false)
            {
                _autoExpanded = true;
                Expand?.Invoke();
            }
        }
        else if (_autoExpanded)
        {
            // 仅回收到「因进入选择模式而展开」的卡片，用户手动展开的不动
            _autoExpanded = false;
            if (GetExpanded?.Invoke() == true) Collapse?.Invoke();
        }
    }
}
