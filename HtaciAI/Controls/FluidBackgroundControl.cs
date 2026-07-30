using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HtaciAI.Controls;

/// <summary>
/// 流体背景控件 —— 模拟网页版柔和大块光球漂移动效
/// 三层叠加方案中的第二层：若干个带径向渐变的大圆在窗口中缓慢漂移 + 缩放
/// </summary>
public class FluidBackgroundControl : UserControl
{
    // ============================================================
    // 可调参数 —— 修改这里来微调视觉效果
    // ============================================================

    /// <summary>全局速度倍率。1.0 = 默认，2.0 = 翻倍，0.5 = 减半</summary>
    public double Speed { get; set; } = 3.0;

    /// <summary>光球数量</summary>
    private const int BlobCount = 6;

    /// <summary>光球尺寸范围（相对于窗口宽度的比例）</summary>
    private const double SizeMin = 0.4;   // 最小 40% 窗口宽
    private const double SizeMax = 0.8;   // 最大 80% 窗口宽

    /// <summary>光球透明度范围</summary>
    private const double OpacityMin = 0.45;
    private const double OpacityMax = 0.75;

    // ---- 移动频率范围（正弦波的角频率，值越大移动越快）----
    // 网页版 CSS animation duration = 16~28s 一个完整循环
    // 对应角频率 = 2π / 周期 ≈ 0.22 ~ 0.39 rad/s
    private const double FreqMin = 0.22;
    private const double FreqMax = 0.39;

    // ---- 移动幅度范围（相对于窗口尺寸的比例）----
    private const double AmpMin = 0.05;   // 最小振幅 5% 窗口宽/高
    private const double AmpMax = 0.20;   // 最大振幅 20% 窗口宽/高

    // ---- 缩放参数 ----
    private const double ScaleAmpMin = 0.04;   // 缩放振幅最小值
    private const double ScaleAmpMax = 0.12;   // 缩放振幅最大值
    private const double ScaleFreqMin = 0.07;  // 缩放频率最小值
    private const double ScaleFreqMax = 0.20;  // 缩放频率最大值

    // ---- 径向渐变：中心实色区域的占比 ----
    private const double GradientCoreRatio = 0.2;  // 前 20% 为实色，之后渐变到透明

    // ============================================================
    // 内部实现
    // ============================================================

    /// <summary>帧刷新定时器，约 30fps</summary>
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private readonly List<BlobState> _blobs = new();
    private readonly Random _rng = new();
    private readonly Canvas _canvas = new();
    private DateTime _startTime;
    private double _w, _h;
    private bool _initialized;

    /// <summary>光球可选颜色 —— 淡青、淡粉</summary>
    private static readonly Color[] Palette = [Color.Parse("#D7F8FF"), Color.Parse("#FFE4F7")];

    public FluidBackgroundControl()
    {
        IsHitTestVisible = false;
        Content = _canvas;
        _startTime = DateTime.Now;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>
    /// 布局回调：首次获取到有效尺寸时创建光球并启动定时器；
    /// 窗口大小变化时按比例重新计算所有光球的位置和大小
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // 尺寸无效时跳过
        if (finalSize.Width <= 1 || finalSize.Height <= 1)
            return finalSize;

        // 内部 Canvas 填满整个控件
        _canvas.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));

        if (!_initialized)
        {
            _w = finalSize.Width;
            _h = finalSize.Height;
            CreateBlobs();
            _timer.Start();
            _initialized = true;
        }
        else if (Math.Abs(finalSize.Width - _w) > 1 || Math.Abs(finalSize.Height - _h) > 1)
        {
            // 窗口缩放 → 等比缩放所有光球参数
            var sx = finalSize.Width / _w;
            var sy = finalSize.Height / _h;
            _w = finalSize.Width;
            _h = finalSize.Height;
            foreach (var b in _blobs)
            {
                b.BaseX *= sx;
                b.BaseY *= sy;
                b.Element.Width *= sx;
                b.Element.Height *= sy;
                b.Element.CornerRadius = new CornerRadius(b.Element.Width / 2);
                b.AmpX *= sx;
                b.AmpY *= sy;
            }
        }

        return finalSize;
    }

    /// <summary>
    /// 创建所有光球。每个光球 = 圆形 Border + 径向渐变背景 + 随机运动参数
    /// </summary>
    private void CreateBlobs()
    {
        for (int i = 0; i < BlobCount; i++)
        {
            var color = Palette[_rng.Next(Palette.Length)];
            var size = _w * (_rng.NextDouble() * (SizeMax - SizeMin) + SizeMin);
            var peakOpacity = _rng.NextDouble() * (OpacityMax - OpacityMin) + OpacityMin;

            // 圆形光球：用 CornerRadius = 半径 裁剪为圆形
            // 背景用径向渐变模拟 CSS blur() 的柔边效果
            var blob = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = CreateRadialBrush(color, peakOpacity),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(),
            };

            // 随机运动参数
            var state = new BlobState
            {
                Element = blob,
                BaseX = _rng.NextDouble() * (_w - size),
                BaseY = _rng.NextDouble() * (_h - size),
                // 移动振幅
                AmpX = _rng.NextDouble() * _w * (AmpMax - AmpMin) + _w * AmpMin,
                AmpY = _rng.NextDouble() * _h * (AmpMax - AmpMin) + _h * AmpMin,
                // 移动频率（决定漂移快慢）
                FreqX = _rng.NextDouble() * (FreqMax - FreqMin) + FreqMin,
                FreqY = _rng.NextDouble() * (FreqMax - FreqMin) + FreqMin,
                // 随机相位偏移，让各光球运动不同步
                PhaseX = _rng.NextDouble() * Math.PI * 2,
                PhaseY = _rng.NextDouble() * Math.PI * 2,
                // 缩放参数
                ScaleBase = 1.0,
                ScaleAmp = _rng.NextDouble() * (ScaleAmpMax - ScaleAmpMin) + ScaleAmpMin,
                ScaleFreq = _rng.NextDouble() * (ScaleFreqMax - ScaleFreqMin) + ScaleFreqMin,
                ScalePhase = _rng.NextDouble() * Math.PI * 2,
            };

            _canvas.Children.Add(blob);
            _blobs.Add(state);
            ArrangeBlob(state, state.BaseX, state.BaseY);
        }
    }

    /// <summary>
    /// 构造径向渐变画刷：中心实色 → 边缘完全透明
    /// 用来模拟 CSS filter: blur(100px) 产生的柔化边缘效果
    /// </summary>
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

    /// <summary>
    /// 每帧更新：用正弦/余弦函数驱动光球的位移和缩放
    /// 每个光球有独立的频率、振幅、相位 → 产生看似随机的有机运动
    /// </summary>
    private void Tick()
    {
        // 经过的时间（秒）× 全局速度倍率
        var t = (DateTime.Now - _startTime).TotalSeconds * Speed;

        foreach (var b in _blobs)
        {
            // sin/cos 产生的值在 [-1, 1] 之间，乘振幅后叠加到基准位置
            var x = b.BaseX + b.AmpX * Math.Sin(t * b.FreqX + b.PhaseX);
            var y = b.BaseY + b.AmpY * Math.Cos(t * b.FreqY + b.PhaseY);
            var s = b.ScaleBase + b.ScaleAmp * Math.Sin(t * b.ScaleFreq + b.ScalePhase);

            ArrangeBlob(b, x, y);

            if (b.Element.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = s;
                st.ScaleY = s;
            }
        }
    }

    /// <summary>将光球定位到指定坐标（直接调用 Arrange 避免 Canvas 布局开销）</summary>
    private static void ArrangeBlob(BlobState b, double x, double y)
    {
        b.Element.Arrange(new Rect(x, y, b.Element.Width, b.Element.Height));
    }

    /// <summary>单个光球的运行时状态</summary>
    private sealed class BlobState
    {
        public Border Element { get; set; } = null!;

        /// <summary>基准 X/Y 位置</summary>
        public double BaseX, BaseY;

        /// <summary>X/Y 方向移动振幅（像素）</summary>
        public double AmpX, AmpY;

        /// <summary>X/Y 方向移动频率（rad/s），值越大移动越快</summary>
        public double FreqX, FreqY;

        /// <summary>X/Y 方向相位偏移（rad），让各光球运动错开</summary>
        public double PhaseX, PhaseY;

        /// <summary>缩放基准值</summary>
        public double ScaleBase;

        /// <summary>缩放振幅</summary>
        public double ScaleAmp;

        /// <summary>缩放频率（rad/s）</summary>
        public double ScaleFreq;

        /// <summary>缩放相位偏移</summary>
        public double ScalePhase;
    }
}
