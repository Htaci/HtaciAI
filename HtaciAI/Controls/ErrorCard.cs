using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Controls;

/// <summary>
/// 请求出错时插在那一轮回复里的红色卡片：平时只显示简短原因，
/// 点「详情」展开完整错误信息（异常类型 / 消息 / 内部异常 / 调用栈），供事后排查。
///
/// 正文格式由 <see cref="ChatError"/> 定：首行是简短原因，其后是详情段。
///
/// <b>卡片内的文字是「原生选择」区</b>（见 <see cref="CustomSelectableTextBlock.NativeSelectionEnabled"/>）：
/// 卡内拖选 / 双击选词 / 复制由框架完成；同时卡片不参与轮内的自定义选区 ——
/// 错误只会出现在一轮末尾，没有「错误卡与正文连选」的需求，隔出去能让轮内选区运算简单一层。
/// </summary>
public sealed class ErrorCard
{
    private const string IconGlyph = "\uF3F2";     // Fluent System Icons：error_circle_24_regular
    private const string DetailTitle = "详情";

    /// <summary>详情区最大高度。错误详情带调用栈，往往几十行，不限高会把整条对话撑开。</summary>
    private const double ExpandedMaxHeight = 300;

    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    private readonly Border _header;
    private readonly TextBlock _toggleLabel;
    private readonly TextBlock _detailBlock;
    private readonly SmoothScrollViewer _detailHost;
    private readonly RotateTransform _chevron;
    private bool _expanded;

    /// <summary>卡片根控件（插进消息流的就是它）。</summary>
    public Border Root { get; }

    /// <summary>
    /// <paramref name="onRetry"/>
    /// 为 null 时不出现「重试」按钮（例如没有可重试的上下文）。
    /// 有值时按钮<b>不</b>自动判定「该不该重试」—— 决定权交给用户：余额用完的就能去充值，
    /// 配错模型的就能去改配置，改完再点它接着跑，不必重开会话。
    /// </summary>
    public ErrorCard(string? content, Action? onRetry = null)
    {
        var summary = ChatError.Summary(content);
        var detail = ChatError.Detail(content);

        var icon = new TextBlock
        {
            Text = IconGlyph,
            FontFamily = AppFonts.Icons,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 卡片里的文字一律用 CustomSelectableTextBlock 并开原生选择：卡片自己是一个独立的
        // 选择区（像代码块那样），卡内拖选 / 双击选词 / 复制由框架完成；同时它不会被收进轮内的
        // 自定义选区 —— 错误只发生在一轮末尾，没有「错误卡与正文连选」的需求，
        // 把这块多行长文本隔出去，轮内选择器就不用跨它做选区运算。
        var title = new CustomSelectableTextBlock
        {
            NativeSelectionEnabled = true,
            Text = "请求出错",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#B91C1C")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        _toggleLabel = new TextBlock
        {
            Text = DetailTitle,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#B91C1C")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.Parse("#B91C1C")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        _chevron = new RotateTransform();
        chevron.RenderTransform = _chevron;
        chevron.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _chevron.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = RotateTransform.AngleProperty,
                Duration = AnimDuration,
                Easing = new CubicEaseInOut(),
            },
        };

        var toggle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = detail.Length > 0,
            Children = { _toggleLabel, chevron },
        };

        // 展开/收起只在右侧那个「详情 ›」上响应，标题与错误原因区域留给选字 ——
        // 卡片里的文字现在都是可拖选的，若整行都当按钮，拖选标题就会顺手把详情开合一次。
        var toggleHit = new Border
        {
            Child = toggle,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        toggleHit.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(toggleHit).Properties.IsLeftButtonPressed)
                Toggle();
        };
        toggleHit.PointerEntered += (_, _) => toggleHit.Background = new SolidColorBrush(Color.Parse("#33DC2626"));
        toggleHit.PointerExited += (_, _) => toggleHit.Background = Brushes.Transparent;

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var iconHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, title },
        };
        Grid.SetColumn(iconHost, 0);
        headerGrid.Children.Add(iconHost);
        Grid.SetColumn(toggleHit, 2);
        headerGrid.Children.Add(toggleHit);

        if (onRetry is not null)
        {
            // 「重试」与「详情」并排：一个管「接着跑」，一个管「为什么停了」。
            // 文案用「重试」而不是「重新生成」—— 它复用同一份上下文再发一次，
            // 上一轮已经产生的思考 / 正文都还留在上面，不是从零开始。
            var retryHit = new Border
            {
                Child = new TextBlock
                {
                    Text = "重试",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#B91C1C")),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(retryHit, "用同一份上下文再发一次；上面已生成的内容会保留");
            retryHit.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(retryHit).Properties.IsLeftButtonPressed)
                    onRetry();
            };
            retryHit.PointerEntered += (_, _) => retryHit.Background = new SolidColorBrush(Color.Parse("#33DC2626"));
            retryHit.PointerExited += (_, _) => retryHit.Background = Brushes.Transparent;

            Grid.SetColumn(retryHit, 1);
            headerGrid.Children.Add(retryHit);
        }

        _header = new Border
        {
            Child = headerGrid,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
        };

        var summaryBlock = new CustomSelectableTextBlock
        {
            Name = "ErrorSummary",
            NativeSelectionEnabled = true,
            Text = summary.Length > 0 ? summary : "未知错误",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#7F1D1D")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(21, 6, 0, 0),
        };

        _detailBlock = new CustomSelectableTextBlock
        {
            Name = "ErrorDetail",
            NativeSelectionEnabled = true,
            Text = detail,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Color.Parse("#7F1D1D")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
        };
        _detailHost = new SmoothScrollViewer
        {
            Content = _detailBlock,
            MaxHeight = 0,
            Opacity = 0,
            // IsVisible 必须显式关掉：MaxHeight=0 只影响布局，测出来仍是全高
            IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Classes = { "thin-scrollbar" },
            Margin = new Thickness(21, 8, 0, 0),
        };
        // 详情里按下选字不该把这个框自己滚滚（原因见 SmoothScrollViewer.DisableFocusBringIntoView）
        SmoothScrollViewer.DisableFocusBringIntoView(_detailHost);
        _detailHost.Transitions = new Transitions
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

        Root = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#FEF2F2")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FECACA")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Child = new StackPanel
            {
                Children = { _header, summaryBlock, _detailHost },
            },
        };
    }

    /// <summary>展开 / 收起详情。</summary>
    public void Toggle()
    {
        _expanded = !_expanded;
        _detailHost.IsVisible = _expanded;
        _detailHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
        _detailHost.Opacity = _expanded ? 1 : 0;
        _chevron.Angle = _expanded ? 90 : 0;
        _toggleLabel.Text = _expanded ? "收起" : DetailTitle;
    }
}
