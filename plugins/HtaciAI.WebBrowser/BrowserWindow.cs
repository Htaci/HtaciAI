using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Plugin.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 一个浏览器窗口 = 宿主里一个标签页。内部再分多个网页标签。
///
/// <b>布局上有一处硬约束</b>：<see cref="WebViewSurface"/> 用的是 Win32 子窗口，
/// 它<b>永远画在 Avalonia 渲染的内容之上</b>，浮在它上面的 Avalonia 控件都会被盖住。
/// 所以工具栏必须放在 WebView <em>上方</em>（Grid 第 0 行），
/// 不能用「浮层 / 叠放」——那样工具栏会整条看不见，看起来像没渲染。
///
/// <b>标签页切换只切 IsVisible，不销毁内核</b>：销毁重建会丢掉页面状态（滚动位置、
/// 表单输入、SPA 路由），还要重走一次导航。
/// </summary>
internal sealed class BrowserWindow : UserControl
{
    /// <summary>单个浏览器窗口最多几个网页标签。每个标签是一个独立渲染进程，按百 MB 计。</summary>
    public const int MaxPages = 8;

    private readonly CoreWebView2Environment _environment;
    private readonly IPluginLogger _log;
    private readonly List<PageEntry> _pages = new();
    private readonly Grid _pageHost = new();

    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly TextBox _addressBar;
    private readonly Button _tabButton;
    private readonly TextBlock _tabLabel;
    private readonly TextBlock _statusText;

    /// <summary>共享状态条（未共享时是「共享给智能体」按钮，共享后是胶囊）。</summary>
    private readonly Border _shareBar = new() { IsVisible = false };
    private readonly Border _shareRing = new();

    /// <summary>请求用户把这个窗口共享给某个会话。由 BrowserManager 提供（它知道会话列表与授权表）。</summary>
    public Func<string, Task>? ShareRequested { get; set; }

    /// <summary>请求取消共享。</summary>
    public Func<string, Task>? UnshareRequested { get; set; }

    /// <summary>共享状态条的锚点（会话选择浮层从它上面弹出）。</summary>
    public Control ShareAnchor => _shareBar;

    private GradientRing? _ring;

    /// <summary>
    /// 当前是否处于共享状态。
    /// <b>为什么要记</b>：描边靠给 WebView 加 Margin 腾空间，而标签是之后才创建的 ——
    /// 只靠 <see cref="SetSharedState"/> 里遍历一遍现有标签，新建的那个不会被覆盖，
    /// 结果描边被网页整条盖住（看起来像「共享状态没生效」）。
    /// </summary>
    private bool _isShared;

    private int _activeIndex = -1;

    /// <summary>标签编号发号器。只增不减 —— 复用已关闭的编号会让旧 tab_id 指向新页面。</summary>
    private int _pageSeq;

    public BrowserWindow(string id, CoreWebView2Environment environment, IPluginLogger log)
    {
        Id = id;
        _environment = environment;
        _log = log;

        _backButton = NavButton("←", "后退", () => Active is { } s ? s.GoBackAsync(CancellationToken.None) : Task.CompletedTask);
        _forwardButton = NavButton("→", "前进", () => Active is { } s ? s.GoForwardAsync(CancellationToken.None) : Task.CompletedTask);
        // 这两个按钮永远可点（点了没得退就什么也不做），不可用时只把字变灰。
        // 用 IsEnabled=false 会让整个按钮连同背景一起灰掉，看起来像"坏掉了"而不是"暂时不能用"。
        var reloadButton = NavButton("⟳", "刷新", () => Active is { } s ? s.ReloadAsync(CancellationToken.None) : Task.CompletedTask);

        _addressBar = new TextBox
        {
            PlaceholderText = "输入网址后回车",
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Background = new SolidColorBrush(Color.Parse("#F1F3F7")),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#E2E6EE")),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        _addressBar.KeyDown += OnAddressBarKeyDown;

        var newTabButton = NavButton("+", "新建标签页", () => AddPageAsync(null, CancellationToken.None));

        _tabLabel = new TextBlock
        {
            Text = "新标签页",
            FontSize = 13,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 胶囊而不是图标：用户一眼要看到「现在是哪个标签」，
        // 图标按钮只能表达「这里有东西」，表达不了「是哪一页」。
        _tabButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "▤",
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.Parse("#6B7686")),
                    },
                    _tabLabel,
                    new TextBlock
                    {
                        Text = "▾",
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.Parse("#8A93A5")),
                    },
                },
            },
            Height = 30,
            Padding = new Thickness(12, 0, 10, 0),
            Background = new SolidColorBrush(Color.Parse("#F1F3F7")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        ToolTip.SetTip(_tabButton, "切换这个窗口里的标签页");
        _tabButton.Click += (_, _) => ShowTabMenu();

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#C2410C")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 12, 0),
            IsVisible = false,
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto"),
        };

        AddToColumn(toolbarGrid, _backButton, 0, new Thickness(0, 0, 2, 0));
        AddToColumn(toolbarGrid, _forwardButton, 1, new Thickness(0, 0, 2, 0));
        AddToColumn(toolbarGrid, reloadButton, 2, new Thickness(0, 0, 8, 0));
        AddToColumn(toolbarGrid, _addressBar, 3);

        // 共享状态放在地址栏右侧、标签胶囊左侧：它是「这个窗口归谁」的属性，
        // 与地址、标签同属窗口级信息，理应排在同一行。
        // （早先放在工具栏下方的独立一行，占了高度又显得像页面的一部分。）
        AddToColumn(toolbarGrid, _shareBar, 4, new Thickness(0, 0, 8, 0));

        AddToColumn(toolbarGrid, _tabButton, 5);
        AddToColumn(toolbarGrid, newTabButton, 6);

        var toolbar = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#E8EBF1")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6),
            Child = toolbarGrid,
        };

        _pageHost.Background = Brushes.White;

        // 描边套在页面容器外面，靠给 WebView 加 Margin 腾出可见空间。
        // 反过来做（把描边画在 WebView 上面）在 Win32 下不可能：网页会整条盖掉它。
        _shareRing.Child = _pageHost;
        _shareRing.BorderThickness = new Thickness(0);
        _shareRing.CornerRadius = new CornerRadius(0);

        BuildShareBar();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_statusText, 1);
        Grid.SetRow(_shareRing, 2);

        root.Children.Add(toolbar);
        root.Children.Add(_statusText);
        root.Children.Add(_shareRing);

        Content = root;
    }

    public string Id { get; }

    /// <summary>
    /// 共享控件在工具栏里占的那一格。
    ///
    /// <b>为什么只能放工具栏 / 独立行，不能浮在网页上</b>：WebView2 是 Win32 子窗口，
    /// 永远画在 Avalonia 内容之上。叠在它上面的控件会被网页整条盖住，
    /// 看起来像没渲染。要「浮」只能用独立 popup 窗口（Flyout 那种）。
    /// </summary>
    private void BuildShareBar()
    {
        _shareBar.Background = Brushes.Transparent;
        _shareBar.BorderThickness = new Thickness(0);
        _shareBar.Padding = new Thickness(0);
        _shareBar.VerticalAlignment = VerticalAlignment.Center;
    }

    // ============================================================
    // 共享状态
    // ============================================================

    /// <summary>
    /// 刷新共享状态。
    /// <paramref name="sharedWith"/> 为 null 表示未共享 —— 这时显示「共享给智能体」按钮；
    /// 非 null 时显示胶囊和渐变描边，点胶囊可以取消。
    /// </summary>
    public void SetSharedState(string? sharedWith)
    {
        var shared = sharedWith is not null;
        _isShared = shared;

        _shareBar.IsVisible = true;
        _shareBar.Child = shared
            ? BuildSharedCapsule(sharedWith!)
            : BuildShareButton();

        // 描边靠让出边距实现：WebView 必须缩小，否则那圈线被它自己盖住。
        // 所有标签页都要加，不只是当前那个 —— 切过去的时候也得是对的。
        var inset = shared ? GradientRing.Thickness : 0;
        foreach (var page in _pages)
            page.Surface.Margin = new Thickness(inset);

        if (shared && _ring is null) _ring = new GradientRing(_shareRing);
        else if (!shared && _ring is not null)
        {
            _ring.Dispose();
            _ring = null;
        }

        RefreshToolbar();
    }

    /// <summary>未共享时的那颗按钮。</summary>
    private Control BuildShareButton()
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    AgentsIcon(14),
                    new TextBlock
                    {
                        Text = "共享给智能体",
                        FontSize = 12.5,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.Parse("#EEF4FB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#CFE0F5")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, "把这个浏览器窗口交给某个会话使用后，AI 才能操作它");

        button.Click += (_, _) => _ = RunActionAsync("共享浏览器",
            () => ShareRequested?.Invoke(Id) ?? Task.CompletedTask);

        return button;
    }

    /// <summary>
    /// 共享中的胶囊。点它弹「取消共享」。
    /// 配色照搬 VSCode 那一条：深底 + 青色描边 + 低饱和文字。
    /// </summary>
    private Control BuildSharedCapsule(string sessionTitle)
    {
        var text = new TextBlock
        {
            Text = "正在与智能体共享",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E6F7FF")),
        };

        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { AgentsIcon(14, "#22D3EE"), text },
            },
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.Parse("#0E7490")),
            BorderBrush = new SolidColorBrush(Color.Parse("#22D3EE")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        ToolTip.SetTip(button, $"当前授权给会话「{sessionTitle}」，点击可取消共享");

        button.Click += (_, _) => ShowUnshareMenu(button, sessionTitle);
        return button;
    }

    /// <summary>点胶囊后弹出的小菜单：确认取消共享。</summary>
    private void ShowUnshareMenu(Control anchor, string sessionTitle)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 260 };

        panel.Children.Add(new TextBlock
        {
            Text = $"当前授权给「{sessionTitle}」",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            TextWrapping = TextWrapping.Wrap,
        });

        var revoke = new Button
        {
            Content = "取消共享",
            FontSize = 13,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.Parse("#FEF2F2")),
            Foreground = new SolidColorBrush(Color.Parse("#B91C1C")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = panel,
        };

        revoke.Click += (_, _) =>
        {
            flyout.Hide();
            _ = RunActionAsync("取消共享", () => UnshareRequested?.Invoke(Id) ?? Task.CompletedTask);
        };

        panel.Children.Add(revoke);

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// 智能体图标（Fluent agents-32-regular）。
    /// Avalonia 不吃 SVG 标记，所以把路径直接写成 Path 的 Data——
    /// 用 StreamGeometry 解析 path 的 d 属性，省掉一个图片加载环节。
    /// </summary>
    private static Control AgentsIcon(double size, string colorHex = "#4A90D9")
    {
        var geometry = StreamGeometry.Parse(
            "M10.774 5a1 1 0 0 0-.866.5l-5.773 10a1 1 0 0 0 0 1L9.8 26.314a1.37 1.37 0 0 0 2.512-.332" +
            "L17.76 5.503a3.37 3.37 0 0 1 6.177-.819l5.667 9.815a3 3 0 0 1 0 3l-5.774 10a3 3 0 0 1-2.598 1.5" +
            "h-4.228a1 1 0 1 1 0-2h4.228a1 1 0 0 0 .866-.5l5.774-10a1 1 0 0 0 0-1l-5.667-9.815" +
            "a1.37 1.37 0 0 0-2.512.334l-5.446 20.476a3.37 3.37 0 0 1-6.177.82L2.403 17.5a3 3 0 0 1 0-3" +
            "l5.773-10A3 3 0 0 1 10.774 3h4.229a1 1 0 1 1 0 2z");

        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Width = size,
            Height = size,
            // 原始 viewBox 是 32×32，缩到目标尺寸
            Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(Color.Parse(colorHex)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>宿主标签页标题变了（标签数或当前页变了）。由 BrowserManager 转给宿主。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>
    /// 某个标签页的地址变了（导航完成、重定向、SPA 路由）。<b>在 UI 线程触发。</b>
    /// 参数是（地址, 标题）。BrowserManager 拿它记访问历史。
    /// </summary>
    public event Action<string, string>? Navigated;

    /// <summary>这个窗口里当前有几个标签。</summary>
    public int PageCount => _pages.Count;

    /// <summary>当前显示中的标签页内核。截图工具要用。</summary>
    public WebViewSurface? ActiveSurface => Active;

    private WebViewSurface? Active
        => _activeIndex >= 0 && _activeIndex < _pages.Count ? _pages[_activeIndex].Surface : null;

    /// <summary>当前标签页的地址。</summary>
    public string CurrentUrl => Active?.CurrentUrl ?? "";

    // ============================================================
    // 标签页
    // ============================================================

    /// <summary>
    /// 新建一个网页标签并切过去。<paramref name="url"/> 为空时显示起始页。
    /// 返回加载完成后的地址；超过 <see cref="MaxPages"/> 时抛异常而不是静默失败。
    /// </summary>
    public async Task<string> AddPageAsync(string? url, CancellationToken ct)
    {
        var surface = CreatePageTab();

        try
        {
            var result = string.IsNullOrWhiteSpace(url)
                ? await surface.EnsureStartPageAsync(ct)
                : await surface.NavigateAsync(BrowserManager.NormalizeUrl(url), ct);

            return result;
        }
        catch (Exception ex)
        {
            // 失败也要留住这个标签：把它悄悄删掉会让用户看到「点了没反应」，
            // 而罪魁祸首（比如内核没装）就永远只躺在日志里。
            var index = _pages.FindIndex(p => ReferenceEquals(p.Surface, surface));
            if (index >= 0) _pages[index].Title = "加载失败";

            ShowStatus($"标签页加载失败：{ex.Message}");
            RefreshToolbar();
            throw;
        }
    }

    /// <summary>
    /// 建一个标签但<b>不导航</b>，返回它的内核控件。
    /// 用于接管 <c>target="_blank"</c>：改由内核把目标页渲染进这个新标签。
    /// </summary>
    public async Task<WebViewSurface> AddEmptyPageAsync(CancellationToken ct)
    {
        var surface = CreatePageTab();
        await surface.ReadyTask.WaitAsync(ct);
        return surface;
    }

    /// <summary>
    /// 建一个新标签并切过去（不含导航）。
    /// <b>建标签的三件事都收在这里</b>：套描边边距、加入页面容器、切为活动标签。
    /// 分散写会让「新窗口接管」这条路径漏掉其中一步。
    /// </summary>
    private WebViewSurface CreatePageTab()
    {
        if (_pages.Count >= MaxPages)
            throw new InvalidOperationException($"一个浏览器窗口最多 {MaxPages} 个标签页（每个标签都是独立渲染进程）。");

        _log.Info($"{Id}：正在新建标签页（当前 {_pages.Count} 个）");

        var surface = new WebViewSurface(_environment, _log);
        var entry = new PageEntry(surface, "tab-" + ++_pageSeq);

        // 页面要求新窗口时，收进我们的标签体系（见 WebViewSurface.NewWindowRequested）
        surface.NewWindowRequested = async uri => await OpenInNewTabAsync(uri);

        surface.SourceChanged += source =>
        {
            if (string.IsNullOrWhiteSpace(source) || source == "about:blank") return;

            entry.Title = ShortTitle(source);
            RefreshToolbar();

            // 只在这一处抛：它是所有导航路径（工具栏、工具、页内跳转、_blank 接管）的共同出口
            Navigated?.Invoke(source, entry.Title);
        };

        surface.Failed += ex => ShowStatus($"内核创建失败：{ex.Message}");

        // 已经是共享状态时，新标签也要让出描边的宽度。
        // 这一步必须在加入可视树之前做：先加后改会让 WebView 以满尺寸创建一次，
        // 那一次它会盖住描边。
        if (_isShared) surface.Margin = new Thickness(GradientRing.Thickness);

        _pages.Add(entry);
        _pageHost.Children.Add(surface);

        _log.Info($"{Id}：控件已加入页面容器，切到第 {_pages.Count - 1} 个标签（Stage 子项 {_pageHost.Children.Count} 个）");

        SetActive(_pages.Count - 1);
        RefreshToolbar();

        return surface;
    }

    /// <summary>
    /// 把「页面想开新窗口」变成「在当前浏览器里新开一个标签」。
    ///
    /// <b>为什么不直接用内核默认行为</b>：那会弹出一个独立窗口 —— 没有工具栏、
    /// 没有共享授权、宿主标签页关掉时也不会被清理。用户会看到一个完全失控的窗口。
    ///
    /// <b>为什么返回 CoreWebView2 而不是自己导航</b>：内核已经把目标页准备好了，
    /// 交给它渲染比自己再 <c>Navigate</c> 一次更准确（能带上 referrer、POST 体等）。
    /// </summary>
    private async Task<CoreWebView2?> OpenInNewTabAsync(string uri)
    {
        // 标签数已满：直接返回 null，由 WebViewSurface 统一退回「在当前标签导航」。
        // <b>不要在这里自己导航</b>：那样 WebViewSurface 收到 null 后会再导航一次，
        // 同一个地址被请求两遍 —— 碰上带副作用的 URL（下单、删除）就出事了。
        if (_pages.Count >= MaxPages)
        {
            _log.Warn($"{Id}：标签已满（{MaxPages} 个），新窗口改为在当前标签打开：{uri}");
            return null;
        }

        try
        {
            var surface = await AddEmptyPageAsync(CancellationToken.None);
            var core = await surface.RequireCoreAsync(CancellationToken.None);

            _log.Info($"{Id}：页面请求的新窗口已在标签 {_pages[^1].Id} 中打开：{uri}");
            return core;
        }
        catch (Exception ex)
        {
            _log.Error("为新窗口创建标签失败", ex);
            return null;   // 同上：交给 WebViewSurface 退回当前标签
        }
    }

    /// <summary>切到指定标签。更新工具栏与宿主标签页标题。</summary>
    public void SetActive(int index)
    {
        if (index < 0 || index >= _pages.Count) return;

        _activeIndex = index;

        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Surface.IsVisible = i == index;

        // 后台标签页的控制器是以 1x1 建的（那时布局还没给它尺寸）。
        // 「变成可见」不一定带来一次 Bounds 变化，所以这里显式补一次，
        // 否则切过去会是一片空白。
        _pages[index].Surface.RefreshBounds();

        ClearStatus();

        RefreshToolbar();
    }

    /// <summary>关掉指定标签。只剩一个时返回 false —— 窗口里始终保有一个标签。</summary>
    public bool ClosePage(int index)
    {
        if (index < 0 || index >= _pages.Count || _pages.Count <= 1) return false;

        var entry = _pages[index];
        _pages.RemoveAt(index);
        _pageHost.Children.Remove(entry.Surface);

        // 内核跟着控件走：移出可视树后 attachment 会销毁子窗口，
        // WebViewSurface 在 DestroyNativeControlCore 里关掉控制器。
        if (_activeIndex >= _pages.Count) _activeIndex = _pages.Count - 1;
        SetActive(_activeIndex);

        return true;
    }

    /// <summary>全部标签页的标识、标题与地址，给下拉菜单和 AI 用。</summary>
    public IReadOnlyList<(string Id, string Title, string Url)> GetPageInfo()
        => _pages.Select(p => (p.Id, p.Title, p.Surface.CurrentUrl ?? "")).ToList();

    /// <summary>当前活动标签的 id（没有标签时返回 null）。</summary>
    public string? ActivePageId
        => _activeIndex >= 0 && _activeIndex < _pages.Count ? _pages[_activeIndex].Id : null;

    /// <summary>
    /// 按 id 找一个标签。<paramref name="tabId"/> 为空时返回当前活动的那个 ——
    /// 这是最常用的情形（模型刚 snapshot 完就点），不必逼它每次都带 tab_id。
    /// </summary>
    public WebViewSurface? FindPage(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId)) return Active;

        return _pages.FirstOrDefault(p =>
            string.Equals(p.Id, tabId, StringComparison.OrdinalIgnoreCase))?.Surface;
    }

    /// <summary>开着哪些标签，用于在找不到时给模型一份可用清单。</summary>
    public string DescribeTabs()
        => _pages.Count == 0
            ? "（没有标签）"
            : string.Join("、", _pages.Select(p => p.Id + (p.Id == ActivePageId ? "（当前）" : "")));

    /// <summary>切到指定标签。工具在操作前调用，让用户看到模型在动哪一页。</summary>
    public bool ActivatePage(string tabId)
    {
        var index = _pages.FindIndex(p =>
            string.Equals(p.Id, tabId, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return false;

        SetActive(index);
        return true;
    }

    // ============================================================
    // 工具栏
    // ============================================================

    private void RefreshToolbar()
    {
        var surface = Active;
        if (surface is null) return;

        var (canGoBack, canGoForward, url) = surface.GetNavigationState();

        SetNavEnabled(_backButton, canGoBack);
        SetNavEnabled(_forwardButton, canGoForward);

        // 用户正在地址栏里输入时不要抢光标位置
        if (!_addressBar.IsFocused)
            _addressBar.Text = url is "" or "about:blank" ? "" : url;

        // 胶囊上显示当前标签的标题：这是用户判断「我在哪一页」的唯一依据
        var activeTitle = _activeIndex >= 0 && _activeIndex < _pages.Count
            ? _pages[_activeIndex].Title
            : "新标签页";
        _tabLabel.Text = activeTitle;

        TitleChanged?.Invoke(_pages.Count == 1 || _activeIndex < 0
            ? "内置浏览器"
            : $"内置浏览器 · {activeTitle}");
    }

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;

        var text = _addressBar.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        _ = NavigateActiveAsync(BrowserManager.NormalizeUrl(text));
    }

    private async Task NavigateActiveAsync(string url)
    {
        var surface = Active;
        if (surface is null) return;

        // 先清掉上一次的错误。否则「标题栏报错但页面明明好好的」会一直挂着 ——
        // 那条错误属于上一次导航，和眼前这页无关，但用户没法知道这一点。
        ClearStatus();

        try
        {
            await surface.NavigateAsync(url, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error($"{Id}：导航失败 {url}", ex);
            ShowStatus($"打开失败：{ex.Message}");
        }

        RefreshToolbar();
    }

    private void ShowTabMenu()
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 280 };

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = panel,
        };

        for (int i = 0; i < _pages.Count; i++)
        {
            var index = i;
            var entry = _pages[i];

            var switchButton = new Button
            {
                Background = index == _activeIndex
                    ? new SolidColorBrush(Color.Parse("#EEF4FF"))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock
                {
                    Text = entry.Title,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 200,
                },
            };
            switchButton.Click += (_, _) =>
            {
                SetActive(index);
                flyout.Hide();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(switchButton, 0);
            row.Children.Add(switchButton);

            // 只剩一个标签时不给关，否则窗口会变成一个没有页面的空壳
            if (_pages.Count > 1)
            {
                var closeButton = NavButton("×", "关掉这个标签", () =>
                {
                    ClosePage(index);
                    flyout.Hide();
                    return Task.CompletedTask;
                });
                closeButton.Width = 26;
                Grid.SetColumn(closeButton, 1);
                row.Children.Add(closeButton);
            }

            panel.Children.Add(row);
        }

        var addButton = new Button
        {
            Content = "＋ 新建标签页",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
        };
        addButton.Click += (_, _) =>
        {
            flyout.Hide();
            _ = RunActionAsync("新建标签页", () => AddPageAsync(null, CancellationToken.None));
        };
        panel.Children.Add(addButton);

        FlyoutBase.SetAttachedFlyout(_tabButton, flyout);
        flyout.ShowAt(_tabButton);
    }

    private void ShowStatus(string message)
    {
        _statusText.Text = message;
        _statusText.IsVisible = true;
    }

    private void ClearStatus()
    {
        if (!_statusText.IsVisible) return;

        _statusText.Text = "";
        _statusText.IsVisible = false;
    }

    // ============================================================
    // 小工具
    // ============================================================

    private Button NavButton(string glyph, string tip, Func<Task>? onClick, string? what = null)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 15 },
            Width = 32,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = onClick is not null,
        };

        ToolTip.SetTip(button, tip);

        if (onClick is not null)
            button.Click += (_, _) => _ = RunActionAsync(what ?? tip, onClick);

        return button;
    }

    /// <summary>
    /// 执行工具栏动作。<b>吞掉异常但必须留下痕迹</b>：
    /// 完全静默的结果是「点了没反应」，而那正是最难查的一类问题 ——
    /// 用户看到的是按钮坏了，日志里什么都没有。
    /// </summary>
    private Task RunActionAsync(Func<Task> action)
        => RunActionAsync("工具栏操作", action);

    private async Task RunActionAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消，不需要提示
        }
        catch (Exception ex)
        {
            _log.Error($"{Id}：{what}失败", ex);
            ShowStatus($"{what}失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 导航按钮的「不可用」状态：<b>变色不变禁用</b>。
    /// 禁用的按钮会连背景一起变灰，看起来像控件坏了；只把字形调淡，
    /// 用户一眼就知道「这个方向暂时没得走」。
    /// </summary>
    private static void SetNavEnabled(Button button, bool enabled)
    {
        if (button.Content is not TextBlock glyph) return;

        glyph.Foreground = new SolidColorBrush(Color.Parse(enabled ? "#374151" : "#C0C0C0"));
        button.Cursor = enabled
            ? new Cursor(StandardCursorType.Hand)
            : new Cursor(StandardCursorType.Arrow);
    }

    private static void AddToColumn(Grid grid, Control control, int column, Thickness? margin = null)
    {
        if (margin.HasValue) control.Margin = margin.Value;
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    /// <summary>把地址压成一个能放进标签名的短标题。</summary>
    private static string ShortTitle(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Host + uri.AbsolutePath.TrimEnd('/');
        }
        catch
        {
            // 不是合法 URI 就原样显示
        }

        return url;
    }

    private sealed class PageEntry(WebViewSurface surface, string id)
    {
        public WebViewSurface Surface { get; } = surface;

        /// <summary>
        /// 稳定标识，形如 <c>tab-1</c>。<b>不用下标</b>：关掉中间一个标签后，
        /// 后面全部前移，模型手里的旧下标会操作到错误的页面 —— 这种错在日志里看不出来。
        /// </summary>
        public string Id { get; } = id;

        public string Title { get; set; } = "新标签页";
    }
}
