using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 输入框右侧的上下文用量环：按占用比例画一段圆环，悬停显示「已用 / 上限（百分比）」与模型名。
///
/// 只在「拿到过真实 usage」且「模型声明了上下文长度」时显示 —— 不估算、不猜。
/// 占用口径 = 最后一次请求的 input_tokens + output_tokens：无状态 API 的最后一次输入
/// 已经包含之前所有轮的输入与输出，所以这一个数就是当前窗口的占用，不需要累加。
/// </summary>
public class ContextUsageRing : UserControl
{
    private const double Size = 20;
    private const double RingThickness = 2.5;

    /// <summary>描边中心线的周长（描边画在半径中点，所以要扣掉一个线宽）。</summary>
    private const double Circumference = Math.PI * (Size - RingThickness);

    private const double TipBarWidth = 168;

    private readonly Ellipse _progress;
    private readonly Border _tipBarFill;
    private readonly TextBlock _tipText;
    private readonly TextBlock _tipModel;

    public ContextUsageRing()
    {
        _progress = new Ellipse
        {
            Width = Size,
            Height = Size,
            StrokeThickness = RingThickness,
            StrokeLineCap = PenLineCap.Round,
            // 让圆环从 12 点方向起、顺时针增长
            RenderTransform = new RotateTransform(-90),
        };

        var track = new Ellipse
        {
            Width = Size,
            Height = Size,
            StrokeThickness = RingThickness,
            Stroke = new SolidColorBrush(Color.Parse("#E8EAED")),
        };

        // 透明（而不是 null）背景：透明是可命中的，鼠标落在环中间的空心处也能触发 tooltip
        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { track, _progress },
        };
        Width = Size;
        Height = Size;

        _tipBarFill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _tipText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#374151")) };
        _tipModel = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            Margin = new Thickness(10, 0, 0, 0),
        };

        ToolTip.SetTip(this, new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "上下文用量",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                },
                new Border
                {
                    Width = TipBarWidth,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = _tipBarFill,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _tipText, _tipModel },
                },
            },
        });

        // 默认弹在指针左下角会盖住环本身；固定到上方，再往上顶一点留出间距
        ToolTip.SetPlacement(this, PlacementMode.Top);
        ToolTip.SetVerticalOffset(this, -10);

        Clear();
    }

    /// <summary>
    /// 按占用刷新。上限未知（<paramref name="totalTokens"/> ≤ 0）或用量无效时自动隐藏，
    /// 所以调用方不需要自己判断。
    /// </summary>
    public void SetUsage(long usedTokens, long totalTokens, string modelName)
    {
        if (usedTokens < 0 || totalTokens <= 0)
        {
            Clear();
            return;
        }

        var ratio = Math.Clamp((double)usedTokens / totalTokens, 0, 1);
        var color = ColorFor(ratio);
        var units = Circumference / RingThickness;

        _progress.Stroke = new SolidColorBrush(color);
        _progress.StrokeDashArray = ratio >= 1
            // 画满：dash 留一个 0 长度的间隙，某些实现下整段会消失，干脆不设虚线
            ? null
            : new AvaloniaList<double> { ratio * units, Math.Max(0.001, (1 - ratio) * units) };

        _tipBarFill.Width = TipBarWidth * ratio;
        _tipBarFill.Background = new SolidColorBrush(color);
        _tipText.Text = $"{usedTokens:N0} / {totalTokens:N0} ({ratio * 100:0}%)";
        _tipModel.Text = modelName;
        _tipModel.IsVisible = !string.IsNullOrEmpty(modelName);
        IsVisible = true;
    }

    /// <summary>隐藏用量环（没有真实 usage，或当前模型没有上下文长度信息）。</summary>
    public void Clear()
    {
        IsVisible = false;
        _progress.StrokeDashArray = null;
    }

    private static Color ColorFor(double ratio) => ratio switch
    {
        >= 0.9 => Color.Parse("#DC2626"),
        >= 0.7 => Color.Parse("#D97706"),
        _ => Color.Parse("#7CB342"),
    };
}
