using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HtaciAI.Controls;

/// <summary>
/// 请求等待指示：一段圆弧绕圈转。只在「已发出请求、还没收到任何输出」这段时间显示，
/// 首个正文 / 思考分片到达后由调用方设 <see cref="Visual.IsVisible"/> = false 隐藏。
///
/// 用 <see cref="DispatcherTimer"/> 手转角度而不是 Animation：项目里已有
/// SmoothScrollViewer 这个先例，行为可控，也不依赖动画 API 的细节。
/// </summary>
public class LoadingRing : UserControl
{
    private const double Size = 16;
    private const double Thickness = 2;

    private readonly RotateTransform _rotate = new();
    private readonly DispatcherTimer _timer;

    public LoadingRing()
    {
        // 先建计时器：下面设置属性会触发 OnPropertyChanged，那里要用到它
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => _rotate.Angle = (_rotate.Angle + 7) % 360;

        var circumference = Math.PI * (Size - Thickness);
        var units = circumference / Thickness;

        Content = new Ellipse
        {
            Width = Size,
            Height = Size,
            StrokeThickness = Thickness,
            Stroke = new SolidColorBrush(Color.Parse("#4A90D9")),
            StrokeLineCap = PenLineCap.Round,
            // 只画 1/4 圈、其余留空，转起来就是常见的加载环
            StrokeDashArray = new AvaloniaList<double> { units / 4, units * 3 / 4 },
            RenderTransform = _rotate,
        };
        Width = Size;
        Height = Size;
        IsVisible = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    /// <summary>只在「可见且挂在视觉树上」时转，隐藏时不白烧 CPU。</summary>
    private void UpdateTimer()
    {
        if (IsVisible && this.IsAttachedToVisualTree())
            _timer.Start();
        else
            _timer.Stop();
    }
}
