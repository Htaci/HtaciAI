using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件操作会话的门面（需要声明 <see cref="PluginCapabilities.ChatControl"/>）。
///
/// <b>它为什么存在</b>：「跑一轮对话」这件事本身已经不在窗口里，而在宿主的会话运行时里，
/// 所以插件可以在<b>会话窗口没打开时</b>发一句话、停一轮、改权限档位。
/// 桌宠「点击就开口」走的就是这里。
///
/// <b>线程</b>：所有方法都可以在任意线程调用；宿主会自己调度到 UI 线程。
/// <b>不要在 UI 线程上同步等待</b>（别用 <c>.Result</c> / <c>.Wait()</c>）：
/// <see cref="SendAsync"/> 要等到整轮跑完（流式 + 工具循环），期间会话仍要能正常渲染。
///
/// <b>它不改用户的选择</b>：发起一轮用的是会话存档里的模型、思考档位、工具与权限档位，
/// 与你手里打开的界面看到的是同一套。
/// </summary>
public interface ISessionFacade
{
    /// <summary>
    /// 用这个会话当前的配置跑一轮：<paramref name="text"/> 作为一条用户消息发出并落库。
    ///
    /// 会话窗口开着时会实时出现在窗口里；没开时同样会跑完并落库，下次打开就能看到。
    /// 同一会话已有任务在跑时返回 <see cref="SessionSendStatus.Busy"/>，不会排队也不会插队。
    ///
    /// <b>没有窗口时工具审批与提问拿不到用户</b>：审批按「拒绝」处理、提问按「用户不在」回答，
    /// 工具链如实把这些回给模型 —— 这一轮照常跑完，不会卡住。
    /// </summary>
    /// <param name="ct">
    /// 只作用于<b>发起之前</b>的准备步骤（读库、解析模型）。发起之后要停下来用
    /// <see cref="CancelAsync"/>：那一轮有自己的生命周期，取消等待并不该把用户能看到的一轮拦腰截断。
    /// </param>
    Task<SessionSendResult> SendAsync(string sessionId, string text, CancellationToken ct = default);

    /// <summary>停掉这个会话正在跑的一轮（等价于用户按「停止」）。返回是否真的停了一轮。</summary>
    Task<bool> CancelAsync(string sessionId);

    /// <summary>改这个会话的权限档位（会落库，等同于用户在输入框右下角切换）。</summary>
    Task<bool> SetPermissionModeAsync(string sessionId, PluginPermissionMode mode);

    /// <summary>
    /// 压缩这个会话的上下文（等价于点「压缩」）。返回是否真的压了。
    /// </summary>
    /// <param name="keepRecentTurns">
    /// 末尾保留几轮原文不压（默认 0 = 全压）。压缩之后最近这几轮仍然逐字发给模型，
    /// 只有更早的部分变成摘要 —— <c>/compact -n</c> 用它做「保留最后 N 轮」。
    /// 可压内容本来就不足 N 轮时什么都不压、返回 <c>false</c>：宁可不动，也不会把
    /// 半轮历史塞进摘要再从原文里删掉。传负数按 0 处理。
    /// </param>
    Task<bool> CompressAsync(string sessionId, int keepRecentTurns = 0);

    /// <summary>
    /// 读这个会话最近 <paramref name="take"/> 条消息。会话没读过库时会顺带读一次。
    /// 返回的是快照，改它不会影响会话。
    /// </summary>
    Task<IReadOnlyList<SessionMessageSnapshot>> GetRecentMessagesAsync(
        string sessionId, int take, CancellationToken ct = default);

    /// <summary>
    /// 列出会话（按最近更新倒序），最多 <paramref name="take"/> 个。
    ///
    /// 给「挑一个会话把东西送过去」这类界面用 —— 插件不该硬编码会话 id，
    /// 那东西只有用户知道。
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int take = 50, CancellationToken ct = default);

    // ---- 插件自己的会话区 ----

    /// <summary>
    /// 给本插件新建一个<b>只属于自己的会话</b>（独立会话区，不与用户的普通会话混在一起）。
    /// 返回新会话 id，失败返回 <c>null</c>。
    ///
    /// <b>它不会出现在用户的会话列表里</b>：插件会话单独存一张表，用户的会话列表、默认模型、
    /// 「已删除会话」恢复页都扫不到它。要展示自己的会话，用 <see cref="ListMySessionsAsync"/>。
    ///
    /// 新会话默认不给任何工具（工具与 MCP 都为空、权限档位为普通）。
    /// </summary>
    /// <param name="title">会话标题，留空则用「插件会话」。</param>
    Task<string?> CreateSessionAsync(string? title = null, CancellationToken ct = default);

    /// <summary>列出本插件自己建的会话（最近活动在前），最多 <paramref name="take"/> 个。</summary>
    Task<IReadOnlyList<SessionSummary>> ListMySessionsAsync(int take = 50, CancellationToken ct = default);

    /// <summary>
    /// 删除本插件自己的一个会话（软删除，消息保留）。<b>只能删自己的</b>：
    /// 传别人的会话 id 一律返回 <c>false</c>，不会误删用户会话。
    /// 正在跑的一轮会先被停掉。
    /// </summary>
    Task<bool> DeleteSessionAsync(string sessionId);

    // ---- 用量、模型、工具集、交互桥 ----

    /// <summary>
    /// 这个会话的用量汇总：累计输入/输出/缓存命中、API 往返次数、当前上下文占用与窗口。
    ///
    /// <b>只读库，不加载会话</b>：问一次用量不该把整个会话的历史读进内存
    /// （列 10 个会话就会读 10 份）。
    ///
    /// 会话不存在返回 <c>null</c>。窗口未知（模型没声明上限）时 <see cref="SessionUsage.ContextWindow"/> &lt;= 0，
    /// 插件据此显示「未知」而不是算出一个 0%。
    /// </summary>
    Task<SessionUsage?> GetUsageAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 这个会话用的模型与它的能力（是否支持视觉、窗口多大、是否支持思考）。
    ///
    /// 会话还没选过模型 / 选的模型已被删除时按宿主的默认模型回退，并在
    /// <see cref="SessionModelInfo.IsFallbackToHostDefault"/> 里告诉你发生了回退。
    /// 会话不存在返回 <c>null</c>。
    /// </summary>
    Task<SessionModelInfo?> GetModelInfoAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 宿主当前可登记的全部工具（不含内部哨兵工具）。
    /// 插件用它做 <c>/tools ls</c>，也用它校验自己要写进会话的 id 是否存在。
    /// </summary>
    Task<IReadOnlyList<PluginToolInfo>> ListAvailableToolsAsync(CancellationToken ct = default);

    /// <summary>
    /// 这个会话当前勾选的工具 id。会话不存在返回 <c>null</c>（与「勾了零个」区分开）。
    /// </summary>
    Task<IReadOnlyList<string>?> GetSessionToolsAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 覆盖式写入这个会话的工具集。<b>只接受本插件自己的会话</b>：
    /// 普通会话与工作空间会话的工具是用户在界面上选的，插件改它等于悄悄替用户做决定，
    /// 那些会话一律返回 <c>false</c> 且不写库。
    ///
    /// 不存在的工具 id 会被丢掉（而不是让会话里留着解析不到的 id）。
    /// 写入后下一轮请求就用新工具集，不必重开会话。
    /// </summary>
    Task<bool> SetSessionToolsAsync(
        string sessionId, IReadOnlyList<string> toolIds, CancellationToken ct = default);

    /// <summary>
    /// 给这个会话登记一个交互桥：<b>会话窗口没打开时</b>，工具审批 / <c>ask_user_question</c> /
    /// 「等人做完一件事」都转发给它（比如 QQ 机器人发一条消息等用户回复）。
    ///
    /// 不登记时的现状是：审批一律拒绝、提问返回「用户不在」—— 机器人于是只能跑只读工具。
    ///
    /// <b>窗口优先</b>：会话窗口打开着的时候，这些交互仍然交给界面，插件<em>不会</em>收到
    /// （两处同时弹一张卡只会让人不知道点哪个）。
    ///
    /// 传 <c>null</c> 撤销登记。会话不存在返回 <c>false</c>。
    /// </summary>
    Task<bool> SetInteractionAsync(string sessionId, IPluginInteractionHost? host);
}

/// <summary>会话列表里的一项。只够画一个下拉列表，不含正文。</summary>
/// <param name="SessionId">会话 id，之后传给 <c>SendAsync</c> 用这个。</param>
/// <param name="Title">标题（可能为空，取决于宿主当前的自动命名状态）。</param>
/// <param name="UpdatedAt">最近更新时间，Unix 毫秒。</param>
/// <param name="ContextUsed">当前上下文占用（token）。取不到时为 0。</param>
/// <param name="ContextWindow">上下文窗口上限（token）。&lt;= 0 表示未知。</param>
public sealed record SessionSummary(
    string SessionId,
    string Title,
    long UpdatedAt,
    long ContextUsed = 0,
    long ContextWindow = 0);

/// <summary><see cref="ISessionFacade.GetUsageAsync"/> 的结果。</summary>
/// <param name="ModelId">会话存档里的模型 id；空 = 会话没选过模型。</param>
/// <param name="ContextWindow">上下文窗口上限。&lt;= 0 = 未知（模型没声明）。</param>
/// <param name="ContextUsed">当前上下文占用。口径与界面上的用量环一致。</param>
/// <param name="InputTokens">累计输入（所有有 usage 的消息之和）。</param>
/// <param name="CachedInputTokens">累计缓存命中输入。</param>
/// <param name="OutputTokens">累计输出。</param>
/// <param name="RequestCount">有 usage 的消息条数，约等于 API 往返次数。</param>
/// <param name="UpdatedAt">最后一条有 usage 的消息时间，Unix 毫秒；没有则为 0。</param>
public sealed record SessionUsage(
    string ModelId,
    long ContextWindow,
    long ContextUsed,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int RequestCount,
    long UpdatedAt);

/// <summary><see cref="ISessionFacade.GetModelInfoAsync"/> 的结果。</summary>
/// <param name="ModelId">解析出来的模型 id（会话选的那个，或回退到的默认模型）。</param>
/// <param name="DisplayName">给人看的名字。</param>
/// <param name="ContextWindow">上下文窗口。&lt;= 0 = 未知。</param>
/// <param name="MaxOutputTokens">最大输出 token。&lt;= 0 = 未知。</param>
/// <param name="SupportsVision">是否支持图像输入。不支持时图片只能落成文件路径交给 view_image。</param>
/// <param name="SupportsThinking">是否支持思考链。</param>
/// <param name="IsFallbackToHostDefault">true = 会话没选模型（或选的模型已失效），用的是宿主默认模型。</param>
public sealed record SessionModelInfo(
    string ModelId,
    string DisplayName,
    long ContextWindow,
    long MaxOutputTokens,
    bool SupportsVision,
    bool SupportsThinking,
    bool IsFallbackToHostDefault);

/// <summary><see cref="ISessionFacade.ListAvailableToolsAsync"/> 里的一个工具。</summary>
/// <param name="Id">工具 id（写进 <see cref="ISessionFacade.SetSessionToolsAsync"/> 的就是它）。</param>
/// <param name="Name">发给模型的调用名。</param>
/// <param name="Description">给模型看的说明（可能为空）。</param>
/// <param name="DangerLevel">危险等级，与宿主审批闸门用的是同一套值。</param>
/// <param name="Source">来源：<c>builtin</c> / <c>script</c> / <c>mcp</c> / <c>plugin</c>。</param>
public sealed record PluginToolInfo(
    string Id,
    string Name,
    string? Description,
    PluginToolDangerLevel DangerLevel,
    string Source);

/// <summary><see cref="ISessionFacade.SendAsync"/> 的结果。</summary>
public enum SessionSendStatus
{
    /// <summary>这一轮真的跑了。成功与否看 <see cref="SessionSendResult.Success"/>。</summary>
    Sent,

    /// <summary>这个会话已经有一轮在跑（用户或别的插件发起的）。</summary>
    Busy,

    /// <summary>会话不存在（可能刚被删除）。</summary>
    NotFound,

    /// <summary>会话没有可用模型，宿主也没有可回退的默认模型。</summary>
    NoModel,

    /// <summary>发起之前就失败了（读库异常、插件已停用等）。</summary>
    Failed,
}

/// <summary><see cref="ISessionFacade.SendAsync"/> 的结果。</summary>
/// <param name="Status">这一轮处于哪种状态。</param>
/// <param name="Success">仅当 <see cref="Status"/> 为 <see cref="SessionSendStatus.Sent"/> 时有意义。</param>
/// <param name="Error">失败原因（已压成一行，可直接显示）。</param>
public sealed record SessionSendResult(SessionSendStatus Status, bool Success = false, string? Error = null)
{
    public static SessionSendResult Sent(bool success, string? error = null)
        => new(SessionSendStatus.Sent, success, error);

    public static SessionSendResult Busy { get; }
        = new(SessionSendStatus.Busy, false, "这个会话已经有一轮在跑了。");

    public static SessionSendResult NotFound { get; }
        = new(SessionSendStatus.NotFound, false, "会话不存在。");

    public static SessionSendResult NoModel { get; }
        = new(SessionSendStatus.NoModel, false, "会话没有可用模型，宿主也没有可回退的默认模型。");

    public static SessionSendResult Failed(string error) => new(SessionSendStatus.Failed, false, error);
}

/// <summary>
/// 权限审批档位。与宿主界面上的盾牌图标是同一套值，单独定义一份是因为
/// 契约程序集不引用宿主程序集。
/// </summary>
public enum PluginPermissionMode
{
    /// <summary>严格：所有工具都要确认。</summary>
    Strict,

    /// <summary>普通：安全工具自动通过，其余确认。</summary>
    Normal,

    /// <summary>宽松：只有危险工具要确认。</summary>
    Loose,

    /// <summary>自由：全部自动通过。</summary>
    Free,
}

/// <summary>一条消息的只读快照。</summary>
/// <param name="Role">user / assistant / tool / error / agent（agent 是压缩摘要）。</param>
/// <param name="Content">正文。</param>
/// <param name="Thinking">思考链（只有 assistant 消息可能有）。</param>
/// <param name="Status">completed / interrupted / failed / invalid。</param>
/// <param name="SequenceNumber">会话内顺序号，越小越早。</param>
/// <param name="CreatedAt">Unix 毫秒时间戳。</param>
public sealed record SessionMessageSnapshot(
    string Role,
    string? Content,
    string? Thinking,
    string Status,
    int SequenceNumber,
    long CreatedAt);
