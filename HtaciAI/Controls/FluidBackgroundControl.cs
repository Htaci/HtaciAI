using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HtaciAI.Controls;

/// <summary>
/// 流体背景控件 — 模拟网页版的柔和大块光球漂移动效
/// 每个光球沿随机生成的路径往返漂移，拥有 30~60s 生命周期，
/// 结束时淡出缩小，重生时淡入放大，首轮生命周期错峰防止同时消亡。
/// </summary>
public class FluidBackgroundControl : UserControl
{
    // ============================================================
    // 可调参数
    // ============================================================

    /// <summary>全局速度倍率</summary>
    public double Speed { get; set; } = 0.3;

    /// <summary>光球数量</summary>
    private const int BlobCount = 6;

    /// <summary>路径途经点数量</summary>
    private const int WaypointCount = 5;

    /// <summary>光球尺寸范围（相对窗口宽度的比例）</summary>
    private const double SizeMin = 0.4;
    private const double SizeMax = 0.8;

    /// <summary>光球透明度范围</summary>
    private const double OpacityMin = 0.45;
    private const double OpacityMax = 0.75;

    /// <summary>完整路径周期范围（秒），网页版 CSS animation duration = 16~28s</summary>
    private const double CycleDurationMin = 16.0;
    private const double CycleDurationMax = 28.0;

    /// <summary>生命周期范围（秒）</summary>
    private const double LifeMin = 30.0;
    private const double LifeMax = 60.0;

    /// <summary>出生动画时长（淡入 + 放大）</summary>
    private const double BirthDuration = 2.5;

    /// <summary>死亡动画时长（淡出 + 缩小），不要太快</summary>
    private const double DeathDuration = 3.5;

    /// <summary>环境缩放振幅（微弱的呼吸感）</summary>
    private const double AmbientScaleAmp = 0.03;

    /// <summary>环境缩放频率</summary>
    private const double AmbientScaleFreq = 0.5;

    // ---- 径向渐变：中心实色区域的占比 ----
    private const double GradientCoreRatio = 0.2;

    // ============================================================
    // 内部字段
    // ============================================================

    /// <summary>帧刷新定时器，约 30fps</summary>
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private readonly List<BlobState> _blobs = new();
    private readonly Random _rng = new();
    private readonly Canvas _canvas = new();
    private DateTime _lastTick;
    private double _w, _h;
    private bool _initialized;

    /// <summary>可选颜色 —— 淡青、淡粉</summary>
    private static readonly Color[] Palette = [Color.Parse("#D7F8FF"), Color.Parse("#FFE4F7")];

    public FluidBackgroundControl()
    {
        IsHitTestVisible = false;
        Content = _canvas;
        _lastTick = DateTime.Now;
        _timer.Tick += (_, _) => Tick();
    }

    // ============================================================
    // 布局
    // ============================================================

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width <= 1 || finalSize.Height <= 1)
            return finalSize;

        _canvas.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));

        if (!_initialized)
        {
            _w = finalSize.Width;
            _h = finalSize.Height;
            CreateInitialBlobs();
            _timer.Start();
            _initialized = true;
        }
        else if (Math.Abs(finalSize.Width - _w) > 1 || Math.Abs(finalSize.Height - _h) > 1)
        {
            var sx = finalSize.Width / _w;
            var sy = finalSize.Height / _h;
            _w = finalSize.Width;
            _h = finalSize.Height;
            foreach (var b in _blobs)
            {
                b.Element.Width *= sx;
                b.Element.Height *= sy;
                b.Element.CornerRadius = new CornerRadius(b.Element.Width / 2);
                for (int i = 0; i < b.Waypoints.Length; i++)
                    b.Waypoints[i] = new Point(b.Waypoints[i].X * sx, b.Waypoints[i].Y * sy);
            }
        }

        return finalSize;
    }

    // ============================================================
    // 光球创建
    // ============================================================

    private void CreateInitialBlobs()
    {
        for (int i = 0; i < BlobCount; i++)
        {
            var color = PickColor();
            var blob = CreateBlobElement(color);
            var state = new BlobState { Element = blob, Color = color };
            GenerateWaypoints(state);
            state.CycleDuration = _rng.NextDouble() * (CycleDurationMax - CycleDurationMin) + CycleDurationMin;
            state.TargetOpacity = _rng.NextDouble() * (OpacityMax - OpacityMin) + OpacityMin;
            state.TargetScale = _rng.NextDouble() * (SizeMax - SizeMin) + SizeMin;
            state.Age = 0;

            // 首轮生命周期错峰：第 i 个 = (前 i 次随机的平均值) + 一次随机
            state.MaxLife = CalculateStaggeredLife(i);

            _canvas.Children.Add(blob);
            _blobs.Add(state);
            UpdateBlobVisual(state, 0);
        }
    }

    /// <summary>
    /// 首轮生命周期错峰：防止所有光球同时消亡
    /// 公式：life[i] = avg(i个30~60随机) + random(30,60)
    /// </summary>
    private double CalculateStaggeredLife(int index)
    {
        double sum = 0;
        for (int j = 0; j < index; j++)
            sum += _rng.NextDouble() * (LifeMax - LifeMin) + LifeMin;

        double avg = index > 0 ? sum / index : 0;
        double rand = _rng.NextDouble() * (LifeMax - LifeMin) + LifeMin;
        return avg + rand;
    }

    /// <summary>创建圆形光球元素</summary>
    private Border CreateBlobElement(Color color)
    {
        var size = _w * (_rng.NextDouble() * (SizeMax - SizeMin) + SizeMin);
        var peakOpacity = _rng.NextDouble() * (OpacityMax - OpacityMin) + OpacityMin;

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = CreateRadialBrush(color, peakOpacity),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new ScaleTransform(),
        };
    }

    /// <summary>径向渐变画刷 —— 模拟 CSS blur() 柔边效果</summary>
    private static IBrush CreateRadialBrush(Color color, double peakOpacity)
    {
        var peak = new Color((byte)(byte.MaxValue * peakOpacity), color.R, color.G, color.B);
        var transparent = new Color(0, color.R, color.G, color.B);

        return new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(peak, 0.0),
                new GradientStop(peak, GradientCoreRatio),
                new GradientStop(transparent, 1.0),
            },
        };
    }

    // ============================================================
    // 颜色约束：至少保留 2 个同色，避免极端（如 5粉1蓝）
    // ============================================================

    private Color PickColor()
    {
        int cyan = _blobs.Count(b => b.Color == Palette[0]);
        int pink = _blobs.Count(b => b.Color == Palette[1]);

        // 已达上限则强制选少数色
        if (cyan >= BlobCount - 2) return Palette[1];
        if (pink >= BlobCount - 2) return Palette[0];

        return Palette[_rng.Next(2)];
    }

    // ============================================================
    // 路径生成
    // ============================================================

    /// <summary>生成随机途经点，分布在屏幕各处（含屏幕外边缘）</summary>
    private void GenerateWaypoints(BlobState state)
    {
        state.Waypoints = new Point[WaypointCount];
        for (int i = 0; i < WaypointCount; i++)
        {
            // X/Y 范围：-30% ~ 130%，允许部分光球从屏幕外飘入
            state.Waypoints[i] = new Point(
                _rng.NextDouble() * _w * 1.6 - _w * 0.3,
                _rng.NextDouble() * _h * 1.6 - _h * 0.3);
        }
    }

    // ============================================================
    // 光球重生
    // ============================================================

    private void RespawnBlob(BlobState state)
    {
        var color = PickColor();
        state.Color = color;
        state.Age = 0;
        state.MaxLife = _rng.NextDouble() * (LifeMax - LifeMin) + LifeMin;
        state.TargetOpacity = _rng.NextDouble() * (OpacityMax - OpacityMin) + OpacityMin;
        state.TargetScale = _rng.NextDouble() * (SizeMax - SizeMin) + SizeMin;

        // 更新元素尺寸和背景颜色
        var size = _w * state.TargetScale;
        state.Element.Width = size;
        state.Element.Height = size;
        state.Element.CornerRadius = new CornerRadius(size / 2);
        state.Element.Background = CreateRadialBrush(color, state.TargetOpacity);
        state.Element.Opacity = 0; // 从透明开始出生动画

        GenerateWaypoints(state);
        state.CycleDuration = _rng.NextDouble() * (CycleDurationMax - CycleDurationMin) + CycleDurationMin;
    }

    // ============================================================
    // 每帧更新
    // ============================================================

    private void Tick()
    {
        var now = DateTime.Now;
        var dt = (now - _lastTick).TotalSeconds * Speed;
        _lastTick = now;

        foreach (var state in _blobs)
        {
            state.Age += dt;

            // 检查是否死亡
            if (state.Age >= state.MaxLife)
            {
                RespawnBlob(state);
                continue;
            }

            UpdateBlobVisual(state, state.Age);
        }
    }

    /// <summary>根据 age 更新光球的：路径位置、缩放、透明度</summary>
    private void UpdateBlobVisual(BlobState state, double age)
    {
        // ---- 路径位置 ----
        var position = GetPathPosition(state, age);

        // ---- 缩放：出生放大 × 死亡缩小 × 环境呼吸 ----
        double scale = 1.0;
        if (age < BirthDuration)
            scale *= SmoothStep(age / BirthDuration);           // 出生：0 → 1
        if (age > state.MaxLife - DeathDuration)
            scale *= SmoothStep((state.MaxLife - age) / DeathDuration); // 死亡：1 → 0

        scale += AmbientScaleAmp * Math.Sin(age * AmbientScaleFreq);

        double finalScale = state.TargetScale * scale;

        // ---- 透明度：出生淡入 × 死亡淡出 ----
        double opacity = 1.0;
        if (age < BirthDuration)
            opacity = SmoothStep(age / BirthDuration);
        if (age > state.MaxLife - DeathDuration)
            opacity = SmoothStep((state.MaxLife - age) / DeathDuration);

        // ---- 应用 ----
        Canvas.SetLeft(state.Element, position.X - state.Element.Width * finalScale / state.TargetScale / 2 + state.Element.Width / 2);
        Canvas.SetTop(state.Element, position.Y - state.Element.Height * finalScale / state.TargetScale / 2 + state.Element.Height / 2);

        if (state.Element.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }

        state.Element.Opacity = opacity;
    }

    /// <summary>
    /// 计算光球在路径上的当前位置（alternate 往返模式）
    /// cycleProgress 在 0~1 之间，0→0.5 正向，0.5→1 反向
    /// </summary>
    private Point GetPathPosition(BlobState state, double age)
    {
        double cycleT = (age % state.CycleDuration) / state.CycleDuration;

        // 正向(0→1) 或 反向(1→0)
        double pathT = cycleT <= 0.5
            ? cycleT * 2
            : 2.0 - cycleT * 2;

        // 将 pathT (0~1) 映射到途经点区间
        int segCount = state.Waypoints.Length - 1;
        double rawSeg = pathT * segCount;
        int seg = (int)rawSeg;
        if (seg >= segCount) seg = segCount - 1;

        double segT = rawSeg - seg; // 0~1 在当前段内的进度
        segT = CubicEaseInOut(segT);

        var a = state.Waypoints[seg];
        var b = state.Waypoints[seg + 1];
        return new Point(a.X + (b.X - a.X) * segT, a.Y + (b.Y - a.Y) * segT);
    }

    // ============================================================
    // 缓动工具函数
    // ============================================================

    /// <summary>三次方缓入缓出，使运动更柔滑</summary>
    private static double CubicEaseInOut(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Smoothstep 缓入缓出</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    // ============================================================
    // 光球状态
    // ============================================================

    private sealed class BlobState
    {
        public Border Element { get; set; } = null!;
        public Color Color;
        public double TargetOpacity;
        public double TargetScale;

        /// <summary>当前已活时间（秒）</summary>
        public double Age;

        /// <summary>总生命周期（秒）</summary>
        public double MaxLife;

        /// <summary>路径途经点</summary>
        public Point[] Waypoints = [];

        /// <summary>完整路径周期（秒）</summary>
        public double CycleDuration;
    }
}
