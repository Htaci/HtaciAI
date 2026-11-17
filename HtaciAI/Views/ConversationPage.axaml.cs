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
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Plugins;
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

    /// <summary>
    /// 已渲染会话行的状态标记：会话 id → 更新动作。
    /// 状态变化时只动这一行，不重建整个导航 —— 重建会打断正在进行的行内重命名。
    /// </summary>
    private readonly Dictionary<string, Action<SessionActivity>> _statusRows = new();

    /// <summary>
    /// 已渲染工作空间节点的状态汇总：工作空间 id → 刷新动作。
    /// 与会话行那一份（<see cref="_statusRows"/>）分开存：会话状态一变，它所属的那一行要跟着重算，
    /// 而这个工作空间下的会话行可能压根没渲染（节点收起时连会话行都不存在）。
    /// </summary>
    private readonly Dictionary<string, Action> _workspaceStatusRows = new();

    /// <summary>两个分组是否各自收起（分组标题上的箭头点击切换）。</summary>
    private bool _workspacesCollapsed;
    private bool _plainSessionsCollapsed;

    /// <summary>每个导航项的统一行高（工作空间节点与会话项一致，避免高度参差）。</summary>
    private const double RowHeight = 36;

    /// <summary>分级展开时每一级多显示多少条会话。</summary>
    private const int SessionsPerLevel = 5;

    /// <summary>
    /// 工作空间会话列表的分级展开级别：工作空间 id → 级别（1 级起）。
    /// 显示条数恒为「级别 × 5」，期间新增的会话只把队尾挤下去，不会撑破当前级别。
    /// </summary>
    private readonly Dictionary<string, int> _workspaceSessionLevels = new();

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
        SessionActivityStore.Changed += OnSessionActivityChanged;
        _ = ReloadAsync();
    }

    // ============================================================
    // 会话窗口缓存
    // ============================================================

    /// <summary>
    /// 切走的会话窗口按 key 留在这里，保留期内切回来直接复用。
    /// 用的是<b>全进程共用</b>的那一份（见 <see cref="SessionViewCache.Shared"/>），
    /// 于是多个标签页不会给同一个会话各建一个窗口。
    /// </summary>
    private readonly SessionViewCache _viewCache = SessionViewCache.Shared;

    /// <summary>当前挂在右侧的窗口对应的缓存键；null 表示当前是「新建会话」页或空提示。</summary>
    private string? _cacheKey;

    private DispatcherTimer? _cacheSweeper;

    /// <summary>左侧导航底部插槽的订阅。离开可视树时释放。</summary>
    private IDisposable? _sidebarSlotSubscription;

    /// <summary>右侧插槽的订阅。离开可视树时释放。</summary>
    private IDisposable? _rightSidebarSlotSubscription;

    /// <summary>右侧现在放的是「这个会话正显示在别处」的占位（而不是会话视图）。</summary>
    private bool _showingDockPlaceholder;

    /// <summary>是否已订阅「会话视图被插件借走 / 还回来」。离开可视树时退订。</summary>
    private bool _dockSubscribed;

    /// <summary>
    /// 保留时长。每次淘汰时现读设置，所以改设置立刻生效、不用重启。
    /// <b>0 = 不缓存</b>（返回 <see cref="TimeSpan.Zero"/>：切走即释放）；
    /// 正数下限仍是 1 分钟，防住手改配置文件写进负数。
    /// </summary>
    private static TimeSpan CacheRetention()
    {
        var settings = AppSettingsStore.Current;
        if (settings.SessionCacheKeepForever) return Timeout.InfiniteTimeSpan;
        if (settings.SessionCacheMinutes <= 0) return TimeSpan.Zero;

        return TimeSpan.FromMinutes(Math.Max(1, settings.SessionCacheMinutes));
    }

    /// <summary>
    /// 缓存关着（保留 0 分钟）时立刻收掉别的窗口，不等 30 秒一次的清扫器 ——
    /// 「不缓存」要是切走之后还能从缓存切回来，就等于没生效。
    ///
    /// <b>正在显示的、以及还在跑任务的窗口不在此列</b>：前者由持有者登记挡住，
    /// 后者由 <see cref="IsPinned"/> 挡住（见那里的注释，淘汰运行中的窗口会让同一个会话出现两个窗口）。
    /// </summary>
    private void TrimCacheIfDisabled()
    {
        if (CacheRetention() != TimeSpan.Zero) return;

        foreach (var key in _viewCache.CollectExpired(DateTime.UtcNow, TimeSpan.Zero, IsPinned))
            _viewCache.Evict(key);
    }

    /// <summary>交回本页持有的缓存登记，并按当前设置顺手收掉不再需要的窗口。</summary>
    private void ReleaseHolder()
    {
        _viewCache.ReleaseHolder(holder: this);
        TrimCacheIfDisabled();
    }

    private void StartCacheSweeper()
    {
        _cacheSweeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _cacheSweeper.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;

            // 正在显示的这一个每轮都刷新：用户可能停在这一页读了一小时，
            // 期间没有任何「切回」动作，不刷新就会被按「上次显示时间」淘汰掉 ——
            // 而淘汰掉正在看的窗口，下次进来就是一个重新读库的新窗口。
            // （「不许淘汰正在显示的」由缓存里的持有者登记保证，跨标签页也成立。）
            if (_cacheKey is not null) _viewCache.Touch(_cacheKey, now);

            foreach (var key in _viewCache.CollectExpired(now, CacheRetention(), IsPinned))
                _viewCache.Evict(key);
        };
        _cacheSweeper.Start();
    }

    // ============================================================
    // 左侧导航底部插槽
    // ============================================================

    private void HookSidebarSlot()
    {
        if (_sidebarSlotSubscription is not null) return;

        RefreshSidebarSlot();
        _sidebarSlotSubscription = PluginSlotPanel.Subscribe(RefreshSidebarSlot);
    }

    private void UnhookSidebarSlot()
    {
        _sidebarSlotSubscription?.Dispose();
        _sidebarSlotSubscription = null;
    }

    private void RefreshSidebarSlot()
        => PluginSlotPanel.Fill(SidebarBelowSlot, PluginSlots.SidebarBelow);

    // ============================================================
    // 右侧插槽
    // ============================================================

    private void HookRightSidebarSlot()
    {
        if (_rightSidebarSlotSubscription is not null) return;

        RefreshRightSidebarSlot();
        _rightSidebarSlotSubscription = PluginSlotPanel.Subscribe(RefreshRightSidebarSlot);
    }

    private void UnhookRightSidebarSlot()
    {
        _rightSidebarSlotSubscription?.Dispose();
        _rightSidebarSlotSubscription = null;
    }

    private void RefreshRightSidebarSlot()
    {
        PluginSlotPanel.Fill(RightSidebarSlotPanel, PluginSlots.ConversationSidebar);

        // Fill 只管内层面板：外面那层 Border 带着分隔线，没内容时整列都要收掉
        RightSidebarSlot.IsVisible = RightSidebarSlotPanel.Children.Count > 0;
    }

    // ============================================================
    // 会话视图被插件借走（conversation.dock）
    // ============================================================

    private void HookDock()
    {
        if (_dockSubscribed) return;

        ConversationDockHost.Instance.DockChanged += OnDockChanged;
        _dockSubscribed = true;
    }

    private void UnhookDock()
    {
        if (!_dockSubscribed) return;

        ConversationDockHost.Instance.DockChanged -= OnDockChanged;
        _dockSubscribed = false;
    }

    /// <summary>
    /// 某个会话的视图被借走 / 还回来了。
    ///
    /// <b>借走</b>：本页把视图交出去（它在插件那边显示），换上一块由宿主生成的占位；
    /// <b>还回来</b>：按会话 id 重新挂上 —— 缓存命中就是原来那一份，滚动位置都在。
    /// </summary>
    private void OnDockChanged(string sessionId)
    {
        if (!string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)) return;

        if (ConversationDockHost.Instance.Find(sessionId) is { } docked)
        {
            ReleaseHolder();
            _cacheKey = null;
            _showingDockPlaceholder = true;
            RightPanel.Content = CreateDockPlaceholder(docked);
            return;
        }

        // 还回来了：只处理「本页正显示着这个会话的占位」的情况，别把用户刚切到的别的东西顶掉
        if (!_showingDockPlaceholder) return;

        _showingDockPlaceholder = false;
        _ = OpenSessionAsync(sessionId);
    }

    /// <summary>
    /// 「这个会话正在别处显示」的占位。
    ///
    /// <b>由宿主统一生成</b>，插件只说「我叫什么」：这样每个插件免费得到一致的提示，
    /// 也不必知道会话页长什么样。召回按钮也是宿主的 —— 用户的意思优先于插件的。
    /// </summary>
    private Control CreateDockPlaceholder(ConversationDockInfo info)
    {
        var recall = new Button
        {
            Content = "召回",
            FontSize = 12.5,
            Padding = new Thickness(14, 5),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#4A90D9")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        recall.Click += (_, _) => ConversationDockHost.Instance.Recall(info.SessionId);

        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"这个会话正在「{info.HostLabel}」中显示",
                    FontSize = 13.5,
                    Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                recall,
            },
        };
    }

    /// <summary>
    /// 这个窗口不许被淘汰：它的会话还有一轮任务在跑（或在等用户授权 / 作答）。
    ///
    /// 运行中的窗口一旦被清掉，切回这个会话时缓存落空、于是重新建一个窗口从库里读 ——
    /// 读到的是一段「assistant 已落库、工具还没执行完」的半成品历史，
    /// 渲染出来正好像任务结束（聚合卡收成「已处理」+ 复制/编辑/删除操作栏 + 发送按钮），
    /// 而旧窗口其实还活着、还在往同一个会话里写。那是「看起来结束了，其实还在跑」的根。
    /// </summary>
    private static bool IsPinned(string key)
        => SessionActivityStore.Get(SessionViewCache.SessionIdOf(key))
           is SessionActivity.Running or SessionActivity.WaitingConfirm;

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
        HookSidebarSlot();
        HookRightSidebarSlot();
        HookDock();

        // 切回这个标签页时右侧是空的（见下面 OnDetachedFromVisualTree）：
        // 按会话 id 重新挂一次 —— 缓存命中就把原来那个窗口原样挂回来（滚动位置都还在），
        // 已过期就用注册表里可能还活着的运行时重绘，都没了才重新读库。
        // 被插件借走的会话会走 OpenSessionAsync 里的占位分支（不会把视图抢回来）。
        if (_activeSessionId is { } id && RightPanel.Content is null)
            _ = OpenSessionAsync(id);
    }

    /// <summary>
    /// 离开可视树（切标签页 / 关标签页）：停掉淘汰定时器，并且<b>把窗口交回缓存</b>。
    ///
    /// 必须交回：隐藏起来的标签页没人看着，它的窗口在缓存里就可能过期被清
    /// （另一个标签页的淘汰器并不知道这一页还攥着一个）。清掉之后如果把 Content 留着，
    /// 切回来看到的就是一个已经退订、不再更新的死窗口。
    /// 交回去之后，切回来按会话 id 重新取：命中就原样挂上，没命中就重建 —— 两种情况都正确。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cacheSweeper?.Stop();
        UnhookSidebarSlot();
        UnhookRightSidebarSlot();
        UnhookDock();

        // 只交回「缓存里的会话窗口」；「新建会话」页与空提示不在缓存里，也不会过期，
        // 清掉它们只会让切回标签页时看到一片空白。
        if (_cacheKey is not null)
        {
            _viewCache.Touch(_cacheKey, DateTime.UtcNow);
            ReleaseHolder();
            RightPanel.Content = null;
        }

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
        // 登记会话归哪个工作空间：工作空间节点要按它汇总「我下面还有没有在跑的会话」。
        // 放在这里而不是各个打开点，是因为「只要有窗口，就一定走这条路径」——
        // 包括从系统提醒跳进来、从缓存挂回来这些不经过列表的情况。
        if (SessionViewCache.WorkspaceIdOf(key) is { } ownerWorkspaceId)
            SessionActivityStore.Bind(SessionViewCache.SessionIdOf(key), ownerWorkspaceId);

        var isNew = !_viewCache.TryGet(key, out var view);

        if (isNew)
        {
            // 运行时按会话复用：插件（桌宠之类）可能早就借着一份在后台跑了，
            // 这时窗口挂上来直接用同一份 —— 既有历史、也接得上正在跑的那一轮。
            var sessionId = SessionViewCache.SessionIdOf(key);

            view = _viewCache.GetOrAdd(key, () =>
            {
                var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, createProfile);
                var created = new ConversationView(lease.Runtime, lease);
                configure?.Invoke(created);
                created.Updated += OnConversationUpdated;
                var createdView = created;
                created.ActivityChanged += a => OnSessionActivity(createdView.SessionId, a);
                return created;
            }, DateTime.UtcNow);
        }
        else
        {
            _viewCache.Touch(key, DateTime.UtcNow);
        }

        // 打开会话就算已读：绿点只在「用户没在看这个会话」的时候才有意义
        ClearUnread(view!.SessionId);

        // 同一件事：这个会话的提醒也该收掉了。用户已经把它打开看过了，
        // 留着那张卡片只能让他再点一次关闭。
        NotificationToast.DismissFor(view.SessionId);

        // 一个会话只能有一份窗口：缓存是共用的一份，这个窗口可能正挂在别的标签页上。
        // 先把它从原来的宿主摘下来，否则把它塞进本页的 ContentControl 会撞上「已有父级」。
        DetachFromOtherHost(view!);

        // 登记「这个窗口在本页显示着」：别的标签页的淘汰器据此跳过它。
        // 顺序是刻意的：先交回旧登记（此时顺手按「不缓存」设置收掉旧窗口），
        // 再登记新窗口 —— 反过来的话 TrimCacheIfDisabled 会把刚要显示的这一个当成缓存里的闲置窗口扔掉。
        _viewCache.ReleaseHolder(holder: this);
        _viewCache.Claim(key, this);
        TrimCacheIfDisabled();

        _cacheKey = key;
        RightPanel.Content = view;
        RebuildNav();

        if (isNew) await view!.InitializeAsync();
        return view!;
    }

    /// <summary>
    /// 把窗口从「别的宿主」上摘下来。只处理 ContentControl / Panel 两种父级 ——
    /// 本项目的会话窗口只可能被挂在页面的 <c>RightPanel</c>（ContentControl）里。
    /// </summary>
    private void DetachFromOtherHost(ConversationView view)
    {
        switch (view.Parent)
        {
            case ContentControl owner when !ReferenceEquals(owner, RightPanel):
                owner.Content = null;
                break;

            case Panel panel:
                panel.Children.Remove(view);
                break;
        }
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

    /// <summary>当前显示在右侧的会话 id；null 表示停在「新建会话」页。供系统提醒判定「用户是否正看着它」。</summary>
    public string? ActiveSessionId => _activeSessionId;

    /// <summary>
    /// 打开指定会话（普通对话 / 工作空间会话都支持）。系统提醒被点击时用它跳转。
    ///
    /// 三种情况都要能开：列表里已有（最快）、列表还没加载完（刚建的标签页）、
    /// 属于一个从没展开过的工作空间（内存里根本没有它的记录）。所以列表找不到就按 id 查库。
    /// </summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // 这个会话的视图正被插件借走显示着：本页只放占位。
        // <b>不能把视图抢回来</b> —— 那会让插件的面板突然空掉，而且抢回来之后
        // 它还以为自己占着，两个宿主各自以为自己拥有同一个窗口。
        if (ConversationDockHost.Instance.Find(sessionId) is { } docked)
        {
            _activeSessionId = sessionId;
            _cacheKey = null;
            ReleaseHolder();
            _showingDockPlaceholder = true;
            RightPanel.Content = CreateDockPlaceholder(docked);
            await ReloadAsync();
            return;
        }

        _showingDockPlaceholder = false;

        if (_plainSessions.FirstOrDefault(s => s.Id == sessionId) is not null
            || await ChatRepository.GetAsync(sessionId) is not null)
        {
            _activeSessionId = sessionId;
            _activeWorkspaceId = null;
            await ShowSessionViewAsync(
                SessionViewCache.KeyForSession(sessionId),
                () => new StandaloneProfile(sessionId));
            return;
        }

        if (await WorkspaceSessionRepository.GetAsync(sessionId) is not { } session) return;

        var ws = _workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId)
                 ?? await WorkspaceRepository.GetAsync(session.WorkspaceId);
        if (ws is null) return;

        _expandedWorkspaceId = ws.Id;
        await LoadWorkspaceSessionsAsync(ws.Id);
        await OpenWorkspaceSessionAsync(ws, session);
    }

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
        ResetStatusRows();
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
            Text = collapsed ? "\uF2B1" : "\uF2A4",   // ChevronRight / ChevronDown
            FontFamily = AppFonts.Icons,
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
            var addBtn = GlyphButton("\uF10A", "新建会话");
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

        // 文件夹图标随展开态切换（Fluent System Icons：Folder / FolderOpen）
        var icon = new TextBlock
        {
            Text = expanded ? "\uF42F" : "\uF419",
            FontFamily = AppFonts.Icons,
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
            Text = "\uE825",
            FontFamily = AppFonts.Icons,
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
            // 显隐由外面那一层 actions 统一控制：逐个设 Opacity 会和父级相乘，永远露不出来
        };
        moreBtn.PointerEntered += (_, _) => moreBtn.Background = HoverBg;
        moreBtn.PointerExited += (_, _) => moreBtn.Background = Brushes.Transparent;
        // 吃掉按下事件，避免冒泡成「展开/收起工作空间」
        moreBtn.PointerPressed += (_, e) => e.Handled = true;

        Flyout? menu = null;
        var menuPanel = new StackPanel { Spacing = 2, MinWidth = 140 };
        menuPanel.Children.Add(MenuRow("\uF3DE", "编辑", danger: false,
            onClick: () => { menu?.Hide(); _ = EditWorkspaceAsync(ws); }));
        menuPanel.Children.Add(MenuRow("\uF34D", "删除", danger: true,
            onClick: () => { menu?.Hide(); _ = DeleteWorkspaceAsync(ws); }));
        menu = new Flyout
        {
            Content = menuPanel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        moreBtn.Click += (_, _) => menu?.ShowAt(moreBtn);

        var newSessionBtn = GlyphButton("\uF10A", "在此工作空间新建会话");
        newSessionBtn.Click += (_, _) => _ = CreateWorkspaceSessionAsync(ws);

        // 右侧一格：状态标记与两个操作按钮互换可见性。
        // 汇总口径见 SessionActivityStore.AggregateOf：里面有会话在等用户 / 在跑 / 有回复没看，
        // 都算这个工作空间的状态 —— 收起之后光看会话行是看不出来的，这正是它存在的理由。
        var status = new SessionStatusBadge("需要处理", "这个工作空间里有会话在等你回答或授权");
        status.HorizontalAlignment = HorizontalAlignment.Right;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,   // 悬停才出现；透明度由 ApplyStatus 统一管
            Children = { moreBtn, newSessionBtn },
        };
        // 两种内容叠在同一格里（等价于会话行右侧那一格）：整行高度写死 24，
        // 换内容时不发生重排，鼠标扫过不会让这一列上下跳。
        var rightHost = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto"), Height = 24 };
        rightHost.Children.Add(status);
        rightHost.Children.Add(actions);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);
        Grid.SetColumn(rightHost, 2);
        grid.Children.Add(rightHost);

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
            ApplyStatus();
        };
        header.PointerExited += (_, _) =>
        {
            // 菜单开着时不收起悬停态：指针是移向菜单，这一行同样会收到 PointerExited
            if (menu?.IsOpen == true) return;
            if (!isActive) header.Background = Brushes.Transparent;
            ApplyStatus();
        };
        menu.Closed += (_, _) =>
        {
            if (!isActive) header.Background = Brushes.Transparent;
            ApplyStatus();
        };
        header.PointerPressed += (_, e) => { _ = ToggleWorkspaceAsync(ws); e.Handled = true; };

        // 悬停归按钮、移开归状态：两者叠在同一格里互换。
        // 顺手把「悬停中状态变了」也算进来 —— 后台那一轮跑完时不该把按钮顶掉。
        void ApplyStatus()
        {
            var activity = SessionActivityStore.AggregateOf(ws.Id);
            var showActions = header.IsPointerOver || menu?.IsOpen == true;

            // 隐藏时按 Idle 落地：转圈只在自己可见时才转，藏起来没必要继续烧 CPU
            status.Set(showActions ? SessionActivity.Idle : activity);
            actions.Opacity = showActions ? 1 : 0;

            ToolTip.SetTip(status, activity switch
            {
                SessionActivity.WaitingConfirm => "这个工作空间里有会话在等你回答或授权",
                SessionActivity.Running => "这个工作空间里有会话正在处理",
                SessionActivity.Done => "这个工作空间里有会话已回复",
                _ => null,
            });
        }
        _workspaceStatusRows[ws.Id] = ApplyStatus;
        ApplyStatus();   // 建行时就定好：是转圈、是要处理，还是让位给按钮

        if (!expanded) return header;

        var group = new StackPanel { Spacing = 2 };
        group.Children.Add(header);

        // 分级展开：默认一级（5 条），点「展开更多」加一级。级别按工作空间记住，
        // 重建导航（发消息、重命名、状态刷新）不会打回一级。
        // 条数只按「级别 × 5」算：期间新增的会话把队尾挤掉，不会让当前级别多出一条。
        var level = _workspaceSessionLevels.TryGetValue(ws.Id, out var lv) && lv > 0 ? lv : 1;


        if (!_workspaceSessions.TryGetValue(ws.Id, out var sessions) || sessions.Count == 0)
            group.Children.Add(EmptyHint("暂无会话，点击「+」新建", indent: 34));
        else
            foreach (var s in sessions.Take(Math.Min(sessions.Count, level * SessionsPerLevel)))
                group.Children.Add(SessionItem(
                    new SessionRef(s.Id, s.Title, s.LastMessageAt ?? s.CreatedAt, false, ws.Id),
                    indent: 34,
                    onOpen: () => _ = OpenWorkspaceSessionAsync(ws, s),
                    onDelete: () => DeleteWorkspaceSessionAsync(s),
                    onRename: t => RenameWorkspaceSessionAsync(s, t)));

        // 不足一级时不出按钮；已有级别就保留「收起」，哪怕删到只剩几条也能点回一级
        if (sessions is not null && (sessions.Count > SessionsPerLevel || level > 1))
            group.Children.Add(SessionLevelRow(ws.Id, level, sessions.Count));

        return group;
    }

    /// <summary>
    /// 会话列表底部的「展开更多 / 收起」。这里的「收起」只回到一级，
    /// 与工作空间节点上那个「收起整个工作空间」不是一回事。
    /// </summary>
    private Control SessionLevelRow(string workspaceId, int level, int total)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            // 缩进 = 会话项的 indent(34) + 行内左内边距(12)，文字才和上面的会话标题对齐
            Margin = new Thickness(34 + 12, 4, RightGutter, 2),
        };

        if (total > level * SessionsPerLevel)
            row.Children.Add(LevelLink("展开更多", () =>
            {
                _workspaceSessionLevels[workspaceId] = level + 1;
                RebuildNav();
            }));

        // 「收起」只在超过一级时出现；一级时自然就是收起的，给了反而点不出效果
        if (level > 1)
            row.Children.Add(LevelLink("收起", () =>
            {
                _workspaceSessionLevels[workspaceId] = 1;
                RebuildNav();
            }));

        return row;
    }

    /// <summary>分级展开用的文字链：无底色，悬停只把字调深。</summary>
    private static TextBlock LevelLink(string text, Action onClick)
    {
        var idle = new SolidColorBrush(Color.Parse("#9CA3AF"));
        var hover = new SolidColorBrush(Color.Parse("#1A1A2E"));

        var link = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = idle,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerEntered += (_, _) => link.Foreground = hover;
        link.PointerExited += (_, _) => link.Foreground = idle;
        link.PointerPressed += (_, e) => { e.Handled = true; onClick(); };
        return link;
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
            Text = "\uE825",
            FontFamily = AppFonts.Icons,
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

        // 时间（约 15px）、状态标记与按钮（24px）互换可见性，容器锁死 24px，
        // 否则整行会在 36↔42 之间跳，鼠标扫过时整列都在动。
        var rightHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto"),
            Height = 24,
        };
        var status = new SessionStatusBadge();
        rightHost.Children.Add(time);
        rightHost.Children.Add(status);
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

        // 这一格三种内容（时间 / 状态标记 / 「···」按钮）互换可见性：
        // 悬停时归按钮用，其余时候有活动状态就显示状态，否则显示时间。
        void ApplyStatus(SessionActivity activity)
        {
            status.Set(activity);
            rightHost.IsVisible = true;

            if (item.IsPointerOver)
            {
                // 悬停中状态变化（后台那一轮正好跑完）也只更新状态本身，别把按钮顶掉
                time.IsVisible = false;
                status.IsVisible = false;
                moreBtn.IsVisible = true;
                return;
            }

            time.IsVisible = activity == SessionActivity.Idle;
            moreBtn.IsVisible = false;
        }
        _statusRows[session.Id] = ApplyStatus;
        ApplyStatus(SessionActivityStore.Get(session.Id));   // 建行时就定好：有时间、有状态，还是给按钮

        void BeginRename()
        {
            ApplyStatus(SessionActivityStore.Get(session.Id));
            rightHost.IsVisible = false;   // 重命名时整格让位给输入框
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
            item.Background = itemBg;
            ApplyStatus(SessionActivityStore.Get(session.Id));   // 顺带把 rightHost 恢复可见

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
        menuPanel.Children.Add(MenuRow("\uF66A", "重命名", danger: false,
            onClick: () => { menu?.Hide(); BeginRename(); }));
        menuPanel.Children.Add(MenuRow("\uF34D", "删除", danger: true,
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
            status.IsVisible = false;
            moreBtn.IsVisible = true;
        };
        item.PointerExited += (_, _) =>
        {
            // 菜单开着时不收起悬停态：指针是移向菜单，会话项同样会收到 PointerExited
            if (menu?.IsOpen == true) return;
            if (!isActive && !renameBox.IsVisible) item.Background = Brushes.Transparent;
            ApplyStatus(SessionActivityStore.Get(session.Id));
        };
        menu.Closed += (_, _) =>
        {
            if (item.IsPointerOver) return;
            if (!isActive && !renameBox.IsVisible) item.Background = Brushes.Transparent;
            ApplyStatus(SessionActivityStore.Get(session.Id));
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
    /// 会话活动状态落地。<see cref="SessionActivity.Done"/> 只有「用户没在看这个会话」时才算未读，
    /// 否则当场就是已读 —— 不然自己盯着屏幕等来的回复也会挂个绿点。
    /// </summary>
    private void OnSessionActivity(string sessionId, SessionActivity activity)
    {
        if (activity == SessionActivity.Done && sessionId == _activeSessionId && IsEffectivelyVisible)
            activity = SessionActivity.Idle;

        SessionActivityStore.Set(sessionId, activity);
    }

    /// <summary>打开会话 = 已读：清掉回答完留下的绿点；运行中 / 等待确认的状态保留。</summary>
    private static void ClearUnread(string sessionId)
    {
        if (SessionActivityStore.Get(sessionId) == SessionActivity.Done)
            SessionActivityStore.Set(sessionId, SessionActivity.Idle);
    }

    /// <summary>状态变化：只刷新对应那一行（找不到就等下次导航重建时自然带上）。</summary>
    private void OnSessionActivityChanged(string sessionId)
    {
        // 任务跑完了（不再是 Running/WaitingConfirm）才给这个会话的窗口「重新开始计时」：
        // 跑的过程中它被钉住、本来也不会被淘汰，淘汰倒计时应当从跑完那一刻算，
        // 而不是从用户当初切走那一刻算 —— 否则长任务一结束，窗口就立刻够期了。
        if (SessionActivityStore.Get(sessionId) is not (SessionActivity.Running or SessionActivity.WaitingConfirm))
            _viewCache.TouchSession(sessionId, DateTime.UtcNow);

        // 工作空间节点上的汇总标记跟着变 —— 哪怕这一页压根没渲染这个会话的行
        // （节点收起 / 会话在别的标签页里跑），工作空间那一行也得刷新。
        if (SessionActivityStore.WorkspaceOf(sessionId) is { } workspaceId &&
            _workspaceStatusRows.TryGetValue(workspaceId, out var applyWorkspace))
            applyWorkspace();

        if (!_statusRows.TryGetValue(sessionId, out var apply)) return;
        apply(SessionActivityStore.Get(sessionId));
    }

    /// <summary>导航重建：旧的更新动作随控件一起作废。</summary>
    private void ResetStatusRows()
    {
        _statusRows.Clear();
        _workspaceStatusRows.Clear();
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
                        FontFamily = AppFonts.Icons,
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
                FontFamily = AppFonts.Icons,
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
        ReleaseHolder();

        var view = new NewChatView();
        view.SendRequested += OnNewChatSend;
        RightPanel.Content = view;

        RebuildNav();
    }

    private async void OnNewChatSend(object? sender, InputSubmit submit)
    {
        if (sender is not NewChatView nv) return;
        nv.SendRequested -= OnNewChatSend;

        var text = submit.Text;
        if (string.IsNullOrWhiteSpace(text) && submit.Attachments.Count == 0) return;

        // 没有可用模型就不往下走：发送按钮这时是禁用的，但回车绕过禁用按钮走到了这里。
        // 必须在建会话<b>之前</b>拦住 —— 否则会先落一条空会话，然后才发送失败。
        // 输入内容留在输入框里（控件提交后不清空），配好模型回来还能接着发。
        if (nv.SelectedModel is null) return;

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

            await wsView.SendMessageAsync(text, submit.Attachments);
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
            KnowledgeBaseId = nv.SelectedKnowledgeBaseId
                              ?? await AppDefaults.ResolveDefaultKnowledgeBaseIdAsync(null),
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

        await view.SendMessageAsync(text, submit.Attachments);
        await ReloadAsync();
    }

    /// <summary>
    /// 「设置 → 默认配置」里配的权限档位。滑到不认识的值（旧配置文件里没有这一项时为 0，
    /// 而 0 恰好是 Strict）时按普通处理 —— 不能让一次配置缺失把所有人的工具全变成要确认。
    /// </summary>
    private static PermissionMode DefaultPermissionMode()
    {
        var configured = (PermissionMode)AppSettingsStore.Current.DefaultPermissionMode;
        return configured is PermissionMode.Strict or PermissionMode.Normal
            or PermissionMode.Loose or PermissionMode.Free
            ? configured
            : PermissionMode.Normal;
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
            ToolPermissionMode = DefaultPermissionMode(),
            KnowledgeBaseId = await AppDefaults.ResolveDefaultKnowledgeBaseIdAsync(null),
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
            ReleaseHolder();
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
        // 工作空间指定了默认模型就用它，否则用「上次使用的模型」；都不成立时留空，
        // 会话窗口会停在「未配置模型」并拦住发送，不再默默落到列表里的第一个模型
        session.Model = (await AppDefaults.ResolveDefaultModelAsync(ws.DefaultModelId))?.ModelName ?? "";
        // 工作空间没指定默认知识库时，回落到全局默认 / 上次使用的那个
        if (string.IsNullOrWhiteSpace(session.KnowledgeBaseId))
            session.KnowledgeBaseId = await AppDefaults.ResolveDefaultKnowledgeBaseIdAsync(null);
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
        SessionActivityStore.Set(session.Id, SessionActivity.Idle);   // 删掉的行不该再给工作空间报状态

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
        SessionActivityStore.Set(session.Id, SessionActivity.Idle);   // 删掉的行不该再给工作空间报状态

        if (_activeSessionId == session.Id)
        {
            _activeSessionId = null;
            _activeWorkspaceId = null;
            _cacheKey = null;
            ReleaseHolder();
            RightPanel.Content = EmptyHint("选择左侧会话，或点击「创建新会话」");
        }

        if (_expandedWorkspaceId is not null)
            await LoadWorkspaceSessionsAsync(_expandedWorkspaceId);
        await ReloadAsync();
    }

    private async Task RenamePlainSessionAsync(ChatSession session, string title)
    {
        session.Title = title;

        if (ConversationRuntimeRegistry.Shared.Find(session.Id) is { } runtime)
        {
            // 会话的运行时手里有一份档案快照：只改库的话，下一轮收尾会把旧标题整份写回去
            // （见 ConversationRuntime.SetTitleAsync）。
            await runtime.SetTitleAsync(title);
        }
        else
        {
            try { await ChatRepository.UpdateAsync(session); } catch { /* 忽略 */ }
        }

        await ReloadAsync();
    }

    private async Task RenameWorkspaceSessionAsync(WorkspaceChatSession session, string title)
    {
        session.Title = title;

        if (ConversationRuntimeRegistry.Shared.Find(session.Id) is { } runtime)
        {
            // 同上：工作空间会话的档案也是快照，落库走运行时才不会在下一轮被覆盖
            await runtime.SetTitleAsync(title);
        }
        else
        {
            try { await WorkspaceSessionRepository.UpdateAsync(session); } catch { /* 忽略 */ }
        }

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
