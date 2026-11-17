using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>一次提交：正文（占位字符已展开成真实路径） + 附件列表。</summary>
/// <param name="Text">发给模型的正文。附件位置上是绝对路径。</param>
/// <param name="Attachments">随正文一起发出的附件。</param>
/// <param name="Steer">Ctrl+Enter 提交：任务进行中是要「立刻引导」，而不是排在队尾等任务结束。</param>
public sealed record InputSubmit(string Text, IReadOnlyList<ChatAttachment> Attachments, bool Steer = false);

/// <summary>提示行的语义色。</summary>
public enum InputHintKind
{
    Info,
    Ok,
    Fail,
}

/// <summary>
/// 会话输入区：输入框 + 工具栏 + 提示行。新建页与对话页共用同一份。
///
/// <b>本控件不认识自己在哪个页面</b>，会话相关的差异全部由宿主的可选输入表达：
/// <list type="bullet">
///   <item><see cref="DraftStore"/> 可空 —— 新建页还没有会话可挂，草稿就不落库。</item>
///   <item><see cref="SetRunning"/> 由宿主调 —— 新建页不发请求，所以不调，
///         「停止」形态自然永远不会出现，控件里不需要为此写分支。</item>
///   <item><see cref="SetContextUsage"/> 传 null 即隐藏用量环。</item>
///   <item><see cref="MoreItems"/> 由宿主注入 —— 「压缩上下文」需要历史，新建页不注入。</item>
/// </list>
///
/// 附件这块是「正文里放占位字符、路径另存」：路径往往是
/// <c>C:\Users\...\很长的一串.png</c>，直接写进输入框会把句子冲散，用户也没法调整它的位置。
/// 占位字符由 <see cref="ChatInputEditor"/> 画成一张真卡片，长得和消息气泡里的附件一样，
/// 所以框里看到的顺序就是发出去的顺序。
/// 「第几个占位字符对应哪个文件」由 <see cref="AttachmentDraft"/> 负责（纯逻辑、可单测），
/// 本类只负责把它接到 Avalonia 的文本与剪贴板事件上。
/// </summary>
public partial class ChatInputControl : UserControl
{
    // ---- 提示配色（与提示行的三种语义对应） ----

    private static readonly IBrush HintInfo = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush HintOk = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush HintFail = new SolidColorBrush(Color.Parse("#DC2626"));

    /// <summary>待发送附件。</summary>
    private readonly AttachmentDraft _draft = new();

    /// <summary>
    /// 草稿写入是否在进行中；<see cref="_draftPending"/> 表示期间又有新输入，写完要补一轮。
    /// </summary>
    private bool _draftSaving;
    private bool _draftPending;

    /// <summary>
    /// 上次落库的草稿内容。用它判断「这次 TextChanged 是不是真的改动」—— 见 <see cref="SaveDraftAsync"/>。
    /// </summary>
    private string? _lastSavedDraft;

    private DispatcherTimer? _hintTimer;

    private bool _running;
    private PermissionMode _permissionMode = PermissionMode.Normal;

    /// <summary>无参构造：供 XAML 运行时加载器使用。</summary>
    public ChatInputControl()
    {
        InitializeComponent();

        // 全部按键处理都走隧道阶段：必须早于编辑器自己的类处理器。
        // 两件事都依赖这个顺序，见 OnInputKeyDownTunnel 的说明。
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDownTunnel, RoutingStrategies.Tunnel);

        // 拖放同样走隧道：AvaloniaEdit 自己也处理拖放（拖文本），
        // 文件拖进来时若被它先吃掉，就只会插入一串路径文字而不会变成附件。
        DragDrop.SetAllowDrop(InputBox, true);
        InputBox.AddHandler(DragDrop.DropEvent, (EventHandler<DragEventArgs>)OnInputDrop, RoutingStrategies.Tunnel);

        InputBox.TextChanged += (_, _) =>
        {
            // 采纳标记时我们自己会改一次文本，那一次不该再往下走（否则递归）。
            if (_adoptingMarkers) return;

            // 粘贴进来的文本可能带着附件标记（如从别的消息里复制的一段）。
            // 这一步把标记换成占位字符并登记成附件，于是正文里直接出现卡片。
            AdoptPastedMarkers();

            // 用户手工改了正文（删掉一段、撤销）之后，正文里已经没有对应占位字符的附件
            // 要从草稿里清掉，否则卡片栏会显示一个正文里根本没有的附件。
            if (_draft.Prune(InputBox.Text)) RefreshAttachmentBar();
            // 运行中框里出现内容，那颗键就从「停止」变回「发送」
            RefreshSendGlyph();
            _ = SaveDraftAsync();
        };

        MenuTheme.Apply(PermModeBtn.Flyout as MenuFlyout);
        MenuTheme.Apply(MoreMenu);

        // 点输入区外面 = 输入框失焦。编辑器不会因为点了别处就自动掉焦点，
        // 于是它的蓝色聚焦描边会一直亮着，看起来像还在编辑。
        AttachedToVisualTree += (_, _) =>
        {
            HookOutsideClick();
            HookPluginToolbar();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnhookOutsideClick();
            UnhookPluginToolbar();
        };

        LoadTools();
        SetupPermissionMode();

        // 三个选择器都在菜单打开时重读一次各自的清单 —— 工具 / 技能 / MCP 服务是用户随时
        // 会在设置页里改的东西，只在构造时注入一次的话，改完回到会话还是旧列表
        // （模型选择器早就是这么做的，见它自己的 RefreshRequested）。
        // 接在这里而不是各个宿主里：新建页与对话页共用这一个控件，接一处两边都对。
        ToolSelector.RefreshRequested += (_, _) => LoadTools();
        SkillSelector.RefreshRequested += (_, _) => LoadSkills();
        McpSelector.RefreshRequested += (_, _) => _ = LoadMcpServersAsync();
        KbSelector.RefreshRequested += (_, _) => _ = LoadKnowledgeBasesAsync();
    }

    // ============================================================
    // 点外面让输入框失焦
    // ============================================================

    private TopLevel? _outsideTopLevel;

    private void HookOutsideClick()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || ReferenceEquals(top, _outsideTopLevel)) return;

        UnhookOutsideClick();
        _outsideTopLevel = top;
        top.AddHandler(InputElement.PointerPressedEvent, OnOutsidePointerPressed, RoutingStrategies.Tunnel);
    }

    private void UnhookOutsideClick()
    {
        if (_outsideTopLevel is null) return;
        _outsideTopLevel.RemoveHandler(InputElement.PointerPressedEvent, OnOutsidePointerPressed);
        _outsideTopLevel = null;
    }

    private void OnOutsidePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(InputShell);
        if (point.X >= 0 && point.Y >= 0 && point.X <= InputShell.Bounds.Width && point.Y <= InputShell.Bounds.Height)
            return;   // 点在输入框 / 工具条上：那是它自己的交互，别抢焦点

        // 延后一拍：焦点是隧道阶段由 FocusManager 分配的，当场清会被随后那次赋值盖掉
        Dispatcher.UIThread.Post(() =>
        {
            if (InputBox.IsInputFocused) TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        }, DispatcherPriority.Background);
    }

    // ============================================================
    // 插件工具栏（插槽 conversation.input.toolbar）
    // ============================================================

    private IDisposable? _pluginToolbarSubscription;

    private string? _sessionId;

    /// <summary>
    /// 本输入框属于哪个会话。<b>新建会话页上为 null</b>（还没有会话）。
    ///
    /// 它只做一件事：填插件工具栏时传给 <see cref="PluginSlotContext"/>，
    /// 让插件知道「我这个按钮是哪个会话的」。会话页在装配时设一次即可。
    /// </summary>
    public string? SessionId
    {
        get => _sessionId;
        set
        {
            if (string.Equals(_sessionId, value, StringComparison.Ordinal)) return;

            _sessionId = value;

            // 已经挂在界面上的旧控件带的是旧会话 id（或者根本没有），必须重建：
            // 会话 id 变了而控件还是那一份，插件会继续按上一个会话显示勾选状态。
            if (_pluginToolbarSubscription is not null) RefreshPluginToolbar();
        }
    }

    /// <summary>
    /// 把插槽里的插件控件填进工具栏。
    ///
    /// 插件可以在这里放任意控件，<b>宿主只负责摆进去</b>：放一个
    /// <c>Button</c> 再给它挂 <c>Flyout</c>，就是一个带自定义菜单的入口。
    /// 插件崩了也只坏一格（<see cref="PluginControlHost"/> 会接住构造期异常）。
    /// </summary>
    private void RefreshPluginToolbar()
    {
        PluginSlotPanel.Fill(PluginToolbarSlot, PluginSlots.ConversationInputToolbar, _sessionId);

        foreach (var child in PluginToolbarSlot.Children)
            child.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        // 没有插件内容就整块隐藏，分隔线一起收：留一条孤零零的竖线像是界面出了问题
        PluginToolbarDivider.IsVisible = PluginToolbarSlot.Children.Count > 0;
    }

    private void HookPluginToolbar()
    {
        if (_pluginToolbarSubscription is not null) return;

        RefreshPluginToolbar();
        _pluginToolbarSubscription = PluginSlotPanel.Subscribe(RefreshPluginToolbar);
    }

    private void UnhookPluginToolbar()
    {
        _pluginToolbarSubscription?.Dispose();
        _pluginToolbarSubscription = null;
    }

    // ============================================================
    // 对外事件
    // ============================================================

    /// <summary>用户提交（点发送或回车）。附件已展开成真实路径。</summary>
    public event EventHandler<InputSubmit>? SendRequested;

    /// <summary>运行中再点一次发送按钮 = 请求中止。</summary>
    public event EventHandler? StopRequested;

    /// <summary>权限模式变化。</summary>
    public event EventHandler<PermissionMode>? PermissionModeChanged;

    // ============================================================
    // 子控件（供宿主持久化各自的存档）
    // ============================================================

    // 这些选择器本来就是独立可复用控件（各自带 Selected* 属性与 SelectionChanged 事件），
    // 这里如实透出而不是把它们的值再包一层：宿主原有的「读初值 / 存改动」接线因此只需要
    // 加个前缀，改动面小、风险低。XAML 里的字段名（ModelPicker 等）刻意与属性名错开，
    // 否则生成的字段会与本属性同名冲突。
    public ModelSelectorControl ModelSelector => ModelPicker;
    public ToolSelectorControl ToolSelector => ToolPicker;
    public SkillSelectorControl SkillSelector => SkillPicker;
    public McpSelectorControl McpSelector => McpPicker;
    public KnowledgeBaseSelectorControl KbSelector => KbPicker;

    /// <summary>
    /// 排队中那一栏。队列本身归 runtime（视图重建后还在），这里只负责显示与转发点击。
    /// </summary>
    public PendingQueueControl Pending => PendingQueue;

    // ============================================================
    // 输入框
    // ============================================================

    /// <summary>当前正文（含占位字符）。</summary>
    public string Text
    {
        get => InputBox.Text;
        set
        {
            InputBox.Text = value ?? "";
            InputBox.CaretIndex = (value ?? "").Length;
        }
    }

    /// <summary>把焦点交给输入框（切到某个会话时用）。</summary>
    public void FocusInput() => InputBox.FocusInput();

    /// <summary>清空正文与附件。发送之后调用。</summary>
    public void Clear()
    {
        InputBox.Text = "";
        _draft.Clear();
        RefreshAttachmentBar();
    }

    /// <summary>已附加的文件数（宿主用来判断空消息该不该发）。</summary>
    public int AttachmentCount => _draft.Count;

    // ============================================================
    // 宿主驱动的状态
    // ============================================================

    /// <summary>
    /// 运行态：不再无条件变成「停止」—— 这颗键表达的是「按下会发生什么」。
    ///
    /// 运行中框里有字时仍然是发送（排进队列 / 作为引导插入），只有框里空着才是停止。
    /// 原先一概切成停止，导致任务跑着的时候没法把下一句排队：想发就得先点停止。
    /// </summary>
    public void SetRunning(bool running)
    {
        _running = running;

        LoadingRing.IsVisible = running;

        // 运行中不许再压缩：压缩要在「这一轮发出去之前」定下上下文，
        // 中途插一条只会让本轮上下文前后不一致（请求已带旧历史发出，压缩记录却已落库）。
        foreach (var item in MoreItems) item.IsEnabled = !running;

        RefreshSendGlyph();
    }

    /// <summary>
    /// 重算发送键的形态与配色。判据只有一条：此刻按下去是发还是停。
    /// 运行中 + 框里有内容 → 发送（蓝）；运行中 + 框里空 → 停止（红）；没在跑 → 发送。
    /// </summary>
    private void RefreshSendGlyph()
    {
        var asStop = _running && IsBlank();

        SendGlyph.IsVisible = !asStop;
        StopGlyph.IsVisible = asStop;

        var brush = new SolidColorBrush(Color.Parse(asStop ? "#DC2626" : "#4A90D9"));
        SendBtn.Background = brush;
        SendBtn.BorderBrush = brush;

        ToolTip.SetTip(SendBtn, asStop ? "停止" : _running ? "排队发送（Ctrl+Enter 立即插入本轮）" : "发送");
    }

    /// <summary>框里没有任何要发的东西（纯附件也算有 —— 只发一个文件是合法操作）。</summary>
    private bool IsBlank() => string.IsNullOrWhiteSpace(InputBox.Text) && _draft.IsEmpty;

    /// <summary>首个正文 / 思考分片到达：请求已进入输出阶段，收起等待指示。</summary>
    public void MarkFirstOutput() => LoadingRing.IsVisible = false;

    /// <summary>上下文用量环。任一参数为 null（或上限非正）则隐藏 —— 新建页没有历史，就是这种情况。</summary>
    public void SetContextUsage(long? used, long? total, string displayName)
    {
        if (used is not { } u || total is not { } t || t <= 0)
        {
            UsageRing.Clear();
            return;
        }

        UsageRing.SetUsage(u, t, displayName);
    }

    /// <summary>在输入框旁边显示一条提示。<paramref name="autoHide"/> 为 true 时几秒后自动消失。</summary>
    public void ShowHint(string text, InputHintKind kind, bool autoHide)
    {
        HintText.Text = text;
        HintText.Foreground = kind switch
        {
            InputHintKind.Ok => HintOk,
            InputHintKind.Fail => HintFail,
            _ => HintInfo,
        };
        HintText.IsVisible = true;

        _hintTimer?.Stop();
        if (!autoHide) return;

        // 定时器自己会停 —— 运行中的 DispatcherTimer 会吊住整个页面。
        _hintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hintTimer.Tick -= OnHintTick;
        _hintTimer.Tick += OnHintTick;
        _hintTimer.Start();
    }

    /// <summary>手动收起提示（如「正在加载历史」结束后立刻清掉，不必等定时器）。</summary>
    public void HideHint()
    {
        _hintTimer?.Stop();
        HintText.IsVisible = false;
    }

    private void OnHintTick(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        HintText.IsVisible = false;
    }

    // ============================================================
    // 更多菜单（宿主注入）
    // ============================================================

    /// <summary>
    /// 「更多功能」里的菜单项。由宿主注入 —— 需要会话历史的功能（压缩上下文）只有宿主知道能不能用。
    /// 注入的项在运行中会被自动禁用。
    /// </summary>
    public IList<MenuItem> MoreItems { get; } = new List<MenuItem>();

    /// <summary>
    /// 新增一个菜单项并挂上触发动作。
    ///
    /// 菜单（<see cref="MenuFlyout"/>）在这里按需装配，而不是写进 XAML：Flyout 内部的元素
    /// 不在本控件的命名域里，给它的项写 x:Name 拿不到字段。宿主一个项都不注入时按钮保持无菜单，
    /// 点它不会有任何反应（新建页就是这种情况 —— 它没有历史可压）。
    /// </summary>
    public void AddMoreItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header, IsEnabled = !_running };
        item.Click += (_, _) => onClick();
        MoreItems.Add(item);

        MoreMenu.Items.Add(item);
        MoreBtn.Flyout = MoreMenu;
        ToolTip.SetTip(MoreBtn, "更多功能");
    }

    /// <summary>「更多功能」的菜单，随第一个注入项一起挂到按钮上。</summary>
    private MenuFlyout MoreMenu { get; } = new MenuFlyout();

    // 说明：附件按钮没有做下拉菜单。粘贴走 Ctrl+V（含截图直接粘贴），
    // 上传走按钮本身 —— 再套一层菜单只是多一个会失效的入口。

    // ============================================================
    // 权限模式
    // ============================================================

    /// <summary>当前权限审批档位。</summary>
    public PermissionMode PermissionMode
    {
        get => _permissionMode;
        set
        {
            _permissionMode = value;
            ApplyPermissionMode();
        }
    }

    private MenuFlyout? PermMenu => PermModeBtn.Flyout as MenuFlyout;

    private void SetupPermissionMode()
    {
        if (PermMenu is not { } menu) return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Tag is not string tag) continue;
            mi.ToggleType = MenuItemToggleType.CheckBox;
            mi.Click += (_, _) => SetPermissionMode(ParsePermissionMode(tag));
        }

        ApplyPermissionMode();
    }

    private void SetPermissionMode(PermissionMode mode)
    {
        _permissionMode = mode;
        ApplyPermissionMode();
        PermissionModeChanged?.Invoke(this, mode);
    }

    private void ApplyPermissionMode()
    {
        if (PermMenu is { } menu)
            foreach (var item in menu.Items)
                if (item is MenuItem mi && mi.Tag is string tag)
                    mi.IsChecked = ParsePermissionMode(tag) == _permissionMode;

        PermModeIcon.Foreground = new SolidColorBrush(Color.Parse(_permissionMode switch
        {
            PermissionMode.Strict => "#DC2626",  // 红
            PermissionMode.Normal => "#4A90D9",  // 蓝
            PermissionMode.Loose => "#D97706",   // 橙
            PermissionMode.Free => "#16A34A",    // 绿
            _ => "#9CA3AF",
        }));

        // 档位名直接写在按钮上：只给一个图标时，用户要先点开才知道现在是哪一档
        PermModeLabel.Text = _permissionMode switch
        {
            PermissionMode.Strict => "严格",
            PermissionMode.Normal => "普通",
            PermissionMode.Loose => "宽松",
            PermissionMode.Free => "自由",
            _ => "普通",
        };

        ToolTip.SetTip(PermModeBtn, _permissionMode switch
        {
            PermissionMode.Strict => "权限模式：严格（所有工具需确认）",
            PermissionMode.Normal => "权限模式：普通（安全工具自动通过）",
            PermissionMode.Loose => "权限模式：宽松（安全/风险工具自动通过）",
            PermissionMode.Free => "权限模式：自由（所有工具免确认）",
            _ => "",
        });
    }

    private static PermissionMode ParsePermissionMode(string tag) => tag switch
    {
        "Strict" => PermissionMode.Strict,
        "Normal" => PermissionMode.Normal,
        "Loose" => PermissionMode.Loose,
        "Free" => PermissionMode.Free,
        _ => PermissionMode.Normal,
    };

    // ============================================================
    // 工具 / 技能 / MCP / 模型
    // ============================================================

    /// <summary>把注册表里的工具集注入工具选择器。</summary>
    public void LoadTools()
    {
        var registry = ToolRegistry.Instance;
        ToolSelector.Toolsets = registry.GetToolsets();
        ToolSelector.Tools = registry.GetEnabled();
    }

    /// <summary>把已启用的技能注入技能选择器。</summary>
    public void LoadSkills() => SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();

    /// <summary>
    /// 把知识库列表注入选择器。当前选中值由宿主通过 <see cref="SetKnowledgeBaseId"/> 设，
    /// 这里只负责清单本身。
    /// </summary>
    public async Task LoadKnowledgeBasesAsync()
    {
        try
        {
            KbSelector.Libraries = await KnowledgeBaseRepository.GetAllAsync();
        }
        catch
        {
            // 数据库未就绪时保持空列表，选择器显示「知识库」
        }
    }

    /// <summary>设置当前会话用的知识库（不触发会话落库，用于回显）。</summary>
    public void SetKnowledgeBaseId(string? libraryId) => KbSelector.SetSelected(libraryId);

    /// <summary>把 MCP 服务注入选择器（含未启动的：勾选后发送时自动启动）。</summary>
    public async Task LoadMcpServersAsync()
    {
        try
        {
            McpSelector.Servers = await Data.McpServerRepository.GetAllAsync();
        }
        catch
        {
            // 数据库未就绪时保持空列表，选择器显示「暂无 MCP 服务」
        }
    }

    /// <summary>
    /// 没有可用模型就禁用发送并说明原因。
    /// Opacity 得自己给：Button 禁用不会自动变灰，看起来还是能点的样子。
    /// </summary>
    public void RefreshSendAvailability(string? hintWhenUnavailable = null)
    {
        var ready = ModelSelector.SelectedModel is not null;
        SendBtn.IsEnabled = ready;
        SendBtn.Opacity = ready ? 1 : 0.45;
        ToolTip.SetTip(SendBtn, ready ? null : hintWhenUnavailable);
    }

    // ============================================================
    // 发送
    // ============================================================

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        // 运行中且框里空着：这颗键此刻是「停止」
        if (_running && IsBlank())
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Submit();
    }

    /// <summary>
    /// 提交当前内容。空文本且无附件时不发（纯附件也应该能发出去，所以两个条件都要看）。
    /// </summary>
    public void Submit() => Submit(steer: false);

    /// <param name="steer">
    /// Ctrl+Enter：任务进行中希望<b>立刻</b>插进当前这一轮，而不是等它跑完。没有任务在跑时无区别。
    /// </param>
    public void Submit(bool steer)
    {
        // 首尾空白在这里去掉。用户粘贴进来的文本可能自带尾部换行。
        // 只规范化本地这份「要发出去的文本」，<b>不动输入框里的内容</b> ——
        // 发送失败时输入框要原样保留，而草稿（占位字符 ↔ 路径）必须继续与框内文本对得上。
        // 占位字符不是空白字符，Trim 不会把它去掉。
        var raw = InputBox.Text.Trim();

        if (raw.Length == 0 && _draft.IsEmpty) return;

        // 发给模型的是真实路径，不是占位字符
        var text = _draft.Expand(raw);
        var attachments = _draft.Items.Select(i => i.Info).ToList();

        SendRequested?.Invoke(this, new InputSubmit(text, attachments, steer));
    }

    // ============================================================
    // 附件
    // ============================================================

    /// <summary>弹出文件选择器并附加所选文件。</summary>
    private async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要附加的文件",
                AllowMultiple = true,
            });

            AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
        }
        catch
        {
            // 选择器不可用时不做处理
        }
    }

    /// <summary>采纳标记期间置位，避免「改文本 → 触发 TextChanged → 再采纳」的递归。</summary>
    private bool _adoptingMarkers;

    /// <summary>
    /// 把正文里的附件标记（<c>[agent-file:///…]</c>）换成占位字符并登记为附件。
    ///
    /// 放在 TextChanged 里而不是只挂在粘贴事件上：这样手打的标记、拖进来的文本、
    /// 以及撤销恢复出来的标记都能被同一段逻辑处理，不必为每个入口各写一遍
    /// （那正是「两处实现只改一处」的老毛病）。
    /// </summary>
    private void AdoptPastedMarkers()
    {
        var text = InputBox.Text;
        if (!AttachmentMarker.Contains(text)) return;

        if (!_draft.TryAdoptMarkers(text, out var edit)) return;

        _adoptingMarkers = true;
        try
        {
            InputBox.ReplaceText(edit.Text, edit.Caret);
        }
        finally
        {
            _adoptingMarkers = false;
        }

        RefreshAttachmentBar();
    }

    /// <summary>附加一批文件（路径已解析好的除外）。供宿主在外部拖放等场景调用。</summary>
    public void AddAttachments(IEnumerable<ChatAttachment?> items)
    {
        var added = false;

        foreach (var item in items)
        {
            if (item is null) continue;

            // TryAdd 内部会去重并同时给出新正文与新光标；顺序由它保证，
            // 调用方拿到结果后只赋一次文本，不会出现「登记后被自己的清理逻辑当孤儿删掉」。
            if (!_draft.TryAdd(item, InputBox.Text, InputBox.CaretIndex, out var edit)) continue;

            InputBox.ReplaceText(edit.Text, edit.Caret);
            added = true;
        }

        if (added) RefreshAttachmentBar();
    }

    /// <summary>
    /// 卡片控件缓存：<see cref="AttachmentDraft.Item"/> → 那张卡片。
    ///
    /// 必须缓存实例而不能每次重建：内联控件滚出视口会被摘出视觉树、滚回来再挂回去，
    /// 每次 new 一个的话，卡片里的悬停态与按钮状态每次滚动都要丢一次。
    /// </summary>
    private readonly Dictionary<AttachmentDraft.Item, AttachmentChip> _chips = new();

    /// <summary>兜底卡片（见 <see cref="OrphanChip"/>）：序号 → 控件。</summary>
    private readonly Dictionary<int, Control> _orphans = new();

    /// <summary>
    /// 输入框里附件的行内高度。取 18 而不是 <see cref="AttachmentChip"/> 的 19：
    /// 14px 正文的行高约 21.6，卡片比行高只高一点点时，靠下面的基线把它压回行内。
    /// </summary>
    private const double InlineChipHeight = 18;

    /// <summary>
    /// 卡片的行内基线：卡片底边在基线上方多少像素（即 <c>TextBlock.BaselineOffset</c>）。
    ///
    /// 这一项决定带卡片的那一行有多高。AvaloniaEdit 的 <c>InlineObjectRun.Baseline</c> 取
    /// <c>TextBlock.GetBaselineOffset(Element)</c>，取不到时才用控件高度；而 Avalonia 12 里
    /// 这个附加属性在没设置时返回 0 而不是 NaN —— 于是卡片整个落在基线以下，
    /// 行高变成「文字高 15.1 + 卡片高 18」= 33，比普通文字行高出半行。
    ///
    /// 对齐到文字基线（14px 正文约 15）后，行高回到 21.6，卡片顶和文字顶齐平，
    /// 底边探进下缘的内边距里，看起来就是同一行里的一个标签。
    /// </summary>
    private const double InlineChipBaseline = 15;

    /// <summary>
    /// 把草稿里的附件接到正文的占位字符上：正文里第 n 个占位字符显示第 n 张卡片。
    ///
    /// 卡片本身直接复用消息气泡里那个 <see cref="AttachmentChip"/>（compact 形态），
    /// 于是输入框预览与发出去之后的样子是同一个控件，不会出现「框里长这样、发出去变那样」。
    /// </summary>
    private void RefreshAttachmentBar()
    {
        // 已经不在草稿里的卡片从缓存里清掉，否则反复增删附件会让缓存无限长
        foreach (var stale in _chips.Keys.Where(k => !_draft.Items.Contains(k)).ToList())
            _chips.Remove(stale);

        // 兜底卡片按「第几个占位字符」缓存，删掉一个之后所有序号都会变，只能整批丢掉重建
        _orphans.Clear();

        InputBox.SetWidgetProvider(index =>
            index >= 0 && index < _draft.Count ? ChipFor(_draft.Items[index]) : OrphanChip(index));

        InputBox.Redraw();

        // 只贴了个文件、一个字没打，也算「有东西要发」：那颗键该从停止变回发送
        RefreshSendGlyph();
    }

    /// <summary>
    /// 正文里有占位字符、草稿里却已经没有对应附件时显示的兜底卡片。
    ///
    /// 正常情况下走不到这里：<see cref="AttachmentDraft"/> 的删除记录会把撤销/重做放回来的
    /// 占位字符接回原附件。只有删除记录也丢了（撤得很远、或正文被外部改过）才会命中。
    /// 与其让编辑器把占位字符画成 <c>[OBJ]</c> 这种字面量，不如给一张点得掉的灰卡片。
    /// </summary>
    private Control OrphanChip(int index)
    {
        if (_orphans.TryGetValue(index, out var cached)) return cached;

        var chip = new Border
        {
            Height = InlineChipHeight,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E1E5EA")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "附件已失效",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#9AA3AF")),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        ToolTip.SetTip(chip, "这条卡片对应的附件已经不在草稿里，点一下把它删掉");
        TextBlock.SetBaselineOffset(chip, InlineChipBaseline);
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            DropOrphan(index);
        };

        _orphans[index] = chip;
        return chip;
    }

    /// <summary>删掉正文里第 <paramref name="index"/> 个占位字符（兜底卡片点一下时用）。</summary>
    private void DropOrphan(int index)
    {
        var text = InputBox.Text;
        var seen = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != AttachmentDraft.Token) continue;
            if (seen++ != index) continue;

            InputBox.ReplaceText(text.Remove(i, 1), i);
            RefreshAttachmentBar();
            return;
        }
    }

    private AttachmentChip ChipFor(AttachmentDraft.Item item)
    {
        if (_chips.TryGetValue(item, out var chip)) return chip;

        chip = new AttachmentChip(item.Info, () => RemoveAttachment(item), compact: true)
        {
            // 卡片是内联对象，两侧没有文本间隔；留 1px 免得和相邻文字贴死
            Margin = new Thickness(1, 0, 1, 0),

            // 气泡里 19 刚好，输入框里得跟着正文行高走，否则这一行会被撑高
            Height = InlineChipHeight,
        };

        // 对齐文字基线，行高才不会因为卡片多出半行（见 InlineChipBaseline）
        TextBlock.SetBaselineOffset(chip, InlineChipBaseline);

        _chips[item] = chip;
        return chip;
    }

    private void RemoveAttachment(AttachmentDraft.Item item)
    {
        var edit = _draft.Remove(item, InputBox.Text, InputBox.CaretIndex);
        InputBox.ReplaceText(edit.Text, edit.Caret);
        RefreshAttachmentBar();
    }

    /// <summary>
    /// 粘贴：剪贴板里是文件（含截图工具直接放进去的位图）就转成附件，
    /// 否则交回编辑器走它自己的文本粘贴。
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            InputBox.Paste();
            return;
        }

        try
        {
            // 资源管理器里复制的文件：本来就有路径，直接用
            var files = (await clipboard.TryGetFilesAsync())?.ToList();
            if (files is { Count: > 0 })
            {
                AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
                return;
            }

            // 截图工具直接塞进剪贴板的位图：没有路径，落成缓存文件
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using var buffer = new MemoryStream();
                bitmap.Save(buffer);   // Avalonia 默认存 PNG
                var attachment = await AttachmentStore.SaveClipboardImageAsync(buffer.ToArray());
                if (attachment is not null)
                {
                    AddAttachments(new[] { attachment });
                    return;
                }
            }
        }
        catch
        {
            // 剪贴板读取失败就退回普通文本粘贴
        }

        InputBox.Paste();
    }

    /// <summary>
    /// 输入框的隧道阶段按键。三件事都必须在编辑器自己的类处理器之前截下来：
    ///
    /// <list type="bullet">
    ///   <item><b>回车发送</b> —— 编辑器会在自己的 OnKeyDown 里先把换行插进正文。
    ///     若把「回车就发送」注册在冒泡阶段（更靠后），读到的文本末尾
    ///     已经多了个换行，发出去的消息气泡里就会多一个空行。
    ///     注意 Avalonia 里<b>类处理器先于同元素的实例处理器</b>执行，所以哪怕加
    ///     <c>handledEventsToo: true</c> 也拦不住它 —— 只有隧道阶段够早。</item>
    ///   <item><b>粘贴（Cmd+V / Ctrl+V）</b> —— 否则剪贴板里的文件/位图已经被当成文本
    ///     处理掉了（表现就是粘贴图片没反应）。修饰键随平台，见 <see cref="IsPasteGesture"/>。</item>
    ///   <item><b>Backspace / Delete</b> —— 光标紧邻一张卡片时删掉它，并把草稿里对应的那条
    ///     一起移除（编辑器自己删也能删掉字符，但草稿会留着一条没有正文的附件）。</item>
    /// </list>
    /// </summary>
    private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        // 回车：发送。Shift+Enter 故意不接管，让它落到编辑器默认行为上（换行）。
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // 置 Handled 就阻止了编辑器插入换行 —— 这正是本分支存在的意义。
            e.Handled = true;

            // 运行中回车不当作「停止」：那颗按钮的语义切换只对鼠标点击成立，
            // 键盘上误按回车就中止一轮生成太容易发生了。
            //
            // 运行中回车现在有意义了：排队。是否真的排队由宿主判断（它才知道有没有任务在跑），
            // 控件这边只负责把 Ctrl 的意图带出去。
            Submit(steer: e.KeyModifiers.HasFlag(KeyModifiers.Control));
            return;
        }

        if (InputGestures.IsPaste(e))
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;

        if (e.Key == Key.Back)
        {
            if (_draft.TryDeleteBefore(InputBox.Text, InputBox.CaretIndex,
                    InputBox.SelectionStart, InputBox.SelectionEnd, out var edit))
            {
                ApplyTokenDelete(edit);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete)
        {
            if (_draft.TryDeleteAfter(InputBox.Text, InputBox.CaretIndex,
                    InputBox.SelectionStart, InputBox.SelectionEnd, out var edit))
            {
                ApplyTokenDelete(edit);
                e.Handled = true;
            }
        }
    }

    private void ApplyTokenDelete(TextEdit edit)
    {
        InputBox.ReplaceText(edit.Text, edit.Caret);
        RefreshAttachmentBar();
    }

    private void OnInputDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files is null || files.Count == 0) return;

        AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
        e.Handled = true;
    }

    // ============================================================
    // 草稿持久化
    // ============================================================

    /// <summary>
    /// 草稿存档。为 null 时不落库 —— 新建页就是这种情况：会话还没建，没有地方可挂草稿。
    /// （接口 <see cref="IConversationProfile.Draft"/> 本来就是可空的，正是为这类会话留的口子。）
    /// </summary>
    public IDraftStore? DraftStore { get; set; }

    /// <summary>
    /// 从存档恢复草稿。宿主在会话内容加载完成后调用一次。
    /// </summary>
    public void RestoreDraft()
    {
        var raw = DraftStore?.Load();

        // 必须在碰 InputBox.Text 之前记下「库里现在存的是什么」：下面所有分支都会给输入框赋值，
        // 而整段替换正文一定触发一次 TextChanged。
        // 先记下来，SaveDraftAsync 才有依据判断「这次触发只是恢复、没有真的改动」。
        _lastSavedDraft = raw ?? "";

        var edit = _draft.Restore(raw);
        InputBox.ReplaceText(edit.Text, edit.Caret);
        RefreshAttachmentBar();
    }

    /// <summary>
    /// 保存草稿。正文与附件一起存 —— 只存正文的话，重开会话后正文里那个占位字符还在，
    /// 但「它是哪个文件」已经丢了。
    /// </summary>
    public async Task SaveDraftAsync()
    {
        if (DraftStore is not { } store) return;

        // 内容与库里一致就什么都不做。这不是优化，是修 bug：
        // 打开会话时 RestoreDraft 会整体替换一次正文，那次替换同样触发 TextChanged。
        // 少了这道闸门，光是点开一个会话就会往数据库写一次草稿。
        if (_draft.Serialize(InputBox.Text) == _lastSavedDraft) return;

        // 串行化：上一次写还没回来时只做个标记，等它回来后用最新内容再写一遍。
        // 不去重不行 —— 并发写同一条记录时，先发的未必先落，旧草稿会盖掉新的。
        if (_draftSaving)
        {
            _draftPending = true;
            return;
        }

        _draftSaving = true;
        try
        {
            do
            {
                _draftPending = false;

                // 排队期间内容可能又变回原样（或变回已落盘的值），再判一次
                var text = _draft.Serialize(InputBox.Text);
                if (text == _lastSavedDraft) break;

                await store.SaveAsync(text);
                _lastSavedDraft = text;
            }
            while (_draftPending);
        }
        catch
        {
            // 草稿落库失败不该影响输入 —— 用户正在打字，弹个错反而打断他
        }
        finally
        {
            _draftSaving = false;
        }
    }
}

