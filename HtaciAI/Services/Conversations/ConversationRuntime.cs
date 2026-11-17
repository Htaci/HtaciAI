using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Notifications;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Subagents;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Conversations;

/// <summary>一轮对话结束后交给宿主的结果。</summary>
/// <param name="Result">网关的返回值；压缩/准备阶段就失败时为 null。</param>
/// <param name="StoppedByUser">是不是用户按「停止」结束的。</param>
/// <param name="IsRegenerate">这一轮是不是「重新生成」（宿主据此决定要不要整体重排历史）。</param>
/// <param name="TitleChanged">只是标题变了（自动话题命名完成），没有新的轮次结束。</param>
public sealed record TurnOutcome(
    ChatTurnResult? Result,
    bool StoppedByUser,
    bool IsRegenerate,
    bool TitleChanged = false);

/// <summary>一次回复总结的结果。</summary>
public sealed record TurnSummaryResult(bool Summarized, string? Error)
{
    /// <summary>没总结（不满足条件，或已经在总结了）。自动总结走的就是这条。</summary>
    public static TurnSummaryResult Skipped { get; } = new(false, null);

    public static TurnSummaryResult Done { get; } = new(true, null);

    public static TurnSummaryResult Failed(string reason) => new(false, reason);
}

/// <summary>一次压缩的结果。<see cref="MessageCount"/> 只在真的压了时才有意义。</summary>
public sealed record CompressResult(bool Compressed, int MessageCount, string? Error, bool NothingToCompress)
{
    /// <summary>没压（不满足条件，或已经在压了）。自动压缩走的就是这条。</summary>
    public static CompressResult Skipped { get; } = new(false, 0, null, false);

    /// <summary>手动触发，但没有可压的内容。</summary>
    public static CompressResult Empty { get; } = new(false, 0, null, true);

    public static CompressResult Failed(string reason) => new(false, 0, reason, false);
}

/// <summary>
/// 一次对话的<b>状态与请求执行</b>：全量历史、会话档案、请求网关、运行标记、取消源，
/// 以及「跑一轮」这件事本身。
///
/// <b>为什么不在视图里</b>：这些状态原本长在 <c>ConversationView</c> 上，于是「这一轮跑到哪了」
/// 只能通过一个已经打开的视图观察 —— 插件（比如桌宠）在会话没打开时既发起不了对话，
/// 也看不到它跑完了没有。搬出来之后，视图退化成「订阅事件并渲染」的一侧。
///
/// <b>线程约定</b>：只在 UI 线程上访问，内部不加锁（与 <see cref="SessionViewCache"/> 相同）。
/// 所有事件也在 UI 线程上发出 —— 网关的流式回调本来就在 UI 上下文里跑。
/// </summary>
public sealed class ConversationRuntime
{
    private readonly ToolExecutorDispatcher _toolExecutor = new();

    /// <summary>本轮是不是用户自己按「停止」结束的（那种情况不发出错提醒）。</summary>
    private bool _stoppedByUser;

    public ConversationRuntime(IConversationProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ConfigureToolExecutor();

        // 重试提示不在这里直接弹：runtime 只负责「正在重试」这件事发生，怎么显示由宿主决定
        Gateway.RetryScheduled = (attempt, max) => RetryScheduled?.Invoke(attempt, max);
    }

    // ============================================================
    // 状态
    // ============================================================

    /// <summary>会话档案：会话状态、system 提示词、落库路径。</summary>
    public IConversationProfile Profile { get; }

    /// <summary>会话 id。日志与提醒文案里标识「这是哪个会话」用。</summary>
    public string SessionId => Profile.State.SessionId;

    /// <summary>
    /// 全量历史。<b>发给模型的就是它全量</b>；渲染可以只取其中一段（视图的分段渲染），
    /// 两者语义不同，别把「渲染窗口」当成「上下文」。
    /// </summary>
    public List<ChatMessage> Messages { get; } = new();

    /// <summary>请求网关：客户端解析、自动重试、工具循环、落库。</summary>
    public ChatGateway Gateway { get; } = new();

    /// <summary>
    /// 当前是否有任务在跑。
    /// <b>放在这里而不是视图上</b>，是为了让「同一会话不能并发跑两轮」由状态本身保证，
    /// 不必指望每个入口都记得先看视图。
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>本轮请求的取消源：运行中点「停止」就是取消它。每轮新建、收尾时置空并释放。</summary>
    public CancellationTokenSource? SendCts { get; private set; }

    /// <summary>
    /// 交互出口（工具审批 / 提问）。没有宿主时按「用户不在」处理：审批拒绝、提问返回 null。
    /// </summary>
    public IInteractionHost? Interaction { get; set; }

    /// <summary>
    /// 插件登记的交互桥（<c>ISessionFacade.SetInteractionAsync</c> 挂上来的）。
    ///
    /// <b>只在没有窗口时接手</b>：<see cref="Interaction"/> 有值时界面优先 —— 两处同时弹一张
    /// 审批卡，用户根本不知道该点哪个。会话窗口关掉之后（视图把自己的 <see cref="Interaction"/>
    /// 清空），审批与提问又落回这里，机器人在手机上也就还能回话。
    ///
    /// 与 <see cref="Interaction"/> 一样：为空就按「用户不在」处理（审批拒绝、提问返回 null）。
    /// </summary>
    public IInteractionHost? PluginInteraction { get; set; }

    /// <summary>这一轮该问谁：窗口优先，其次插件桥，都没有就是「用户不在」。</summary>
    private IInteractionHost? ActiveInteraction => Interaction ?? PluginInteraction;

    /// <summary>
    /// 这一轮是不是插件经 <c>ISessionFacade</c> 发起的（来自
    /// <see cref="ConversationTurnRequest.PluginInitiated"/>）。
    /// 插件的事件总线靠它标出「这一轮是我派的」—— 同一会话上用户自己聊的那几轮不该被认领。
    /// </summary>
    public bool IsPluginInitiatedRound { get; private set; }

    /// <summary>
    /// 这一轮是哪个插件派的（用户自己发的一轮为 null）。
    /// 收尾提醒靠它查该插件声明的提醒策略（常驻 / 不弹 / 自动收起），见 <see cref="SessionNotifier"/>。
    /// </summary>
    private string? _noticePluginId;

    /// <summary>
    /// 当前模型的上下文窗口（token）。每轮发送时按请求里的模型刷新；
    /// 宿主换模型时也可以直接赋值 —— 用量环要在还没发过一轮时就显示。
    /// </summary>
    public long ContextWindow { get; set; } = -1;

    /// <summary>
    /// 压缩摘要用哪个模型。为 null 时退回会话存档里的模型（<c>Profile.State.Model</c>）。
    /// </summary>
    public Func<string?>? SummaryModelIdProvider { get; set; }

    /// <summary>压缩是否在进行中。防重入：自动压缩与手动点击可能撞上。</summary>
    public bool IsCompressing { get; private set; }

    /// <summary>
    /// 正在生成的那一轮的 turn_id；没有轮次在跑时为 null。
    /// 由 <see cref="BeginRound"/> 记下、<see cref="SendAsync"/> 收尾时清掉。
    /// 只用来回答「这一轮是不是还在跑」，不要拿它当「会话忙不忙」——
    /// 后者是 <see cref="IsBusy"/>，两者语义不同（见 <see cref="IsRunningTurn"/>）。
    /// </summary>
    private string? _runningTurnId;

    /// <summary>
    /// 这一轮是不是正在生成中。
    ///
    /// 只看 <see cref="IsBusy"/> 是不够的：那是「会话里有没有任务在跑」，而用户经常在下一轮
    /// 已经开跑之后回去总结上一轮 —— 那一轮早就定稿了，总结它没有任何风险。
    /// </summary>
    private bool IsRunningTurn(string turnId)
        => IsBusy && !string.IsNullOrEmpty(_runningTurnId) &&
           string.Equals(_runningTurnId, turnId, StringComparison.Ordinal);

    /// <summary>
    /// 正在被总结的那些轮次（turn_id）。
    ///
    /// <b>按轮次记而不是一个全局布尔</b>：总结不申请序号、只 UPDATE 各自那一行，
    /// 不同轮次之间没有任何共享状态，本来就可以同时进行。卡成全局布尔会带来一个很难受的
    /// 副作用 —— 用户手动总结某一轮时，另一轮刚跑完的自动总结会被静默跳过。
    /// </summary>
    private readonly HashSet<string> _summarizingTurns = new(StringComparer.Ordinal);

    /// <summary>下一段回复要插进历史的哪个位置之后（重新生成用）。取一次就清，见 <see cref="TakePendingInsertSequence"/>。</summary>
    private int? _pendingInsertSequence;

    /// <summary>会话存档与全量历史是否已就绪（读过一次库）。</summary>
    public bool IsHistoryLoaded { get; private set; }

    /// <summary>就绪过程本身。<b>并发调用共用一份</b>：视图与插件可能同时刚上手。</summary>
    private Task? _readyTask;

    // ============================================================
    // 事件（都在 UI 线程上发出；没人订阅也能正常跑完一轮）
    // ============================================================

    /// <summary>一段 assistant 回复开始流式（工具循环里每一轮各一次）。</summary>
    public event Action<ChatMessage>? RoundStarted;

    /// <summary>正文增量。</summary>
    public event Action<string>? ContentDelta;

    /// <summary>思考增量。</summary>
    public event Action<string>? ThinkingDelta;

    /// <summary>工具调用分片（同一轮里的多个调用靠 Index 区分）。</summary>
    public event Action<ToolCallDelta>? ToolCallDelta;

    /// <summary>一条新消息落库（user / assistant / tool / error）。</summary>
    public event Action<ChatMessage>? MessageInserted;

    /// <summary>同一条消息的第二次落库（工具跑完、改写为 invalid、补本轮耗时）。</summary>
    public event Action<ChatMessage>? MessageUpdated;

    /// <summary>运行标记变化（true = 本轮开始）。</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>某次 API 往返即将自动重试（下一个尝试序号, 总尝试数）。</summary>
    public event Action<int, int>? RetryScheduled;

    /// <summary>本轮的活动状态变化（运行中 / 等待确认）。宿主转发给左侧列表。</summary>
    public event Action<SessionActivity>? ActivityChanged;

    /// <summary>排队的那几条消息变了（增删、状态切换、正文改写）。宿主据此重画输入框上方那一栏。</summary>
    public event Action? PendingChanged;

    /// <summary>
    /// 整轮结束。<b>返回的 Task 会被 await</b>（在运行标记仍然立着的时候）——
    /// 「重新生成」要求宿主整体重排一次历史，那件事必须发生在允许下一次发送之前。
    /// 不订阅的话界面会停在「正在生成」。
    /// </summary>
    public event Func<TurnOutcome, Task>? TurnEnded;

    /// <summary>发请求前修过残缺的工具消息（视图据此重建入参索引）。</summary>
    public event Action? ToolCallsRepaired;

    /// <summary>
    /// 历史整体换了（压缩后重读、重新生成后重排、删了轮次）。参数是「至少渲染到这条消息
    /// 所在的分组」，null = 照常渲染最后一批。runtime 只负责数据，重绘归宿主。
    /// </summary>
    public event Action<string?>? HistoryRebuildRequested;

    /// <summary>压缩开始 / 结束（宿主据此显示「正在压缩上下文…」）。</summary>
    public event Action<bool>? CompressingChanged;

    /// <summary>
    /// 某一轮的回复总结落库了（参数是那一轮的 turn_id）。宿主据此重画那一轮的操作栏，
    /// 让「已总结」标记立刻出现 —— 否则要等下一次整块重绘才看得见。
    /// </summary>
    public event Action<string>? TurnSummarized;

    /// <summary>
    /// 某一轮的回复总结开始 / 结束（参数是那一轮的 turn_id）。
    ///
    /// <b>自动总结也要发它</b>：自动总结是从这里 fire-and-forget 出去的，宿主不订阅这个事件就
    /// 完全看不见过程 —— 用户看到的是「按钮静默地从『总结』变成『已总结』」，
    /// 中间既没有反馈、出错也没人知道。
    /// </summary>
    public event Action<string, bool>? SummarizingChanged;

    /// <summary>
    /// 某一轮的回复总结失败了（turn_id, 原因）。
    ///
    /// 自动总结的失败原来是全吞的（见 <see cref="AutoSummarizeTurnAsync"/>）：那一轮始终没被压掉，
    /// 用户却不知道为什么 —— 一旦是「没配模型」这类要他动手的原因，静默就等于死路。
    /// </summary>
    public event Action<string, string>? TurnSummaryFailed;

    /// <summary>
    /// 这一轮的「最后对话时间」刚写进库里（档案的 last_message_at）。
    /// 宿主据此把会话挪到列表最前面 —— 侧栏排序看的正是这一列，请求真正开始的那一刻它就已经变了。
    ///
    /// 为什么不能等到 <see cref="TurnEnded"/>：那时一轮可能已经跑了几十秒（工具循环），
    /// 用户看到的是「发了消息但列表纹丝不动」。也不复用 <see cref="BusyChanged"/>：
    /// 那个在写库<b>之前</b>就发了，宿主此刻重读列表拿到的还是旧时间。
    /// </summary>
    public event Action? SessionTouched;

    // ============================================================
    // 入口
    // ============================================================

    /// <summary>
    /// 跑一轮。<returns>null 表示没跑（当前已有任务在跑）。</returns>
    /// </summary>
    public async Task<ChatTurnResult?> SendAsync(ConversationTurnRequest request)
    {
        if (IsBusy) return null;

        // 历史没读完就发，模型收到的是一段残缺的上下文，而且这种丢失是静默的。
        // 放在这里而不是交给各个调用方：插件也可能在会话没打开时发起对话。
        await EnsureReadyAsync();

        // 等读库期间别人可能已经开跑了（连点两下、插件与视图同时发）
        if (IsBusy) return null;

        IsBusy = true;
        BusyChanged?.Invoke(true);
        ActivityChanged?.Invoke(SessionActivity.Running);

        ChatTurnResult? turn;
        try
        {
            // 标题还是占位说明此前没定过名：按设置补一个（配了命名模型时先留占位，等首轮回复再生成）。
            // 记下「这一轮之前标题是不是占位」：不是占位就是用户手动改过名，
            // 首轮的自动命名不许覆盖它 —— 否则「改名 → 发第一条消息」名字又被打回自动生成的。
            var mayAutoTitle = ConversationTitle.IsPlaceholder(Profile.State.Title);
            if (mayAutoTitle && request.Text is not null)
                Profile.State.Title = ConversationTitle.InitialTitle(request.Text);

            Profile.State.Model = request.Settings.Model.ModelName;
            Profile.State.Thinking = (int)request.Settings.Thinking;
            Profile.State.EnabledToolIds = request.Settings.ToolIds?.ToList() ?? new List<string>();

            // 上下文窗口跟着这一轮的模型走：自动压缩的阈值判定要用它，
            // 而插件在后台跑时没人替它把「当前模型的上限」塞进来。
            ContextWindow = request.Settings.Model.ContextWindow;

            var isFirstTurn = Messages.Count == 0;
            var firstText = request.Text ?? "";

            turn = await RunTurnAsync(request);

            // 自动话题命名只跑首轮一次：首轮之后 Messages 非空，isFirstTurn 恒为假。
            // 首轮之前标题就不是占位的，说明用户自己起了名，同样不覆盖。
            if (isFirstTurn && mayAutoTitle) _ = ApplyAutoTitleAsync(turn, firstText);
        }
        finally
        {
            IsBusy = false;
            // 这一轮跑完了：它不再是「正在生成的那一轮」，此后总结它完全安全
            _runningTurnId = null;
            BusyChanged?.Invoke(false);
        }

        // 回复总结：<b>必须放在运行标记清掉之后</b>。
        //
        // 它原来写在 RunTurnAsync 的末尾，而那里 IsBusy 还是 true、_runningTurnId 还是这一轮 ——
        // 于是「只总结已经跑完的轮次」那道防御把自己刚跑完的这一轮也挡住了，
        // 表现就是自动总结永远不触发（手动点一下却可以）。
        // 特意不 await：它是一次完整的模型请求（一到几秒），用户没理由为框架自己的记账多等。
        if (turn is { Success: true, FinalAssistant: { } finished } && finished.TurnId.Length > 0)
            _ = AutoSummarizeTurnAsync(finished.TurnId);

        // 这一轮结束，排队里还有话等着发：接着发下一条。
        // 放在最后（而不是 TurnEnded 事件里）：宿主在 TurnEnded 里做的事是重排界面，
        // 若那时就开下一轮，重排与新一轮的增量会挤在一起，可能画出重复的一轮。
        await DrainPendingAsync();

        return turn;
    }

    /// <summary>
    /// 准备好这个会话：载入档案、读一次全量历史、修一次残缺的工具消息。<b>幂等</b>。
    ///
    /// 这件事从视图搬到了这里 —— 谁先上手谁触发（视图、插件都行），
    /// 之后所有人共用同一份历史。否则插件在会话没打开时发起对话，模型收到的是空上下文。
    ///
    /// 读库失败也会置为「已就绪」：不这么做的话，每个等待方都会永远挂在这里，
    /// 用户看到的是「点了发送但什么都不会发生」—— 那是最难查的一类故障。
    /// </summary>
    public Task EnsureReadyAsync()
    {
        if (IsHistoryLoaded) return Task.CompletedTask;

        _readyTask ??= ReadyCoreAsync();
        return _readyTask;
    }

    private async Task ReadyCoreAsync()
    {
        try
        {
            await Profile.LoadAsync();

            // 档案读回来才知道这个会话选的是哪一档。构造时 ConfigureToolExecutor 拿到的
            // 只是默认值（Normal），不同步的话「自由」存档在打开会话后仍然会弹审批卡，
            // 得再点一次菜单才生效。
            _toolExecutor.Mode = Profile.State.PermissionMode;

            Messages.Clear();
            try
            {
                Messages.AddRange(await ChatRepository.GetBySessionAsync(SessionId));
            }
            catch
            {
                // 忽略：见 EnsureReadyAsync 的说明
            }

            // 打开会话时懒修一次：只碰这个会话，不做全库扫描
            await RepairToolCallPairingAsync();
        }
        finally
        {
            IsHistoryLoaded = true;
        }
    }

    /// <summary>用户点「停止」：取消本轮。没有在跑时什么也不做。</summary>
    public void Cancel() => SendCts?.Cancel();

    // ============================================================
    // 排队 / 引导
    // ============================================================

    private readonly List<PendingInput> _pending = new();

    /// <summary>当前排队的消息，按入队顺序。</summary>
    public IReadOnlyList<PendingInput> Pending => _pending;

    /// <summary>
    /// 有人正在编辑队列里的某条 → 整条队列都不自动发送。
    ///
    /// 这是刻意的「全队列暂停」而不是「只暂停这一条」：用户点开编辑，是为了把话说清楚；
    /// 这时让排在后面的那条照常发出去，模型会先回应后一条，用户正在改的那句再接上，
    /// 顺序就乱了 —— 而顺序正是排队这件事唯一的意义。
    /// </summary>
    public bool PendingPaused => _pending.Any(p => p.State == PendingInputState.Editing);

    private void RaisePendingChanged() => PendingChanged?.Invoke();

    /// <summary>
    /// 把一条消息排进队列。<paramref name="steer"/> = true 表示直接标成「等下一个轮边界插入」。
    /// </summary>
    public PendingInput Enqueue(string text, IReadOnlyList<ChatAttachment>? attachments,
        TurnSettings settings, bool steer)
    {
        var item = new PendingInput
        {
            Text = text,
            Attachments = attachments,
            Settings = settings,
            State = steer ? PendingInputState.Steer : PendingInputState.Queued,
        };

        _pending.Add(item);
        RaisePendingChanged();
        return item;
    }

    /// <summary>改成「等下一个轮边界插入」。消息已在跑着的那一轮里，下一轮请求就会带上它。</summary>
    public void RequestSteer(string id)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is not { } item) return;
        item.State = PendingInputState.Steer;
        RaisePendingChanged();
    }

    /// <summary>
    /// 撤回「引导插入」，退回普通排队。
    ///
    /// 已经取走的那批撤不回来：它们此刻在网关的 <c>messages</c> 里，本轮已经发出去了。
    /// 所以这个方法只对还挂在队列里的那条有意义 —— 取走时就从队列里移除了，两边不会打架。
    /// </summary>
    public void CancelSteer(string id)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is not { } item) return;
        if (item.State != PendingInputState.Steer) return;

        item.State = PendingInputState.Queued;
        RaisePendingChanged();
    }

    /// <summary>进入编辑：暂停自动发送，直到 <see cref="CommitPendingEdit"/> 或被取消。</summary>
    public void BeginPendingEdit(string id)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is not { } item) return;
        if (item.State == PendingInputState.Editing) return;

        item.BeforeEdit = item.State;
        item.State = PendingInputState.Editing;
        RaisePendingChanged();
    }

    /// <summary>改完。回到编辑之前那一档，然后尝试继续发送（若此刻已经不忙）。</summary>
    public async Task CommitPendingEditAsync(string id, string text)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is not { } item) return;

        item.Text = text;
        item.State = item.BeforeEdit;
        RaisePendingChanged();

        await DrainPendingAsync();
    }

    /// <summary>取消这一条，返回它（调用方负责把正文还回输入框）。没有这条时返回 null。</summary>
    public PendingInput? RemovePending(string id)
    {
        if (_pending.FirstOrDefault(p => p.Id == id) is not { } item) return null;

        _pending.Remove(item);
        RaisePendingChanged();

        // 队列可能因为这次移除而恢复「可以发」了（暂停的唯一原因就是有人正在编辑）。
        // 不在这里试一次的话，剩下那几条要等到用户再发一条消息才会动 ——
        // 界面上就是「队列还挂着，但什么都不会发生」。
        _ = DrainPendingAsync();

        return item;
    }

    /// <summary>
    /// 取走所有「等插入」的消息。由网关在每一轮请求之前调用。
    ///
    /// 不 await、不加锁：它只被当前这一轮的工具循环调用，而那一轮在同一时刻只有一处
    /// 在推进（网关的 for 循环是串行的）。加锁反而会让人以为这里支持并发。
    /// </summary>
    internal IReadOnlyList<SteerMessage> TakeSteers()
    {
        if (_pending.Count == 0) return Array.Empty<SteerMessage>();

        // 有人正在编辑：整条队列都停着，引导也一样。
        // 用户的意图是「等我改完再接着说」，这时候把另一条插进当前这一轮，
        // 与他按下的那个暂停是矛盾的。
        if (PendingPaused) return Array.Empty<SteerMessage>();

        var taken = _pending.Where(p => p.State == PendingInputState.Steer).ToList();
        if (taken.Count == 0) return Array.Empty<SteerMessage>();

        foreach (var item in taken) _pending.Remove(item);
        RaisePendingChanged();

        return taken.Select(p => new SteerMessage(p.Text, p.Attachments)).ToList();
    }

    /// <summary>
    /// 有空就发下一条排队消息。整轮结束、编辑提交之后各调一次。
    ///
    /// 三个前置条件缺一不可：没有任务在跑（否则会立刻被 SendAsync 挡回去）、
    /// 没人在编辑（否则顺序会乱）、队首确实是「排队」而不是「等引导」的那条。
    /// </summary>
    private async Task DrainPendingAsync()
    {
        if (IsBusy || PendingPaused) return;

        // 走到这里说明已经没有任务在跑了：还在等「下一个工具循环边界」的那些消息，
        // 那个边界永远不会再来了。它们退回成普通排队消息 —— 否则它们会永远挂在这里，
        // 用户看到的是「点了引导，什么也没发生，而且这条再也发不出去了」。
        var stale = _pending.Where(p => p.State == PendingInputState.Steer).ToList();
        if (stale.Count > 0)
        {
            foreach (var item in stale) item.State = PendingInputState.Queued;
            RaisePendingChanged();
        }

        if (_pending.FirstOrDefault(p => p.State == PendingInputState.Queued) is not { } next) return;

        _pending.Remove(next);
        RaisePendingChanged();

        var result = await SendAsync(new ConversationTurnRequest
        {
            Settings = next.Settings,
            Text = next.Text,
            Attachments = next.Attachments,
            RequestPreview = next.Text,
        });

        // 没发出去（这期间用户自己发了一条、或插件抢先了）：放回队首，等下一次机会。
        // 静默丢掉是最坏的处理 —— 用户看着它从列表里消失，却从来没被发出去过。
        if (result is null)
        {
            next.State = PendingInputState.Queued;
            _pending.Insert(0, next);
            RaisePendingChanged();
        }
    }

    /// <summary>改权限档位（会话级）。</summary>
    public async Task SetPermissionModeAsync(PermissionMode mode)
    {
        Profile.State.PermissionMode = mode;
        _toolExecutor.Mode = mode;
        await PersistSessionAsync();
    }

    /// <summary>把会话状态写回底层并落库。失败不外抛（落库问题不该打断对话）。</summary>
    public async Task PersistSessionAsync()
    {
        try { await Profile.SaveAsync(); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 用户手动改名。
    ///
    /// <b>为什么必须走运行时而不是直接改库</b>：一轮对话的收尾会
    /// <see cref="PersistSessionAsync"/> 把档案<b>整份</b>写回去，而档案是打开会话时从库里读的<b>快照</b>。
    /// 只改库的话，下一轮收尾就把旧标题原样覆盖回去 —— 用户看到的是「改完名跑一次任务，名字又变回去了」。
    ///
    /// 先 <see cref="EnsureReadyAsync"/>：运行时可能刚被插件借出来、档案还是空的，
    /// 这时候落库会把模型、工具这些一并写成空值。
    /// </summary>
    public async Task SetTitleAsync(string title)
    {
        await EnsureReadyAsync();

        Profile.State.Title = title;
        await PersistSessionAsync();
    }

    // ============================================================
    // 一轮的执行
    // ============================================================

    /// <summary>
    /// 一次请求的公共执行路径（普通发送与重新生成共用）：
    /// 定位/落库会话状态 → 组装请求 → 流式 → 收尾。
    ///
    /// <see cref="ConversationTurnRequest.Text"/> 为 null 表示<b>重新生成</b>：不新建用户消息，
    /// 新回复按 <see cref="ConversationTurnRequest.InsertAfterSequence"/> 给出的位置插回历史中间。
    /// 调用方负责置位/清掉运行标记（<see cref="SendAsync"/> 已经做了）。
    /// </summary>
    private async Task<ChatTurnResult?> RunTurnAsync(ConversationTurnRequest request)
    {
        // 没显式给就从上下文里取最后一条用户消息（重新生成走的就是这条路）
        var preview = request.RequestPreview
                      ?? Messages.LastOrDefault(m => m.Role == "user")?.Content
                      ?? "";

        // 本轮是不是用户自己按「停止」结束的：那种情况不发出错提醒
        _stoppedByUser = false;

        // 这一轮是谁派的：插件派的那几轮要在收尾事件上标出来，否则插件没法
        // 把「我派的任务跑完了」和「用户自己聊的一轮跑完了」区分开
        IsPluginInitiatedRound = request.PluginInitiated;
        _noticePluginId = request.PluginId;

        // 发送前再修一次：会话是打开着的，但坏消息可能是上一轮刚被强杀时留下的。
        // 没坏消息时这只是内存里的一遍扫描，不写库。
        await RepairToolCallPairingAsync();

        Profile.State.LastMessageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistSessionAsync();
        SessionTouched?.Invoke();

        // system 提示词 = 档案自己的 + 各插件登记的贡献（后者带来源标记，不截断）。
        // 包一层而不是改 IConversationProfile：那两个实现只是纯字符串拼接，
        // 改成异步会把接口与「工作空间的拼装不能与普通对话合流」那套注释一起搅动。
        // 这里取消源还没建（它在下面压缩之前才建），所以不给令牌：
        // 单个贡献者本来就有 500 ms 上限，拖不住这一轮。
        var system = await PromptComposer.ComposeAsync(
            Profile, Profile.State.Model, SessionId, CancellationToken.None);
        var vision = SupportsVision(request.Settings.Model);
        var video = SupportsVideo(request.Settings.Model);

        // MCP 是惰性连接的：发消息前确保会话启用的服务都连上并发现了工具。
        // 连不上就跳过（内部已记日志），不打断这次对话。
        await McpConnectionManager.Instance.EnsureToolsAsync(Profile.State.McpServers, CancellationToken.None);

        var tools = ResolveActiveTools(request.Settings.ToolIds);

        var options = new ChatRequestOptions
        {
            Thinking = request.Settings.Thinking,
            Tools = tools,
            SupportsVision = vision,
            SupportsVideo = video,
        };

        // 工具执行与请求构造共用同一个开关：不支持视觉时 view_image 直接报错，
        // 历史里遗留的图片 tool 结果也降级成文本，避免整轮请求被 API 拒掉
        _toolExecutor.Context.SupportsVision = vision;
        _toolExecutor.Context.SupportsVideo = video;

        // 知识库每次发送前现读：用户在输入框里换了库，下一条消息就该按新库检索
        _toolExecutor.Context.KnowledgeBaseId = Profile.State.KnowledgeBaseId ?? "";

        // task 的继承项必须取「本轮真正发给模型的工具」而不是存档里的原始列表：
        // 用户在设置里关掉的自举工具、没连上的 MCP，本来就不该出现在子会话里。
        ConfigureSubagentDispatch(request.Settings, tools);

        // 每次发送都现读设置：改完「最大工具循环次数」不用重开会话就生效
        Gateway.MaxToolRounds = AppDefaults.ResolveMaxToolRounds();

        // 取消源提前到压缩之前建：压缩本身也是一次模型请求，期间点「停止」得能停下来。
        // 放在后面建的话，压缩阶段那颗按钮是「停止」却没有任何东西可取消。
        SendCts = new CancellationTokenSource();
        var ct = SendCts.Token;

        ChatTurnResult? turn = null;
        var persisted = new List<string>();
        try
        {
            // 上下文超阈值时先自动压缩一次（受设置控制，失败或不满足条件都安静跳过）
            await MaybeAutoCompressAsync(ct);

            turn = await Gateway.ChatAsync(
                SessionId,
                system,
                // 有压缩记录时只发「最后一条压缩记录 + 它之后的消息」，更早的历史被摘要在语义上替代；
                // 再往后，末尾 N 轮之外的轮次换成它们各自的回复总结（见 ContextCompression.BuildWireHistory）
                ContextCompression.BuildWireHistory(
                    (request.History ?? Messages).ToList(),
                    ContextCompression.ResolveKeepTurns(request.History ?? Messages)),
                // 正文里的占位符（@图片1）已由输入控件在提交时展开成真实路径，
                // 这里拿到的就是发给模型的内容，不需要再处理一次。
                request.Text,
                request.Attachments,
                options,
                request.Settings.Client,
                _toolExecutor,
                new ChatPersistSink
                {
                    Insert = async m => { persisted.Add(m.Id); await PersistMessageAsync(m); },
                    Update = UpdatePersistedMessageAsync,
                },
                BeginRound,
                ct,
                takeSteers: TakeSteers);

            // 流式阶段的停止由网关内部按 interrupted 收尾并<b>正常返回</b>，不会走到下面的 catch。
            // 按「失败但没有错误信息」识别出来 —— 否则用户自己按的停止也会收到一条「任务出错」提醒。
            if (turn is { Success: false, Error: null })
                _stoppedByUser = true;

        }
        catch (OperationCanceledException)
        {
            // 用户点了「停止」：内容已由网关按 interrupted 落库，这里只做收尾
            _stoppedByUser = true;
        }
        finally
        {
            SendCts.Dispose();
            SendCts = null;
            // 请求已返回（成功 / 停止 / 失败），逐秒计时到此为止。
            // 放在 finally 里而不是只放在收尾逻辑里：请求异常直接抛出去时那条路径
            // 不会走到收尾，定时器会一直跑下去（运行中的 DispatcherTimer 会吊住页面）。
            // 视图在自己的 BusyChanged(false) 里停它。
        }

        // 重新生成：新消息落库时拿到的序列号在会话末尾（当下只能拿到「最大值 + 1」），
        // 这里把它们挪回原位置，否则重生成的回复会跑到最后一轮后面。
        //
        // 挪完必须重读一次：内存里的 Messages 还是「追加在末尾」的顺序，与库不一致。
        // 这件事放在 runtime 里而不是等宿主重绘 —— 插件在后台重新生成时没有宿主，
        // 顺序错了之后就再没人会去纠正它。
        if (request.InsertAfterSequence is { } after)
        {
            try { await ChatRepository.InsertMessagesAtAsync(SessionId, after, persisted); }
            catch { /* 忽略：位置不对总好过整轮回复丢掉 */ }

            await ReloadMessagesAsync();
        }

        await RaiseTurnEndedAsync(new TurnOutcome(
            Result: turn,
            StoppedByUser: _stoppedByUser,
            IsRegenerate: request.IsRegenerate));

        // 收尾提醒：跑完和出错都报，措辞避开「失败」，用「出错」。
        // 用户主动点「停止」不报 —— 那是他自己按的，不需要事后被告知。
        if (turn is { Success: true })
            SessionNotifier.Notify(SessionNoticeKind.Done, SessionId, Profile.State.Title,
                $"任务“{PreviewText(preview)}”已完成",
                workspaceName: Profile.WorkspaceName,
                pluginId: _noticePluginId);
        else if (turn is not null && !_stoppedByUser)
            SessionNotifier.Notify(SessionNoticeKind.Failed, SessionId, Profile.State.Title,
                $"任务“{PreviewText(preview)}”出错",
                workspaceName: Profile.WorkspaceName,
                pluginId: _noticePluginId);

        // 回复总结不在这里触发：此刻 IsBusy 还是 true，会被「只总结已跑完的轮次」那道防御挡掉。
        // 触发点在 SendAsync 的 finally 之后，见那里的说明。

        return turn;
    }

    /// <summary>
    /// 逐个 await 订阅方。单个订阅方抛异常只跳过它自己 —— 它是界面或插件代码，
    /// 不该有权把「这一轮已经成功」变成「这一轮报错」。
    /// </summary>
    private async Task RaiseTurnEndedAsync(TurnOutcome outcome)
    {
        if (TurnEnded is not { } handler) return;

        foreach (var callback in handler.GetInvocationList())
        {
            try
            {
                await ((Func<TurnOutcome, Task>)callback)(outcome);
            }
            catch
            {
                // 订阅方自己的问题，不影响这一轮的结论
            }
        }
    }

    // ============================================================
    // 轮次入口：重新生成 / 重试 / 继续
    // ============================================================

    /// <summary>
    /// 取走「下一段回复要插在哪之后」，取一次就清。
    /// 宿主在建这一轮的容器时问一次：<b>不能在发起前就算好下标</b>，
    /// 因为从发起那一刻到建控件之间，历史可能已经被重排过一次。
    /// </summary>
    public int? TakePendingInsertSequence()
    {
        var value = _pendingInsertSequence;
        _pendingInsertSequence = null;
        return value;
    }

    /// <summary>
    /// 重新生成：把触发位置之后的那个 AI 轮整轮软删掉，再按它前面的上下文重跑。
    ///
    /// <paramref name="trigger"/> 可以是用户消息（含自己为上下文），也可以是 AI 轮里的任意一条
    /// （退到这轮开头之前）。已经被删掉的 AI 轮没有可点的 AI 消息，从它前面那条用户消息点即可。
    /// </summary>
    /// <returns>null 表示没跑（已有任务在跑，或这一轮没有可用的上下文）。</returns>
    public async Task<ChatTurnResult?> RegenerateAsync(ChatMessage trigger, TurnSettings settings)
    {
        if (IsBusy) return null;
        await EnsureReadyAsync();

        var triggerIndex = Messages.FindIndex(m => m.Id == trigger.Id);
        if (triggerIndex < 0) return null;

        // 上下文末尾：点用户消息含自己；点 AI 消息则退到这个 AI 轮的第一条消息之前
        var contextEnd = trigger.Role == "user"
            ? triggerIndex + 1
            : IndexOfTurnStart(trigger.TurnId, triggerIndex);

        var context = Messages.Take(contextEnd).ToList();
        if (context.Count == 0) return null;

        // 要被替换掉的旧 AI 轮：上下文之后、且同一个 turn_id
        var replaced = new List<ChatMessage>();
        var replacedTurnId = contextEnd < Messages.Count && Messages[contextEnd].Role is "assistant" or "tool"
            ? Messages[contextEnd].TurnId
            : null;
        if (replacedTurnId is not null)
            for (var i = contextEnd; i < Messages.Count && Messages[i].TurnId == replacedTurnId; i++)
                replaced.Add(Messages[i]);

        var insertAfter = context[^1].SequenceNumber;

        // 这一轮自己可能就是「继续」接出来的：重新生成得把这条身份带上，
        // 否则新回复会突然长出一个模型头像、还会多出一份操作栏 —— 用户看到的是一轮被劈成了两轮。
        var continuedFrom = replaced.FirstOrDefault(m => m.ContinuedFrom is not null)?.ContinuedFrom;

        // 让旧回复先消失：直接改内存 + 让宿主重渲染，比原地找控件删更简单，也顺带把插入位置算出来
        if (replaced.Count > 0)
        {
            // 挂在它后面的「继续」段也要一起换掉：留着的话那些段的分界线会指向一段已经不存在的内容，
            // 用户还会看到「改完的回复 + 旧回复的后半截」。
            var chainTurns = ResolveTurnChain(replacedTurnId!);
            var chainIds = Messages
                .Where(m => chainTurns.Contains(m.TurnId))
                .Select(m => m.Id)
                .ToHashSet();

            foreach (var chained in chainTurns)
                try { await ChatRepository.SoftDeleteTurnAsync(SessionId, chained); } catch { /* 忽略 */ }

            Messages.RemoveAll(m => chainIds.Contains(m.Id));
        }

        // 至少渲染到「上下文末尾那条消息」所在的分组：被点在很旧的轮次上时，
        // 不能因为「首屏只渲染最后一批」而把它所在的区域整个丢掉。
        HistoryRebuildRequested?.Invoke(context[^1].Id);

        _pendingInsertSequence = insertAfter;

        var result = await SendAsync(new ConversationTurnRequest
        {
            Settings = settings,
            InsertAfterSequence = insertAfter,
            ContinuedFrom = continuedFrom,
        });

        // 没发出去（并发抢先了）就别把插入位置留在那儿，否则下一次普通发送会插到上一轮的位置上
        if (result is null) _pendingInsertSequence = null;
        return result;
    }

    /// <summary>
    /// 重试某一轮失败 / 中断的请求（错误卡上的「重试」）。
    ///
    /// 与「重新生成」只差一件事：<b>怎么对待上一轮</b>。重新生成是「不满意，删掉重来」；
    /// 重试是「没跑完，接着跑」—— 所以这里一条消息都不删。上一轮已经产生的思考与正文
    /// 留在原地（网关在失败时也把它们落进那条 failed 消息里了），用户想回看还能看，
    /// 否则一次断网就把好不容易等出来的思考链抹掉，等于白等。
    /// </summary>
    /// <param name="anchor">失败那一轮里的任意一条消息（错误记录 / 失败的 assistant / 中断的工具行）。</param>
    public async Task<ChatTurnResult?> RetryAsync(ChatMessage anchor, TurnSettings settings)
    {
        if (IsBusy) return null;
        await EnsureReadyAsync();

        var anchorIndex = Messages.FindIndex(m => m.Id == anchor.Id);
        if (anchorIndex < 0) return null;

        // 这一轮的起点。错误记录与失败的 assistant 都带着同一个 turn_id，
        // 按它退回轮次开头，才能把「这一轮之前」的上下文完整取出来。
        var start = anchor.TurnId is null ? anchorIndex : IndexOfTurnStart(anchor.TurnId, anchorIndex);

        var context = Messages.Take(start).ToList();
        if (context.Count == 0) return null;   // 失败轮前面什么都没有（连用户消息都没有）就没得重试

        // 不传 InsertAfterSequence：新回复追加在末尾（上一轮的控件与消息都留着）。
        // 但必须显式给出「发到哪为止」的历史 —— 失败那一轮还留在 Messages 里，
        // 直接发全量会把刚失败的那几条也带上，模型看到的是半截上下文。
        return await SendAsync(new ConversationTurnRequest
        {
            Settings = settings,
            History = context,
            // 重试的是「继续」接出来的那一段时，新回复仍然是那一段
            // 不能因为一次失败就让它重新长出模型头像、再挂一份操作栏。
            ContinuedFrom = Messages
                .FirstOrDefault(m => m.TurnId == anchor.TurnId && m.ContinuedFrom is not null)
                ?.ContinuedFrom,
        });
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
    public async Task<ChatTurnResult?> ContinueAsync(string turnId, TurnSettings settings)
    {
        if (IsBusy) return null;
        await EnsureReadyAsync();

        if (!IsTurnResumable(turnId)) return null;

        var history = Messages.ToList();
        history.Add(new ChatMessage
        {
            Role = "user",
            Content = "（上一条回复被中断了。请从断掉的地方接着做，不要重复已经完成的步骤，也不要重新开始。）",
            Status = "completed",
        });

        return await SendAsync(new ConversationTurnRequest
        {
            Settings = settings,
            History = history,
            RequestPreview = Messages.LastOrDefault(m => m.Role == "user")?.Content,
            // 记下「这一段承接的是哪一轮」：界面按它把两段画成一整轮（不重复头像、中间不插操作栏），
            // 删除与重新生成也靠它把整条链当成一个单位。
            ContinuedFrom = turnId,
        });
    }

    /// <summary>
    /// 这一轮是不是「停在半路」：被用户停止、应用中途被杀、或工具还没返回就断了。
    ///
    /// 判据只看这一轮的<b>最后一条</b>消息。中途某个工具失败、模型后来接着做完了，
    /// 那不算 —— 那种轮次给「继续」只会让它把已经做完的事再做一遍。
    /// 请求失败（后面跟着错误记录）也不走这里，那条路上已经有「重试」。
    /// </summary>
    /// <summary>
    /// 这一轮为什么停在半路（「继续中断的任务」那条分界线展开后给用户看的话）。
    /// 判据与 <see cref="IsTurnResumable"/> 一致，只是把同样的形状翻译成了人能读的句子。
    /// </summary>
    public string? DescribeInterruption(string? turnId)
    {
        if (string.IsNullOrEmpty(turnId)) return null;

        ChatMessage? last = null;
        foreach (var m in Messages)
            if (m.TurnId == turnId) last = m;

        if (last is null) return null;

        if (last.Role == "tool" && last.Status == "interrupted")
            return $"工具「{last.ToolName ?? "未知"}」执行到一半就被中断了，没有拿到结果。";

        if (last.Role == "tool" && last.Status == "failed")
            return $"工具「{last.ToolName ?? "未知"}」失败之后这一轮就停住了。";

        if (last.Role == "assistant" && last.Status == "interrupted")
            return "上一段回复在输出到一半时被停止（点了「停止」，或者连接断了）。";

        if (last.Role == "assistant" && last.Status == "failed")
            return "上一段回复在生成时出错了。";

        if (last.Role == "assistant" && ToolCallJson.Deserialize(last.Metadata).Count > 0)
            return "模型已经发起工具调用，但结果还没落库这一轮就断了（多半是应用中途被关掉）。";

        return null;
    }

    /// <summary>
    /// 一条「继续」链上的所有 turn_id（含自己）：先往上找每一段承接谁，再往下找谁承接了它。
    ///
    /// 连着继续好几次会攒出一条链；删除时必须整条一起走 —— 只删末尾那一段，
    /// 前面的分界线就会指向一段不存在的内容。
    /// </summary>
    public List<string> ResolveTurnChain(string turnId)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal) { turnId };

        // 往上：第一段之外的每一段都在自己第一条消息上记着承接谁
        var cursor = turnId;
        for (var guard = 0; guard < 64; guard++)
        {
            var parent = Messages.FirstOrDefault(m => m.TurnId == cursor && m.ContinuedFrom is not null)
                ?.ContinuedFrom;
            if (parent is null || !chain.Add(parent)) break;
            cursor = parent;
        }

        // 往下：同一轮可能被继续过好几次，每次都记着上一段是谁
        for (var guard = 0; guard < 64; guard++)
        {
            var added = false;
            foreach (var m in Messages)
                if (m.ContinuedFrom is not null && chain.Contains(m.ContinuedFrom) && chain.Add(m.TurnId))
                    added = true;
            if (!added) break;
        }

        return chain.ToList();
    }

    /// <summary>
    /// 这一轮后面还接着「继续」出来的段落吗。有的话这一轮的操作栏就收起来 ——
    /// 整条链只在最后一段留一份（见 BuildTurnBlock）。
    /// </summary>
    public bool HasContinuation(string turnId) => Messages.Any(m => m.ContinuedFrom == turnId);

    public bool IsTurnResumable(string turnId)
    {
        // 只有最后一轮才谈得上继续：后面又聊过了，接上中断的那轮等于把上下文顺序搅乱。
        // 先看最后一条就能挡掉绝大部分轮次（渲染历史时每个操作栏都会问一次）。
        if (Messages.Count == 0 || Messages[^1].TurnId != turnId) return false;

        ChatMessage? last = null;
        foreach (var m in Messages)
            if (m.TurnId == turnId) last = m;

        if (last is null || last.Role is not ("assistant" or "tool")) return false;

        // 停止在流式输出里（那条 assistant 落库时就是 interrupted），
        // 或停止在工具执行中（最后剩下的是一条中断占位的 tool 行）
        if (last.Status == "interrupted") return true;

        // 工具已经落库成失败：只剩这一条结果、后面再没有 assistant，说明网关是被停在那里的
        if (last.Role == "tool" && last.Status == "failed") return true;

        // 带 tool_calls 的 assistant 后面一条结果都没有：应用在工具跑起来之前就被关掉了
        return last.Role == "assistant" && ToolCallJson.Deserialize(last.Metadata).Count > 0;
    }

    /// <summary>AI 轮的第一条消息在 <see cref="Messages"/> 里的下标（找不到就退回 <paramref name="fallback"/>）。</summary>
    private int IndexOfTurnStart(string? turnId, int fallback)
    {
        for (var i = 0; i <= fallback && i < Messages.Count; i++)
            if (Messages[i].TurnId == turnId && Messages[i].Role is "assistant" or "tool")
                return i;
        return fallback;
    }

    // ============================================================
    // 历史重读
    // ============================================================

    /// <summary>
    /// 从库里重读全部消息（压缩落了摘要、重新生成改了顺序、删了轮次之后调）。
    /// 只做数据，重绘由宿主订阅 <see cref="HistoryRebuildRequested"/> 负责。
    /// </summary>
    public async Task ReloadMessagesAsync()
    {
        Messages.Clear();
        try
        {
            Messages.AddRange(await ChatRepository.GetBySessionAsync(SessionId));
        }
        catch
        {
            // 读失败就保持空列表：消息是「显示什么」的依据，留着半份比空着更危险
        }

        HistoryRebuildRequested?.Invoke(null);
    }

    // ============================================================
    // 上下文压缩
    // ============================================================

    /// <summary>
    /// 压缩上下文：让模型把「当前分界线之后」的全部内容压成一段摘要，作为一条
    /// <see cref="ContextCompression.CompressRole"/> 消息落库。此后构建请求时以它为界，
    /// 它之前的消息不再发给模型 —— 原文仍然留在库里，也仍然显示在界面上。
    /// </summary>
    /// <param name="manual">
    /// 手动触发时「没什么可压」与失败都要由宿主给出可见反馈（结果里带着原因）；
    /// 自动触发（发请求前的阈值检查）则一律安静，因为用户并没有在等这个结果。
    /// </param>
    /// <param name="ct">
    /// 自动压缩时传当前这一轮的取消源：压缩也是模型请求，用户在压缩阶段点「停止」就该停下来。
    /// </param>
    /// <param name="keepRecentTurns">
    /// 末尾保留几轮原文不压（0 = 全压）。压缩后最近这几轮仍然逐字发给模型，只有更早的部分变成摘要。
    /// 轮数不够时什么都不压（按「没有可压内容」返回），不会把半轮历史塞进摘要再删掉原文。
    /// </param>
    public async Task<CompressResult> CompressAsync(
        bool manual, int keepRecentTurns = 0, CancellationToken ct = default)
    {
        if (IsCompressing) return CompressResult.Skipped;

        await EnsureReadyAsync();

        if (!ContextCompression.HasCompressibleContent(Messages, keepRecentTurns))
            return manual ? CompressResult.Empty : CompressResult.Skipped;

        var slice = ContextCompression.SliceForSummary(Messages, keepRecentTurns);
        if (slice.Count == 0) return CompressResult.Skipped;

        IsCompressing = true;
        CompressingChanged?.Invoke(true);

        try
        {
            // 摘要用当前会话选中的模型：不额外引入设置项，质量也是这里最好的。
            // 没有可用模型就没法压缩：手动触发要说明原因，自动触发安静跳过。
            var modelId = SummaryModelIdProvider?.Invoke() ?? Profile.State.Model;
            var client = await Gateway.ResolveClientAsync(modelId);
            if (client is null)
                return CompressResult.Failed("尚未配置可用的模型，请到「设置 → 模型服务」添加并启用服务商与模型");

            var history = new List<ChatMessage>
            {
                new() { Role = "user", Content = ContextCompression.BuildSummaryRequest(slice) },
            };
            var options = new ChatRequestOptions
            {
                Thinking = ThinkingMode.NoThink,   // 压缩是搬运信息不是解题，不需要推理
                Tools = null,                      // 更不该让它去调工具
                SupportsVision = false,
                // 统计流水记成「压缩」：这类请求没有 chat_message 行，混进对话会把每轮请求数抬高
                ApiCall = new ApiCallContext { Kind = ApiRequestKinds.Compress, SessionId = SessionId },
            };

            var builder = new StringBuilder();
            var result = await client.StreamAsync(
                history,
                ContextCompression.SummarySystemPrompt,
                options,
                new ChatRoundSink { OnContent = chunk => builder.Append(chunk) },
                ct);

            var summary = builder.ToString().Trim();
            if (summary.Length == 0) return CompressResult.Failed("模型没有返回摘要内容");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await ChatRepository.InsertAsync(new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = SessionId,
                TurnId = await ChatRepository.GetNextTurnIdAsync(SessionId),
                SequenceNumber = await ChatRepository.GetNextSequenceAsync(SessionId),
                Role = ContextCompression.CompressRole,
                Content = summary,
                Metadata = ContextCompression.SerializeMetadata(),
                // 这次请求的输入是「被压掉的整段历史」，比压缩前还大，取它算占用是反的；
                // 真正有用的是输出 —— 摘要本身就是压缩之后进入上下文的那部分。
                UsageJson = ChatGateway.BuildUsageJson(result),
                Status = "completed",
                ModelName = client.ModelName,
                CreatedAt = now,
                UpdatedAt = now,
            });

            await ReloadMessagesAsync();
            return new CompressResult(true, slice.Count, null, false);
        }
        catch (OperationCanceledException)
        {
            // 用户点了「停止」。这里必须放出去：调用方（发送那一轮）要据此放弃这次发送，
            // 而不是吞掉异常后继续把消息发出去。
            throw;
        }
        catch (Exception ex)
        {
            return CompressResult.Failed(ex.Message);
        }
        finally
        {
            IsCompressing = false;
            CompressingChanged?.Invoke(false);
        }
    }

    // ============================================================
    // 回复总结（轮内压缩）
    // ============================================================

    /// <summary>
    /// 回复总结：把某一轮 AI 回复（含它的全部工具调用）压成一段摘要，<b>写进那一轮最后一条消息的
    /// metadata</b>（见 <see cref="ContextCompression.MergeSummaryMetadata"/>）。此后构建请求时，
    /// 这一轮如果不在「保留最近 N 轮」里，就用这段摘要替代它的原文。
    ///
    /// <b>它不删任何原文</b>：库里、界面上那一轮都还在，照常能看能复制；
    /// 被替代的只是「发给模型的那一份」。
    ///
    /// <b>为什么不新插一行</b>：新行要申请序号，而序号是「查最大 + 1 再插」两次 I/O。
    /// 一轮任务正在落工具结果时插总结，两边会拿到同一个序号撞唯一索引，整个工具循环当场断开。
    /// 写已有行的 metadata 是纯 UPDATE，不碰序号 —— 也因此可以同时总结多轮。
    /// </summary>
    /// <param name="manual">
    /// 手动触发（操作栏上的「总结」）不看总开关、也不看阈值 —— 用户点了就是要总结。
    /// 自动触发（一轮刚结束时）才受设置和阈值约束。
    /// </param>
    public async Task<TurnSummaryResult> SummarizeTurnAsync(
        string turnId, bool manual, CancellationToken ct = default)
    {
        // 同一轮已经在总结了：再点一次没有意义，重复的请求会互相覆写同一条 metadata
        if (_summarizingTurns.Contains(turnId)) return TurnSummaryResult.Skipped;

        await EnsureReadyAsync();

        // 这一轮的原文。它自己的旧总结挂在某条消息的 metadata 上，不影响这里的取数
        // （正文里没有总结，重总结不会把上一版当原文再转述一层）。
        var turn = Messages.Where(m => m.TurnId == turnId).ToList();

        if (turn.Count == 0)
            return manual
                ? Fail(turnId, "这一轮没有可总结的内容")
                : TurnSummaryResult.Skipped;

        // 防御：<b>只有正在跑的那一轮</b>不许总结，别的轮一律不受影响。
        //
        // 判据必须精确到轮次，不能是「会话里有没有任务在跑」：用户经常是上一轮刚答完、
        // 下一轮已经开跑之后，回头去总结上一轮 —— 那是完全正常的用法（那一轮已经定稿，
        // 总结它只是 UPDATE 一行已有消息，既不申请序号，也没有别人会去改它）。
        //
        // 真去总结正在跑的那一轮才有两个后果：读到的只是已落库的半截内容（总结会误导后面的轮次），
        // 而且网关收尾会拿内存对象整份 update 回库，把刚写进 metadata 的总结覆写成旧的。
        if (IsRunningTurn(turnId))
            return manual
                ? Fail(turnId, "这一轮还在运行，等它结束后再总结")
                : TurnSummaryResult.Skipped;

        if (!manual)
        {
            var settings = AppSettingsStore.Current;
            if (!settings.AutoSummarizeReply) return TurnSummaryResult.Skipped;

            // 阈值：只有几次小工具调用的轮次不值得花一次模型请求去总结 ——
            // 省下来的上下文还没总结本身花掉的多。
            var threshold = Math.Max(0, settings.TurnSummaryThresholdTokens);
            if (threshold > 0 && ContextCompression.EstimateTurnTokens(turn) < threshold)
                return TurnSummaryResult.Skipped;
        }

        var modelId = SummaryModelIdProvider?.Invoke() ?? Profile.State.Model;
        var client = await Gateway.ResolveClientAsync(modelId);
        if (client is null)
            return Fail(turnId, "尚未配置可用的模型，请到「设置 → 模型服务」添加并启用服务商与模型");

        _summarizingTurns.Add(turnId);
        SummarizingChanged?.Invoke(turnId, true);
        try
        {
            var options = new ChatRequestOptions
            {
                Thinking = ThinkingMode.NoThink,   // 总结是搬运信息不是解题，不需要推理
                Tools = null,                      // 更不该让它去调工具
                SupportsVision = false,
                MaxOutputTokens = await ResolveSummaryMaxTokensAsync(modelId),
                ApiCall = new ApiCallContext { Kind = ApiRequestKinds.Compress, SessionId = SessionId },
            };

            var builder = new StringBuilder();
            // 不落 usage：总结改成写已有行的 metadata 之后没有自己的消息行可挂，
            // 而把这次请求的输入（整轮原文）记到那一轮上会严重高估占用。
            await client.StreamAsync(
                new List<ChatMessage>
                {
                    new()
                    {
                        Role = "user",
                        Content = ContextCompression.BuildTurnSummaryRequest(UserTextBefore(turn[0]), turn),
                    },
                },
                ContextCompression.TurnSummarySystemPrompt,
                options,
                new ChatRoundSink { OnContent = chunk => builder.Append(chunk) },
                ct);

            var summary = ContextCompression.ClampSummary(builder.ToString());
            if (summary.Length == 0) return Fail(turnId, "模型没有返回总结内容");

            // 写在那一轮<b>最后一条</b>消息上：最后一条可能是 tool（被打断的轮次就停在那里），
            // 那种轮次同样需要总结。找一条都找不到时退回最后一条消息。
            var carrier = turn[^1];

            // 先读后写：metadata 里还有 tool_calls（assistant 行）或图片路径（tool 行），
            // 整体覆盖会抹掉它们 —— 那会让下一轮请求被服务端以「tool_calls 没有配对结果」拒掉。
            carrier.Metadata = ContextCompression.MergeSummaryMetadata(carrier.Metadata, summary);

            // 只回写 content / thinking / 状态那几个字段走的是同一条 UPDATE，
            // 这里把 metadata 一起写进去，不能只改内存。
            await ChatRepository.UpdateMessageAsync(carrier);

            TurnSummarized?.Invoke(turnId);

            return TurnSummaryResult.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TurnSummaryResult.Skipped;
        }
        catch (Exception ex)
        {
            return Fail(turnId, ex.Message);
        }
        finally
        {
            _summarizingTurns.Remove(turnId);
            SummarizingChanged?.Invoke(turnId, false);
        }
    }

    /// <summary>
    /// 报一个总结失败：记结果、同时让宿主把那行提示挂出来。
    /// 手动点的那条路与自动触发共用它 —— 自动失败原来是完全静默的。
    /// </summary>
    private TurnSummaryResult Fail(string turnId, string reason)
    {
        TurnSummaryFailed?.Invoke(turnId, reason);
        return TurnSummaryResult.Failed(reason);
    }

    /// <summary>
    /// 总结请求的输出上限。
    ///
    /// <b>必须显式下发</b>：OpenAI 兼容接口的 max_tokens 默认值由服务端决定（不少实现只有 4096），
    /// 一轮几十次工具调用的总结写到一半就被硬切 —— 用户看到的是「总结没写完」。
    ///
    /// 取值：模型声明的最大输出优先（那是它真能吐出的量），否则给 <see cref="FallbackSummaryMaxTokens"/>。
    /// 两种情况都夹在 <see cref="FallbackSummaryMaxTokens"/> 以内 —— 总结的价值在于装得下信息，
    /// 不在于把模型的输出预算全用完。
    /// </summary>
    private async Task<int> ResolveSummaryMaxTokensAsync(string? modelId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var details = await Gateway.ResolveModelAsync(modelId);
                if (details?.MaxOutputTokens is > 0 and var declared)
                    return (int)Math.Min(declared, FallbackSummaryMaxTokens);
            }
        }
        catch
        {
            // 读模型表失败不影响总结：退回下面的保守值
        }

        return FallbackSummaryMaxTokens;
    }

    /// <summary>总结请求的输出上限（token）。20000 token 约 1.5 万汉字，足够写全一次大轮次的改动明细。</summary>
    private const int FallbackSummaryMaxTokens = 20000;

    /// <summary>
    /// 一轮刚成功结束时的自动总结。设置关着就直接不打扰；
    /// 其余失败一律安静 —— 这是省钱的优化，不该因为它冒出来打扰用户。
    /// </summary>
    private async Task AutoSummarizeTurnAsync(string turnId)
    {
        try
        {
            var settings = AppSettingsStore.Current;
            if (!settings.TurnSummaryEnabled || !settings.AutoSummarizeReply) return;
            await SummarizeTurnAsync(turnId, manual: false);
        }
        catch
        {
            // 总结失败不影响这一轮的任何事
        }
    }

    /// <summary>这一轮之前最近的一条用户消息 —— 总结里的「任务目标」取自它。</summary>
    private string? UserTextBefore(ChatMessage first)
    {
        var index = Messages.FindIndex(m => m.Id == first.Id);
        if (index < 0) index = Messages.Count;

        for (var i = index - 1; i >= 0; i--)
            if (Messages[i].Role == "user")
                return Messages[i].Content;

        return null;
    }

    /// <summary>
    /// 发送前按设置自动压缩：上下文占用超过阈值就先压一次。任何一步不满足就安静跳过 ——
    /// 这一步只是顺手的优化，绝不能因为它失败或超时就拖住正常发送。
    /// </summary>
    private async Task MaybeAutoCompressAsync(CancellationToken ct)
    {
        var settings = AppSettingsStore.Current;
        if (!settings.AutoCompressContext) return;
        if (!ContextCompression.HasCompressibleContent(Messages)) return;
        if (!TryGetContextUsage(out var used, out var total)) return;

        var threshold = Math.Clamp(settings.AutoCompressThresholdPercent, 1, 100);

        // 用整数乘法比百分比，避免浮点误差把「刚好等于阈值」判到另一边
        if (used * 100 < total * threshold) return;

        await CompressAsync(manual: false, ct: ct);
    }

    /// <summary>
    /// 当前上下文占用的 token 数与窗口上限。无上限（模型未声明）或历史里没有任何 usage 时返回 false。
    ///
    /// 取<b>最新一条有 usage 的来源</b>，两种口径二选一：
    /// <list type="bullet">
    ///   <item>普通 assistant 消息：无状态 API 的最后一次输入已包含之前所有轮的输入与输出，
    ///   所以 <c>total</c> 就是当前占用；</item>
    ///   <item>压缩记录：它之前的历史已经不再进入上下文，<b>它自己才是新的上下文起点</b>，
    ///   所以取摘要的输出 token —— 它的输入是「被压掉的全部原文」，比压缩前还大，不能拿来当占用。</item>
    /// </list>
    /// </summary>
    public bool TryGetContextUsage(out long used, out long total)
        => ContextUsage.TryCompute(Messages, ContextWindow, out used, out total);

    /// <summary>
    /// 网关要求「开一段新的 assistant 回复」时的回调：广播 <see cref="RoundStarted"/>，
    /// 再把三条流式分片接到事件上。
    /// <b>哪怕没有任何订阅者也照常返回 sink</b> —— 这一轮该跑完还是跑完，只是没东西显示。
    /// </summary>
    private ChatRoundSink BeginRound(ChatMessage assistantMsg)
    {
        // 记下正在跑的是哪一轮：总结那边靠它精确挡住「总结一个还在生成中的轮次」，
        // 而不是把整个会话的所有轮次一起挡掉。
        _runningTurnId = assistantMsg.TurnId;

        RoundStarted?.Invoke(assistantMsg);

        return new ChatRoundSink
        {
            OnContent = delta => ContentDelta?.Invoke(delta),
            OnThinking = delta => ThinkingDelta?.Invoke(delta),
            OnToolCall = delta => ToolCallDelta?.Invoke(delta),
        };
    }

    // ============================================================
    // 落库
    // ============================================================

    /// <summary>把一条新消息落库并加入内存列表。</summary>
    private async Task PersistMessageAsync(ChatMessage m)
    {
        await ChatRepository.InsertAsync(m);
        Messages.Add(m);
        MessageInserted?.Invoke(m);
    }

    /// <summary>
    /// 同一条消息的第二次落库（工具跑完了、重新解析成 invalid、补本轮耗时）。
    /// 写失败不外抛：占位行落的是「被中断」这个合法结果，所以库里留下的历史照样是
    /// 完整可发送的（只是少了这次的真实输出），没必要为一次次要写入把整轮对话打断。
    /// </summary>
    private async Task UpdatePersistedMessageAsync(ChatMessage m)
    {
        try { await ChatRepository.UpdateMessageAsync(m); } catch { /* 忽略 */ }
        MessageUpdated?.Invoke(m);
    }

    /// <summary>
    /// 修当前会话里「assistant 有 tool_calls、却没有 tool 回它」的残缺消息。
    /// 只在打开会话与发送前各跑一次 —— 全库扫描在长会话上代价太大。
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

        // metadata 变了，视图的入参索引跟着失效，让它重建一次
        ToolCallsRepaired?.Invoke();
    }

    // ============================================================
    // 自动话题命名
    // ============================================================

    /// <summary>
    /// 首轮回复结束后给会话定标题。三种情形：开关关闭 → 保持占位等用户手动改名；
    /// 未配置命名模型 → 直接截断首条消息；配置了模型 → 让 AI 总结一个短名。
    /// 任何失败都静默回退到截断，不打扰已经完成的对话。
    /// </summary>
    private async Task ApplyAutoTitleAsync(ChatTurnResult? turn, string firstText)
    {
        try
        {
            var settings = AppSettingsStore.Current;

            string title;
            if (!settings.AutoTopicNaming)
                title = ConversationTitle.Placeholder;
            else if (string.IsNullOrWhiteSpace(settings.TopicNamingModelId))
                title = ConversationTitle.FromMessage(firstText);
            else
                title = ConversationTitle.FromMessage(await GenerateTitleAsync(turn) ?? firstText);

            if (string.IsNullOrEmpty(title)) return;

            Profile.State.Title = title;
            await PersistSessionAsync();

            // 标题变了，宿主页面要刷新导航列表 —— 没有别的途径能知道。
            // 单独发一条「只是标题变了」的收尾，避免宿主把它当成又跑完了一轮（重复收尾卡片）。
            await RaiseTurnEndedAsync(new TurnOutcome(
                Result: turn, StoppedByUser: false, IsRegenerate: false, TitleChanged: true));
        }
        catch
        {
            // 定名失败不该影响对话本身
        }
    }

    /// <summary>让「话题命名模型」给这一轮起个短名。任何异常都返回 null，由调用方回退。</summary>
    private async Task<string?> GenerateTitleAsync(ChatTurnResult? turn)
    {
        var modelId = AppSettingsStore.Current.TopicNamingModelId;
        if (string.IsNullOrWhiteSpace(modelId) || turn is null) return null;

        var details = await Gateway.ResolveModelAsync(modelId);
        if (details is null) return null;

        var client = Gateway.BuildClient(details);

        const string system =
            "你是会话标题生成器。根据用户的第一条消息和助手的回复，给出一个简短的会话标题。" +
            "要求：不超过 12 个字，只输出标题本身，不要引号、标点、序号或任何解释。";

        var body = $"用户：{AttachmentMarker.StripMarkers(turn.User?.Content)}\n助手：{ConversationTitle.Truncate(turn.FinalAssistant?.Content, 500)}";
        var history = new List<ChatMessage> { new() { Role = "user", Content = body } };

        var options = new ChatRequestOptions
        {
            Thinking = ThinkingMode.NoThink,
            Tools = null,
            SupportsVision = false,
            // 统计流水记成「话题命名」：它花的是真金白银，但和本次对话的轮次无关
            ApiCall = new ApiCallContext { Kind = ApiRequestKinds.Title, SessionId = SessionId },
        };

        var builder = new StringBuilder();
        await client.StreamAsync(history, system, options,
            new ChatRoundSink { OnContent = chunk => builder.Append(chunk) }, CancellationToken.None);

        return builder.ToString().Trim().Trim('"', '\'', '“', '”', '。', '，', '、', '\r', '\n', ' ');
    }

    // ============================================================
    // 工具
    // ============================================================

    /// <summary>
    /// 本轮要发给模型的工具：用户选中的 + 本会话启用的 MCP 服务发现到的。
    /// 一个都没有时返回 null（请求体里就不带 tools 字段）。
    /// </summary>
    private IReadOnlyList<ToolDefinition>? ResolveActiveTools(IReadOnlyList<string>? toolIds)
    {
        var tools = new List<ToolDefinition>();

        if (toolIds is { Count: > 0 })
            tools.AddRange(ToolRegistry.Instance.ResolveByIds(toolIds));

        foreach (var serverId in Profile.State.McpServers)
        {
            if (McpConnectionManager.Instance.GetTools(serverId) is not { } discovered) continue;

            foreach (var info in discovered)
            {
                // 按 id 取（而不是按远端名），因为注册时可能因重名给 Name 加过服务名前缀
                var tool = ToolRegistry.Instance.ResolveById(McpConnectionManager.BuildToolId(serverId, info.Name));
                if (tool is not null) tools.Add(tool);
            }
        }

        // 「允许 AI 创建工具与技能」关掉时不把这两个工具发给模型。
        // 只有这一个出口需要过滤：会话存档里可能还留着它们的 id，这里挡掉就等于模型看不见、也调不到。
        // （执行侧还会再查一次开关，防的是「列表过滤漏了」这类改动。）
        if (!AppSettingsStore.Current.AllowAgentSelfExtension)
            tools.RemoveAll(t => t.IsSelfExtension);

        // 同一个工具可能既在勾选列表里、又来自 MCP 服务发现（子 Agent 会话的工具快照就是这样：
        // 派发时把父会话本轮实际可用的工具 id 整份抄了下来，其中含 MCP 工具）。
        // 重复的 schema 发给模型只会让它在两个同名工具之间犹豫，按 id 去一次。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        tools = tools.Where(t => seen.Add(t.Id)).ToList();

        return tools.Count > 0 ? tools : null;
    }

    private void ConfigureToolExecutor()
    {
        _toolExecutor.Context = new BuiltinToolContext
        {
            BaseDirectory = Profile.BaseDirectory,
            // 插件工具会带上它：判断「这个资源是否授权给了这个会话」全靠这个值
            SessionId = SessionId,
            GetSessionSkills = () => Task.FromResult<List<SessionSkill>?>(Profile.State.EnabledSkills),
            SaveSessionSkills = SaveSessionSkillsAsync,
            // 没有交互宿主时返回 null：工具链会据此把「用户不在」如实回给模型
            AskUser = (questions, token) => ActiveInteraction is { } host
                ? host.AskAsync(questions, token)
                : Task.FromResult<List<AskAnswer>?>(null),
            // 同理：没窗口时返回 null（「没等到人」），而不是让这一轮永远悬着
            WaitForUser = (title, instructions, token) => ActiveInteraction is { } host
                ? host.WaitForUserAsync(title, instructions, token)
                : Task.FromResult<bool?>(null),
        };

        _toolExecutor.ApprovalGate = (call, tool, token) => ActiveInteraction is { } host
            ? host.ApproveAsync(call, tool, token)
            : Task.FromResult(false);

        _toolExecutor.Mode = Profile.State.PermissionMode;
    }

    /// <summary>
    /// 把「派发子 Agent」这个能力接到工具上下文上。<b>每轮重挂一次</b>：继承项必须跟着这一轮的
    /// 模型、工具、权限走 —— 第一次挂上去的闭包会一直攥着旧设置，用户改了工具之后子 Agent 还按老配置跑。
    /// </summary>
    private void ConfigureSubagentDispatch(TurnSettings settings, IReadOnlyList<ToolDefinition>? tools)
    {
        _toolExecutor.Context.RunSubagent = (request, ct) => SubagentRunner.RunAsync(
            new SubagentRunContext
            {
                ParentSessionId = SessionId,
                BaseDirectory = Profile.BaseDirectory,
                ParentToolIds = tools?.Select(t => t.Id).ToList() ?? new List<string>(),
                // 只传 allowed：loaded 的全文注入在子会话里会再算一遍，token 直接翻倍
                ParentSkills = Profile.State.EnabledSkills,
                ParentMcpServers = Profile.State.McpServers,
                PermissionMode = Profile.State.PermissionMode,
                Settings = settings,
                Interaction = ActiveInteraction,
            },
            request,
            ct);
    }

    /// <summary>工具改动了本会话启用的技能：落库并通知宿主刷新显示。</summary>
    private async Task SaveSessionSkillsAsync(List<SessionSkill> skills)
    {
        Profile.State.EnabledSkills = skills;
        await PersistSessionAsync();
        ActiveInteraction?.OnSessionSkillsChanged(skills);
    }

    /// <summary>模型是否支持视觉。历史里可能留着 view_image 产生的图片结果。</summary>
    internal static bool SupportsVision(ModelDetails? model)
        => model?.Capabilities.Contains("vision") == true;

    /// <summary>
    /// 模型是否支持视频输入。要求两件事同时成立：
    /// 模型表里标了 video 能力，且协议是 Chat Completions 形态 ——
    /// video 内容块只有 OpenAI 兼容协议认（Anthropic 与 Responses 没有这个字段，
    /// 标了 video 能力也没处放，发出去会被服务端拒掉整个请求）。
    /// </summary>
    internal static bool SupportsVideo(ModelDetails? model)
        => model is not null
           && model.Capabilities.Contains("video")
           && model.Protocol is ModelProtocol.OpenAI or ModelProtocol.LMStudio;

    /// <summary>把用户请求压成一行短文案：取前 10 个字，超出部分用省略号（换行也折成空格）。</summary>
    private static string PreviewText(string? text)
    {
        const int max = 10;
        var flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (flat.Length == 0) return "本轮请求";
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
