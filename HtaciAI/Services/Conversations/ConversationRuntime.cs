using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Notifications;
using HtaciAI.Services.Storage;
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
    /// 发请求前的准备钩子。当前唯一用途是「上下文超阈值时先自动压缩一次」——
    /// 压缩逻辑还在视图里（M1-C 才搬），所以这里留个钩子而不是让 runtime 反过来依赖视图。
    /// </summary>
    public Func<CancellationToken, Task>? BeforeRequest { get; set; }

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

    /// <summary>
    /// 整轮结束。<b>返回的 Task 会被 await</b>（在运行标记仍然立着的时候）——
    /// 「重新生成」要求宿主整体重排一次历史，那件事必须发生在允许下一次发送之前。
    /// 不订阅的话界面会停在「正在生成」。
    /// </summary>
    public event Func<TurnOutcome, Task>? TurnEnded;

    /// <summary>发请求前修过残缺的工具消息（视图据此重建入参索引）。</summary>
    public event Action? ToolCallsRepaired;

    // ============================================================
    // 入口
    // ============================================================

    /// <summary>
    /// 跑一轮。<returns>null 表示没跑（当前已有任务在跑）。</returns>
    /// </summary>
    public async Task<ChatTurnResult?> SendAsync(ConversationTurnRequest request)
    {
        if (IsBusy) return null;

        IsBusy = true;
        BusyChanged?.Invoke(true);
        ActivityChanged?.Invoke(SessionActivity.Running);

        try
        {
            // 标题还是占位说明此前没定过名：按设置补一个（配了命名模型时先留占位，等首轮回复再生成）
            if (ConversationTitle.IsPlaceholder(Profile.State.Title) && request.Text is not null)
                Profile.State.Title = ConversationTitle.InitialTitle(request.Text);

            Profile.State.Model = request.Model.ModelName;
            Profile.State.Thinking = (int)request.Thinking;
            Profile.State.EnabledToolIds = request.ToolIds?.ToList() ?? new List<string>();

            var isFirstTurn = Messages.Count == 0;
            var firstText = request.Text ?? "";

            var turn = await RunTurnAsync(request);

            // 自动话题命名只跑首轮一次：首轮之后 Messages 非空，isFirstTurn 恒为假。
            if (isFirstTurn) _ = ApplyAutoTitleAsync(turn, firstText);

            return turn;
        }
        finally
        {
            IsBusy = false;
            BusyChanged?.Invoke(false);
        }
    }

    /// <summary>用户点「停止」：取消本轮。没有在跑时什么也不做。</summary>
    public void Cancel() => SendCts?.Cancel();

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

        // 发送前再修一次：会话是打开着的，但坏消息可能是上一轮刚被强杀时留下的。
        // 没坏消息时这只是内存里的一遍扫描，不写库。
        await RepairToolCallPairingAsync();

        Profile.State.LastMessageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistSessionAsync();

        var system = Profile.BuildSystemPrompt(Profile.State.Model);
        var vision = SupportsVision(request.Model);

        // MCP 是惰性连接的：发消息前确保会话启用的服务都连上并发现了工具。
        // 连不上就跳过（内部已记日志），不打断这次对话。
        await McpConnectionManager.Instance.EnsureToolsAsync(Profile.State.McpServers, CancellationToken.None);

        var options = new ChatRequestOptions
        {
            Thinking = request.Thinking,
            Tools = ResolveActiveTools(request.ToolIds),
            SupportsVision = vision,
        };

        // 工具执行与请求构造共用同一个开关：不支持视觉时 view_image 直接报错，
        // 历史里遗留的图片 tool 结果也降级成文本，避免整轮请求被 API 拒掉
        _toolExecutor.Context.SupportsVision = vision;

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
            if (BeforeRequest is { } before) await before(ct);

            turn = await Gateway.ChatAsync(
                SessionId,
                system,
                // 有压缩记录时只发「最后一条压缩记录 + 它之后的消息」，更早的历史被摘要在语义上替代
                ContextCompression.BuildWireHistory((request.History ?? Messages).ToList()),
                // 正文里的占位符（@图片1）已由输入控件在提交时展开成真实路径，
                // 这里拿到的就是发给模型的内容，不需要再处理一次。
                request.Text,
                request.Attachments,
                options,
                request.Client,
                _toolExecutor,
                new ChatPersistSink
                {
                    Insert = async m => { persisted.Add(m.Id); await PersistMessageAsync(m); },
                    Update = UpdatePersistedMessageAsync,
                },
                BeginRound,
                ct);

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
        if (request.InsertAfterSequence is { } after)
        {
            try { await ChatRepository.InsertMessagesAtAsync(SessionId, after, persisted); }
            catch { /* 忽略：位置不对总好过整轮回复丢掉 */ }
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
                workspaceName: Profile.WorkspaceName);
        else if (turn is not null && !_stoppedByUser)
            SessionNotifier.Notify(SessionNoticeKind.Failed, SessionId, Profile.State.Title,
                $"任务“{PreviewText(preview)}”出错",
                workspaceName: Profile.WorkspaceName);

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

    /// <summary>
    /// 网关要求「开一段新的 assistant 回复」时的回调：广播 <see cref="RoundStarted"/>，
    /// 再把三条流式分片接到事件上。
    /// <b>哪怕没有任何订阅者也照常返回 sink</b> —— 这一轮该跑完还是跑完，只是没东西显示。
    /// </summary>
    private ChatRoundSink BeginRound(ChatMessage assistantMsg)
    {
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
                title = ConversationTitle.Truncate(firstText);
            else
                title = ConversationTitle.Truncate(await GenerateTitleAsync(turn) ?? firstText);

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

        var body = $"用户：{turn.User?.Content}\n助手：{ConversationTitle.Truncate(turn.FinalAssistant?.Content, 500)}";
        var history = new List<ChatMessage> { new() { Role = "user", Content = body } };

        var options = new ChatRequestOptions
        {
            Thinking = ThinkingMode.NoThink,
            Tools = null,
            SupportsVision = false,
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

        return tools.Count > 0 ? tools : null;
    }

    private void ConfigureToolExecutor()
    {
        _toolExecutor.Context = new BuiltinToolContext
        {
            BaseDirectory = Profile.BaseDirectory,
            GetSessionSkills = () => Task.FromResult<List<SessionSkill>?>(Profile.State.EnabledSkills),
            SaveSessionSkills = SaveSessionSkillsAsync,
            // 没有交互宿主时返回 null：工具链会据此把「用户不在」如实回给模型
            AskUser = (questions, token) => Interaction is { } host
                ? host.AskAsync(questions, token)
                : Task.FromResult<List<AskAnswer>?>(null),
        };

        _toolExecutor.ApprovalGate = (call, tool, token) => Interaction is { } host
            ? host.ApproveAsync(call, tool, token)
            : Task.FromResult(false);

        _toolExecutor.Mode = Profile.State.PermissionMode;
    }

    /// <summary>工具改动了本会话启用的技能：落库并通知宿主刷新显示。</summary>
    private async Task SaveSessionSkillsAsync(List<SessionSkill> skills)
    {
        Profile.State.EnabledSkills = skills;
        await PersistSessionAsync();
        Interaction?.OnSessionSkillsChanged(skills);
    }

    /// <summary>模型是否支持视觉。历史里可能留着 view_image 产生的图片结果。</summary>
    private static bool SupportsVision(ModelDetails? model)
        => model?.Capabilities.Contains("vision") == true;

    /// <summary>把用户请求压成一行短文案：取前 10 个字，超出部分用省略号（换行也折成空格）。</summary>
    private static string PreviewText(string? text)
    {
        const int max = 10;
        var flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (flat.Length == 0) return "本轮请求";
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
