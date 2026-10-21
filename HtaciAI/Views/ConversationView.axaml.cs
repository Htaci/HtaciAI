using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Attachments;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Diagnostics;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Notifications;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 会话视图（普通对话与工作空间会话**共用**）：模型头 + 可折叠思考卡 + 工具卡 +
/// 「处理中/已处理」聚合卡 + 正文原文选择模式（蓝框）+ 复制/编辑/删除与 Tokens 汇总。
///
/// 与具体会话模型解耦——所有差异（工具基准目录、system 提示词、落库路径、草稿能力）
/// 都来自构造时传入的 <see cref="IConversationProfile"/>。消息仍复用 chat_message 表。
/// </summary>
public partial class ConversationView : UserControl, IInteractionHost, IDisposable
{
    /// <summary>
    /// 会话状态与请求执行上下文（历史、档案、网关、运行标记、取消源）。
    /// 之所以不留在视图里：插件要在「会话没打开」时也能读写这些（见 ConversationRuntime 的说明）。
    /// </summary>
    private readonly ConversationRuntime _runtime;

    private bool _skillEventsHooked;
    private bool _toolEventsHooked;

    // 草稿的「上次落盘内容」与串行化标志已随草稿逻辑搬进 ChatInputControl
    // （那边有 _lastSavedDraft / _draftSaving / _draftPending 同一套）。

    /// <summary>会话档案：会话状态、system 提示词、落库路径。</summary>
    private IConversationProfile Profile => _runtime.Profile;

    /// <summary>全量历史。渲染可以只取其中一段，但发给模型的始终是它全量。</summary>
    private List<ChatMessage> Messages => _runtime.Messages;

    /// <summary>请求网关：客户端解析、自动重试、工具循环、落库。</summary>
    private ChatGateway Gateway => _runtime.Gateway;

    // ============================================================
    // 历史分段渲染（长会话首屏加速）
    // ============================================================

    /// <summary>
    /// 全量历史按「轮」分组后的结果，由 <see cref="RenderHistory"/> 算一次。
    /// <b>注意：这只用于渲染</b>，发给模型的历史始终取 <see cref="Messages"/> 全量。
    /// </summary>
    private List<(string? TurnId, List<ChatMessage> Msgs)> _turnGroups = new();

    /// <summary>压缩记录覆盖的条数，仅在渲染压缩分割线时用。</summary>
    private readonly Dictionary<string, int> _compressionCovered = new();

    /// <summary>
    /// 已渲染窗口的起始分组下标。0 = 最早的分组也已渲染（没有更早的可加载了）。
    /// </summary>
    private int _firstRenderedGroup;

    /// <summary>正在向上补齐历史：挡住滚动事件重入。</summary>
    private bool _loadingEarlier;

    /// <summary>补历史时正在回填滚动位置：此间忽略「滚到顶部」的判定，否则会连锁触发下一批。</summary>
    private bool _restoringScroll;

    /// <summary>已排队一次「等布局完成再滚到底」，避免重复排队。</summary>
    private bool _scrollToEndPending;

    /// <summary>
    /// 首屏装配期间置位。<see cref="RenderHistory"/> 之后 <see cref="ScrollToEnd"/> 还没落到布局上，
    /// 此刻 Offset 仍是 0，会被 <see cref="IsNearTop"/> 判成「滚到顶部」从而把全部历史一次补齐 ——
    /// 正好把分段渲染的意义抵消掉。所以等首个布局帧跑完再开放判定。
    /// </summary>
    private bool _suppressEarlierLoad;

    /// <summary>本轮渲染的批大小（个「轮」）。</summary>
    private int _historyBatchTurns = 10;

    /// <summary>
    /// 这个窗口从注册表借来的那一份租约。窗口被丢掉时还回去；
    /// 自己 new 出来给设计器 / 测试用的窗口没有租约（<see cref="ConversationView(IConversationProfile)"/>）。
    /// </summary>
    private readonly IDisposable? _lease;

    /// <summary>已经 Dispose 过（防止退订 / 还租约被重复执行）。</summary>
    private bool _disposed;

    /// <summary>
    /// tool_call_id → 该调用的 arguments 文本。
    /// 渲染每个工具卡都要按 id 反查入参，逐条遍历全部消息会退化成 O(n²)，
    /// 这在工具调用多的长会话里是主要卡顿来源。一次建索引，查询变 O(1)。
    /// </summary>
    private Dictionary<string, string> _toolCallInputs = new();

    /// <summary>
    /// 实时轮里已经建出来的工具卡：tool_call_id → 卡。
    /// 参数分片一到就建卡（见 <see cref="OnToolCallDelta"/>），之后
    /// 「开始执行」「执行完成」都只是更新这一张 —— 不复用的话一次调用会出现两张卡，
    /// 一张永远停在「执行中」。只在一轮内有效，每轮开始清空。
    /// </summary>
    private readonly Dictionary<string, ToolCallCard> _toolCardsById = new(StringComparer.Ordinal);

    /// <summary>
    /// 本轮建了卡、但流式分片里还没带 tool_call_id 的卡（少数服务商首片不带 id）。
    /// 落库时按先后认领，认领不到才新建。
    /// </summary>
    private readonly List<ToolCallCard> _unclaimedToolCards = new();

    /// <summary>当前轮请求的开始时刻（Unix 毫秒），用于聚合卡显示用时。</summary>
    private long _turnStartedAt;

    private string? _activeTurnId;
    private StackPanel? _activeTurnRoot;

    /// <summary>当前 turn 的聚合卡（收纳思考卡 / 工具卡）；首次出现思考或工具调用时才创建。</summary>
    private TurnActivityCard? _activeTurnCard;

    /// <summary>当前 turn 的选择模式宿主：内容蓝框 + 「显示全部原文」切换。</summary>
    private TurnSelectionHost? _activeTurnHost;

    /// <summary>当前段落在卡外的正文块；一旦后面又出现工具调用，说明它只是过程叙述，就收进聚合卡。</summary>
    private TurnBodyView? _activeTurnBody;

    private int _turnToolCount;
    private int _turnThinkingCount;

    /// <summary>插件插槽登记表变化的订阅。只在可见时持有。</summary>
    private IDisposable? _slotSubscription;

    /// <summary>无参构造，仅供 XAML 预览器 / 设计器使用（无真实会话，不会读写数据库）。</summary>
    public ConversationView() : this(new StandaloneProfile("")) { }

    /// <summary>
    /// 自建运行时（不经过注册表）。设计器与「一个窗口独占一份数据的场景」用它；
    /// 正常打开会话走 <see cref="ConversationView(ConversationRuntime, IDisposable?)"/>。
    /// </summary>
    public ConversationView(IConversationProfile profile) : this(new ConversationRuntime(profile)) { }

    /// <param name="runtime">从 <see cref="ConversationRuntimeRegistry"/> 借来的运行时（可能已被插件用过）。</param>
    /// <param name="lease">对应的租约；窗口丢掉时由 <see cref="Dispose"/> 还回去。</param>
    public ConversationView(ConversationRuntime runtime, IDisposable? lease = null)
    {
        InitializeComponent();
        _runtime = runtime;
        _lease = lease;
        HookRuntime();

        // 插件插槽：可见时才跟着登记表刷新（窗口被缓存挂着时不占这份开销）
        AttachedToVisualTree += (_, _) => HookPluginSlots();
        DetachedFromVisualTree += (_, _) => UnhookPluginSlots();
        HookPluginSlots();

        // 只做不依赖会话内容的一次性 UI 装配；依赖会话状态的初始化统一放到 InitializeAsync
        // （普通对话要等 LoadAsync 查库回来才有内容，工作空间会话则由页面先行加载）
        // 工具集/工具的装载由 ChatInputControl 自己在构造时完成 —— 它要能独立成立，
        // 新建页那边同样依赖这一点。这里不再重复调用。

        // 输入区（附件、粘贴、拖放、回车发送、光标处整体删占位符）全部由 ChatInputControl 自己处理，
        // 这里只接它抛出来的几件事。原因：新建页与对话页原先各有一份实现，于是附件只加在了后者身上。
        Input.SendRequested += (_, submit) => _ = SendMessageAsync(submit.Text, submit.Attachments);
        Input.StopRequested += (_, _) => _runtime.Cancel();

        // 换模型 = 换上下文上限，占用读数要按新上限重算（用量本身取自历史，不变）
        Input.ModelSelector.SelectedModelChanged += (_, _) => RefreshContextUsage();

        // 每次打开模型菜单都重拉一次列表：服务商/模型改了名、新增了模型都能立刻看到，
        // 不再只依赖会话创建时那一次注入。
        Input.ModelSelector.RefreshRequested += async (_, _) => await RefreshModelsAsync();

        // 压缩要在「这一轮发出去之前」定下上下文，所以入口放在输入区的更多菜单里。
        // 运行中控件会自己把注入的项禁用掉（中途插一条压缩会让本轮上下文前后不一致）。
        Input.AddMoreItem("压缩上下文", () => _ = CompressManuallyAsync());

        // 滚到接近顶部时补齐更早的历史（长会话首屏只渲染最后一批，见 RenderHistory）
        MessagesScroll.ScrollChanged += OnMessagesScrollChanged;
        SetupAutoScroll();

        // 消息区里按下选字不该把视口拽来拽去（原因见 SmoothScrollViewer.DisableFocusBringIntoView）
        SmoothScrollViewer.DisableFocusBringIntoView(MessagesScroll);
    }

    /// <summary>输入区那条「正在重试」的提示是不是我显示的（收尾时只有我该去清它）。</summary>
    private bool _retryHintShown;

    /// <summary>
    /// 网关即将自动重试（一次请求内最多 5 次尝试）。
    /// 提示不复位等待环：重试期间这一轮仍然在跑，运行态不能闪回发送态。
    /// </summary>
    private void OnRetryScheduled(int attempt, int maxAttempts)
    {
        void Show()
        {
            _retryHintShown = true;
            Input.ShowHint($"请求中断，正在重试（{attempt - 1}/{maxAttempts - 1} 次失败）…",
                InputHintKind.Info, autoHide: false);
        }

        // 重试发生在本轮请求内部，通常已经在 UI 线程上；仍按防御写法处理，别赌调用上下文。
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
    }

    private bool _initialized;

    /// <summary>
    /// 载入会话与历史。由调用方显式 await —— 统一了原来「普通对话 await / 工作空间构造期
    /// fire-and-forget」的两种时序，顺带消除后者两路并发的竞态。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // 会话存档与全量历史都由 runtime 读一次：插件可能先于视图把它读完了，
        // 那种情况下这里不会重复读库。
        var alreadyLoaded = _runtime.IsHistoryLoaded;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _runtime.EnsureReadyAsync();
        if (!alreadyLoaded && sw.ElapsedMilliseconds > 0)
            PerfTrace.Note("① 读库（含配对修复）", sw.Elapsed.TotalMilliseconds);

        ConfigureToolExecutor();
        LoadSkills();
        LoadToolsSelection();
        await LoadMcpSelectionAsync();

        // 草稿落库交给控件；这里把存档交过去（为 null 时不落库，控件那边自然处理）
        Input.DraftStore = Profile.Draft;
        Input.RestoreDraft();

        await LoadModelsAsync();
        RenderLoadedHistory();
    }

    /// <summary>当前思考模式：透传模型选择器。</summary>
    public ThinkingMode ThinkingMode
    {
        get => Input.ModelSelector.ThinkingMode;
        set => Input.ModelSelector.ThinkingMode = value;
    }

    /// <summary>
    /// 当前激活的工具 id：透传工具选择器。
    /// 新建会话流程用它把「新建页选好的工具」沿用到首条消息（原 ChatView 的公开入口）。
    /// </summary>
    public IReadOnlyList<string> SelectedToolIds
    {
        get => Input.ToolSelector.SelectedToolIds;
        set => Input.ToolSelector.SelectedToolIds = value;
    }

    // ---- 工具 / 技能选择器 ----

    private void LoadSkills()
    {
        Input.SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();
        Input.SkillSelector.SelectedSkills = Profile.State.EnabledSkills;
        if (_skillEventsHooked) return;
        _skillEventsHooked = true;
        Input.SkillSelector.SelectionChanged += async (_, _) => await SyncSkillsToSessionAsync();
    }

    private async Task SyncSkillsToSessionAsync()
    {
        Profile.State.EnabledSkills = Input.SkillSelector.SelectedSkills.ToList();
        await PersistSessionAsync();
    }

    private void LoadToolsSelection()
    {
        // 无条件回填：空集合也是有意义的状态（「默认工具」里可以选择一个都不开）
        Input.ToolSelector.SelectedToolIds = Profile.State.EnabledToolIds;
        if (_toolEventsHooked) return;
        _toolEventsHooked = true;
        Input.ToolSelector.SelectionChanged += async (_, _) => await SyncToolsToSessionAsync();
    }

    private async Task SyncToolsToSessionAsync()
    {
        Profile.State.EnabledToolIds = Input.ToolSelector.SelectedToolIds.ToList();
        await PersistSessionAsync();
    }

    // ---- MCP 选择器 ----

    private bool _mcpEventsHooked;

    /// <summary>
    /// 注入全部 MCP 服务（含未启动的）并回填本会话启用的项。
    /// 未启动的服务也能勾——发送前 <see cref="McpConnectionManager.EnsureToolsAsync"/> 会把它启动起来。
    /// </summary>
    private async Task LoadMcpSelectionAsync()
    {
        try
        {
            Input.McpSelector.Servers = await McpServerRepository.GetAllAsync();
        }
        catch
        {
            // 忽略：保持空列表
        }

        Input.McpSelector.SelectedServerIds = Profile.State.McpServers;

        if (_mcpEventsHooked) return;
        _mcpEventsHooked = true;
        Input.McpSelector.SelectionChanged += async (_, _) => await SyncMcpToSessionAsync();
    }

    private async Task SyncMcpToSessionAsync()
    {
        Profile.State.McpServers = Input.McpSelector.SelectedServerIds.ToList();
        await PersistSessionAsync();
    }

    // ---- 工具执行器 ----

    /// <summary>
    /// 把交互出口与本会话的权限档位接上。
    ///
    /// 工具执行器、工具解析、落库都已经在 <see cref="ConversationRuntime"/> 里；
    /// 这里只做「视图才做得了」的两件事：审批卡 / 提问卡由一个视图实现
    /// （<see cref="IInteractionHost"/>），以及权限菜单归输入控件。
    /// </summary>
    private void ConfigureToolExecutor()
    {
        _runtime.Interaction = this;

        // 压缩摘要用「当前选中的模型」：它可能还没发过一轮，所以不能只看会话存档里的
        _runtime.SummaryModelIdProvider = () => Input.ModelSelector.SelectedModel?.ModelId;

        // 权限菜单归输入控件，这里只把「会话存档里的档位」回填进去，
        // 并订阅它的选择结果落库。刷新提醒里点「全部允许」那条路走 SetPermissionModeAsync，
        // 它会反过来同步控件显示。
        Input.PermissionMode = Profile.State.PermissionMode;
        Input.PermissionModeChanged += (_, mode) => _ = SetPermissionModeAsync(mode);
    }

    /// <summary>
    /// 工具审批入口：审批卡片同样插进当前轮次的正文之间，等用户点「允许执行 / 拒绝」。
    /// </summary>
    public async Task<bool> ApproveAsync(ChatToolCall call, ToolDefinition tool, CancellationToken ct)
    {
        ToolApprovalCard? card = null;
        ReportActivity(SessionActivity.WaitingConfirm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            card = new ToolApprovalCard(tool, call);
            AddInlineCard(card);
        });
        if (card is null) return false;

        // 提醒上带三个按钮，可以直接作答，不必先跳到这个会话。
        // 「全部同意」顺带把本会话权限改成自由 —— 之后的工具都不再询问。
        var approvalCard = card;
        SessionNotifier.Notify(SessionNoticeKind.Approval, Profile.State.SessionId, Profile.State.Title,
            $"工具（{tool.Name}）待审批",
            workspaceName: Profile.WorkspaceName,
            onAction: async action =>
            {
                if (action == NoticeAction.AllowAll)
                    await SetPermissionModeAsync(PermissionMode.Free);

                approvalCard.Respond(action != NoticeAction.Deny);
            });

        bool allowed;
        try
        {
            // WaitAsync(ct)：点「停止」时这张卡要一起作废。
            // 原来这里是裸 await —— 审批卡一挂起，停止按钮就完全没反应，直到用户去点允许/拒绝。
            allowed = await approvalCard.Allowed.WaitAsync(ct);
        }
        finally
        {
            // 取消时也要撤掉卡片：否则它会留在正文里，变成一个永远等不到结果的控件
            await Dispatcher.UIThread.InvokeAsync(() => RemoveInlineCard(approvalCard));
        }

        ReportActivity(SessionActivity.Running);   // 授权只是打断一下，本轮请求还在继续
        return allowed;
    }

    /// <summary>
    /// ask_user_question 的作答入口：把提问卡片插进当前轮次的正文之间
    /// （没有活动轮次时兜底挂到消息面板末尾），等用户作答后把结果交回工具。
    /// 答完就把卡片撤掉：重新打开会话本来也不会渲染它，这次询问的记录留在工具卡里。
    /// </summary>
    public async Task<List<AskAnswer>?> AskAsync(List<AskQuestion> questions, CancellationToken ct)
    {
        AskUserCard? card = null;
        ReportActivity(SessionActivity.WaitingConfirm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            card = new AskUserCard(questions);
            AddInlineCard(card);
        });
        if (card is null) return null;

        SessionNotifier.Notify(SessionNoticeKind.Question, Profile.State.SessionId, Profile.State.Title,
            "需要你的协助，请完成问题回答。",
            workspaceName: Profile.WorkspaceName);

        List<AskAnswer>? answers;
        try
        {
            // 同审批卡：用户点「停止」时不能再等一个没人会答的问题
            answers = await card.Answer.WaitAsync(ct);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => RemoveInlineCard(card));
        }

        ReportActivity(SessionActivity.Running);
        return answers;
    }

    /// <summary>把交互卡片挂到当前轮次的正文流末尾；没有活动轮次时挂到消息面板末尾。</summary>
    private void AddInlineCard(Control card)
    {
        if (_activeTurnHost is not null)
            _activeTurnHost.ContentPanel.Children.Add(card);
        else
            MessagesPanel.Children.Add(card);
        ScrollToEnd();
    }

    private static void RemoveInlineCard(Control card)
    {
        if (card.Parent is Panel parent)
            parent.Children.Remove(card);
    }

    private async Task PersistSessionAsync()
    {
        try { await Profile.SaveAsync(); } catch { /* 忽略 */ }
    }

    // ---- 权限模式（会话级盾牌图标） ----
    //
    // 菜单本身、图标配色、四种档位的文案都在 ChatInputControl 里（两个页面完全一样）。
    // 这里只负责把「选了什么」落到会话状态与工具执行器上。

    private async Task SetPermissionModeAsync(PermissionMode mode)
    {
        await _runtime.SetPermissionModeAsync(mode);

        // 控件的菜单是唯一入口，所以从这里改（比如提醒里点了「全部允许」）要把它的显示同步过去，
        // 否则盾牌图标会停在旧档位上。
        if (Input.PermissionMode != mode) Input.PermissionMode = mode;

        await PersistSessionAsync();
    }

    /// <summary>一条新消息落库后同步界面（assistant 的控件已在 <see cref="OnRoundStarted"/> 里建好）。</summary>
    private void OnMessageInserted(ChatMessage m)
    {
        if (m.Role == "user")
            AddMessage(m);
        else if (m.Role == "assistant")
            IndexToolCallInputs(m);
        else if (m.Role == "tool")
            UpsertToolCard(m);
        else if (ChatError.IsError(m))
            AddErrorCard(m);
    }

    /// <summary>
    /// 把一条 assistant 消息里的 tool_calls 记进入参索引。实时轮的工具卡靠它显示入参 ——
    /// 这个索引原先只在读历史时构建，于是实时轮的调用查不到入参、展开看到的是「{}」，
    /// 要重开会话才正常。<see cref="OnToolCallDelta"/> 的流式回调也会实时写一遍
    /// （让卡片在参数还在传时就有内容），这里是流结束后的权威版本。
    /// </summary>
    private void IndexToolCallInputs(ChatMessage assistantMsg)
    {
        foreach (var call in ToolCallJson.Deserialize(assistantMsg.Metadata))
            if (!string.IsNullOrEmpty(call.Id))
                _toolCallInputs[call.Id] = call.Arguments ?? "{}";
    }

    /// <summary>
    /// 同一条消息的第二次落库：工具行正好就是「卡片从执行中翻成结果」，所以要更新界面。
    /// </summary>
    private void OnMessageUpdated(ChatMessage m)
    {
        if (m.Role == "tool")
            UpsertToolCard(m);
        else if (m.Role == "assistant")
            // 未解析的工具会被改写成 invalid（工具名与入参都换了），索引跟着更新
            IndexToolCallInputs(m);
    }

    /// <summary>工具改了本会话启用的技能：把输入区的技能选择器拉到最新。</summary>
    public void OnSessionSkillsChanged(IReadOnlyList<SessionSkill>? skills)
    {
        // 没有技能传空列表而不是 null：选择器的 SelectedSkills 是只读列表属性，
        // null 会让它按「别人刚清空」处理，而不是「没有数据」
        Input.SkillSelector.SelectedSkills = skills is null ? Array.Empty<SessionSkill>() : skills.ToList();
    }

    /// <summary>
    /// 工具行的实时更新落到卡片上：流式阶段建过卡的就复用它，没有才新建。
    /// 复用是必须的 —— 同一次调用建两张卡的话，一张会永远停在「执行中」。
    /// </summary>
    private void UpsertToolCard(ChatMessage toolMsg)
    {
        var id = toolMsg.ToolCallId;
        var name = toolMsg.ToolName ?? "工具";
        var input = FindToolCallInput(id);

        ToolCallCard? card = null;
        if (!string.IsNullOrEmpty(id))
            _toolCardsById.TryGetValue(id, out card);

        // 流式阶段建过卡，但分片里始终没带 id：按先后认领，别让它变成一张永远没有结果的孤儿卡
        if (card is null && _unclaimedToolCards.Count > 0)
        {
            card = _unclaimedToolCards[0];
            _unclaimedToolCards.RemoveAt(0);
            if (!string.IsNullOrEmpty(id))
                _toolCardsById[id] = card;
        }

        if (card is null)
        {
            // 没见过这张卡（服务商没在流里下发 tool_calls、或是「重新生成」这类回放）：照旧新建
            AddToolCallCard(toolMsg);
            return;
        }

        if (toolMsg.Status is "completed" or "failed")
            card.SetData(name, input, toolMsg.Content ?? "");
        else
            card.SetRunning(name, input);
    }

    /// <summary>
    /// 实时：错误记录渲染成红色错误卡，追加到当前 turn 的内容区
    /// （没有活动 turn 时兜底挂到消息面板）。
    /// </summary>
    private void AddErrorCard(ChatMessage msg)
    {
        var card = new ErrorCard(msg.Content, onRetry: () => _ = RetryTurnAsync(msg));
        if (_activeTurnHost is null)
            MessagesPanel.Children.Add(card.Root);
        else
            _activeTurnHost.ContentPanel.Children.Add(card.Root);
        ScrollToEnd();
    }

    // ---- 加载模型与消息 ----

    private async Task LoadModelsAsync()
    {
        try
        {
            Input.ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
            if (!string.IsNullOrEmpty(Profile.State.Model))
            {
                var saved = Input.ModelSelector.Models.FirstOrDefault(m => m.ModelName == Profile.State.Model);
                if (saved is not null)
                    Input.ModelSelector.SelectedModel = saved;
            }
        }
        catch
        {
            // 数据库未就绪时选择器保持空（显示「未配置模型」），发送侧会拦下并提示
        }
        // 恢复会话保存的思考模式
        Input.ModelSelector.ThinkingMode = (ThinkingMode)Profile.State.Thinking;
    }

    /// <summary>
    /// 重新拉取模型列表（每次打开模型菜单时调用）。
    /// 只刷新列表，<b>不</b>碰当前选中项与思考模式 —— 选择器内部按模型 id 保住选中项；
    /// 沿用 <see cref="LoadModelsAsync"/> 会把用户刚改的思考模式重置回会话保存值。
    /// </summary>
    private async Task RefreshModelsAsync()
    {
        try
        {
            Input.ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
        }
        catch
        {
            // 读取失败就保持现有列表，不要清空选择器
        }
    }

    /// <summary>
    /// 渲染 runtime 里已有的历史。<b>不读库</b> —— 读库是 <see cref="ConversationRuntime.EnsureReadyAsync"/>
    /// 的事，这里只负责把它画出来（插件可能已经把历史读好了，再读一遍是白花时间）。
    /// </summary>
    private void RenderLoadedHistory()
    {
        // 窗口实例会被缓存复用，切回会话时恢复「跟着最新内容走」
        _autoScroll = true;

        PerfTrace.Begin($"会话 {Profile.State.SessionId}");
        PerfTrace.Info($"消息 {Messages.Count} 条");

        using (PerfTrace.Measure("② 首屏渲染（含分组）"))
        {
            RenderHistory();
            ScrollToEnd();
        }

        // 没有更早的历史就到此结束；还有的话报告由首屏布局那次 Flush 输出
        if (_firstRenderedGroup <= 0) PerfTrace.Flush();
        RefreshContextUsage();
    }

    /// <summary>
    /// 刷新上下文用量环：占用取自最后一条带 usage 的 assistant 消息（口径见 <see cref="TryGetContextUsage"/>）。
    /// 模型未声明上下文长度或历史里没有任何 usage 时自动隐藏。
    /// </summary>
    private void RefreshContextUsage()
    {
        var model = Input.ModelSelector.SelectedModel;

        // 换模型 = 换上下文上限，占用读数要按新上限重算（用量本身取自历史，不变）。
        // runtime 拿它算自动压缩的阈值，两边必须是同一个数。
        if (model is not null) _runtime.ContextWindow = model.ContextWindow;

        if (model is null || !_runtime.TryGetContextUsage(out var used, out var total))
        {
            // 传 null = 隐藏用量环。新建页没有历史可算，走的就是这条路。
            Input.SetContextUsage(null, null, "");
            return;
        }

        Input.SetContextUsage(used, total, model.DisplayName ?? "");
    }

    /// <summary>外部触发：会话标题/时间变化后由宿主页面刷新导航列表（仅供通知）。</summary>
    public event Action? Updated;

    /// <summary>本会话的活动状态变化：列表里据此显示转圈 / 等待确认 / 未读绿点。</summary>
    public event Action<SessionActivity>? ActivityChanged;

    /// <summary>本视图对应的会话 id（宿主页面按它把状态落到左侧列表）。</summary>
    public string SessionId => Profile.State.SessionId;

    private void ReportActivity(SessionActivity activity)
    {
        if (ActivityChanged is not { } handler) return;

        // 工具回调可能不在 UI 线程上（提问 / 授权都由工具执行链路触发），先回到 UI 线程再通知
        if (Dispatcher.UIThread.CheckAccess()) handler(activity);
        else Dispatcher.UIThread.Post(() => handler(activity));
    }

    // ============================================================
    // 上下文压缩
    // ============================================================

    /// <summary>
    /// 菜单里的「压缩上下文」。压缩本身在 <see cref="ConversationRuntime.CompressAsync"/> 里，
    /// 这里只负责把结果翻成人话 —— runtime 不知道有没有界面在看它。
    /// </summary>
    private async Task CompressManuallyAsync()
    {
        try
        {
            var result = await _runtime.CompressAsync(manual: true);

            if (result.NothingToCompress)
            {
                Input.ShowHint("还没有可压缩的内容", InputHintKind.Info, autoHide: true);
                return;
            }

            if (result.Compressed)
                Input.ShowHint($"已压缩 {result.MessageCount} 条消息，此前的历史不再进入上下文", InputHintKind.Ok, autoHide: true);
            else if (result.Error is { } reason)
                Input.ShowHint($"压缩失败：{reason}", InputHintKind.Fail, autoHide: true);
        }
        catch (OperationCanceledException)
        {
            // 手动压缩没有可取消的东西，走不到这里；真到了也不该让异常冒到界面上
        }
    }

    /// <summary>压缩记录在消息流里的呈现：一条分割线 + 可展开的摘要卡。</summary>
    private Control CreateCompressionDivider(ChatMessage message, int covered)
    {
        var model = string.IsNullOrWhiteSpace(message.ModelName) ? "" : $" · {message.ModelName}";

        var card = new CompressedContextCard();
        card.SetContent(
            message.Content ?? "",
            $"压缩了 {covered} 条历史消息{model} · {FormatTime(message.CreatedAt)}");
        card.DeleteRequested += () => _ = DeleteCompressionAsync(message);
        return card;
    }

    private async Task DeleteCompressionAsync(ChatMessage message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "删除这条压缩记录？\n\n" +
            "删除后，这段历史会重新以原文进入上下文，占用会变大。\n" +
            "如果后面还有更新的压缩记录，删除这一条不影响上下文内容（更新的摘要已经把它包含进去了）。",
            "删除压缩记录");
        if (!confirmed) return;

        try { await ChatRepository.SoftDeleteMessageAsync(Profile.State.SessionId, message.Id); }
        catch { /* 忽略 */ }

        await ReloadMessagesAsync();
        Input.ShowHint("已删除压缩记录，这段历史重新进入上下文", InputHintKind.Ok, autoHide: true);
    }

    // ---- 发送 ----

    /// <summary>
    /// 历史没读完就先等它。界面为了快速进窗只渲染了最后一批，但「读库」是另一回事：
    /// 这时发送会把残缺历史交给模型，上下文静默丢一段 —— 所以宁可让用户等这一下。
    ///
    /// 正常情况这里早就完成了（打开会话时就读完了），立刻返回；
    /// 只有「刚打开窗口就抢着发」、或者「会话只被插件用过、刚被借回来」才会真的等。
    /// </summary>
    private async Task WaitForHistoryAsync(string action)
    {
        if (_runtime.IsHistoryLoaded) return;

        Input.ShowHint($"正在加载历史记录，完成后自动{action}…", InputHintKind.Info, autoHide: false);
        await _runtime.EnsureReadyAsync();
        Input.HideHint();
    }

    /// <summary>
    /// 发送前解析模型客户端。返回 null 表示这一轮发不出去，<b>并且提示已经在这里给过了</b>，
    /// 调用方直接放弃即可。
    ///
    /// 解析失败（数据库异常）也必须收成 null 并给出提示 —— 让它抛出去的话，
    /// 用户等到的是一条没有任何解释的空响应，输入框里的内容也已经清掉了。
    /// </summary>
    private async Task<IChatClient?> ResolveSendClientAsync(ModelDetails selected)
    {
        try
        {
            var client = await Gateway.ResolveClientAsync(selected.ModelId);
            if (client is not null) return client;

            Input.ShowHint("所选模型已停用或被删除，请到「设置 → 模型服务」重新启用", InputHintKind.Fail, autoHide: false);
            return null;
        }
        catch (Exception ex)
        {
            Input.ShowHint($"读取模型配置失败：{ex.Message}", InputHintKind.Fail, autoHide: false);
            return null;
        }
    }

    public async Task SendMessageAsync(string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        if (_runtime.IsBusy || string.IsNullOrWhiteSpace(text)) return;

        await WaitForHistoryAsync("发送");

        // 没有可用模型就挡在这里，输入内容原样保留 —— 用户去配好模型回来还能接着发。
        // 这一步必须在清空输入框之前：失败一次就要人重打一遍，那是很糟的体验。
        var selected = Input.ModelSelector.SelectedModel;
        if (selected is null)
        {
            Input.ShowHint("尚未配置模型：请到「设置 → 模型服务」添加并启用服务商与模型", InputHintKind.Fail, autoHide: false);
            return;
        }

        var client = await ResolveSendClientAsync(selected);
        if (client is null) return;   // 提示已经由 ResolveSendClientAsync 给过

        Input.Text = "";

        // 用户主动发消息：无论刚才在看哪一段，都跟着这一轮走
        _autoScroll = true;

        var turn = await _runtime.SendAsync(new ConversationTurnRequest
        {
            Settings = new TurnSettings(selected, client, Input.ModelSelector.ThinkingMode,
                Input.ToolSelector.SelectedToolIds),
            Text = text,
            Attachments = attachments,
            RequestPreview = text,
        });

        // 发送成功后清空输入区（正文 + 附件一起）。空附件栏由控件自己收起。
        // 没被接受（已有任务在跑）时不动，用户刚贴上的附件还在。
        if (turn is not null) Input.Clear();
    }

    // ============================================================
    // 订阅 runtime：视图只负责「把它画出来」
    // ============================================================

    /// <summary>
    /// 接上 runtime 的事件。请求执行、工具循环、落库、标题、提醒全在
    /// <see cref="ConversationRuntime"/> 里 —— 这样同一套逻辑在没有窗口时也能跑完（插件后台任务）。
    ///
    /// <b>订阅活到窗口被丢弃为止</b>（<see cref="Dispose"/> 里退订），而不是「离开可视树就退订」：
    /// 用户切到别的会话时这个窗口只是从可视树上摘下来，缓存还留着它，
    /// 这一轮如果还在跑，后续增量必须继续画进它——退订的话切回来会缺一大段。
    /// </summary>
    private void HookRuntime()
    {
        _runtime.RoundStarted += OnRoundStarted;
        _runtime.ContentDelta += OnContentDelta;
        _runtime.ThinkingDelta += OnThinkingDelta;
        _runtime.ToolCallDelta += OnToolCallDelta;
        _runtime.MessageInserted += OnMessageInserted;
        _runtime.MessageUpdated += OnMessageUpdated;
        _runtime.BusyChanged += OnBusyChanged;
        _runtime.RetryScheduled += OnRetryScheduled;
        _runtime.ActivityChanged += ReportActivity;
        _runtime.TurnEnded += OnTurnEnded;
        _runtime.ToolCallsRepaired += BuildToolCallIndex;
        _runtime.HistoryRebuildRequested += OnHistoryRebuildRequested;
        _runtime.CompressingChanged += OnCompressingChanged;
    }

    /// <summary>
    /// 刷新两个插件插槽（顶部一行、输入区左侧一排）。
    /// <b>用工厂重建而不是缓存控件</b>：窗口会被缓存淘汰后重进，缓存控件只会拿到已经脱离可视树的旧对象。
    /// </summary>
    private void RefreshPluginSlots()
    {
        PluginSlotPanel.Fill(HeaderSlot, PluginSlots.ConversationHeader);
        PluginSlotPanel.Fill(InputSideSlot, PluginSlots.ConversationInputSide);
    }

    private void HookPluginSlots()
    {
        if (_slotSubscription is not null) return;

        RefreshPluginSlots();
        _slotSubscription = PluginSlotPanel.Subscribe(RefreshPluginSlots);
    }

    private void UnhookPluginSlots()
    {
        _slotSubscription?.Dispose();
        _slotSubscription = null;
    }

    /// <summary>
    /// 退订。**必须与 <see cref="HookRuntime"/> 严格对称** ——
    /// runtime 可以比窗口活得久（插件还借着手里的租约），漏退一个订阅，
    /// 这个已经没人显示的窗口就会继续收增量、继续占着整棵消息树的内存。
    /// </summary>
    private void UnhookRuntime()
    {
        _runtime.RoundStarted -= OnRoundStarted;
        _runtime.ContentDelta -= OnContentDelta;
        _runtime.ThinkingDelta -= OnThinkingDelta;
        _runtime.ToolCallDelta -= OnToolCallDelta;
        _runtime.MessageInserted -= OnMessageInserted;
        _runtime.MessageUpdated -= OnMessageUpdated;
        _runtime.BusyChanged -= OnBusyChanged;
        _runtime.RetryScheduled -= OnRetryScheduled;
        _runtime.ActivityChanged -= ReportActivity;
        _runtime.TurnEnded -= OnTurnEnded;
        _runtime.ToolCallsRepaired -= BuildToolCallIndex;
        _runtime.HistoryRebuildRequested -= OnHistoryRebuildRequested;
        _runtime.CompressingChanged -= OnCompressingChanged;
    }

    /// <summary>
    /// 扔掉这个窗口：退订 runtime、断开它对本窗口的两处引用，再把租约还回去。
    ///
    /// <b>由 <see cref="SessionViewCache"/> 在淘汰时调用</b>，不是「离开可视树」时调用 ——
    /// 缓存里的窗口只是暂时没显示，还要继续接收正在跑的那一轮的增量。
    ///
    /// 还掉租约之后，如果没人再借这个会话的运行时、且当前没有轮次在跑，
    /// 注册表会把它一并丢掉；插件还借着的则照常活着，继续跑它的任务。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnhookRuntime();
        UnhookPluginSlots();

        // runtime 上有两处直接指向本窗口的东西，不清掉就等于把整棵控件树留在 runtime 上
        if (ReferenceEquals(_runtime.Interaction, this)) _runtime.Interaction = null;
        _runtime.SummaryModelIdProvider = null;

        _lease?.Dispose();
    }

    /// <summary>
    /// 历史换了一批（压缩落了摘要、重新生成重排了顺序、删了轮次）：整块重绘。
    /// <paramref name="keepRenderedMessageId"/> 非空时要至少渲染到它所在的分组 ——
    /// 被点中的轮次可能落在「首屏最后一批」之外。
    /// </summary>
    private void OnHistoryRebuildRequested(string? keepRenderedMessageId)
    {
        // 重绘之后这一轮的控件都不在了，实时渲染的上下文一起丢掉
        _activeTurnId = null;
        _activeTurnRoot = null;
        _activeTurnHost = null;
        _activeTurnCard = null;
        _activeTurnBody = null;
        _round = null;

        RenderHistory(keepRenderedMessageId);
        ScrollToEnd();
        RefreshContextUsage();
    }

    /// <summary>压缩开始 / 结束：进度的可见反馈只有这里能给。</summary>
    private void OnCompressingChanged(bool compressing)
    {
        if (compressing) Input.ShowHint("正在压缩上下文…", InputHintKind.Info, autoHide: false);
        else Input.HideHint();
    }

    /// <summary>
    /// 运行标记变化：切换输入区的发送 / 停止形态，并在本轮结束时收掉两件只在本轮有效的东西。
    /// </summary>
    private void OnBusyChanged(bool running)
    {
        Input.SetRunning(running);
        if (running) return;

        // 请求已返回（成功 / 停止 / 失败），逐秒计时到此为止。
        // 放在这里而不是只放在收尾里：请求异常直接抛出去时收尾逻辑不会走到，
        // 定时器会一直跑下去（运行中的 DispatcherTimer 会吊住页面）。
        StopTurnTimer();

        // 「正在重试」是临时提示：本轮既然已经有结论了（成功或彻底失败），就把它收掉，
        // 别让一行「正在重试」留在输入框旁边冒充当前状态。
        if (_retryHintShown)
        {
            _retryHintShown = false;
            Input.HideHint();
        }
    }

    /// <summary>一轮结束：把这一轮的控件收成终态，再决定要不要整体重排历史。</summary>
    private async Task OnTurnEnded(TurnOutcome outcome)
    {
        if (outcome.TitleChanged)
        {
            // 只是自动话题命名完成了：刷新导航列表就够了，不要再收一次尾（会多出一组操作栏）
            Updated?.Invoke();
            return;
        }

        // 停在半路的工具卡（参数还在接收 / 执行中）不会再有回调，就地收成终态。
        // 两条停止路径都要覆盖：工具执行中被取消（异常上抛）、流式输出中被取消（网关正常返回）。
        if (outcome.StoppedByUser)
            MarkLiveToolCardsInterrupted();

        if (_activeTurnId is not null && _activeTurnRoot is not null && _activeTurnHost is not null)
        {
            // 回复结束：聚合卡翻为用时统计并折叠；操作栏（含原文按钮）追加到蓝框外
            FinishActivityCard();

            var turnAssistants = Messages
                .Where(m => m.Role == "assistant" && m.TurnId == _activeTurnId)
                .ToList();
            _activeTurnRoot.Children.Add(CreateAiReplyOps(_activeTurnId, turnAssistants, _activeTurnHost));
        }

        if (outcome.IsRegenerate)
        {
            // 重新生成：runtime 已经把库里的新顺序读回来了，重绘也随 HistoryRebuildRequested 做过了
            ScrollToEnd();
        }
        else if (_activeTurnRoot is null)
        {
            // 这一轮没有对应的控件（视图是它跑到一半才挂上来的，或者它由插件在后台发起）：
            // 流式增量没有落点，内容只在 Messages 里，不重绘就永远看不见
            await ReloadMessagesAsync();
        }
        else
        {
            ScrollToEnd();
        }

        // 这一段的控件归属到此为止：之后不会再有增量，留着只会让迟到的回调写进旧块
        _round = null;

        RefreshContextUsage();
        ReportActivity(SessionActivity.Done);
        Updated?.Invoke();
    }

    /// <summary>
    /// 编辑用户消息。两个出口：
    /// <list type="bullet">
    ///   <item><b>保存</b>：只写库，不重发 —— 用户常常只是想订正措辞，是否重生成由他自己决定；</item>
    ///   <item><b>发送</b>：写库后走重发链路，用改后的内容重新生成那一轮的回复。</item>
    /// </list>
    /// </summary>
    private async Task EditUserMessageAsync(ChatMessage msg)
    {
        if (_runtime.IsBusy)
        {
            Input.ShowHint("正在生成回复，先点「停止」再编辑", InputHintKind.Info, autoHide: true);
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var (result, text) = await MessageEditDialog.ShowAsync(owner, "编辑消息", msg.Content ?? "");
        if (result == MessageEditResult.Cancel) return;

        var content = text.Trim();
        if (content.Length == 0)
        {
            Input.ShowHint("消息内容不能为空", InputHintKind.Fail, autoHide: true);
            return;
        }

        if (content != msg.Content)
        {
            try
            {
                await ChatRepository.UpdateMessageContentAsync(msg.Id, content);
                msg.Content = content;
            }
            catch
            {
                Input.ShowHint("保存失败，消息未改动", InputHintKind.Fail, autoHide: true);
                return;
            }
        }

        if (result == MessageEditResult.Save)
        {
            await ReloadMessagesAsync();   // 让气泡显示改后的文本
            return;
        }

        await RegenerateAsync(msg);
    }

    /// <summary>
    /// 重新生成一轮 AI 回复。点用户消息或点 AI 消息都走这里，两者语义等价：
    /// 上下文取「触发点及以上」—— 点 AI 消息 → 这条 AI 消息之前；点用户消息 → 含这条用户消息。
    /// 于是「用户1 / AI2」这轮里，点用户1 或点 AI2 都是重新生成 AI2。
    /// 已经被删掉的 AI 轮次没有可点的 AI 消息，从它前面那条用户消息点即可补生成。
    /// </summary>
    private async Task RegenerateAsync(ChatMessage trigger)
    {
        if (_runtime.IsBusy)
        {
            Input.ShowHint("正在生成回复，先点「停止」再重新生成", InputHintKind.Info, autoHide: true);
            return;
        }

        // 历史没读完不能重生成：上下文会缺一段
        await WaitForHistoryAsync("重新生成");

        var settings = await BuildTurnSettingsAsync("重新生成");
        if (settings is null) return;

        // 无论刚才在看哪一段，都跟着这一轮走
        _autoScroll = true;

        await _runtime.RegenerateAsync(trigger, settings);
    }

    /// <summary>
    /// 重试某一轮失败 / 中断的请求（错误卡上的「重试」）。
    ///
    /// 与「重新生成」只差一件事：<b>怎么对待上一轮</b>。重新生成是「不满意，删掉重来」；
    /// 重试是「没跑完，接着跑」—— 所以这里一条消息都不删。上一轮已经产生的思考与正文
    /// 留在原地（网关在失败时也把它们落进那条 failed 消息里了），用户想回看还能看，
    /// 否则一次断网就把好不容易等出来的思考链抹掉，等于白等。
    ///
    /// 成功与否都靠自动重试先兜一遍（一次请求 5 次尝试），所以能走到这个按钮面前的基本是
    /// 「重试也没用」的失败（余额用尽、鉴权失败、模型名不存在）—— 那正是该让用户自己决定
    /// 是否继续的场景：去充值、去改配置，回来点它接着跑。
    /// </summary>
    /// <param name="anchor">失败那一轮里的任意一条消息（错误记录 / 失败的 assistant / 中断的工具行）。</param>
    private async Task RetryTurnAsync(ChatMessage anchor)
    {
        if (_runtime.IsBusy)
        {
            Input.ShowHint("正在生成回复，先点「停止」再重试", InputHintKind.Info, autoHide: true);
            return;
        }

        await WaitForHistoryAsync("重试");

        var settings = await BuildTurnSettingsAsync("重试");
        if (settings is null) return;

        _autoScroll = true;
        await _runtime.RetryAsync(anchor, settings);
    }

    /// <summary>
    /// 继续被停止 / 打断的那一轮（操作栏上的「继续」）。
    ///
    /// 与「重试」只差一件事：<b>中断的那一轮算不算数</b>。重试是「这一轮不算，重来」，
    /// 所以把它整轮甩在上下文之外；继续是「这一轮算一半」—— 已经跑过的工具结果、
    /// 已经想出来的思考、已经收到的正文全部原样留在上下文里，模型接着往下做。
    /// 一次长任务跑到一半被停掉，代价不该是前面那些步骤全部白费。
    ///
    /// 末尾补一句 user 提示：<b>只随这一轮发出去，不落库</b>（历史里凭空多出一条用户没打过的
    /// 消息会很怪）。没有这句话，模型看到的最后一条是它自己的半截回复，多半会直接收尾不动手。
    /// </summary>
    private async Task ContinueTurnAsync(string turnId)
    {
        if (_runtime.IsBusy)
        {
            Input.ShowHint("正在生成回复，先点「停止」再继续", InputHintKind.Info, autoHide: true);
            return;
        }

        await WaitForHistoryAsync("继续");

        // 按钮的显示条件已经保证过一次，但期间历史可能变了（这一轮后面又跑出了新回复、或那一轮被删了）。
        // 说清楚为什么不动，别让点了没反应 —— 那颗按钮留在原地是正常的，收掉它反而更奇怪。
        if (!_runtime.IsTurnResumable(turnId))
        {
            Input.ShowHint("这一轮后面已经有新内容了，不能再接着继续", InputHintKind.Info, autoHide: true);
            return;
        }

        var settings = await BuildTurnSettingsAsync("继续");
        if (settings is null) return;

        _autoScroll = true;
        await _runtime.ContinueAsync(turnId, settings);
    }

    /// <summary>
    /// 三个入口（重新生成 / 重试 / 继续）共用的准备：解析当前模型与客户端。
    /// 返回 null 表示发不出去，<b>提示已经给过了</b>。
    /// </summary>
    private async Task<TurnSettings?> BuildTurnSettingsAsync(string action)
    {
        var selected = Input.ModelSelector.SelectedModel;
        if (selected is null)
        {
            Input.ShowHint("尚未配置模型：请到「设置 → 模型服务」添加并启用服务商与模型", InputHintKind.Fail, autoHide: false);
            return null;
        }

        var client = await ResolveSendClientAsync(selected);
        if (client is null) return null;

        return new TurnSettings(selected, client, Input.ModelSelector.ThinkingMode,
            Input.ToolSelector.SelectedToolIds);
    }


    /// <summary>包含指定消息的分组下标；找不到返回 -1。</summary>
    private int FindGroupIndex(string messageId)
    {
        for (var i = 0; i < _turnGroups.Count; i++)
            if (_turnGroups[i].Msgs.Any(m => m.Id == messageId))
                return i;
        return -1;
    }

    /// <summary>
    /// 新回复要插进消息面板的下标：上下文末尾那组之后。
    /// 那一组没被渲染时返回面板末尾（生成结束后会整体重排，位置最终是对的）。
    /// </summary>
    private int ComputeInsertIndex(int afterSequence)
    {
        var group = FindGroupIndex(Messages.FirstOrDefault(m => m.SequenceNumber == afterSequence)?.Id ?? "");
        if (group < 0) return MessagesPanel.Children.Count;

        var index = group + 1 - _firstRenderedGroup;
        return Math.Clamp(index, 0, MessagesPanel.Children.Count);
    }

    // 说明：发送按钮 / 回车 / 停止按钮的处理、以及运行态视觉，都在 ChatInputControl 里。
    // 视图这边通过 Input.SendRequested / Input.StopRequested 事件接入（见构造函数）。

    // ---- 历史渲染 ----

    /// <param name="keepRendered">
    /// 至少渲染到包含这条消息的分组（含）。重新生成用：被点中的轮次可能在「最后一批」之外，
    /// 不能因为首屏只渲染最后 N 轮就把它所在的区域整个丢掉。
    /// </param>
    private void RenderHistory(string? keepRendered = null)
    {
        MessagesPanel.Children.Clear();

        _turnGroups = new List<(string? TurnId, List<ChatMessage> Msgs)>();
        _compressionCovered.Clear();
        using (PerfTrace.Measure("   分组 / 工具索引"))
        {
            BuildToolCallIndex();
            GroupMessages();
        }

        // 长会话首屏只渲染最后一批：历史再多也只付出这一批的建控件开销，
        // 用户能看到最新内容、能马上开始输入，更早的轮次等滚到顶部再加载。
        _historyBatchTurns = AppDefaults.ResolveHistoryBatchTurns();
        _firstRenderedGroup = Math.Max(0, _turnGroups.Count - _historyBatchTurns);
        if (keepRendered is not null)
        {
            var keep = FindGroupIndex(keepRendered);
            if (keep >= 0) _firstRenderedGroup = Math.Min(_firstRenderedGroup, keep);
        }
        PerfTrace.Info($"分组 {_turnGroups.Count} 轮，首屏渲染 {_turnGroups.Count - _firstRenderedGroup} 轮");

        using (PerfTrace.Measure("   首屏建控件"))
            AppendGroups(_firstRenderedGroup, _turnGroups.Count);

        _activeTurnId = null;
        _activeTurnRoot = null;
        _activeTurnHost = null;
        _activeTurnCard = null;
        _activeTurnBody = null;

        // 首屏这批渲染完，ScrollToEnd 还没落到布局上；先关掉「滚到顶部」的自动补齐，
        // 等布局帧跑完（此时 Offset 才真正到底部）再开放，否则会把历史一次全补回来。
        _suppressEarlierLoad = true;
        var firstScreenShownTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(
            () =>
            {
                // 到这里说明首屏那一批的布局已经跑完 —— 这段就是「首屏布局耗时」
                PerfTrace.Note("③ 首屏布局（插入→布局完成）",
                    System.Diagnostics.Stopwatch.GetElapsedTime(firstScreenShownTicks).TotalMilliseconds);

                _suppressEarlierLoad = false;

                // 报告到此为止：这里就是「打开会话窗口」的完整成本。
                // 之后再往上滚加载的批次会各自出一份小报告。
                PerfTrace.Flush();
            },
            DispatcherPriority.Background);
    }

    /// <summary>把消息按「轮」分组，并统计每条压缩记录覆盖了多少条消息。</summary>
    private void GroupMessages()
    {
        // 压缩记录要单独摘出来渲染成一条分割线。不能走下面的分组：role 是 agent，
        // 既不是 user 也不是 assistant/tool，会被整个漏掉（上下文里有它、界面上却没有）。
        // 覆盖条数在这里顺手统计：从上一个压缩记录（不含）数到这一条。
        var lastCompression = -1;

        for (var i = 0; i < Messages.Count; i++)
        {
            var m = Messages[i];

            if (ContextCompression.IsCompression(m))
            {
                _compressionCovered[m.Id] = lastCompression < 0 ? i : i - lastCompression;
                lastCompression = i;
                _turnGroups.Add((m.Id, new List<ChatMessage> { m }));
                continue;
            }

            if (m.Role == "user")
            {
                _turnGroups.Add((null, new List<ChatMessage> { m }));
            }
            else if (m.Role is "assistant" or "tool" or "error")
            {
                var key = m.TurnId;
                var last = _turnGroups.Count > 0 ? _turnGroups[^1] : default;
                if (last.TurnId is not null && last.TurnId == key)
                    last.Msgs.Add(m);
                else
                    _turnGroups.Add((key, new List<ChatMessage> { m }));
            }
        }
    }

    /// <summary>渲染 <c>[from, to)</c> 区间的分组并追加到消息面板末尾。</summary>
    private void AppendGroups(int from, int to)
    {
        for (var i = from; i < to && i < _turnGroups.Count; i++)
            MessagesPanel.Children.Add(BuildGroupControl(_turnGroups[i]));
    }

    /// <summary>
    /// 滚到接近顶部时把更早的一批补到列表最前面，每批 <see cref="_historyBatchTurns"/> 轮，
    /// 直到最早的一轮也已渲染。
    ///
    /// <b>刻意不做后台预渲染。</b>曾经试过「进窗后趁空闲把更早的轮次一批批补上」，
    /// 实测反而更糟：长会话下它要在 UI 线程上分好几批干活（构造 + 每批一次全量重新布局），
    /// 而这正是用户切标签页时抢不到 UI 线程的原因。既然渲染已经不便宜，
    /// 就让「谁滚谁加载」——用户明确要看才付这个成本，不看不付。
    /// </summary>
    private void LoadEarlierHistory()
    {
        if (_loadingEarlier || _restoringScroll) return;
        if (_firstRenderedGroup <= 0) return;

        PerfTrace.Begin($"会话 {Profile.State.SessionId} — 向上加载一批");
        PerfTrace.Info($"此前已渲染 {_turnGroups.Count - _firstRenderedGroup} 轮");

        using (PerfTrace.Measure("① 向上加载：构造本批"))
            RenderEarlierBatch();
    }

    /// <summary>
    /// 把更早的一批（<see cref="_historyBatchTurns"/> 轮）补到列表最前面，并补偿滚动位置。
    ///
    /// 补偿是必须的：插入点在视口上方时内容整体下移，而 Offset 不变，
    /// 视口就会滑向更早的内容（表现为「越来越往上跑、回不到底部」）。
    /// 用户本来就在底部时，补偿后正好仍等于新的底部。
    /// </summary>
    private void RenderEarlierBatch()
    {
        if (_firstRenderedGroup <= 0) return;

        _loadingEarlier = true;
        try
        {
            var from = Math.Max(0, _firstRenderedGroup - _historyBatchTurns);

            // 先把这一批构造出来（构造期间不碰面板，避免多次布局）
            var batch = new List<Control>();
            for (var i = from; i < _firstRenderedGroup; i++)
                batch.Add(BuildGroupControl(_turnGroups[i]));

            // 记下插入前的滚动状态：控件插在最前面会把已有内容整体下推，
            // 不补偿的话视口会「跳」——用户正在看的那条消息会瞬移出屏幕。
            var oldExtent = MessagesScroll.Extent.Height;
            var oldOffset = MessagesScroll.Offset.Y;
            var wasAtBottom = IsAtBottom();

            for (var i = 0; i < batch.Count; i++)
                MessagesPanel.Children.Insert(i, batch[i]);

            _firstRenderedGroup = from;
            _restoringScroll = true;

            // 等这一帧布局跑完，Extent 才反映插入后的高度，这时才能算补偿量
            var insertedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // 到这里布局已经跑完 —— 记下这段（构造之外的真正开销）
                    PerfTrace.Note("② 向上加载：布局",
                        System.Diagnostics.Stopwatch.GetElapsedTime(insertedTicks).TotalMilliseconds);

                    if (wasAtBottom)
                    {
                        // 本来贴底就直接滚到底：用高度差做加法会有累积误差，
                        // 几十批下来会累积成肉眼可见的偏移。
                        MessagesScroll.ScrollToEnd();
                        return;
                    }

                    var delta = MessagesScroll.Extent.Height - oldExtent;
                    if (delta > 0)
                        MessagesScroll.Offset = new Vector(MessagesScroll.Offset.X, oldOffset + delta);
                }
                finally
                {
                    _restoringScroll = false;
                    PerfTrace.Flush();
                }
            }, DispatcherPriority.Loaded);
        }
        finally
        {
            _loadingEarlier = false;
        }
    }

    /// <summary>是否已滚到接近顶部（该补下一批了）。</summary>
    private bool IsNearTop()
    {
        // 按视口高度的一屏多一点判定：滚轮滑得快时也不容易「冲过」触发点
        var threshold = Math.Max(200, MessagesScroll.Viewport.Height * 1.2);
        return MessagesScroll.Offset.Y <= threshold;
    }

    /// <summary>是否已贴到底部（留一点容差，浮点与滚动动画都可能差几个像素）。</summary>
    private bool IsAtBottom()
    {
        var max = MessagesScroll.ScrollBarMaximum.Y;
        return max - MessagesScroll.Offset.Y <= 2;
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateAutoScroll();

        // 首屏还没定位到底部，此时的 Offset=0 不代表用户在看顶部
        if (_suppressEarlierLoad) return;
        // 只在还有更早历史时才关心滚动位置
        if (_firstRenderedGroup <= 0) return;
        if (IsNearTop()) LoadEarlierHistory();
    }

    // ---- 自动跟随最新内容 ----

    /// <summary>
    /// 是否让新内容把视图拽到底部。默认开；用户一往上滚就关（他正在回看历史），
    /// 滚回底部或主动发消息时再打开。
    /// </summary>
    private bool _autoScroll = true;

    /// <summary>正在拖滚动条。用来区分「用户滚走了」与「内容自己长高了」。</summary>
    private bool _scrollbarGesture;

    /// <summary>
    /// 只认「用户操作」与「是否贴底」两个信号，不去比对偏移量。
    ///
    /// 为什么不能靠偏移量判断：内容一追加，<c>Extent</c> 就变高，此时 Offset 一动没动却已经
    /// 「不贴底」了 —— 拿这个去关闭自动跟随，等于每次流式输出都会自己把自己关掉。
    /// 所以：滚轮向上 / 拖滚动条离开底部 → 关；任何时候贴底 → 开。
    /// </summary>
    private void SetupAutoScroll()
    {
        // handledEventsToo: SmoothScrollViewer 自己也会在隧道阶段截滚轮做缓动并标记 handled，
        // 不打开这个开关，事件在被它吃掉之后我们就再也看不到了。
        MessagesScroll.AddHandler(
            PointerWheelChangedEvent,
            OnMessagesWheel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesScroll.AddHandler(
            PointerPressedEvent,
            OnMessagesPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessagesScroll.PointerReleased += (_, _) => _scrollbarGesture = false;
    }

    private void OnMessagesWheel(object? sender, PointerWheelEventArgs e)
    {
        // 向上滚 = 我要回看历史，别再把我拽下去。
        // 反方向（滚回底部就恢复跟随）不在这里处理：滚轮的位移要好几帧缓动才落定，
        // 此刻测到的位置还不是最终位置；交给 ScrollChanged 里的「贴底即恢复」一条规则管。
        if (e.Delta.Y > 0) _autoScroll = false;
    }

    private void OnMessagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 拖滚动条也是一种「我要自己控制位置」，和滚轮同等对待
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, MessagesScroll); v = v.GetVisualParent())
            if (v is ScrollBar or Thumb)
            {
                _scrollbarGesture = true;
                return;
            }
    }

    private void UpdateAutoScroll()
    {
        if (IsAtBottom()) _autoScroll = true;
        else if (_scrollbarGesture) _autoScroll = false;
    }

    /// <summary>把一个分组渲染成控件。分组只有两种形态：压缩分割线、或一个完整的轮次块。</summary>
    private Control BuildGroupControl((string? TurnId, List<ChatMessage> Msgs) group)
    {
        var (turnId, msgs) = group;

        if (msgs.Count == 1 && ContextCompression.IsCompression(msgs[0]))
            return CreateCompressionDivider(msgs[0], _compressionCovered.GetValueOrDefault(msgs[0].Id));

        return BuildTurnBlock(turnId, msgs);
    }

    /// <summary>
    /// 渲染一个轮次块并<b>返回</b>控件（不直接挂到面板上）。
    /// 之所以要先构造后挂载：向上补齐历史时需要把控件插到列表最前面，
    /// 直接往面板追加的老写法做不到这件事。
    /// </summary>
    private Control BuildTurnBlock(string? turnId, List<ChatMessage> msgs)
    {
        var first = msgs[0];
        if (first.Role == "user")
            return CreateUserMessageBlock(first);

        var (displayName, providerName) = ModelInfo(first.ModelName ?? "");
        var rootPanel = new StackPanel
        {
            Margin = new Thickness(20, 2, 20, 2),
            Spacing = 10,
        };
        rootPanel.Children.Add(CreateModelHeader(displayName, providerName, first.CreatedAt));

        // 内容区（思考/工具聚合卡 + 各正文块）统一放进蓝框宿主，供原文模式切换
        var host = new TurnSelectionHost { BuildFullRawText = () => BuildFullRawText(turnId) };
        rootPanel.Children.Add(host.Border);

        TurnActivityCard? card = null;
        var toolCount = 0;
        var thinkingCount = 0;

        // 整轮只有最后那段正文是结论（留在卡外），更早的正文都只是过程叙述，收进聚合卡里
        var lastContentIndex = -1;
        for (var i = 0; i < msgs.Count; i++)
            if (msgs[i].Role == "assistant" && !string.IsNullOrWhiteSpace(msgs[i].Content))
                lastContentIndex = i;

        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                {
                    using (PerfTrace.Measure("     思考卡 ThinkingCard"))
                    {
                        card ??= AttachActivityCard(host, expand: false);
                        var thinkCard = new ThinkingCard();
                        thinkCard.SetText(m.Thinking);
                        thinkCard.Show();
                        card.Steps.Children.Add(thinkCard.Root);
                    }
                    thinkingCount++;
                }
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    using (PerfTrace.Measure("     正文块 TurnBodyView"))
                    {
                        var body = new TurnBodyView();
                        body.AppendText(m.Content);
                        body.Complete();

                        if (i == lastContentIndex)
                            host.AddBody(body);
                        else
                        {
                            card ??= AttachActivityCard(host, expand: false);
                            host.AddNarration(body);
                        }
                    }
                }
            }
            else if (m.Role == "tool")
            {
                using (PerfTrace.Measure("     工具卡 ToolCallCard"))
                {
                    card ??= AttachActivityCard(host, expand: false);
                    var toolCard = new ToolCallCard();
                    toolCard.SetData(m.ToolName ?? "工具", FindToolCallInput(m.ToolCallId), m.Content ?? "");
                    card.Steps.Children.Add(toolCard.Root);
                }
                toolCount++;
            }
            else if (ChatError.IsError(m))
            {
                // 错误卡排在那一轮的末尾：它就是这一轮的结果。
                // 历史里的错误卡同样带「重试」：隔了半天回来看到「余额用尽」，
                // 充完钱在这一轮上接着跑，比翻上去找原来那句用户消息再「重新生成」顺手。
                host.ContentPanel.Children.Add(
                    new ErrorCard(m.Content, onRetry: () => _ = RetryTurnAsync(m)).Root);
            }
        }

        // 历史记录：聚合卡显示推算出的用时并保持折叠
        card?.SetFinished(toolCount, thinkingCount, TurnDuration(msgs));

        if (turnId is not null)
            rootPanel.Children.Add(CreateAiReplyOps(turnId, msgs.Where(m => m.Role == "assistant").ToList(), host));

        return rootPanel;
    }

    /// <summary>
    /// 创建并挂上一轮的空聚合卡（首次出现思考或工具调用时调用）。
    /// 实时轮出生即展开（<paramref name="expand"/> 默认 true），历史轮保持折叠。
    /// </summary>
    private TurnActivityCard AttachActivityCard(TurnSelectionHost host, bool expand = true)
    {
        var card = new TurnActivityCard();
        // 历史轮（没有在计时）显示「处理中」；实时轮直接带上当前耗时，避免先闪一下「处理中」
        card.SetProcessing(0, 0, ElapsedSinceTurnStart());
        if (expand) card.Expand();
        host.SetCard(card);
        return card;
    }

    /// <summary>流式过程中刷新聚合卡头部（已耗时 + 计数）。</summary>
    private void RefreshActivityHeader()
        => _activeTurnCard?.SetProcessing(_turnToolCount, _turnThinkingCount, ElapsedSinceTurnStart());

    /// <summary>
    /// 本轮运行期间的逐秒计时器。计数只在思考/工具到达时才变，光靠那些事件刷新的话
    /// 头部的时间会冻在最后一次事件的时刻，所以另开一个定时器让它自己走。
    /// </summary>
    private DispatcherTimer? _turnTimer;

    private void StartTurnTimer()
    {
        StopTurnTimer();
        _turnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _turnTimer.Tick += OnTurnTimerTick;
        _turnTimer.Start();
    }

    private void OnTurnTimerTick(object? sender, EventArgs e) => RefreshActivityHeader();

    /// <summary>停止逐秒计时。运行中的 DispatcherTimer 会吊住整个页面，收尾必须显式停。</summary>
    private void StopTurnTimer()
    {
        if (_turnTimer is null) return;
        _turnTimer.Stop();
        _turnTimer.Tick -= OnTurnTimerTick;
        _turnTimer = null;
    }

    /// <summary>回复结束：聚合卡翻为用时统计并折叠。</summary>
    private void FinishActivityCard()
    {
        StopTurnTimer();
        _activeTurnCard?.SetFinished(_turnToolCount, _turnThinkingCount, TurnElapsed());
        _activeTurnCard?.Collapse();
        _activeTurnBody = null;   // 这就是最后那段正文，留在卡外
        _turnStartedAt = 0;
    }

    /// <summary>
    /// 本轮用时。优先用网关落库的值 —— 它从请求真正发出时开始算，
    /// 与重开会话后读到的完全一致；拿不到才退回视图自己的计时。
    /// </summary>
    private TimeSpan? TurnElapsed()
    {
        var stored = _activeTurnId is null
            ? 0
            : Messages.Where(m => m.Role == "assistant" && m.TurnId == _activeTurnId)
                       .Max(m => m.DurationMs ?? 0);
        return stored > 0 ? TimeSpan.FromMilliseconds(stored) : ElapsedSinceTurnStart();
    }

    /// <summary>本轮自请求开始至今的耗时；没有开始时刻（历史轮、异常路径）时返回 null。</summary>
    private TimeSpan? ElapsedSinceTurnStart()
        => _turnStartedAt <= 0
            ? null
            : TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _turnStartedAt);

    /// <summary>
    /// 历史轮的用时。优先取落库的 duration_ms；老数据没有这一列，退回时间戳推算
    /// （首条创建 → 末条最后更新）—— 单轮消息的这两个值相等，推不出来，那种情况返回 null。
    /// </summary>
    private static TimeSpan? TurnDuration(List<ChatMessage> msgs)
    {
        var stored = msgs.Count == 0 ? 0 : msgs.Max(m => m.DurationMs ?? 0);
        if (stored > 0) return TimeSpan.FromMilliseconds(stored);

        if (msgs.Count == 0) return null;
        var start = msgs.Min(m => m.CreatedAt);
        var end = msgs.Max(m => Math.Max(m.UpdatedAt, m.CreatedAt));
        return start > 0 && end > start ? TimeSpan.FromMilliseconds(end - start) : null;
    }

    /// <summary>
    /// 拼装一轮回复的「全部原文」：按消息顺序输出思考 / 工具调用 / 正文的未渲染原文。
    /// </summary>
    private string BuildFullRawText(string? turnId)
    {
        var sb = new StringBuilder();
        foreach (var m in Messages)
        {
            if (m.TurnId != turnId) continue;

            if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                    sb.AppendLine("[思考] " + m.Thinking.TrimEnd()).AppendLine();
                if (!string.IsNullOrWhiteSpace(m.Content))
                    sb.AppendLine("[正文] " + m.Content.TrimEnd()).AppendLine();
            }
            else if (m.Role == "tool")
            {
                sb.AppendLine($"[工具] {m.ToolName ?? "工具"}");
                sb.AppendLine("输入: " + FindToolCallInput(m.ToolCallId));
                sb.AppendLine("输出: " + (string.IsNullOrWhiteSpace(m.Content) ? "（无输出）" : m.Content.TrimEnd()));
                sb.AppendLine();
            }
            else if (ChatError.IsError(m))
            {
                sb.AppendLine("[错误] " + (m.Content ?? "").TrimEnd()).AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>实时：工具结果以可折叠工具卡追加到当前 turn 根（无根时兜底追加到消息面板）。</summary>
    private void AddToolCallCard(ChatMessage toolMsg)
    {
        var card = new ToolCallCard();
        card.SetData(toolMsg.ToolName ?? "工具", FindToolCallInput(toolMsg.ToolCallId), toolMsg.Content ?? "");
        AttachToolCard(card);
    }

    /// <summary>
    /// 用户按下停止：把还停在「接收参数 / 执行中」的实时工具卡收成终态，
    /// 然后清掉索引。网关不会再回调这些卡了 —— 不处理的话它们会一直转下去，
    /// 直到用户重开这个会话（历史是按落库状态重绘的）。
    /// </summary>
    private void MarkLiveToolCardsInterrupted()
    {
        foreach (var card in _toolCardsById.Values) card.MarkInterrupted();
        foreach (var card in _unclaimedToolCards) card.MarkInterrupted();

        _toolCardsById.Clear();
        _unclaimedToolCards.Clear();
    }

    /// <summary>
    /// 把一张工具卡挂进本轮：计数、必要时把卡外那段叙述收进聚合卡（顺序才对）、滚动。
    /// 参数刚开始流式时就要挂卡，所以建卡与挂卡分成两步。
    /// </summary>
    private void AttachToolCard(ToolCallCard card)
    {
        _turnToolCount++;
        if (_activeTurnHost is null)
        {
            // 兜底：无活动 turn 时直接挂到消息面板
            MessagesPanel.Children.Add(card.Root);
            return;
        }

        _activeTurnCard ??= AttachActivityCard(_activeTurnHost);
        // 卡外那段正文后面又来了工具调用，说明它只是过程叙述：先把它收进卡里，顺序正好在这次调用之前
        MoveActiveBodyToCard();
        _activeTurnCard.Steps.Children.Add(card.Root);
        RefreshActivityHeader();
        _activeTurnCard.ScrollContentToEnd();
        ScrollToEnd();
    }

    /// <summary>
    /// 把卡外那段正文收进聚合卡。整轮只有最后一段正文是结论，留在卡外 ——
    /// 所以只在「后面确实又出现了工具调用 / 新一轮」时调用它。
    /// </summary>
    private void MoveActiveBodyToCard()
    {
        if (_activeTurnBody is null || _activeTurnHost is null) return;
        _activeTurnCard ??= AttachActivityCard(_activeTurnHost);
        _activeTurnHost.MoveBodyIntoCard(_activeTurnBody);
        _activeTurnBody = null;
    }

    /// <summary>
    /// 懒修复历史里「配不上对」的工具调用：带 tool_calls 的 assistant 消息后面缺了对应的 tool 结果。
    ///
    /// 成因是应用在工具执行途中被关掉 —— assistant 那条（含 tool_calls）已经落库，工具结果还没写。
    /// 危害不止这一轮：那条残缺消息会一直留在历史里，此后<b>每次</b>发送都被服务端 400 拒掉
    /// （<c>insufficient tool messages following tool_calls message</c>），会话再也发不出消息，
    /// 除非手动删掉那一轮。
    ///
    /// 只修当前会话，且只在<b>打开会话</b>与<b>发送前</b>各跑一次 —— 全库扫描在长会话上代价太大，
    /// 而这两个时刻正好是「马上要发出去」和「刚打开来看」的时候，懒检查足够覆盖。
    /// 没有坏消息时它就只是一遍内存扫描，一个字都不写库。
    /// </summary>
    private async Task RepairToolCallPairingAsync()
    {
        var touched = ToolCallRepair.RepairInPlace(Messages);
        if (touched.Count == 0) return;

        foreach (var message in touched)
        {
            // 落库失败不影响这次发送：网关拼请求时还会再修一遍（ToolCallRepair.ForWire）
            try { await ChatRepository.UpdateMessageAsync(message); } catch { /* 忽略 */ }
        }

        // metadata 变了，入参索引跟着失效，重建一次
        BuildToolCallIndex();
    }

    /// <summary>按 tool_call_id 反查输入参数（来自对应 assistant 消息的 metadata 里的 tool_calls）。</summary>
    private string FindToolCallInput(string? toolCallId)
        => string.IsNullOrWhiteSpace(toolCallId)
            ? "{}"
            : _toolCallInputs.GetValueOrDefault(toolCallId, "{}");

    /// <summary>
    /// 一次性建好 tool_call_id → arguments 的索引。
    /// 原先 FindToolCallInput 每渲染一个工具卡就遍历全部消息并逐个反序列化 metadata，
    /// 整体是 O(消息数 × 工具卡数)；工具调用密集的长会话里，这一项比读库还慢。
    /// </summary>
    private void BuildToolCallIndex()
    {
        _toolCallInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in Messages)
        {
            if (m.Role != "assistant") continue;
            foreach (var call in ToolCallJson.Deserialize(m.Metadata))
            {
                if (!string.IsNullOrEmpty(call.Id))
                    _toolCallInputs[call.Id] = call.Arguments ?? "{}";
            }
        }
    }

    // ---- 消息操作（复制 / 编辑 / 重新生成 / 删除）与 Tokens 汇总 ----

    /// <summary>「重新生成」按钮的字形（Fluent System Icons：Sync）。</summary>
    private const string RegenerateIcon = "\uF191";

    /// <summary>「继续」按钮的字形（Fluent System Icons：Play）。</summary>
    private const string ContinueIcon = "\uF606";

    /// <summary>
    /// 「重新生成」按钮：用户消息与 AI 消息共用同一个入口，
    /// 点哪一条都重新生成「那一轮」的 AI 回复（见 <see cref="RegenerateAsync"/>）。
    /// </summary>
    private Button RegenerateButton(Func<ChatMessage?> resolveTarget)
        => CreateGlyphButton(RegenerateIcon, "重新生成",
            () => { if (resolveTarget() is { } t) _ = RegenerateAsync(t); });

    /// <summary>
    /// 「继续」按钮：接上被停止 / 被打断的那一轮，而不是丢掉它重开一轮（见 <see cref="ContinueTurnAsync"/>）。
    /// 只在「这一轮就是最后一轮、而且它没跑完」时出现在操作栏里。
    /// </summary>
    private Button ContinueButton(string turnId)
        => CreateGlyphButton(ContinueIcon, "继续（接着被中断的那一轮往下做）",
            () => _ = ContinueTurnAsync(turnId), hoverColor: "#2563EB");


    private static (long Input, long Cache, long Output) ParseUsage(ChatMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.UsageJson)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(m.UsageJson);
            var root = doc.RootElement;
            var input = root.TryGetProperty("input_tokens", out var a) ? a.GetInt64() : 0;
            var cache = root.TryGetProperty("cache_hit_tokens", out var b) ? b.GetInt64() : 0;
            var output = root.TryGetProperty("output_tokens", out var c) ? c.GetInt64() : 0;
            return (input, cache, output);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private Button CreateGlyphButton(string glyph, string tooltip, Action onClick, string hoverColor = "#374151")
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = AppFonts.Icons,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = icon,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.PointerEntered += (_, _) => icon.Foreground = new SolidColorBrush(Color.Parse(hoverColor));
        btn.PointerExited += (_, _) => icon.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// AI 回复操作栏（位于内容蓝框之外）：左侧复制/编辑/删除 + 原文模式切换，右侧 Tokens 汇总（同 turn 累加）。
    /// </summary>
    private Control CreateAiReplyOps(string turnId, IReadOnlyList<ChatMessage> assistantMsgs, TurnSelectionHost host)
    {
        long input = 0, cache = 0, output = 0;
        var copyText = new StringBuilder();
        foreach (var m in assistantMsgs)
        {
            var t = ParseUsage(m);
            input += t.Input;
            cache += t.Cache;
            output += t.Output;
            if (!string.IsNullOrWhiteSpace(m.Content))
                copyText.AppendLine(m.Content.TrimEnd());
        }
        var total = input + output;

        var copyBtn = CreateGlyphButton("\uF32C", "复制", async () => await CopyText(copyText.ToString()));
        var delBtn = CreateGlyphButton("\uF34D", "删除", async () => await DeleteTurnAsync(turnId), hoverColor: "#DC2626");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { copyBtn, RegenerateButton(() => Messages.FirstOrDefault(
                m => m.TurnId == turnId && m.Role is "assistant" or "tool")), host.CreateModeButtons(), delBtn },
        };

        // 「继续」排在最前面：这一轮停在半路时它就是用户最想要的那个动作
        if (_runtime.IsTurnResumable(turnId))
            left.Children.Insert(0, ContinueButton(turnId));
        var tokens = new TextBlock
        {
            Text = $"Tokens: {total}  ↑{input}  ↑*{cache}  ↓{output}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        grid.Children.Add(left);
        grid.Children.Add(tokens);
        Grid.SetColumn(tokens, 1);
        return grid;
    }

    private Control CreateUserOps(ChatMessage msg)
    {
        var text = msg.Content ?? "";
        var copyBtn = CreateGlyphButton("\uF32C", "复制", async () => await CopyText(text));
        var editBtn = CreateGlyphButton("\uF3DE", "编辑", () => _ = EditUserMessageAsync(msg));
        var delBtn = CreateGlyphButton("\uF34D", "删除", async () => await DeleteMessageAsync(msg.Id), hoverColor: "#DC2626");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 20, 8),
            Children = { copyBtn, editBtn, RegenerateButton(() => msg), delBtn },
        };
    }

    private Control CreateUserMessageBlock(ChatMessage msg)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 0,
        };
        var (bubble, block) = CreateUserElement();
        SetUserBubbleContent(block, msg);
        stack.Children.Add(bubble);
        stack.Children.Add(CreateUserOps(msg));
        return stack;
    }

    /// <summary>
    /// 用户气泡正文。正文里的附件标记（<c>[agent-file:///…]</c>）渲染成内联文件卡片
    /// （放在文本流里，所以「图片在句子的哪个位置」这个信息是可见的）。
    ///
    /// <b>渲染只认正文本身</b>，不查 metadata —— 标记是自解释的，所以：
    /// 附件被删掉之后卡片照常显示（只是点开无处可去）、用户手打的标记也能渲染、
    /// 复制出去的内容粘回来还是同一个东西。这三件事以前靠「拿 metadata 里的路径
    /// 去正文里反查」是做不到的。
    ///
    /// 旧消息<see cref="AttachmentMarker"/> 建立之前落的正文里是裸路径，
    /// 这里按普通文本显示 —— 那是正常可读的文字，不做特殊适配。
    /// </summary>
    private static void SetUserBubbleContent(CustomSelectableTextBlock block, ChatMessage msg)
    {
        var text = msg.Content ?? "";

        var spans = AttachmentMarker.Scan(text);
        if (spans.Count == 0)
        {
            block.Text = text;
            return;
        }

        var inlines = new InlineCollection();
        var cursor = 0;

        foreach (var span in spans)
        {
            if (span.Start > cursor)
                inlines.Add(new Run(text[cursor..span.Start]));

            // 路径解析不出来（不该发生，Scan 已保证非空）时退回纯文本，绝不吞掉内容
            if (AttachmentMarker.ToAttachment(span.Path) is { } info)
                inlines.Add(new InlineUIContainer(new AttachmentChip(info, null, compact: true)));
            else
                inlines.Add(new Run(text.Substring(span.Start, span.Length)));

            cursor = span.End;
        }

        if (cursor < text.Length)
            inlines.Add(new Run(text[cursor..]));

        block.Inlines = inlines;
    }

    private async Task CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null) return;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            await top.Clipboard.SetDataAsync(transfer);
        }
        catch
        {
            // 剪贴板不可用时忽略
        }
    }

    private async Task DeleteTurnAsync(string turnId)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这段回复吗？")) return;
        try { await ChatRepository.SoftDeleteTurnAsync(Profile.State.SessionId, turnId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    private async Task DeleteMessageAsync(string messageId)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这条消息吗？")) return;
        try { await ChatRepository.SoftDeleteMessageAsync(Profile.State.SessionId, messageId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    /// <summary>
    /// 从库里重读并重绘。数据在 <see cref="ConversationRuntime.ReloadMessagesAsync"/> 里，
    /// 重绘走 <see cref="OnHistoryRebuildRequested"/> —— 两边共用同一条路，
    /// 不会出现「重读了但没重绘」或者反过来。
    /// </summary>
    private Task ReloadMessagesAsync() => _runtime.ReloadMessagesAsync();

    /// <summary>
    /// 正在流式渲染的这一段回复。<b>每段 assistant 消息一个</b>（工具循环里会有多段），
    /// 三条增量事件靠它定位该往哪儿追加。
    /// </summary>
    private sealed class RoundRender
    {
        public required TurnBodyView Body { get; init; }
        public required TurnSelectionHost Host { get; init; }

        /// <summary>首个思考分片到达时才建，之后一直往里追加。</summary>
        public ThinkingCard? ThinkCard { get; set; }

        /// <summary>本轮按 index 累积每个调用的名字与入参（服务端会把一个名字/参数切成多片下发）。</summary>
        public Dictionary<int, (ToolCallCard Card, StringBuilder Name, StringBuilder Args)> Calls { get; } = new();
    }

    private RoundRender? _round;

    /// <summary>
    /// 一段 assistant 回复开始流式：建好这一段要用的控件，并记进 <see cref="_round"/>。
    /// 之后的三条增量事件（正文 / 思考 / 工具调用）都往这里追加。
    /// </summary>
    private void OnRoundStarted(ChatMessage assistantMsg)
    {
        var (displayName, providerName) = ModelInfo(assistantMsg.ModelName ?? "");

        // 上一轮的工具卡到这里已经收尾（结果已经回填），按 id 的索引可以清掉了。
        // 每轮都要清：不同轮的 index 都从 0 开始，留着上一轮的会串卡。
        _toolCardsById.Clear();
        _unclaimedToolCards.Clear();

        // 同一 turn（含多轮工具调用）复用根容器；新 turn 时重建
        if (assistantMsg.TurnId is null || assistantMsg.TurnId != _activeTurnId || _activeTurnRoot is null)
        {
            var root = new StackPanel
            {
                Margin = new Thickness(20, 2, 20, 2),
                Spacing = 10,
            };
            root.Children.Add(CreateModelHeader(displayName, providerName, assistantMsg.CreatedAt));

            var turnId = assistantMsg.TurnId;
            var host = new TurnSelectionHost { BuildFullRawText = () => BuildFullRawText(turnId) };
            root.Children.Add(host.Border);

            // 重新生成时插回原位置。位置由 runtime 在发起时定下、这里才落到控件上：
            // 发起那一刻算出来的下标，到建控件之间可能已经因为重绘而不作数了。
            // 普通发送没有这个值，追加到末尾。
            if (_runtime.TakePendingInsertSequence() is { } insertAfter)
            {
                var at = ComputeInsertIndex(insertAfter);
                if (at >= 0 && at <= MessagesPanel.Children.Count) MessagesPanel.Children.Insert(at, root);
                else MessagesPanel.Children.Add(root);
            }
            else
            {
                MessagesPanel.Children.Add(root);
            }
            ScrollToEnd();
            _activeTurnId = assistantMsg.TurnId;
            _activeTurnRoot = root;
            _activeTurnHost = host;
            _activeTurnCard = null;
            _activeTurnBody = null;
            _turnToolCount = 0;
            _turnThinkingCount = 0;
            // 计时从请求发出算起（此刻紧接着就是本轮第一次 StreamAsync）
            _turnStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            StartTurnTimer();
        }

        // 每条 assistant 消息一个正文块；整轮只把最后那段留在卡外，更早的等下一次工具调用出现时收进卡里
        MoveActiveBodyToCard();
        var body = new TurnBodyView();
        _activeTurnHost!.AddBody(body);
        _activeTurnBody = body;

        _round = new RoundRender { Body = body, Host = _activeTurnHost! };
    }

    private void OnContentDelta(string delta)
    {
        Input.MarkFirstOutput();
        _round?.Body.AppendText(delta);
        ScrollToEnd();
    }

    private void OnThinkingDelta(string delta)
    {
        Input.MarkFirstOutput();
        if (_round is not { } round) return;

        // 首个思考分片到达时才建卡（此前不显示任何卡片），之后持续累积文本
        if (round.ThinkCard is null)
        {
            _activeTurnCard ??= AttachActivityCard(round.Host);
            round.ThinkCard = new ThinkingCard();
            round.ThinkCard.Show();
            _activeTurnCard.Steps.Children.Add(round.ThinkCard.Root);
            _turnThinkingCount++;
            RefreshActivityHeader();
        }
        round.ThinkCard.Append(delta);
        _activeTurnCard?.ScrollContentToEnd();
        ScrollToEnd();
    }

    private void OnToolCallDelta(ToolCallDelta delta)
    {
        Input.MarkFirstOutput();
        if (_round is not { } round) return;

        // 第一次见到这个 index：参数还在传，卡就先立起来。
        // 等流结束才建卡的话，用户要盯着一个毫无反应的界面直到工具跑完 ——
        // 而在模型写调用参数的这段时间里，界面上其实什么都不会变。
        if (!round.Calls.TryGetValue(delta.Index, out var call))
        {
            var card = new ToolCallCard();
            card.SetStreaming(delta.Name ?? "工具");
            AttachToolCard(card);
            _unclaimedToolCards.Add(card);
            round.Calls[delta.Index] = call = (card, new StringBuilder(), new StringBuilder());
        }

        if (!string.IsNullOrEmpty(delta.Name))
            call.Card.SetStreamingName(call.Name.Append(delta.Name).ToString());

        if (!string.IsNullOrEmpty(delta.ArgumentsDelta))
            call.Card.SetStreamingInput(call.Args.Append(delta.ArgumentsDelta).ToString());

        if (!string.IsNullOrEmpty(delta.Id))
        {
            // 实时补入参索引：它原先只在读历史时构建，实时轮的调用查不到入参，
            // 展开工具卡看到的是「{}」（要重开会话才正常）。落库与渲染都靠它取入参。
            _toolCallInputs[delta.Id] = call.Args.ToString();
            _toolCardsById[delta.Id] = call.Card;
            _unclaimedToolCards.Remove(call.Card);
        }

        _activeTurnCard?.ScrollContentToEnd();
        ScrollToEnd();
    }

    /// <summary>按消息渲染：实时 user 气泡；历史加载走 RenderHistory 按 turn 分组，工具结果走 ToolCallCard。</summary>
    private void AddMessage(ChatMessage m)
    {
        switch (m.Role)
        {
            case "user":
                MessagesPanel.Children.Add(CreateUserMessageBlock(m));
                break;

            default:
                var (plainRoot, plainBlock) = CreatePlainBlock("#1A1A2E", 14);
                plainBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(plainRoot);
                break;
        }
    }

    /// <summary>用户消息：浅蓝气泡（黑字，便于选中高亮可辨），靠右。</summary>
    private (Control Root, CustomSelectableTextBlock Block) CreateUserElement()
    {
        // 单块文本，直接开原生选择：拖选/双击选词/Ctrl+C 均可，无跨块问题
        var block = new CustomSelectableTextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
            NativeSelectionEnabled = true,
        };
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#CBE1F3")),
            Padding = new Thickness(12, 10),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(80, 0, 20, 0),
            Child = block,
        };
        return (bubble, block);
    }

    private Control CreateModelHeader(string modelName, string providerName, long createdAt)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 2),
        };

        var avatar = new Border
        {
            Width = 35,
            Height = 35,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-45, 0, 10, 0),
            Child = new TextBlock
            {
                Text = "Ht",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var name = new TextBlock
        {
            Text = $"{modelName} | {providerName}",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var time = new TextBlock
        {
            Text = FormatTime(createdAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, time },
        };
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        return grid;
    }

    /// <summary>
    /// 根据模型调用 id 解析展示信息（显示名 + 服务商名）。早于模型配置体系的历史消息
    /// 可能压根没记模型名，或者记的那个模型后来被删了 —— 两种情况都原样显示能拿到的信息，
    /// 不猜、也不回退到任何写死的模型上。
    /// </summary>
    private (string DisplayName, string ProviderName) ModelInfo(string callId)
    {
        if (string.IsNullOrEmpty(callId)) return ("未知模型", "");
        var m = Input.ModelSelector.Models.FirstOrDefault(x => x.ModelName == callId);
        return m is not null
            ? (m.DisplayName, m.ProviderName)
            : (callId, "未知服务商");
    }

    private static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToLocalTime()
            .ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private (Control Root, TextBlock Block) CreatePlainBlock(string color, double fontSize)
    {
        var block = new CustomSelectableTextBlock
        {
            Text = "",
            FontSize = fontSize,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 2, 20, 2),
            NativeSelectionEnabled = true,
        };
        return (block, block);
    }

    private void ScrollToEnd()
    {
        // 用户正在往上回看：别把视图拽回底部（见 _autoScroll）
        if (!_autoScroll) return;

        MessagesScroll.ScrollToEnd();

        // 刚建出来的控件还没测量，此时 Extent 是旧的，ScrollToEnd 只能落在「当时以为的底部」。
        // 等这一帧布局完成后 Extent 才是真值，所以再补一次 —— 否则首屏会停在半空，
        // 随后又因更早的历史不断插入而越飘越高。
        // 用标志合并：跑一次就够，不必每次流式都排队。
        if (_scrollToEndPending) return;
        _scrollToEndPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollToEndPending = false;
                MessagesScroll.ScrollToEnd();
            },
            DispatcherPriority.Loaded);
    }

    // ---- 附件与草稿 ----
    //
    // 原先这里放着附件草稿（占位符 token ↔ 文件路径）与草稿落库的整套实现。
    // 它们已经搬走，原因见这条链路上的三处：
    //   * Controls/ChatInputControl.axaml(.cs) —— 控件把逻辑接到 Avalonia 事件上
    //   * Services/Attachments/AttachmentDraft.cs —— 纯逻辑（token 碰撞 / 光标处整体删除 / 孤儿清理 / 序列化）
    //
    // 视图这边只需在装配时把 Profile.Draft 交给控件（见 InitializeAsync 与构造函数）。

    // ============================================================
    // 可折叠「已深度思考」卡片
    // ============================================================

    private sealed class ThinkingCard
    {
        private const double ExpandedMaxHeight = 300;
        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

        private readonly ScrollViewer _contentHost;
        private readonly RotateTransform _chevron;
        private readonly Border _header;
        private bool _expanded;

        public Border Root { get; }
        public TextBlock ContentBlock { get; }

        public ThinkingCard()
        {
            var icon = CreateLightbulbIcon(new SolidColorBrush(Color.Parse("#333333")));

            var title = new TextBlock
            {
                Text = "已深度思考",
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#1F1F1F")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.Parse("#C5C5C7")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
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

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(18, 0, 18, 0),
            };
            var iconHost = new Border
            {
                Child = icon,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(iconHost, 0);
            headerGrid.Children.Add(iconHost);
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);
            Grid.SetColumn(chevron, 2);
            headerGrid.Children.Add(chevron);

            _header = new Border
            {
                Child = headerGrid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Height = 46,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _header.PointerEntered += (_, _) => _header.Background = new SolidColorBrush(Color.Parse("#99F0F0F0"));
            _header.PointerExited += (_, _) => _header.Background = Brushes.Transparent;
            _header.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed)
                    Toggle();
            };

            ContentBlock = new CustomSelectableTextBlock
            {
                Text = "",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#666666")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24,
                Margin = new Thickness(5, 18, 5, 18),
            };

            _contentHost = new SmoothScrollViewer()
            {
                Content = ContentBlock,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 0,
                Margin = new Thickness(18, 0, 18, 0),
                Classes = { "thin-scrollbar" },
                Opacity = 0,
                // 折叠时整块移出布局：只设 MaxHeight=0 挡不住测量 —— ScrollViewer 测量时
                // 给内容的约束在滚动方向上是无限大，里面的文本照样每次布局都被测一遍。
                IsVisible = false,
            };
            // 卡片里按下选字不该把卡片内容滚滚（原因见 DisableFocusBringIntoView）
            SmoothScrollViewer.DisableFocusBringIntoView(_contentHost);
            _contentHost.Transitions = new Transitions
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

            var root = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#00F7F8FA")),
                BorderBrush = new SolidColorBrush(Color.Parse("#ECEEF1")),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Children = { _header, _contentHost },
                },
            };
            Root = root;
        }

        public void SetText(string text) => ContentBlock.Text = text;
        public void Append(string text) => ContentBlock.Text += text;

        public void Show()
        {
            if (Root.IsVisible) return;
            Root.IsVisible = true;
        }

        public void Toggle()
        {
            _expanded = !_expanded;

            // 先恢复可见再起步：MaxHeight 从 0 动画到展开高度，顺序反了会先量到 0 高度。
            // 收起时直接不可见（连带跳过测量），代价是没有收起动画 —— 换来的是长会话不卡，
            // 而且折叠卡默认就是收起的，动画本来也看不到。
            _contentHost.IsVisible = _expanded;
            _contentHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
            _contentHost.Opacity = _expanded ? 1 : 0;
            _chevron.Angle = _expanded ? 90 : 0;
        }

        private static Control CreateLightbulbIcon(Brush brush)
            => new TextBlock
            {
                Text = "\uF4D7", // Fluent System Icons：灯泡（lightbulb）
                FontFamily = AppFonts.Icons,
                FontSize = 14,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
    }

    // ============================================================
    // 可折叠「{工具名} 工具」卡片
    // ============================================================

    private sealed class ToolCallCard
    {
        private const double ExpandedMaxHeight = 300;
        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

        private readonly ScrollViewer _contentHost;
        private readonly RotateTransform _chevron;
        private readonly Border _header;
        private readonly TextBlock _titleBlock;
        private readonly TextBlock _inputBlock;
        private readonly TextBlock _outputBlock;

        /// <summary>标题右侧的运行状态（接收参数… / 执行中…）。跑完就收起，所以空文本时整个隐藏。</summary>
        private readonly TextBlock _statusBlock;

        /// <summary>「输出」那一行的小标题。入参还在接收时整段输出都不存在，跟着输出块一起收起。</summary>
        private readonly TextBlock _outputLabel;

        /// <summary>上一次重排流式入参的时刻。参数分片很密，展开状态下不能每片都重排。</summary>
        private long _lastInputPaint;

        /// <summary>流式入参重排的最小间隔（毫秒）。只影响展开中的卡，折叠时一个字都不排版。</summary>
        private const int InputPaintIntervalMs = 120;

        /// <summary>卡片是否已经到了终态（有结果 / 已被中断）。终态不再接受状态切换。</summary>
        private bool _terminal;
        private readonly StackPanel _rawStack;
        private readonly StackPanel _swapRow;
        private readonly TextBlock _swapLabel;
        private readonly Grid _viewLayers;
        private bool _expanded;

        /// <summary>问答卡片视图（仅 ask_user_question 有）。</summary>
        private Control? _answerView;

        /// <summary>展开内容是否显示问答卡片（否则显示入参/出参原文）。</summary>
        private bool _cardMode = true;

        /// <summary>
        /// 工具入参原文，等首次展开时才美化成缩进 JSON。
        /// 历史卡默认折叠，而 write / edit 这类入参是整段文件内容 ——
        /// 为「没人会看」的折叠状态解析并重新排版 JSON 是白费功夫。
        /// </summary>
        private string _rawInputJson = "{}";

        /// <summary>入参是否已美化过。只做一次，展开后收起再展开不重复解析。</summary>
        private bool _inputFormatted;

        public Border Root { get; }

        public ToolCallCard()
        {
            var icon = new TextBlock
            {
                Text = "\uF35C", // Fluent System Icons：扳手（工具）
                FontFamily = AppFonts.Icons,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            _titleBlock = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#1F1F1F")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.Parse("#C5C5C7")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
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

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(18, 0, 18, 0),
            };
            var iconHost = new Border
            {
                Child = icon,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 状态跟在标题后面（同一格里排一行）：卡片默认折叠，运行状态是折叠状态下唯一
            // 能看出「这次调用正在跑」的地方，所以它必须在头上，不能藏在展开内容里。
            _statusBlock = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsVisible = false,
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _titleBlock, _statusBlock },
            };

            Grid.SetColumn(iconHost, 0);
            headerGrid.Children.Add(iconHost);
            Grid.SetColumn(titleRow, 1);
            headerGrid.Children.Add(titleRow);
            Grid.SetColumn(chevron, 2);
            headerGrid.Children.Add(chevron);

            _header = new Border
            {
                Child = headerGrid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Height = 46,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _header.PointerEntered += (_, _) => _header.Background = new SolidColorBrush(Color.Parse("#99F0F0F0"));
            _header.PointerExited += (_, _) => _header.Background = Brushes.Transparent;
            _header.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed)
                    Toggle();
            };

            _inputBlock = new CustomSelectableTextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                FontFamily = new FontFamily("Consolas"),
            };
            _outputBlock = new CustomSelectableTextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            };

            var contentStack = new StackPanel
            {
                Spacing = 4,
            };
            _outputLabel = MakeLabel("输出");
            contentStack.Children.Add(MakeLabel("输入"));
            contentStack.Children.Add(_inputBlock);
            contentStack.Children.Add(_outputLabel);
            contentStack.Children.Add(_outputBlock);
            _rawStack = contentStack;

            // ask_user_question 专用：默认显示问答卡片，右上角的切换按钮回到入参/出参原文
            _swapLabel = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _swapRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsVisible = false,
                Children = { CreateSwapButton(_swapLabel, () => SetCardMode(!_cardMode)) },
            };

            _viewLayers = new Grid();
            _viewLayers.Children.Add(contentStack);

            _contentHost = new SmoothScrollViewer()
            {
                Content = new StackPanel
                {
                    Spacing = 8,
                    Margin = new Thickness(5, 14, 5, 14),
                    Children = { _swapRow, _viewLayers },
                },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 0,
                Margin = new Thickness(18, 0, 18, 0),
                Classes = { "thin-scrollbar" },
                Opacity = 0,
                // 同 ThinkingCard：MaxHeight=0 挡不住测量，必须 IsVisible=false。
                // 工具输出常常很长，这里是长会话布局开销的大头。
                IsVisible = false,
            };
            // 同 ThinkingCard：卡片里按下选字不该把卡片内容滚滚
            SmoothScrollViewer.DisableFocusBringIntoView(_contentHost);
            _contentHost.Transitions = new Transitions
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

            var root = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#00F7F8FA")),
                BorderBrush = new SolidColorBrush(Color.Parse("#ECEEF1")),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Children = { _header, _contentHost },
                },
            };
            Root = root;
        }

        /// <summary>
        /// 参数正在流式接收：调用刚被模型吐出来，名字通常已经有了，入参照着分片一段段长。
        /// 这时候还没有输出，输出那一整段先收起来 —— 显示「（无输出）」会让人以为工具已经跑完了。
        /// </summary>
        public void SetStreaming(string toolName)
        {
            _terminal = false;
            _titleBlock.Text = $"{toolName} 工具";
            _rawInputJson = "";
            _inputFormatted = true;
            ShowOutput(false);
            SetStatus("接收参数…");
        }

        /// <summary>流式期间工具名的续片（服务端可能把名字切成几片，累积后的完整名由调用方给）。</summary>
        public void SetStreamingName(string toolName) => _titleBlock.Text = $"{toolName} 工具";

        /// <summary>
        /// 流式追加入参原文。分片很密，这里只累积原文：卡片默认折叠，没人看的内容一个字都不排版；
        /// 展开状态下按 <see cref="InputPaintIntervalMs"/> 节流重排（入参还没收完时是残缺 JSON，
        /// 排版会退化成原文，这正是我们要的）。
        /// </summary>
        public void SetStreamingInput(string inputJson)
        {
            _rawInputJson = inputJson;
            _inputFormatted = false;

            if (!_expanded) return;
            var now = Environment.TickCount64;
            if (now - _lastInputPaint < InputPaintIntervalMs) return;

            _lastInputPaint = now;
            EnsureInputFormatted();
        }

        /// <summary>参数收全了，工具开始执行：入参定稿，输出还没回来。</summary>
        public void SetRunning(string toolName, string inputJson)
        {
            _terminal = false;
            _titleBlock.Text = $"{toolName} 工具";
            _rawInputJson = inputJson;
            _inputFormatted = false;
            ShowOutput(false);
            SetStatus("执行中…");
            if (_expanded) EnsureInputFormatted();
        }

        private void SetStatus(string text)
        {
            _statusBlock.Text = text;
            _statusBlock.IsVisible = text.Length > 0;
        }

        private void ShowOutput(bool visible)
        {
            _outputLabel.IsVisible = visible;
            _outputBlock.IsVisible = visible;
        }

        /// <summary>
        /// 用户按了停止：卡片收到终态。执行中的工具不会再有回调，停在「执行中…」会一直转下去。
        /// 已经有结果的卡不动（<see cref="_terminal"/>）。
        /// </summary>
        public void MarkInterrupted()
        {
            if (_terminal) return;

            _terminal = true;
            ShowOutput(true);
            _outputBlock.Text = ToolCallRepair.InterruptedResult;
            SetStatus("已中断");
        }

        public void SetData(string toolName, string inputJson, string output)
        {
            _terminal = true;
            _titleBlock.Text = $"{toolName} 工具";
            _outputBlock.Text = string.IsNullOrWhiteSpace(output) ? "（无输出）" : output;
            ShowOutput(true);
            SetStatus("");

            _rawInputJson = inputJson;
            _inputFormatted = false;

            // 折叠着就先不碰：历史卡默认折叠，而 write / edit 的入参是整段文件内容，
            // 为「没人会看」的状态解析 + 重新排版 JSON 是纯浪费。
            if (_expanded)
                EnsureInputFormatted();
            else
                _inputBlock.Text = "";

            if (toolName == BuiltinTools.AskUserName)
                AttachAnswerView(inputJson, output);
        }

        /// <summary>
        /// ask_user_question：把入参（问题）与出参（作答）还原成问答卡片，默认就显示它。
        /// 还原不了（用户取消、工具报错、格式对不上）就保持原文视图。
        /// </summary>
        private void AttachAnswerView(string inputJson, string output)
        {
            var questions = AskUserQuestionTool.Parse(inputJson, out _);
            if (questions is null) return;
            var answers = AskUserQuestionTool.ParseAnswers(questions, output);
            if (answers is null) return;
            if (AskAnswerView.Build(questions, answers) is not { } view) return;

            _answerView = view;
            _viewLayers.Children.Add(view);
            _swapRow.IsVisible = true;
            SetCardMode(true);
        }

        /// <summary>切换展开内容的两种视图；重新展开不会重置用户的选择。</summary>
        private void SetCardMode(bool card)
        {
            if (_answerView is null) return;
            _cardMode = card;
            _answerView.IsVisible = card;
            _rawStack.IsVisible = !card;
            if (!card) EnsureInputFormatted();
            _swapLabel.Text = card ? "查看原始输入/输出" : "查看卡片";
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            // 卡片模式下没人看原文，那份 JSON 也就没必要排版
            if (_expanded && !_cardMode) EnsureInputFormatted();

            // 同 ThinkingCard：先恢复可见，再让 MaxHeight 从 0 动画展开
            _contentHost.IsVisible = _expanded;
            _contentHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
            _contentHost.Opacity = _expanded ? 1 : 0;
            _chevron.Angle = _expanded ? 90 : 0;
        }

        /// <summary>首次展开时才美化入参 JSON，之后复用结果，不重复解析。</summary>
        private void EnsureInputFormatted()
        {
            if (_inputFormatted) return;
            _inputFormatted = true;
            _inputBlock.Text = FormatJson(_rawInputJson);
        }

        private static Button CreateSwapButton(TextBlock label, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                Height = 24,
                Padding = new Thickness(8, 0),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            btn.PointerEntered += (_, _) => btn.Background = new SolidColorBrush(Color.Parse("#66F0F0F0"));
            btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
            btn.Click += (_, e) =>
            {
                // 卡片在可折叠头之外，别让点击冒泡上去把卡片收起来
                e.Handled = true;
                onClick();
            };
            return btn;
        }

        private static TextBlock MakeLabel(string text) => new()
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
        };

        private static string FormatJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }
    }
}
