using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Services;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>
/// 一轮回复末尾的「改动文件」卡片：每行一个文件（类型图标 + 文件名 + 新增/修改/删除标记 + 打开方式）。
///
/// 点整行 = 用默认打开方式（资源管理器定位选中）；「打开方式」是下拉菜单，
/// 目前只有资源管理器一项，其它打开方式以后往 <see cref="BuildOpenButton"/> 里加即可。
///
/// <b>超过 <see cref="CollapseThreshold"/> 个改动时先收成一叠</b>（第一行 + 下面两层露出的边），
/// 点它才自上而下展开：一轮改十几个文件时，平铺会把正文挤得看不见。展开后点卡片外面收起。
/// </summary>
public sealed class FileChangeCard : UserControl
{
    // 半透明填充：页面背景是流动渐变，透一点过去比实色更像一层玻璃。
    // 描边用聚合卡同款的 #ECEEF1 —— 只让填充透明，边线不能跟着变淡，否则卡片轮廓看不清。
    private const string RowBg = "#CCFFFFFF";
    private const string RowHoverBg = "#EBFFFFFF";
    private const string RowBorder = "#ECEEF1";
    private const string TitleHex = "#1F1F1F";
    private const string MutedHex = "#6B7280";
    private const string ButtonBorder = "#E1E5EA";

    /// <summary>行内统一内边距：四边相同，图标和右侧按钮到卡片边缘的距离才一致。</summary>
    private const double RowPadding = 12;

    /// <summary>超过这个数量就先收成一叠。</summary>
    private const int CollapseThreshold = 3;

    /// <summary>量不出高度时的兜底上限（正常走不到）。</summary>
    private const double FallbackExpandedMaxHeight = 4000;

    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>展开后的全部行。收起时整块隐藏（不占布局）。</summary>
    private readonly StackPanel _expandedList = new()
    {
        Spacing = 6,
        IsVisible = false,
        Opacity = 0,
        MaxHeight = 0,
    };

    /// <summary>收起态的「一叠」：第一行 + 两层往下露出的边。不到阈值时为 null（没有收起态）。</summary>
    private readonly Control? _deck;

    /// <summary>露出的那两层边，悬停时往下挪一点（能拉开的手势暗示）。</summary>
    private readonly Border? _band1;
    private readonly Border? _band2;

    private bool _expanded;

    /// <summary>展开期间挂着「点外面收起」的那个顶层窗口。</summary>
    private TopLevel? _outsideTopLevel;

    public FileChangeCard(IReadOnlyList<FileChange> changes)
    {
        _expandedList.Transitions = new Transitions
        {
            // 高度从 0 长起来 = 卡片自上而下依次露出来
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

        foreach (var change in changes)
            _expandedList.Children.Add(BuildRow(change));

        if (changes.Count <= CollapseThreshold)
        {
            // 没到阈值：本来就不长，直接铺开
            _expandedList.IsVisible = true;
            _expandedList.Opacity = 1;
            _expandedList.MaxHeight = double.PositiveInfinity;
            Content = _expandedList;
            return;
        }

        var (deck, band1, band2) = BuildDeck(changes);
        _deck = deck;
        _band1 = band1;
        _band2 = band2;
        Content = new Panel { Children = { _expandedList, deck } };
    }

    // ============================================================
    // 收起态
    // ============================================================

    /// <summary>
    /// 一叠卡片：最上面是第一条改动，下面两层比它窄、露出卡片的边。
    /// <b>这整叠都是一个「展开」开关</b>，所以封面那一行不绑打开文件的行为。
    /// </summary>
    private (Control Deck, Border Band1, Border Band2) BuildDeck(IReadOnlyList<FileChange> changes)
    {
        var deck = new StackPanel
        {
            Spacing = 0,
            // 透明底也拿掉命中测试的空白：两层边收窄之后，左右那点空隙照样是「点它展开」
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        deck.Children.Add(BuildRow(changes[0], interactive: false));

        var band1 = BuildBand(8);
        var band2 = BuildBand(16);
        deck.Children.Add(band1);
        deck.Children.Add(band2);

        foreach (var band in new[] { band1, band2 })
            band.Transitions = new Transitions
            {
                new ThicknessTransition
                {
                    Property = Border.MarginProperty,
                    Duration = TimeSpan.FromMilliseconds(160),
                    Easing = new CubicEaseOut(),
                },
            };

        // 悬停时下面两层微微往下挪：一个「能拉开」的手势暗示，真点下去才展开
        deck.PointerEntered += (_, _) =>
        {
            band1.Margin = new Thickness(8, 3, 8, 0);
            band2.Margin = new Thickness(16, 3, 16, 0);
        };
        deck.PointerExited += (_, _) => ResetBands(band1, band2);
        deck.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(deck).Properties.IsLeftButtonPressed) return;
            Expand();
            e.Handled = true;
        };

        ToolTip.SetTip(deck, $"这一轮改动了 {changes.Count} 个文件，点击展开");

        return (deck, band1, band2);
    }

    /// <summary>露出的那层边：只有左右和下边有描边，圆角在下沿，看起来像被压住的卡片。</summary>
    private static Border BuildBand(double inset) => new()
    {
        Height = 5,
        Margin = new Thickness(inset, 0, inset, 0),
        Background = new SolidColorBrush(Color.Parse("#B8FFFFFF")),
        BorderBrush = new SolidColorBrush(Color.Parse(RowBorder)),
        BorderThickness = new Thickness(1, 0, 1, 1),
        CornerRadius = new CornerRadius(0, 0, 8, 8),
    };

    private static void ResetBands(Border band1, Border band2)
    {
        band1.Margin = new Thickness(8, 0, 8, 0);
        band2.Margin = new Thickness(16, 0, 16, 0);
    }

    // ============================================================
    // 展开 / 收起
    // ============================================================

    private void Expand()
    {
        if (_expanded || _deck is null) return;
        _expanded = true;

        _deck.IsVisible = false;

        // 量一次内容的真实高度：MaxHeight 过渡到「内容高度」，展开才是匀速的。
        // 过渡到一个随便的大数（比如 4000）会在头几毫秒就把内容全放开，看起来像「啪」一下弹出。
        _expandedList.MaxHeight = double.PositiveInfinity;
        _expandedList.Measure(new Size(Bounds.Width, double.PositiveInfinity));
        var target = _expandedList.DesiredSize.Height;
        if (target <= 0) target = FallbackExpandedMaxHeight;

        // 先让布局看到「可见 + 高度 0」，下一拍再给目标值：同一次布局里直接改完，过渡不会播
        _expandedList.MaxHeight = 0;
        _expandedList.IsVisible = true;
        SubscribeOutsidePointer();

        Dispatcher.UIThread.Post(() =>
        {
            _expandedList.Opacity = 1;
            _expandedList.MaxHeight = target;
        }, DispatcherPriority.Render);

        // 展开完就撤掉这个上限：之后窗口变宽、行高变化时不会被当初量到的值切掉
        DispatcherTimer.RunOnce(
            () => _expandedList.MaxHeight = double.PositiveInfinity,
            AnimDuration + TimeSpan.FromMilliseconds(60));
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        UnsubscribeOutsidePointer();

        _expandedList.IsVisible = false;
        _expandedList.Opacity = 0;
        _expandedList.MaxHeight = 0;

        if (_deck is not null)
        {
            _deck.IsVisible = true;
            if (_band1 is not null && _band2 is not null) ResetBands(_band1, _band2);
        }
    }

    /// <summary>展开期间才听「点外面」：收起时不必为每次点击多走一个处理器。</summary>
    private void SubscribeOutsidePointer()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || ReferenceEquals(top, _outsideTopLevel)) return;

        UnsubscribeOutsidePointer();
        _outsideTopLevel = top;
        // 隧道阶段：别的控件（按钮、可选中文字）先把事件吃掉也不影响这里的判定
        top.AddHandler(InputElement.PointerPressedEvent, OnOutsidePointerPressed, RoutingStrategies.Tunnel);
    }

    private void UnsubscribeOutsidePointer()
    {
        if (_outsideTopLevel is null) return;
        _outsideTopLevel.RemoveHandler(InputElement.PointerPressedEvent, OnOutsidePointerPressed);
        _outsideTopLevel = null;
    }

    private void OnOutsidePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_expanded) return;

        var point = e.GetPosition(this);
        if (point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height)
            return;   // 点在自己身上：展开态里这些行还有「打开文件」的用处

        Collapse();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_expanded) SubscribeOutsidePointer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 重新渲染历史会重建整张卡：旧的这张必须放手，否则处理器会挂在窗口上不走
        UnsubscribeOutsidePointer();
        base.OnDetachedFromVisualTree(e);
    }

    // ============================================================
    // 行
    // ============================================================

    /// <summary>
    /// 一个文件行。<paramref name="interactive"/> 为 false 时点它不打开文件 ——
    /// 收起态的那一条只是「展开」的封面。
    /// </summary>
    private static Control BuildRow(FileChange change, bool interactive = true)
    {
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(BuildIcon(change.Path));
        left.Children.Add(new TextBlock
        {
            Text = FileNameOf(change.Path),
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.Parse(TitleHex)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            // 定宽上限：名字太长时省略号截断，而不是把右边那颗标记和按钮挤出卡片
            MaxWidth = 360,
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(BuildKindChip(change.Kind));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);

        if (interactive)
        {
            // 收起态的封面行不放按钮：那一整叠只是一个「展开」开关
            var openButton = BuildOpenButton(change.Path);
            Grid.SetColumn(openButton, 1);
            grid.Children.Add(openButton);
        }

        var row = new Border
        {
            Background = new SolidColorBrush(Color.Parse(RowBg)),
            BorderBrush = new SolidColorBrush(Color.Parse(RowBorder)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(RowPadding),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        // 界面上只显示文件名，完整路径在悬停提示里
        ToolTip.SetTip(row, change.Path);

        row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse(RowHoverBg));
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Color.Parse(RowBg));

        if (interactive)
        {
            // 「打开方式」自己是子控件且会把按下事件吃掉，所以点按钮不会顺带定位文件
            row.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                    AttachmentStore.RevealInExplorer(change.Path);
            };
        }

        return row;
    }

    /// <summary>文件类型图标：圆角色块 + 后缀缩写（沿用 Cherry Studio 那种「一眼看出是什么文件」的做法）。</summary>
    private static Control BuildIcon(string path)
    {
        var (label, background) = IconOf(path);
        return new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse(background)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static (string Label, string Background) IconOf(string path)
    {
        string ext;
        try { ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(); }
        catch { ext = ""; }

        return ext switch
        {
            "cs" => ("C#", "#7B3FB8"),
            "axaml" or "xaml" => ("XA", "#2563EB"),
            "shader" or "hlsl" or "cginc" or "glsl" or "compute" => ("SH", "#15803D"),
            "json" => ("{}", "#B45309"),
            "csproj" or "slnx" or "sln" or "props" or "targets" => ("MS", "#0E7490"),
            "md" => ("MD", "#374151"),
            "xml" or "html" or "htm" or "css" => ("<>", "#0284C7"),
            "ts" => ("TS", "#3178C6"),
            "js" or "mjs" or "cjs" => ("JS", "#A16207"),
            "py" => ("PY", "#3776AB"),
            "" => ("?", MutedHex),
            _ => (ext.Length <= 3 ? ext.ToUpperInvariant() : ext[..3].ToUpperInvariant(), MutedHex),
        };
    }

    /// <summary>改动类型标记：新增（绿）/ 修改（蓝）/ 删除（红）。</summary>
    private static Control BuildKindChip(FileChangeKind kind)
    {
        var (text, foreground, background) = kind switch
        {
            FileChangeKind.Added => ("新增", "#15803D", "#DCFCE7"),
            FileChangeKind.Deleted => ("删除", "#B91C1C", "#FEE2E2"),
            _ => ("修改", "#1D4ED8", "#DBEAFE"),
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(foreground)),
            },
        };
    }

    /// <summary>
    /// 「打开方式」下拉。目前只有「资源管理器」一项（默认方式），
    /// 后续要加编辑器，只在这里追加 <see cref="MenuItem"/> 即可。
    /// </summary>
    private static Control BuildOpenButton(string path)
    {
        var label = new TextBlock
        {
            Text = "打开方式",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new TextBlock
        {
            Text = "\uF2A4", // Fluent System Icons：ChevronDown
            FontFamily = AppFonts.Icons,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(MutedHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var menu = new MenuFlyout();
        MenuTheme.Apply(menu);
        var explorer = new MenuItem { Header = "资源管理器" };
        explorer.Click += (_, _) => AttachmentStore.RevealInExplorer(path);
        menu.Items.Add(explorer);

        // 自绘按钮而不是 Button：Button 的模板自带主题悬停色，会把这里的描边风格顶掉
        var button = new Border
        {
            Height = 28,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse(ButtonBorder)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { label, chevron },
            },
        };
        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(Color.Parse("#14000000"));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) =>
        {
            // 先吃掉事件：否则会冒泡到整行，变成「点了菜单却先把资源管理器打开」
            e.Handled = true;
            menu.ShowAt(button);
        };

        return button;
    }

    private static string FileNameOf(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }
}
