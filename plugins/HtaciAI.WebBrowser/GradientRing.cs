using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 一圈「流动」的渐变描边，用来表示「这个页面正在被智能体共享」。
///
/// <b>为什么描边必须让 WebView 让出边距</b>：WebView2 是 Win32 子窗口，
/// 永远画在 Avalonia 内容之上。把 <see cref="Border"/> 套在它外面不会有任何效果 ——
/// 那圈线会被网页整条盖掉。所以真正显示描边的空间是靠给 WebView 加 Margin 腾出来的，
/// 这个方法返回的宽度就是要让出来的量。
///
/// <b>为什么用定时器而不是 Animation</b>：渐变流动是「持续改画笔」，而 Avalonia 的
/// Animation 建立在属性过渡上（改的是属性值），对 Brush 内部的 GradientStop 集合
/// 没有稳定的过渡支持。定时器每帧递进一组偏移既直观又可控，代价可以忽略。
/// </summary>
internal sealed class GradientRing : IDisposable
{
    /// <summary>描边宽度（DIP）。WebView 的 Margin 必须用同一个值。</summary>
    public const double Thickness = 2.5;

    /// <summary>一整个循环的时长。太快会晃眼，太慢会让人以为是静态装饰。</summary>
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2.2);

    private readonly Border _border;
    private readonly LinearGradientBrush _brush;
    private readonly DispatcherTimer? _timer;
    private readonly double[] _offsets;
    private double _phase;
    private bool _disposed;

    /// <param name="border">承载描边的那个 Border（BorderThickness 应为 <see cref="Thickness"/>）。</param>
    public GradientRing(Border border)
    {
        _border = border;

        // 青→蓝→紫→青：首尾同色，所以循环时不会在接缝处跳变。
        var colors = new[]
        {
            Color.Parse("#22D3EE"),
            Color.Parse("#4A90D9"),
            Color.Parse("#8B5CF6"),
            Color.Parse("#22D3EE"),
        };

        _offsets = new double[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            _offsets[i] = (double)i / (colors.Length - 1);

        _brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        };

        foreach (var color in colors)
            _brush.GradientStops.Add(new GradientStop(color, 0));

        _border.BorderBrush = _brush;
        _border.BorderThickness = new Thickness(Thickness);

        ApplyStops();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _phase += 0.04 / Period.TotalSeconds;

        // 循环回绕：整组偏移一起平移，越界的那一端补到另一头（首尾同色所以看不出来）
        if (_phase >= 1) _phase -= 1;

        ApplyStops();
    }

    private void ApplyStops()
    {
        for (int i = 0; i < _brush.GradientStops.Count; i++)
        {
            var offset = _offsets[i] + _phase;
            if (offset > 1) offset -= 1;

            _brush.GradientStops[i].Offset = offset;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        _border.BorderThickness = new Thickness(0);
        _border.BorderBrush = null;
    }
}
