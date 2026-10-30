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

    /// <summary>压缩这个会话的上下文（等价于用户点「压缩」）。返回是否真的压了。</summary>
    Task<bool> CompressAsync(string sessionId);

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
}

/// <summary>会话列表里的一项。只够画一个下拉列表，不含正文。</summary>
/// <param name="SessionId">会话 id，之后传给 <c>SendAsync</c> 用这个。</param>
/// <param name="Title">标题（可能为空，取决于宿主当前的自动命名状态）。</param>
/// <param name="UpdatedAt">最近更新时间，Unix 毫秒。</param>
public sealed record SessionSummary(string SessionId, string Title, long UpdatedAt);

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
