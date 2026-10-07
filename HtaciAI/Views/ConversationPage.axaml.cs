using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 统一会话页：左侧是「工作空间（树形就地展开）/ 普通对话」两组导航，右侧按选中项挂
/// <see cref="ConversationView"/> —— 普通对话与工作空间会话**共用同一个视图**。
///
/// 数据层仍分别读写 chat_sessions 与 workspace_sessions（两张表、两个模型），
/// 差异由 <see cref="IConversationProfile"/> 的两个实现隔离；两者共用 chat_message 表，
/// 会话 id 是同一套空间，因此可以用一个 <c>_activeSessionId</c> 做高亮。
/// </summary>
public partial class ConversationPage : UserControl
{
    /// <summary>
    /// 列表项需要的会话信息（两类会话模型的最小公共面）。
    /// <paramref name="SortAt"/> 是<b>真实对话时间</b>（最近一次发请求），不是 <c>updated_at</c>——
    /// 后者会被改配置、写草稿、打开会话等动作抬高，拿它显示时间会出现「点一下时间就变成刚刚」。
    /// </summary>
    private sealed record SessionRef(string Id, string Title, long SortAt, bool IsPlain, string? WorkspaceId);

    private readonly List<WorkspaceConfig> _workspaces = new();
    private readonly List<ChatSession> _plainSessions = new();
    private readonly Dictionary<string, List<WorkspaceChatSession>> _workspaceSessions = new();

    /// <summary>当前展开的工作空间（树形就地展开，同一时刻只展开一个）。</summary>
    private string? _expandedWorkspaceId;

    /// <summary>当前打开的会话 id（两类共用 id 空间）。</summary>
    private string? _activeSessionId;

    /// <summary>当前打开的是哪个工作空间的会话；null 表示普通对话。</summary>
    private string? _activeWorkspaceId;

    private bool _reloading;

    /// <summary>两个分组是否各自收起（分组标题上的箭头点击切换）。</summary>
    private bool _workspacesCollapsed;
    private bool _plainSessionsCollapsed;

    /// <summary>每个导航项的统一行高（工作空间节点与会话项一致，避免高度参差）。</summary>
    private const double RowHeight = 36;

    /// <summary>列表右侧留白，给滚动条留出独立槽位，避免按钮贴着滚动条。</summary>
    private const double RightGutter = 12;

    private const string IdleFg = "#4B5563";
    private const string ActiveFg = "#1A1A2E";

    // 半透明高亮：软件背景是流动亚克力，用实色（原来的 #F1F3F5 / #E8F0FA）会把底纹盖死
    private static readonly IBrush HoverBg = new SolidColorBrush(Color.Parse("#14000000"));
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#334A90D9"));

    public ConversationPage()
    {
        InitializeComponent();
        SetupNewChatButton();
        StartCacheSweeper();
        ShowNewChatView();
        _ = ReloadAsync();
    }

    // ============================================================
    // 会话窗口缓存
    // ============================================================

    /// <summary>切走的会话窗口按 key 留在这里，保留期内切回来直接复用。</summary>
    private readonly SessionViewCache _viewCache = new();

    /// <summary>当前挂在右侧的窗口对应的缓存键；null 表示当前是「新建会话」页或空提示。</summary>
    private string? _cacheKey;

    private DispatcherTimer? _cacheSweeper;

    /// <summary>
    /// 保留时长。每次淘汰时现读设置，所以改设置立刻生效、不用重启。
    /// 下限 1 分钟：设置里最小的档位就是 1 分钟，防住手改配置文件写进 0 或负数。
    /// </summary>
    private static TimeSpan CacheRetention()
        => AppSettingsStore.Current.SessionCacheKeepForever
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMinutes(Math.Max(1, AppSettingsStore.Current.SessionCacheMinutes));

    private void StartCacheSweeper()
    {
        _cacheSweeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _cacheSweeper.Tick += (_, _) =>
        {
            foreach (var key in _viewCache.CollectExpired(DateTime.UtcNow, CacheRetention(), _cacheKey))
                _viewCache.Evict(key);
        };
        _cacheSweeper.Start();
    }

    /// <summary>
    /// 标签页被关掉后这个页面就没人引用了，但<b>运行中</b>的 DispatcherTimer 会被 Dispatcher
    /// 持有，于是它反过来把页面、以及缓存里的每个会话窗口都吊住不放。
    /// 所以离开可视树就停表（停止的计时器不再被 Dispatcher 引用，整张对象图随之可回收），
    /// 切回标签页再启动。**不清理缓存**——切标签页不等于放弃这些窗口。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cacheSweeper?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cacheSweeper?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 取出（或新建）某个会话的窗口并挂到右侧。
    ///
    /// 命中缓存时<b>不</b>再走 <see cref="ConversationView.InitializeAsync"/>——
    /// 复用的窗口本来就带着完整状态，重跑一遍等于把「省下的重新加载」又还回去了。
    /// </summary>
    /// <param name="configure">只在新建时执行一次；复用的窗口已经带着上次的配置，不该被覆盖。</param>
    private async Task<ConversationView> ShowSessionViewAsync(
        string key,
        Func<IConversationProfile> createProfile,
        Action<ConversationView>? configure = null)
    {
        var isNew = !_viewCache.TryGet(key, out var view);

        if (isNew)
        {
            view = _viewCache.GetOrAdd(key, () =>
            {
                var created = new ConversationView(createProfile());
                configure?.Invoke(created);
                created.Updated += OnConversationUpdated;
                return created;
            }, DateTime.UtcNow);
        }
        else
        {
            _viewCache.Touch(key, DateTime.UtcNow);
        }

        _cacheKey = key;
        RightPanel.Content = view;
        RebuildNav();

        if (isNew) await view!.InitializeAsync();
        return view!;
    }

    private void SetupNewChatButton()
    {
        NewChatShadow.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new CubicEaseOut(),
            },
        };

        NewChatBtn.PointerEntered += (_, _) => NewChatShadow.Opacity = 1;
        NewChatBtn.PointerExited += (_, _) => NewChatShadow.Opacity = 0;
        NewChatBtn.PointerPressed += (_, e) =>
        {
            ShowNewChatView();
            e.Handled = true;
        };
    }

    // ============================================================
    // 数据加载
    // ============================================================

    private async Task ReloadAsync()
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            _workspaces.Clear();
            _workspaces.AddRange(await WorkspaceRepository.GetAllAsync());

            _plainSessions.Clear();
            _plainSessions.AddRange(await ChatRepository.GetAllAsync());

            _workspaceSessions.Clear();
            if (_expandedWorkspaceId is not null)
                await LoadWorkspaceSessionsAsync(_expandedWorkspaceId);

            // 会话可能已被删除，清掉悬空的高亮
            if (_activeSessionId is not null && FindSession(_activeSessionId) is null)
            {
                _activeSessionId = null;
                _activeWorkspaceId = null;
            }

            RebuildNav();
        }
        finally
        {
            _reloading = false;
        }
    }

    private async Task LoadWorkspaceSessionsAsync(string workspaceId)
        => _workspaceSessions[workspaceId] = await WorkspaceSessionRepository.GetByWorkspaceAsync(workspaceId);

    private SessionRef? FindSession(string sessionId)
    {
        var plain = _plainSessions.FirstOrDefault(s => s.Id == sessionId);
        if (plain is not null)
            return new SessionRef(plain.Id, plain.Title, plain.LastMessageAt ?? plain.CreatedAt, true, null);

        foreach (var (wsId, list) in _workspaceSessions)
        {
            var s = list.FirstOrDefault(x => x.Id == sessionId);
            if (s is not null)
                return new SessionRef(s.Id, s.Title, s.LastMessageAt ?? s.CreatedAt, false, wsId);
        }
        return null;
    }

    // ============================================================
    // 左侧导航构建
    // ============================================================

    private void RebuildNav()
    {
        NavList.Children.Clear();

        // —— 工作空间分组 ——
        NavList.Children.Add(SectionHeader(
            "工作空间",
            collapsed: _workspacesCollapsed,
            onToggle: () => { _workspacesCollapsed = !_workspacesCollapsed; RebuildNav(); },
            onAdd: () => _ = CreateWorkspaceAsync()));

        if (!_workspacesCollapsed)
        {
            if (_workspaces.Count == 0)
                NavList.Children.Add(EmptyHint("暂无工作空间，点击右侧「+」创建"));
            else
                foreach (var ws in _workspaces)
                    NavList.Children.Add(WorkspaceNode(ws));
        }

        // —— 普通会话分组 ——
        NavList.Children.Add(SectionHeader(
            "普通会话",
            collapsed: _plainSessionsCollapsed,
            onToggle: () => { _plainSessionsCollapsed = !_plainSessionsCollapsed; RebuildNav(); },
            onAdd: () => _ = CreatePlainSessionAsync()));

        if (!_plainSessionsCollapsed)
        {
            if (_plainSessions.Count == 0)
                NavList.Children.Add(EmptyHint("暂无对话，点击上方「创建新会话」"));
            else
                foreach (var s in _plainSessions)
                    NavList.Children.Add(SessionItem(
                        new SessionRef(s.Id, s.Title, s.LastMessageAt ?? s.CreatedAt, true, null),
                        indent: 0,
                        onOpen: () => _ = OpenPlainSessionAsync(s),
                        onDelete: () => DeletePlainSessionAsync(s),
                        onRename: t => RenamePlainSessionAsync(s, t)));
        }
    }

    /// <summary>
    /// 分组标题行：左边「名称 + 展开箭头」，右边「+」。
    ///
    /// 箭头跟在名称<b>后面</b>：放在前面会把名称整体挤右，和下面会话项的左边缘对不齐。
    /// 整行既不高亮也不响应点击，只有箭头那个小方块能点。
    /// 箭头用 Opacity 隐去而不是 IsVisible —— IsVisible=false 的控件不参与布局，
    /// 鼠标移入移出会让名称左右跳动。
    ///
    /// 显示规则：<b>已收起时常驻</b>（否则看不出这个分组还有内容），
    /// 展开时只在鼠标悬停该行时出现。
    /// 悬停反馈只把字形调深，不加按钮底色。
    /// </summary>
    private Control SectionHeader(string text, bool collapsed, Action onToggle, Action? onAdd)
    {
        var title = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var chevronIdle = new SolidColorBrush(Color.Parse("#6B7280"));
        var chevronHover = new SolidColorBrush(Color.Parse("#1A1A2E"));

        var chevronText = new TextBlock
        {
            Text = collapsed ? "\uE76C" : "\uE70D",   // ChevronRight / ChevronDown
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 11,
            Foreground = chevronIdle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = chevronText,
            Opacity = collapsed ? 1 : 0,
        };
        ToolTip.SetTip(toggle, collapsed ? "展开" : "收起");
        toggle.PointerEntered += (_, _) => chevronText.Foreground = chevronHover;
        toggle.PointerExited += (_, _) => chevronText.Foreground = chevronIdle;
        toggle.PointerPressed += (_, e) => { onToggle(); e.Handled = true; };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, toggle },
        });

        if (onAdd is not null)
        {
            var addBtn = GlyphButton("\uE710", "新建会话");
            addBtn.Click += (_, _) => onAdd();
            Grid.SetColumn(addBtn, 1);
            grid.Children.Add(addBtn);
        }

        var header = new Border
        {
            Height = 28,
            Padding = new Thickness(12, 0, 8, 0),
            Margin = new Thickness(0, 8, RightGutter, 2),
            Background = Brushes.Transparent,
            Child = grid,
        };
        header.PointerEntered += (_, _) => toggle.Opacity = 1;
        header.PointerExited += (_, _) => { if (!collapsed) toggle.Opacity = 0; };

        return header;
    }

    /// <summary>工作空间节点：折叠/展开工作空间，展开后就地列出其会话。</summary>
    private Control WorkspaceNode(WorkspaceConfig ws)
    {
        var expanded = _expandedWorkspaceId == ws.Id;
        var isActive = expanded && _activeWorkspaceId == ws.Id;

        // 文件夹图标随展开态切换（Segoe Fluent Icons：Folder / FolderOpen）
        var icon = new TextBlock
        {
            Text = expanded ? "\uE838" : "\uE8B7",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var nameBlock = new TextBlock
        {
            Text = ws.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? ActiveFg : IdleFg)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 「···」：悬停该行才出现，位置在「+」左边。
        // 用 Opacity 而不是 IsVisible —— 始终占位，否则鼠标移入移出会把「+」左右推来推去。
        var moreIcon = new TextBlock
        {
            Text = "\uE712",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var moreBtn = new Button
        {
            Content = moreIcon,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = 0,
        };
        moreBtn.PointerEntered += (_, _) => moreBtn.Background = HoverBg;
        moreBtn.PointerExited += (_, _) => moreBtn.Background = Brushes.Transparent;
        // 吃掉按下事件，避免冒泡成「展开/收起工作空间」
        moreBtn.PointerPressed += (_, e) => e.Handled = true;

        Flyout? menu = null;
        var menuPanel = new StackPanel { Spacing = 2, MinWidth = 140 };
        menuPanel.Children.Add(MenuRow("\uE70F", "编辑", danger: false,
            onClick: () => { menu?.Hide(); _ = EditWorkspaceAsync(ws); }));
        menuPanel.Children.Add(MenuRow("\uE74D", "删除", danger: true,
            onClick: () => { menu?.Hide(); _ = DeleteWorkspaceAsync(ws); }));
        menu = new Flyout
        {
            Content = menuPanel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        moreBtn.Click += (_, _) => menu?.ShowAt(moreBtn);

        var newSessionBtn = GlyphButton("\uE710", "在此工作空间新建会话");
        newSessionBtn.Click += (_, _) => _ = CreateWorkspaceSessionAsync(ws);
        newSessionBtn.Opacity = 0;   // 与「···」一样，悬停该行才出现

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);
        Grid.SetColumn(moreBtn, 2);
        grid.Children.Add(moreBtn);
        Grid.SetColumn(newSessionBtn, 3);
        grid.Children.Add(newSessionBtn);

        // 与会话项完全同高同边距；「N工具 · N技能」那行已去掉，改为单行
        var header = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(10),
            Background = isActive ? ActiveBg : Brushes.Transparent,
            Padding = new Thickness(12, 0, 8, 0),
            Margin = new Thickness(0, 0, RightGutter, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        header.PointerEntered += (_, _) =>
        {
            if (!isActive) header.Background = HoverBg;
            moreBtn.Opacity = 1;
            newSessionBtn.Opacity = 1;
        };
        header.PointerExited += (_, _) =>
        {
            // 菜单开着时不收起悬停态：指针是移向菜单，这一行同样会收到 PointerExited
            if (menu?.IsOpen == true) return;
            if (!isActive) header.Background = Brushes.Transparent;
            moreBtn.Opacity = 0;
            newSessionBtn.Opacity = 0;
        };
        menu.Closed += (_, _) =>
        {
            if (header.IsPointerOver) return;
            if (!isActive) header.Background = Brushes.Transparent;
            moreBtn.Opacity = 0;
            newSessionBtn.Opacity = 0;
        };
        header.PointerPressed += (_, e) => { _ = ToggleWorkspaceAsync(ws); e.Handled = true; };

        if (!expanded) return header;

        var group = new StackPanel { Spacing = 2 };
        group.Children.Add(header);

        if (!_workspaceSessions.TryGetValue(ws.Id, out var sessions) || sessions.Count == 0)
            group.Children.Add(EmptyHint("暂无会话，点击「+」新建", indent: 34));
        else
            foreach (var s in sessions)
                group.Children.Add(SessionItem(
                    new SessionRef(s.Id, s.Title, s.LastMessageAt ?? s.CreatedAt, false, ws.Id),
                    indent: 34,
                    onOpen: () => _ = OpenWorkspaceSessionAsync(ws, s),
                    onDelete: () => DeleteWorkspaceSessionAsync(s),
                    onRename: t => RenameWorkspaceSessionAsync(s, t)));

        return group;
    }

    /// <summary>会话列表项（普通对话与工作空间会话共用一套样式）。</summary>
    private Border SessionItem(SessionRef session, double indent, Action onOpen,
                               Func<Task> onDelete, Func<string, Task> onRename)
    {
        var isActive = session.Id == _activeSessionId;
        var itemBg = isActive ? ActiveBg : Brushes.Transparent;

        var title = new TextBlock
        {
            Text = session.Title,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? ActiveFg : IdleFg)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        // 行内重命名：和标题同处一列，靠可见性互换，不改变行高
        var renameBox = new TextBox
        {
            Text = session.Title,
            FontSize = 13,
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4A90D9")),
            BorderThickness = new Thickness(1),
            IsVisible = false,
        };
        // 输入框内的按下不能冒泡出去，否则会话项会把它当成「打开会话」
        renameBox.PointerPressed += (_, e) => e.Handled = true;

        var time = new TextBlock
        {
            Text = FormatTime(session.SortAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ---- 「更多」按钮：平时显示时间，鼠标悬停时换成它 ----
        var moreIcon = new TextBlock
        {
            Text = "\uE712",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var moreBtn = new Button
        {
            Content = moreIcon,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // 显式居中：默认的内容对齐会让字形偏下偏右
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        moreBtn.PointerEntered += (_, _) => moreBtn.Background = HoverBg;
        moreBtn.PointerExited += (_, _) => moreBtn.Background = Brushes.Transparent;
        // 吃掉按下事件，避免冒泡到会话项触发「打开」
        moreBtn.PointerPressed += (_, e) => e.Handled = true;

        // 时间（约 15px）与按钮（24px）互换可见性，容器锁死 24px，
        // 否则整行会在 36↔42 之间跳，鼠标扫过时整列都在动。
        var rightHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto"),
            Height = 24,
        };
        rightHost.Children.Add(time);
        rightHost.Children.Add(moreBtn);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(title);
        grid.Children.Add(renameBox);
        Grid.SetColumn(rightHost, 1);
        grid.Children.Add(rightHost);

        // 先建出来：下面的 BeginRename 要清掉它的底色。
        // 重命名时若保留行底色，输入框会嵌在一个圆角块里、两端露出「框套框」。
        var item = new Border
        {
            Height = RowHeight,                           // 行高写死，悬停换按钮时不发生重排
            CornerRadius = new CornerRadius(10),
            Background = itemBg,
            Padding = new Thickness(12, 0, 8, 0),
            Margin = new Thickness(indent, 0, RightGutter, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        Flyout? menu = null;

        void BeginRename()
        {
            time.IsVisible = true;
            moreBtn.IsVisible = false;
            rightHost.IsVisible = false;
            item.Background = Brushes.Transparent;
            renameBox.Text = session.Title;
            renameBox.IsVisible = true;
            title.IsVisible = false;
            renameBox.Focus();
            renameBox.SelectAll();
        }

        async Task CommitRenameAsync(bool save)
        {
            // 防重入：提交后 onRename 会触发整表重建，本控件随即被丢弃
            if (!renameBox.IsVisible) return;
            renameBox.IsVisible = false;
            title.IsVisible = true;
            rightHost.IsVisible = true;
            item.Background = itemBg;

            if (!save) return;
            var newTitle = (renameBox.Text ?? "").Trim();
            if (newTitle.Length > 0 && newTitle != session.Title)
                await onRename(newTitle);
        }

        renameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = CommitRenameAsync(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { _ = CommitRenameAsync(false); e.Handled = true; }
        };
        renameBox.LostFocus += (_, _) => _ = CommitRenameAsync(true);

        // ---- 悬浮菜单 ----
        var menuPanel = new StackPanel { Spacing = 2, MinWidth = 140 };
        menuPanel.Children.Add(MenuRow("\uE8AC", "重命名", danger: false,
            onClick: () => { menu?.Hide(); BeginRename(); }));
        menuPanel.Children.Add(MenuRow("\uE74D", "删除", danger: true,
            onClick: () => { menu?.Hide(); _ = onDelete(); }));

        menu = new Flyout
        {
            Content = menuPanel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        moreBtn.Click += (_, _) => menu?.ShowAt(moreBtn);

        item.PointerEntered += (_, _) =>
        {
            if (!isActive && !renameBox.IsVisible) item.Background = HoverBg;
            time.IsVisible = false;
            moreBtn.IsVisible = true;
        };
        item.PointerExited += (_, _) =>
        {
            // 菜单开着时不收起悬停态：指针是移向菜单，会话项同样会收到 PointerExited
            if (menu?.IsOpen == true) return;
            if (!isActive && !renameBox.IsVisible) item.Background = Brushes.Transparent;
            time.IsVisible = true;
            moreBtn.IsVisible = false;
        };
        menu.Closed += (_, _) =>
        {
            if (item.IsPointerOver) return;
            if (!isActive && !renameBox.IsVisible) item.Background = Brushes.Transparent;
            time.IsVisible = true;
            moreBtn.IsVisible = false;
        };
        item.PointerPressed += (_, e) =>
        {
            if (renameBox.IsVisible) return;               // 重命名中不触发打开
            onOpen();
            e.Handled = true;
        };

        return item;
    }

    /// <summary>
    /// 自绘的菜单行。内边距与 hover 底色与 Fluent 的 MenuItem 对齐
    /// （App.axaml 里 <c>MenuFlyoutItemThemePaddingNarrow</c> = 12,8），
    /// 这样「···」这种自定义 Flyout 与真正的 MenuFlyout 看起来是同一套菜单。
    /// </summary>
    private static Border MenuRow(string glyph, string text, bool danger, Action onClick)
    {
        var fg = danger ? "#DC2626" : "#374151";

        var row = new Border
        {
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 14,
                        Width = 18,
                        TextAlignment = TextAlignment.Center,
                        Foreground = new SolidColorBrush(Color.Parse(fg)),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse(fg)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                    },
                },
            },
        };

        var hover = new SolidColorBrush(Color.Parse(danger ? "#26DC2626" : "#14000000"));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, e) => { e.Handled = true; onClick(); };

        return row;
    }

    private static Button GlyphButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private static TextBlock EmptyHint(string text, double indent = 12) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(indent, 6, 8, 6),
    };

    // ============================================================
    // 右侧内容
    // ============================================================

    /// <summary>回到「新建普通对话」输入页。</summary>
    private void ShowNewChatView()
    {
        _activeSessionId = null;
        _activeWorkspaceId = null;
        _cacheKey = null;             // 「新建」页不是某个会话的窗口，别让它挡住缓存淘汰

        var view = new NewChatView();
        view.SendRequested += OnNewChatSend;
        RightPanel.Content = view;

        RebuildNav();
    }

    private async void OnNewChatSend(object? sender, string text)
    {
        if (sender is not NewChatView nv) return;
        nv.SendRequested -= OnNewChatSend;
        if (string.IsNullOrWhiteSpace(text)) return;

        var firstLine = ConversationTitle.InitialTitle(text);

        // ---- 工作空间模式：在所选工作空间里立即建会话并直接发出首条消息 ----
        if (nv.IsWorkspaceMode)
        {
            if (nv.SelectedWorkspace is not { } ws) return;

            // Agent 层已停用，第二个参数恒为 null：默认工具/技能只从工作空间取
            var wsSession = WorkspaceAgentPipeline.CreateSessionDefaults(ws, null);
            wsSession.Title = firstLine;
            wsSession.Model = nv.SelectedModel?.ModelName ?? "";
            wsSession.Thinking = (int)nv.ThinkingMode;
            wsSession.ToolPermissionMode = nv.SelectedPermissionMode;
            // 只有用户确实勾了才覆盖：空集合会把工作空间的默认工具整个清掉
            if (nv.SelectedToolIds.Count > 0) wsSession.EnabledToolIds = nv.SelectedToolIds.ToList();
            if (nv.SelectedSkills.Count > 0) wsSession.EnabledSkills = nv.SelectedSkills.ToList();
            if (nv.SelectedMcpServerIds.Count > 0) wsSession.McpServers = nv.SelectedMcpServerIds.ToList();

            await WorkspaceSessionRepository.CreateAsync(wsSession);

            _activeSessionId = wsSession.Id;
            _activeWorkspaceId = ws.Id;
            _expandedWorkspaceId = ws.Id;

            var wsView = await ShowSessionViewAsync(
                SessionViewCache.KeyForWorkspaceSession(ws.Id, wsSession.Id),
                () => new WorkspaceProfile(ws, wsSession));

            await wsView.SendMessageAsync(text);
            await ReloadAsync();
            return;
        }

        // ---- 普通会话：会话在首条消息发出时才落库 ----
        var session = new ChatSession
        {
            Title = firstLine,
            Model = nv.SelectedModel?.ModelName ?? "",
            Thinking = (int)nv.ThinkingMode,
            EnabledToolIds = nv.SelectedToolIds.ToList(),
            EnabledSkills = nv.SelectedSkills.ToList(),
            McpServers = nv.SelectedMcpServerIds.ToList(),
            ToolPermissionMode = nv.SelectedPermissionMode,
        };
        await ChatRepository.CreateAsync(session);

        _activeSessionId = session.Id;
        _activeWorkspaceId = null;

        var view = await ShowSessionViewAsync(
            SessionViewCache.KeyForSession(session.Id),
            () => new StandaloneProfile(session.Id),
            created =>
            {
                created.ThinkingMode = nv.ThinkingMode;        // 首条消息沿用新建页选择的思考模式
                created.SelectedToolIds = nv.SelectedToolIds;  // 以及新建页激活的工具
            });

        await view.SendMessageAsync(text);
        await ReloadAsync();
    }

    /// <summary>
    /// 直接创建一个空白普通会话并立刻打开：会话马上落库，用户随后在会话窗口里发第一条消息。
    /// 与「创建新会话」按钮那条路（先在 NewChatView 配置、首条消息才建库）并存。
    /// </summary>
    private async Task CreatePlainSessionAsync()
    {
        var session = new ChatSession
        {
            Title = ConversationTitle.Placeholder,
            Model = (await AppDefaults.ResolveDefaultModelAsync())?.ModelName ?? "",
            EnabledToolIds = AppDefaults.ResolveToolIds(),
            EnabledSkills = AppDefaults.ResolveSkills(),
            McpServers = AppDefaults.ResolveMcpServerIds(),
            ToolPermissionMode = PermissionMode.Normal,
        };
        await ChatRepository.CreateAsync(session);

        _activeSessionId = session.Id;
        _activeWorkspaceId = null;

        await ShowSessionViewAsync(
            SessionViewCache.KeyForSession(session.Id),
            () => new StandaloneProfile(session.Id));

        await ReloadAsync();
    }

    private async Task OpenPlainSessionAsync(ChatSession session)
    {
        _activeSessionId = session.Id;
        _activeWorkspaceId = null;
        await ShowSessionViewAsync(
            SessionViewCache.KeyForSession(session.Id),
            () => new StandaloneProfile(session.Id));
    }

    private async Task OpenWorkspaceSessionAsync(WorkspaceConfig ws, WorkspaceChatSession session)
    {
        _activeSessionId = session.Id;
        _activeWorkspaceId = ws.Id;
        await ShowSessionViewAsync(
            SessionViewCache.KeyForWorkspaceSession(ws.Id, session.Id),
            () => new WorkspaceProfile(ws, session));
    }

    private async void OnConversationUpdated() => await ReloadAsync();

    // ============================================================
    // 动作
    // ============================================================

    private async Task ToggleWorkspaceAsync(WorkspaceConfig ws)
    {
        if (_expandedWorkspaceId == ws.Id)
        {
            _expandedWorkspaceId = null;
        }
        else
        {
            _expandedWorkspaceId = ws.Id;
            await LoadWorkspaceSessionsAsync(ws.Id);
        }
        RebuildNav();
    }

    private async Task CreateWorkspaceAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dlg = new CreateWorkspaceWindow();
        if (owner is not null)
            await dlg.ShowDialog(owner);
        if (dlg.Result is null) return;

        await WorkspaceRepository.CreateAsync(dlg.Result);
        _expandedWorkspaceId = dlg.Result.Id;
        await ReloadAsync();
    }

    /// <summary>删除工作空间（二次确认）。它下面的会话由仓储级联软删除，工作目录里的文件不动。</summary>
    private async Task DeleteWorkspaceAsync(WorkspaceConfig ws)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(
                owner,
                $"确定删除工作空间「{ws.Name}」吗？\n\n它下面的会话会一并删除（消息仍保留在库中）；工作目录里的文件不受影响。",
                "删除工作空间"))
            return;

        try { await WorkspaceRepository.DeleteAsync(ws.Id); } catch { /* 忽略 */ }

        // 缓存里属于这个工作空间的窗口一并清掉（它下面的会话已级联删除）
        _viewCache.EvictWhere(key => key.StartsWith($"w:{ws.Id}:", StringComparison.Ordinal));

        // 清掉指向它的悬空状态
        if (_expandedWorkspaceId == ws.Id)
            _expandedWorkspaceId = null;
        if (_activeWorkspaceId == ws.Id)
        {
            _activeSessionId = null;
            _activeWorkspaceId = null;
            _cacheKey = null;
            RightPanel.Content = EmptyHint("选择左侧会话，或点击「创建新会话」");
        }

        await ReloadAsync();
    }

    /// <summary>打开工作空间的编辑窗口；确认后写回仓储并刷新侧栏。</summary>
    private async Task EditWorkspaceAsync(WorkspaceConfig ws)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dlg = new CreateWorkspaceWindow(ws);
        if (owner is not null)
            await dlg.ShowDialog(owner);
        if (dlg.Result is null) return;

        // 编辑模式返回的就是 ws 本身，仓储 UpdateAsync 按 Id 覆盖
        try { await WorkspaceRepository.UpdateAsync(dlg.Result); } catch { /* 忽略 */ }
        await ReloadAsync();
    }

    private async Task CreateWorkspaceSessionAsync(WorkspaceConfig ws)
    {
        // 工作空间会话是「立即创建」，与普通对话的「首条消息才创建」不同
        var session = WorkspaceAgentPipeline.CreateSessionDefaults(ws, null);
        await WorkspaceSessionRepository.CreateAsync(session);

        _expandedWorkspaceId = ws.Id;
        await LoadWorkspaceSessionsAsync(ws.Id);
        await OpenWorkspaceSessionAsync(ws, session);
    }

    private async Task DeletePlainSessionAsync(ChatSession session)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, $"确定删除会话「{session.Title}」吗？", "删除会话")) return;

        try { await ChatRepository.SoftDeleteSessionAsync(session.Id); } catch { /* 忽略 */ }
        _viewCache.EvictBySessionId(session.Id);   // 否则删掉之后还能从缓存切回这个窗口

        if (_activeSessionId == session.Id)
        {
            ShowNewChatView();
        }
        await ReloadAsync();
    }

    private async Task DeleteWorkspaceSessionAsync(WorkspaceChatSession session)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, $"确定删除会话「{session.Title}」吗？", "删除会话")) return;

        try { await WorkspaceSessionRepository.SoftDeleteAsync(session.Id); } catch { /* 忽略 */ }
        _viewCache.EvictBySessionId(session.Id);   // 否则删掉之后还能从缓存切回这个窗口

        if (_activeSessionId == session.Id)
        {
            _activeSessionId = null;
            _activeWorkspaceId = null;
            _cacheKey = null;
            RightPanel.Content = EmptyHint("选择左侧会话，或点击「创建新会话」");
        }

        if (_expandedWorkspaceId is not null)
            await LoadWorkspaceSessionsAsync(_expandedWorkspaceId);
        await ReloadAsync();
    }

    private async Task RenamePlainSessionAsync(ChatSession session, string title)
    {
        session.Title = title;
        try { await ChatRepository.UpdateAsync(session); } catch { /* 忽略 */ }
        await ReloadAsync();
    }

    private async Task RenameWorkspaceSessionAsync(WorkspaceChatSession session, string title)
    {
        session.Title = title;
        try { await WorkspaceSessionRepository.UpdateAsync(session); } catch { /* 忽略 */ }
        await ReloadAsync();
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var now = DateTimeOffset.Now;
        if (dt.Date == now.Date) return dt.ToString("HH:mm");
        if (dt.Date == now.AddDays(-1).Date) return "昨天";
        return dt.ToString("M/d");
    }
}
