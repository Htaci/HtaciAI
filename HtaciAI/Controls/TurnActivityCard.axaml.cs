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

namespace HtaciAI.Controls;

/// <summary>
/// 一轮回复的「活动汇聚卡」：把同一轮里多次思考 / 工具调用（及其间正文）收进一个可折叠卡片。
/// 折叠态只显示汇总（N 个工具调用 | M 次思考 | K 条消息），点击展开查看步骤序列。
/// 仅当一轮内活动卡片数 ≥2 时由视图在回复结束时包裹使用；单思考无工具调用时不使用。
/// </summary>
public partial class TurnActivityCard : UserControl
{
    private const double ExpandedMaxHeight = 400;
    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    private readonly RotateTransform _chevron;
    private bool _expanded;

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

    /// <summary>折叠到只显示汇总头（回复结束时调用）。</summary>
    public void Collapse()
    {
        if (_expanded) Toggle();
    }

    public void Toggle()
    {
        _expanded = !_expanded;
        ContentScroll!.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
        ContentScroll.Opacity = _expanded ? 1 : 0;
        _chevron.Angle = _expanded ? 90 : 0;
    }

    /// <summary>
    /// 把一轮里的步骤收进一个聚合卡。仅当「思考卡 + 工具卡 ≥ 2」时执行；
    /// 除 <paramref name="finalBody"/>（最后一次输出/最终回答）保留在卡片外，其余步骤（思考卡/工具卡/中间正文）全部移进卡片内容。
    /// 卡片插到模型头（root 索引 0）之后。返回是否执行了聚合。
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
