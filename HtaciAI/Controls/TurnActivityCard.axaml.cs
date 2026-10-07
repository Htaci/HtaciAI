using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace HtaciAI.Controls;

/// <summary>
/// 一轮回复的「活动汇聚卡」：把同一轮内所有思考卡 / 工具卡收进一个可折叠卡片（正文不在此列，始终排在卡外）。
/// 头部显示状态与计数（处理中 / 已处理 · N 次思考 | M 个工具调用），点击展开查看步骤序列。
/// 视图在首个思考分片或工具调用到达时即创建并展开，回复结束后翻状态并折叠。
/// </summary>
public partial class TurnActivityCard : UserControl
{
    private const double ExpandedMaxHeight = 400;
    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    private readonly RotateTransform _chevron;
    private bool _expanded;
    private bool _autoExpandedForSelection;

    /// <summary>步骤序列宿主（视图向其追加思考卡 / 工具卡 / 正文块）。</summary>
    public StackPanel Steps => ContentPanel!;

    public TurnActivityCard()
    {
        InitializeComponent();

        _chevron = new RotateTransform();
        Chevron.RenderTransform = _chevron;
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

        ContentScroll!.Transitions = new Transitions
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

        Header!.PointerEntered += (_, _) => Header.Background = new SolidColorBrush(Color.Parse("#99F0F0F0"));
        Header.PointerExited += (_, _) => Header.Background = Brushes.Transparent;
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(Header).Properties.IsLeftButtonPressed)
                Toggle();
        };
    }

    /// <summary>设置折叠头汇总文本：N 个工具调用 | M 次思考 | K 条消息。</summary>
    public void SetSummary(int tools, int thinkings, int messages)
        => HeaderText!.Text = $"{tools} 个工具调用   |   {thinkings} 次思考   |   {messages} 条消息";

    /// <summary>流式中：形如「处理中  3次思考  2次工具调用」。</summary>
    public void SetProcessing(int tools, int thinkings)
        => HeaderText!.Text = Join("处理中", thinkings, tools);

    /// <summary>
    /// 回复结束：形如「用时 15秒  1次思考」「用时 17分22秒  32次思考  45次工具调用」。
    /// 没有工具调用就不出现该段；<paramref name="elapsed"/> 为 null 时退回「已处理」。
    /// </summary>
    public void SetFinished(int tools, int thinkings, TimeSpan? elapsed)
        => HeaderText!.Text = Join(elapsed is { } e ? $"用时 {FormatDuration(e)}" : "已处理", thinkings, tools);

    /// <summary>各段之间用两个空格分隔，计数为 0 的段直接不出现。</summary>
    private static string Join(string head, int thinkings, int tools)
    {
        var parts = new List<string> { head };
        if (thinkings > 0) parts.Add($"{thinkings}次思考");
        if (tools > 0) parts.Add($"{tools}次工具调用");
        return string.Join("  ", parts);
    }

    /// <summary>时长展示：不足 1 分显示「N秒」，不足 1 时显示「M分S秒」，否则「H时M分S秒」。</summary>
    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}时{t.Minutes}分{t.Seconds}秒";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}分{t.Seconds}秒";
        return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}秒";
    }

    /// <summary>展开内容区。</summary>
    public void Expand()
    {
        if (!_expanded) Toggle();
    }

    /// <summary>折叠到只显示汇总头（回复结束时调用）。</summary>
    public void Collapse()
    {
        if (_expanded) Toggle();
    }

    /// <summary>内容区滚动到底（流式追加步骤时保持最新可见）。</summary>
    public void ScrollContentToEnd() => ContentScroll?.ScrollToEnd();

    /// <summary>
    /// 原文模式开关：进入时自动展开（折叠状态下内部文本不可见、无从选中），
    /// 退出时仅回收本次因选择模式而展开的卡片，用户手动展开的不动。
    /// 随后递归把开关下发到内部的折叠容器与文本控件。
    /// </summary>
    public void SetSelectionMode(bool on)
    {
        if (on)
        {
            if (!_expanded)
            {
                _autoExpandedForSelection = true;
                Toggle();
            }
        }
        else if (_autoExpandedForSelection)
        {
            _autoExpandedForSelection = false;
            if (_expanded) Toggle();
        }

        ApplySelectionModeDeep(this, on);
    }

    /// <summary>递归下发原文模式：折叠容器转展开、文本控件转原生可选。</summary>
    public static void ApplySelectionModeDeep(Control root, bool on)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is SelectionModeHost host)
                host.SetSelectionMode(on);
            else if (child is CustomSelectableTextBlock block)
                block.NativeSelectionEnabled = on;

            if (child is Control c)
                ApplySelectionModeDeep(c, on);
        }
    }

    public void Toggle()
    {
        _expanded = !_expanded;
        ContentScroll!.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
        ContentScroll.Opacity = _expanded ? 1 : 0;
        _chevron.Angle = _expanded ? 90 : 0;
    }

    /// <summary>
    /// 【保留备用】回复结束后一次性把步骤收进聚合卡。
    /// 现行流程已改为「首个思考/工具调用到达时即创建卡片并持续追加」，且正文始终排在卡外，
    /// 因此视图不再调用本方法；保留以便将来需要别的聚合形态时复用。
    /// </summary>
    public static bool TryWrap(StackPanel root, List<Control> steps, int tools, int thinkings, Control? finalBody)
    {
        if (thinkings + tools < 2 || steps.Count == 0) return false;

        var card = new TurnActivityCard();
        card.SetSummary(tools, thinkings, steps.Count);
        foreach (var c in steps)
        {
            if (ReferenceEquals(c, finalBody)) continue;
            root.Children.Remove(c);
            card.Steps.Children.Add(c);
        }
        root.Children.Insert(Math.Min(1, root.Children.Count), card);
        card.Collapse();
        return true;
    }
}
