using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace HtaciAI.Controls;

/// <summary>
/// 带平滑滚动的 <see cref="ScrollViewer"/>，用法与原生完全一致，可直接替换。
///
/// Windows 下滚轮一次给 ±1，Avalonia 默认把它换算成固定像素后<b>一步到位</b>，观感是跳的。
/// 这里在 Tunnel 阶段截下滚轮事件，只累加一个目标偏移，再逐帧把 <see cref="ScrollViewer.Offset"/>
/// 向目标逼近。
///
/// <b>为什么用 <see cref="TopLevel.RequestAnimationFrame"/> 而不是 DispatcherTimer</b>：
/// <list type="bullet">
///   <item>它跟着渲染帧（垂直同步）回调，不会像固定 16ms 的定时器那样与渲染错拍——
///     错拍正是「帧率低」那种一卡一卡观感的来源；</item>
///   <item>回调带时间戳，于是可以按<b>真实经过的时间</b>推进缓动（见 <see cref="Tau"/>），
///     掉帧时步长自动变大。原来按「每帧固定比例」逼近的写法，帧率一抖动速度就跟着变。</item>
/// </list>
///
/// ⚠️ 继承 <see cref="ScrollViewer"/> 这种「已有控件」时，必须在 App.axaml 里补一份
/// <c>BasedOn="{StaticResource {x:Type ScrollViewer}}"</c> 的 <c>ControlTheme</c>，
/// 因为 ControlTheme 是按精确类型匹配的，不会自动套用基类的主题。
/// </summary>
public class SmoothScrollViewer : ScrollViewer
{
    /// <summary>
    /// 缓动时间常数（秒）。每帧把剩余距离乘上 <c>exp(-dt / Tau)</c>，
    /// 即每经过 Tau 秒，剩余距离衰减到约 37%。
    ///
    /// 这是唯一的「手感」旋钮：**调小更跟手**（0.05 很利落），**调大更飘**（0.12 绵软）。
    /// </summary>
    private const double Tau = 0.07;

    /// <summary>一格滚轮对应的目标位移（像素）。</summary>
    private const double WheelStep = 50;

    /// <summary>小于这个距离就直接落定，避免为了零点几像素无限请求下一帧。</summary>
    private const double SettleThreshold = 0.3;

    private Vector _targetOffset;
    private bool _animating;
    private TimeSpan _lastFrame;

    /// <summary>为 true 时表示这次 Offset 变化是本控件自己推进动画写进去的，不算「外部改动」。</summary>
    private bool _applyingFrame;

    public SmoothScrollViewer()
    {
        AddHandler(
            PointerWheelChangedEvent,
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 外部改了偏移（ScrollToEnd、代码直接赋 Offset、用户拖滚动条）就放弃当前这一轮动画，
        // 否则动画会继续朝旧目标推进，表现为「刚滚到底又被拽回去」。
        if (change.Property == OffsetProperty && !_applyingFrame)
        {
            _animating = false;
            _targetOffset = Offset;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _targetOffset = Offset;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 只是停止推进；_targetOffset 留着，重新挂上时按真实 Offset 重新起步
        _animating = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;

        // 隧道阶段是「由外向内」派发的，所以外层会先跑。而真正的原生滚轮处理在
        // ScrollContentPresenter 里（比 ScrollViewer 更深），因此外层若无脑吃掉事件，
        // 内层嵌套列表就永远滚不动。这里先看一眼指针路径上有没有「这个方向还能滚」的内层
        // 滚动容器，有就让位——内层自己会处理，滚到头时它同样会让位出来，链条自然形成。
        if (HasInnerScrollableUnderPointer(e)) return;

        var maxY = Math.Max(0, Extent.Height - Viewport.Height);
        var maxX = Math.Max(0, Extent.Width - Viewport.Width);

        // 动画没在跑，说明上一轮已经落定（或用户刚拖过滚动条），
        // 此时以真实 Offset 为准重新起步，避免拿陈旧目标值把视图拽回去。
        if (!_animating)
            _targetOffset = Offset;

        var deltaY = e.Delta.Y == 0 ? 0 : -e.Delta.Y * WheelStep;
        var deltaX = e.Delta.X == 0 ? 0 : -e.Delta.X * WheelStep;

        var targetY = Math.Clamp(_targetOffset.Y + deltaY, 0, maxY);
        var targetX = Math.Clamp(_targetOffset.X + deltaX, 0, maxX);

        // 这个方向已经滚不动了就把事件放过去，让它冒泡给外层容器。
        // 少了这一步，「鼠标停在代码块上」这类场景会因为事件被吞掉而完全滚不动整页。
        var movedY = Math.Abs(targetY - Offset.Y) > SettleThreshold;
        var movedX = Math.Abs(targetX - Offset.X) > SettleThreshold;
        if (!movedY && !movedX) return;

        _targetOffset = new Vector(targetX, targetY);
        e.Handled = true;

        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        RequestFrame();
    }

    /// <summary>指针路径上（本控件之下）是否存在「往这个方向还能滚」的内层滚动容器。</summary>
    private bool HasInnerScrollableUnderPointer(PointerWheelEventArgs e)
    {
        if (e.Source is not Visual source) return false;

        for (var v = source; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
        {
            if (v is ScrollViewer inner && CanScrollTowards(inner, e.Delta)) return true;
        }

        return false;
    }

    /// <summary>
    /// 该滚动容器在滚轮方向上是否还有余量。
    ///
    /// ⚠️ 方向不能想当然：Avalonia 的 <c>Delta.Y &gt; 0</c> 是<b>向上</b>滚（内容往下走），
    /// 需要的是「还没到顶」（Offset &gt; 0）；<c>Delta.Y &lt; 0</c> 是向下滚，需要的是「还没到底」。
    /// 这里曾经把两者写反，后果很隐蔽：向下滚时内层明明还能滚，却判定它滚不动，
    /// 于是外层消息列表把滚轮吞掉 —— 表现就是「思考卡/压缩卡里的内容完全滚不动」，
    /// 而且因为向上滚那一侧碰巧是对的，看起来像是「有时灵有时不灵」。
    /// </summary>
    private static bool CanScrollTowards(ScrollViewer viewer, Vector delta)
    {
        var max = viewer.ScrollBarMaximum;
        const double epsilon = 0.5;

        if (delta.Y > 0) return viewer.Offset.Y > epsilon;            // 向上滚：还有上方余量
        if (delta.Y < 0) return viewer.Offset.Y < max.Y - epsilon;    // 向下滚：还有下方余量
        if (delta.X > 0) return viewer.Offset.X > epsilon;
        if (delta.X < 0) return viewer.Offset.X < max.X - epsilon;
        return false;
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

    /// <summary>推进动画时的赋值：标记来源，免得被 OnPropertyChanged 当成「外部改动」而自我取消。</summary>
    private void ApplyOffset(Vector offset)
    {
        _applyingFrame = true;
        try { Offset = offset; }
        finally { _applyingFrame = false; }
    }

    /// <param name="now">渲染帧时间戳。第一帧用它作基准，之后用相邻两帧的差作为 dt。</param>
    private void OnFrame(TimeSpan now)
    {
        if (!_animating) return;

        // 已经脱离可视树（切走标签页、换会话）就没必要继续推进
        if (TopLevel.GetTopLevel(this) is null)
        {
            _animating = false;
            return;
        }

        if (_lastFrame == TimeSpan.Zero)
        {
            // 首帧只记基准，本帧不动——省掉一次 dt 未知的跳跃
            _lastFrame = now;
            RequestFrame();
            return;
        }

        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        // 帧间隔异常（调试断点、系统卡顿）时按一帧算，避免瞬间跳一大段
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60;

        var current = Offset;
        var dy = _targetOffset.Y - current.Y;
        var dx = _targetOffset.X - current.X;

        if (Math.Abs(dy) < SettleThreshold && Math.Abs(dx) < SettleThreshold)
        {
            ApplyOffset(_targetOffset);
            _animating = false;
            return;
        }

        // 按时间衰减：dt 越大这一帧走得越多，帧率变化不会改变整体速度
        var alpha = 1 - Math.Exp(-dt / Tau);

        ApplyOffset(new Vector(
            current.X + dx * alpha,
            current.Y + dy * alpha));

        RequestFrame();
    }
}
