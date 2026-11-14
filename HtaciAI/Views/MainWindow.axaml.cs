using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Controls;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Notifications;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Updates;

namespace HtaciAI.Views;

public partial class MainWindow : Window
{
    private int _selectedIndex;

    /// <summary>
    /// 全部标签页。每一项把「页面 / 容器 / 标签头 / 标题块」绑成一体。
    ///
    /// <b>为什么不再用三个按同一下标对齐的平行数组</b>：加一个数组就多一处必须同步的地方，
    /// 漏一处就变成「标题对、内容错」这种极难查的错位。而且现在还要再维护
    /// <see cref="TabStage"/> 的 Children 顺序 —— 四个平行列表同步是不可接受的。
    /// </summary>
    private readonly List<TabSlot> _tabs = new();

    /// <summary>插件开的标签页句柄。用户点 × 关掉时要把 Closed 事件补上。</summary>
    private readonly List<PluginTabHandle> _pluginTabs = new();

    /// <summary>标题栏右侧插槽的订阅。窗口关掉时释放。</summary>
    private readonly System.IDisposable _titlebarSlotSubscription;

    /// <summary>
    /// 一个标签页的全部组成部分。
    ///
    /// <see cref="Host"/> 是常驻在 <see cref="TabStage"/> 里的容器，
    /// <b>切换标签只改它的 <c>IsVisible</c></b>：这样页面本身永远不脱离可视树，
    /// 嵌在里面的原生控件（WebView2）也就不会被销毁。
    /// </summary>
    private sealed class TabSlot(Border host, Control content, Border header, TextBlock titleBlock)
    {
        /// <summary>常驻容器。<c>IsVisible</c> 挂在它上面。</summary>
        public Border Host { get; } = host;

        /// <summary>页面本身。就地替换时改它的父容器的 Child。</summary>
        public Control Content { get; set; } = content;

        public Border Header { get; } = header;

        public TextBlock TitleBlock { get; } = titleBlock;
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // 标题栏右侧插槽：主窗口是单例，订阅活到窗口关掉为止，不用跟着可视树来回挂
        RefreshTitlebarSlot();
        _titlebarSlotSubscription = PluginSlotPanel.Subscribe(RefreshTitlebarSlot);
        Closed += (_, _) => _titlebarSlotSubscription.Dispose();

        AddTab("智能对话", new ConversationPage());

        SetupSessionNotices();

        // 最大化按钮的图标要随窗口状态变化（最大化后变成「还原」）。
        // 用属性监听而不是只在自己点击时改，这样双击标题栏、系统快捷键等
        // 任何方式触发的状态变化都能同步到图标。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateMaximizeGlyph();
        };
        UpdateMaximizeGlyph();

        // 静默检查一次：不这么做的话，「有新版本才显示绿色」在用户不主动点击时永远看不到。
        // 没配置地址时 CheckAsync 直接返回 NotConfigured，不会发请求。
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void RefreshTitlebarSlot()
        => PluginSlotPanel.Fill(TitlebarRightSlot, PluginSlots.MainWindowTitlebarRight);

    /// <summary>按当前窗口状态切换「最大化 / 还原」的图标。</summary>
    private void UpdateMaximizeGlyph()
    {
        // Fluent System Icons：EB76 = 最大化，F78C = 还原。
        MaximizeGlyph.Text = WindowState == WindowState.Maximized
            ? "\uF78C"
            : "\uEB76";
    }

    // ============================================================
    // 系统消息提醒
    // ============================================================

    /// <summary>
    /// 把「要不要打扰」和「点了跳到哪」接到本窗口上。
    ///
    /// 判定只认窗口是否聚焦：用户人坐在这个软件前面时，侧栏的未读绿点已经够提示了，
    /// 不必再叠一层系统提醒；他去看别的程序（或最小化）时才值得弹。
    /// </summary>
    private void SetupSessionNotices()
    {
        SessionNotifier.IsAppActive = () => IsActive;
        SessionNotifier.OpenSession = sessionId => _ = JumpToSessionAsync(sessionId);
        SessionNotifier.Raised += NotificationToast.Show;

        // 用户回到窗口、而屏幕上正是那条提醒指的会话：提醒已经没意义了（他看的就是它，
        // 点上去也不会有任何反应），直接收掉。只在激活时判定，不追着每一次状态变化跑。
        Activated += (_, _) => DismissNoticesForActiveSession();
    }

    /// <summary>
    /// 收掉「用户正看着的那个会话」的提醒。
    /// <b>只认当前选中的标签页</b>：别的标签页里开着的会话他这会儿看不见，
    /// 那条提醒还有存在的必要。
    /// </summary>
    private void DismissNoticesForActiveSession()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;

        if (_tabs[_selectedIndex].Content is ConversationPage { ActiveSessionId: { } sessionId })
            NotificationToast.DismissFor(sessionId);
    }

    /// <summary>跳到某个会话：切到（没有就先用已有的）智能对话标签页，把窗口叫到前台，再打开会话。</summary>
    private async Task JumpToSessionAsync(string sessionId)
    {
        var index = _tabs.FindIndex(t => t.Content is ConversationPage);
        if (index < 0) return;

        SelectTab(index);
        BringToFront();

        if (_tabs[index].Content is ConversationPage page)
            await page.OpenSessionAsync(sessionId);
    }

    /// <summary>把主窗口从最小化 / 后台叫到前面（点提醒时用）。</summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    // ============================================================
    // 检查更新（标题栏按钮）
    // ============================================================

    private enum UpdateButtonState
    {
        /// <summary>平时：灰色的「检查更新」。</summary>
        Idle,

        /// <summary>正在请求清单。</summary>
        Checking,

        /// <summary>发现新版本：绿色的「立即更新」，点击启动更新程序。</summary>
        UpdateAvailable,

        /// <summary>已经是最新。</summary>
        UpToDate,

        /// <summary>没配地址或检查失败。</summary>
        Problem,
    }

    private UpdateButtonState _updateState = UpdateButtonState.Idle;
    private UpdateCheckResult? _lastCheck;
    private DispatcherTimer? _updateResetTimer;

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        switch (_updateState)
        {
            case UpdateButtonState.UpdateAvailable:
                _ = ApplyUpdateAsync();
                return;

            case UpdateButtonState.Checking:
                return;

            case UpdateButtonState.Problem when string.IsNullOrWhiteSpace(AppSettingsStore.Current.UpdateManifestUrl):
                // 还没配更新地址：直接把人送到「设置」去填，比弹个「请先配置」有用
                OnSettingsClick(sender, e);
                return;

            default:
                _ = CheckForUpdatesAsync(manual: true);
                return;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateState == UpdateButtonState.Checking) return;

        _updateResetTimer?.Stop();

        var url = AppSettingsStore.Current.UpdateManifestUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            SetUpdateState(UpdateButtonState.Problem, "检查更新", "还没有配置更新地址，点击前往「设置 → 常规设置」填写");
            return;
        }

        SetUpdateState(UpdateButtonState.Checking, "检查中…", null);

        var result = await UpdateChecker.CheckAsync(url);
        _lastCheck = result;

        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable:
                SetUpdateState(UpdateButtonState.UpdateAvailable, "立即更新",
                    $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）" +
                    (result.Notes is null ? "" : "\n" + result.Notes));
                break;

            case UpdateStatus.UpToDate:
                SetUpdateState(UpdateButtonState.UpToDate, "已是最新", $"当前已是最新版本 {result.CurrentVersion}");
                ResetUpdateButtonLater();
                break;

            default:
                // 失败原因放 ToolTip 里，手动检查时至少按钮会变成「检查失败」提示用户去看
                SetUpdateState(UpdateButtonState.Problem, "检查更新", "检查更新失败：" + (result.Error ?? "未知原因"));
                if (manual) SetUpdateLabel("检查失败");
                break;
        }
    }

    /// <summary>「立即更新」：交给更新程序接管，然后退出自身。</summary>
    private async Task ApplyUpdateAsync()
    {
        if (_lastCheck is null) return;

        var result = await UpdateLauncher.LaunchAsync(_lastCheck);
        if (result.Started) return;

        // 更新程序还没做出来：如实说明，并让按钮回到「可再次点击」的状态
        SetUpdateLabel("更新程序未接入");
        ToolTip.SetTip(UpdateBtn, result.Message);
        ResetUpdateButtonLater();
    }

    /// <summary>把按钮恢复成当前该有的样子（「已是最新」这类瞬时状态用完要退回去）。</summary>
    private void ResetUpdateButtonLater()
    {
        _updateResetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };

        // 每次都换一份处理器，避免多次点击后累积重复回调
        // 定时器只建一次、只挂一个处理器：反复点击不该累积回调
        if (_updateResetTimer is null)
        {
            _updateResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _updateResetTimer.Tick += (_, _) =>
            {
                _updateResetTimer!.Stop();
                SetUpdateState(
                    _lastCheck?.HasUpdate == true ? UpdateButtonState.UpdateAvailable : UpdateButtonState.Idle,
                    _lastCheck?.HasUpdate == true ? "立即更新" : "检查更新",
                    ToolTip.GetTip(UpdateBtn) as string);
            };
        }

        _updateResetTimer.Stop();
        _updateResetTimer.Start();
    }

    private void SetUpdateState(UpdateButtonState state, string label, string? tip)
    {
        _updateState = state;

        var (glyph, fg, bg) = state switch
        {
            UpdateButtonState.UpdateAvailable => ("\uF151", "#FFFFFF", "#16A34A"),   // Download
            UpdateButtonState.Checking => ("\uF13E", "#9CA3AF", null),              // Refresh
            UpdateButtonState.UpToDate => ("\uF295", "#16A34A", null),              // CheckMark
            UpdateButtonState.Problem => ("\uF13E", "#9CA3AF", null),
            _ => ("\uF13E", "#374151", null),
        };

        UpdateLabel.Text = label;
        UpdateLabel.Foreground = new SolidColorBrush(Color.Parse(fg));
        UpdateIcon.Text = glyph;
        UpdateIcon.Foreground = new SolidColorBrush(Color.Parse(fg));
        UpdateBtn.Background = bg is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(bg));
        UpdateBtn.IsEnabled = state != UpdateButtonState.Checking;
        UpdateBtn.Opacity = state == UpdateButtonState.Checking ? 0.6 : 1;

        ToolTip.SetTip(UpdateBtn, tip);
    }

    private void SetUpdateLabel(string label) => UpdateLabel.Text = label;

    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        var newTabPage = new NewTabPage();
        newTabPage.SmartChatSelected += OnConversationSelected;
        newTabPage.ToolsSelected += OnToolsSelected;
        newTabPage.SkillsSelected += OnSkillsSelected;
        newTabPage.PluginsSelected += OnPluginsSelected;
        newTabPage.McpSelected += OnMcpSelected;
        newTabPage.StatsSelected += OnStatsSelected;
        newTabPage.SubagentsSelected += OnSubagentsSelected;
        AddTab("新标签页", newTabPage);
    }

    /// <summary>新建页的「智能对话」卡片：把占位页替换为统一会话页。</summary>
    private void OnConversationSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SmartChatSelected -= OnConversationSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new ConversationPage(), "智能对话");
        }
    }

    private void OnToolsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.ToolsSelected -= OnToolsSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new ToolsPage(), "工具");
        }
    }

    private void OnSkillsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SkillsSelected -= OnSkillsSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new SkillsPage(), "技能");
        }
    }

    private void OnPluginsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.PluginsSelected -= OnPluginsSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new PluginsPage(), "插件");
        }
    }

    private void OnMcpSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.McpSelected -= OnMcpSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new McpPage(), "MCP服务");
        }
    }

    /// <summary>新建页的「统计」卡片：换成 API 请求流水页。</summary>
    private void OnStatsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.StatsSelected -= OnStatsSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new ApiStatsPage(), "统计");
        }
    }

    private void OnSubagentsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SubagentsSelected -= OnSubagentsSelected;

            var index = _tabs.FindIndex(t => ReferenceEquals(t.Content, newTabPage));
            if (index < 0) return;

            ReplaceTabContent(index, new AgentsPage(), "子 Agent");
        }
    }

    /// <summary>
    /// 添加新标签页
    /// </summary>
    /// <param name="title"></param>
    /// <param name="content"></param>
    private void AddTab(string title, Control content)
    {
        var index = _tabs.Count;
        var (header, titleBlock) = CreateTabHeader(title, index);

        // 容器常驻 Stage。先给 IsVisible=false，SelectTab 会把它打开 ——
        // 这样中途不会闪一下「上一个页面还在这、新页面也在这」的重叠。
        var host = new Border { Child = content, IsVisible = false };

        _tabs.Add(new TabSlot(host, content, header, titleBlock));
        TabStage.Children.Add(host);
        TabHeaderPanel.Children.Insert(TabHeaderPanel.Children.Count - 1, header);

        SelectTab(index);
    }

    /// <summary>
    /// 就地换掉某个标签页的页面（「新标签页」卡片点进去时用）。
    ///
    /// <b>只能改 Host.Child，不能把 Host 从 Stage 里摘了重加</b>：
    /// 摘了重加等于让整棵子树脱离再挂回可视树，嵌着的原生控件会先被销毁再重建 ——
    /// 那正是这次改造要消灭的行为。
    /// </summary>
    private void ReplaceTabContent(int index, Control content, string title)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var slot = _tabs[index];
        slot.Content = content;
        slot.Host.Child = content;   // 旧页面由 Avalonia 自己解除父级
        slot.TitleBlock.Text = title;
    }

    /// <summary>
    /// 插件开标签页的入口（<see cref="HtaciAI.Services.Plugins.PluginTabHost"/> 调这里）。
    ///
    /// 与 <see cref="AddTab"/> 的区别只在返回句柄：插件需要之后改标题、切过来、关掉。
    /// </summary>
    internal PluginTabHandle AddPluginTab(string title, Control content)
    {
        AddTab(title, content);

        // 用 Host 而不是 content 做身份：页面可以被就地替换（ReplaceTabContent），
        // 但 Host 永远不变，所以按 Host 反查下标不会因为换页面而失效。
        var host = _tabs[^1].Host;
        var handle = new PluginTabHandle(this, host, title);
        _pluginTabs.Add(handle);

        // 句柄自己关掉时也要从表里摘掉，否则会一直堆着已经关掉的标签
        handle.Closed += () => _pluginTabs.Remove(handle);

        return handle;
    }

    /// <summary>
    /// 插件标签页的句柄。
    ///
    /// <b>不缓存下标</b>：用户在中间关掉一个标签，后面的全部前移，缓存的旧下标会改到
    /// 别人的标题上。所以每次操作都按<b>常驻容器</b>在 <c>_tabs</c> 里反查当前位置
    /// （不用页面实例：页面会被 <c>ReplaceTabContent</c> 换掉，容器不会）。
    /// </summary>
    internal sealed class PluginTabHandle(MainWindow window, Border host, string title) : IPluginTab
    {
        public string Title { get; private set; } = title;

        public event Action? Closed;

        /// <summary>本句柄对应的常驻容器，MainWindow 用它反查下标。</summary>
        internal Border Host { get; } = host;

        /// <summary>容器还在列表里就说明标签还活着。</summary>
        private int IndexOf() => window._tabs.FindIndex(t => ReferenceEquals(t.Host, host));

        public void SetTitle(string newTitle)
        {
            var index = IndexOf();
            if (index < 0) return;

            Title = newTitle ?? "";
            window._tabs[index].TitleBlock.Text = Title;
        }

        public void Activate()
        {
            var index = IndexOf();
            if (index >= 0) window.SelectTab(index);
        }

        public void Close()
        {
            var index = IndexOf();

            // 第 0 个是「智能对话」，RemoveTab 也拦着不让关 —— 插件不该造出第二个首页
            if (index <= 0) return;

            window.RemoveTab(index);
        }

        /// <summary>由 <see cref="MainWindow.RemoveTab"/> 在标签真的消失后调用。</summary>
        internal void NotifyClosed() => Closed?.Invoke();
    }
    /// <summary>
    /// 创建标签页头部
    /// </summary>
    /// <param name="title"></param>
    /// <param name="index"></param>
    /// <returns></returns>
    private (Border Header, TextBlock TitleBlock) CreateTabHeader(string title, int index)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0),
            FontSize = 12,
        };

        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\u00d7",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, -1, 0, 0)
            },
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            Width = 0,
            Height = 16,
            CornerRadius = new Avalonia.CornerRadius(12),
            Padding = new Avalonia.Thickness(0),
            Margin = new Avalonia.Thickness(0),
            Tag = index
        };
        closeBtn.Click += OnTabCloseClick;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { titleBlock, closeBtn }
        };
        Grid.SetColumn(closeBtn, 1);

        var border = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Avalonia.Thickness(5),
            CornerRadius = new Avalonia.CornerRadius(10),
            Tag = index,
            Child = headerGrid,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            BorderThickness = new Avalonia.Thickness(0)
        };

        border.PointerEntered += (s, e) =>
        {
            if (index > 0)
            {
                closeBtn.Width = 16;
                closeBtn.Margin = new Avalonia.Thickness(6, 0, 6, 0);
                titleBlock.Margin = new Avalonia.Thickness(10, 0, 2, 0);
                closeBtn.Background = Brushes.Transparent;
                closeBtn.Foreground = Brushes.Gray;
            }

            if ((int)border.Tag != _selectedIndex)
            {
                border.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
            }
        };

        border.PointerExited += (s, e) =>
        {
            if (index > 0)
            {
                closeBtn.Width = 0;
                closeBtn.Margin = new Avalonia.Thickness(0);
                titleBlock.Margin = new Avalonia.Thickness(10, 0);
                closeBtn.Background = Brushes.Transparent;
                closeBtn.Foreground = Brushes.Gray;
            }

            if ((int)border.Tag != _selectedIndex)
            {
                border.Background = Brushes.Transparent;
            }
        };

        border.PointerPressed += OnTabHeaderClick;
        return (border, titleBlock);
    }

    /// <summary>
    /// 关闭标签页
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
        {
            if (index == 0) return;
            RemoveTab(index);
            e.Handled = true;
        }
    }

    private void RemoveTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        // 先通知插件句柄：它需要在「容器还在列表里」的时候把 Closed 发出去。
        // 反过来（先删再通知）会让句柄反查不到自己，事件就发不出来了。
        var closing = _tabs[index];
        var closingHandle = _pluginTabs.FirstOrDefault(h => ReferenceEquals(h.Host, closing.Host));
        if (closingHandle is not null)
        {
            _pluginTabs.Remove(closingHandle);
            closingHandle.NotifyClosed();
        }

        // Stage 与标签栏都必须按同一个下标删，删完两边顺序仍然一致 ——
        // 这依赖「加的时候都是 Append」这个不变量。若以后有人改成中间插入，
        // 这里要跟着变成 Insert，否则内容会整段错位。
        TabStage.Children.RemoveAt(index);
        TabHeaderPanel.Children.RemoveAt(index);
        _tabs.RemoveAt(index);

        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].Header.Tag = i;

        if (_selectedIndex > index) _selectedIndex--;
        else if (_selectedIndex >= _tabs.Count) _selectedIndex = _tabs.Count - 1;

        SelectTab(_selectedIndex);
    }

    private void OnTabHeaderClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border header && header.Tag is int index)
            SelectTab(index);
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        for (int i = 0; i < _tabs.Count; i++)
        {
            // 关键：只切 IsVisible。
            //
            // NativeControlHost 的 UpdateHost 里只有这一条分支会走 HideWithSize
            // （隐藏、但内核与原生子窗口都留着）；被移出可视树则会 DestroyNativeControl，
            // 也就是 WebView2 的 controller 被关、渲染进程回收、页面状态全丢。
            // 换成 Opacity 或挪出屏幕都不行：前者仍参与布局与命中测试、原生窗口照样显示，
            // 后者会让所有页面继续布局绘制。
            _tabs[i].Host.IsVisible = i == index;

            var header = _tabs[i].Header;
            if (i == index)
            {
                header.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
                header.BorderBrush = new SolidColorBrush(Color.Parse("#D9DDE3"));
                header.BorderThickness = new Avalonia.Thickness(1);
                header.BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0, OffsetY = 1, Blur = 2, Spread = 0,
                    Color = Color.FromArgb(30, 0, 0, 0)
                });
            }
            else
            {
                header.Background = Brushes.Transparent;
                header.BorderBrush = null;
                header.BorderThickness = new Avalonia.Thickness(0);
                header.BoxShadow = default;
            }
        }

        _selectedIndex = index;
    }

    private int GetSelectedIndex() => _selectedIndex;

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var index = _tabs.FindIndex(t => t.Content is SettingsPage);
        if (index >= 0)
        {
            SelectTab(index);
        }
        else
        {
            AddTab("设置", new SettingsPage());
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    /// <summary>最小化窗口。</summary>
    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>最大化 / 还原窗口（点一下在两种状态之间切换）。</summary>
    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Form_OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) this.BeginMoveDrag(e);
    }
}
