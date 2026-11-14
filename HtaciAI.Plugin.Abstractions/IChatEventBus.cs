using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 会话事件订阅（需要声明 <see cref="PluginCapabilities.ChatObserve"/>）。
///
/// <b>不需要那个会话的窗口打开着</b>：事件来自宿主的会话运行时，而不是界面。
/// 所以插件可以在用户没打开任何会话时就开始观察。
///
/// <b>节奏由宿主控制</b>：流式增量按 50 ms 合并后再投递（<see cref="ChatEvent.Text"/> 是这一轮
/// 到目前为止的完整正文，<see cref="ChatEvent.Delta"/> 是本次新增），因为一个 token 一次回调
/// 会把插件和宿主的 UI 线程一起拖死。
///
/// <b>投递不在 UI 线程上</b>，也不阻塞对话本身：插件回调慢不会拖慢模型输出。
/// 要动界面请自己 <c>Dispatcher.UIThread.Post</c>。
///
/// <b>回调抛异常只记日志</b>，不影响这一轮对话，也不会影响别的插件。
/// </summary>
public interface IChatEventBus
{
    /// <summary>
    /// 订阅。返回的 <see cref="IDisposable"/> 用来退订；插件被禁用时宿主也会替你退订。
    /// </summary>
    /// <param name="filter"><c>null</c> = 全部会话、全部事件类型。</param>
    IDisposable Subscribe(IChatEventSink sink, ChatEventFilter? filter = null);
}

/// <summary>事件接收方。</summary>
public interface IChatEventSink
{
    /// <summary>
    /// 收到一条事件。<b>实现里请快进快出</b>：这个方法是串行调用所有订阅的，
    /// 你慢了就排在别人后面（但不会拖住对话）。
    /// </summary>
    ValueTask OnEventAsync(ChatEvent e, CancellationToken ct);
}

/// <summary>事件种类。</summary>
public enum ChatEventKind
{
    /// <summary>一段 assistant 回复开始流式（工具循环里每一轮各一次）。</summary>
    RoundStarted,

    /// <summary>正文增量（已合并）。</summary>
    ContentDelta,

    /// <summary>思考增量（已合并）。<see cref="ChatEvent.Thinking"/> 是这一轮到目前为止的全部思考。</summary>
    ThinkingDelta,

    /// <summary>模型开始请求某个工具（同一轮里每个调用各一次）。</summary>
    ToolCallStarted,

    /// <summary>一条消息落库（新插入或更新）。</summary>
    MessagePersisted,

    /// <summary>整轮结束且成功。</summary>
    RoundCompleted,

    /// <summary>整轮结束但失败或被停止。看 <see cref="ChatEvent.Success"/> 与 <see cref="ChatEvent.Status"/>。</summary>
    RoundFailed,

    /// <summary>会话活动状态变化（运行中 / 等待确认 / 空闲）。看 <see cref="ChatEvent.Status"/>。</summary>
    ActivityChanged,
}

/// <summary>
/// 一条会话事件。一个类型带全部字段，而不是每种事件一个类：
/// 订阅方本来就要用 <c>switch (e.Kind)</c> 分流，多套类型只会让人到处做类型判断。
/// <b>与当前 <see cref="Kind"/> 无关的字段一律为 null。</b>
/// </summary>
public sealed record ChatEvent
{
    public required ChatEventKind Kind { get; init; }

    /// <summary>事件属于哪个会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 正文。<see cref="ChatEventKind.ContentDelta"/> 时是这一轮到目前为止的完整正文，
    /// <see cref="ChatEventKind.MessagePersisted"/> 时是那条消息的正文，
    /// <see cref="ChatEventKind.RoundFailed"/> 时是失败原因。
    /// </summary>
    public string? Text { get; init; }

    /// <summary>本次新增的正文（只有 <see cref="ChatEventKind.ContentDelta"/> 有）。</summary>
    public string? Delta { get; init; }

    /// <summary>这一轮到目前为止的全部思考（只有 <see cref="ChatEventKind.ThinkingDelta"/> 有）。</summary>
    public string? Thinking { get; init; }

    /// <summary>工具名（<see cref="ChatEventKind.ToolCallStarted"/>）。</summary>
    public string? ToolName { get; init; }

    /// <summary>消息角色：user / assistant / tool / error（<see cref="ChatEventKind.MessagePersisted"/>）。</summary>
    public string? Role { get; init; }

    /// <summary>
    /// 状态：消息状态（completed / interrupted / failed / invalid）、
    /// 或活动状态（Running / WaitingApproval / Idle）、
    /// 或 <see cref="ChatEventKind.RoundFailed"/> 下的 stopped / failed。
    /// </summary>
    public string? Status { get; init; }

    /// <summary>这一轮是否成功（只有轮次结束类事件有）。</summary>
    public bool Success { get; init; }

    /// <summary>会话内顺序号（<see cref="ChatEventKind.MessagePersisted"/>）。</summary>
    public int SequenceNumber { get; init; }

    /// <summary>
    /// 这一轮属于哪一次派发（等于 <c>chat_message.turn_id</c>，是宿主内的字符串编号）。
    ///
    /// <b>只有轮次结束类事件带它</b>（<see cref="ChatEventKind.RoundCompleted"/> /
    /// <see cref="ChatEventKind.RoundFailed"/>）。插件派发完一轮再等结果时必须靠它认领：
    /// 同一条会话上可能同时有用户开着窗口在聊，或者插件连着派了两个任务，
    /// 只看 <see cref="SessionId"/> 会把别人的结果当成自己的。
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// 这一轮是不是由插件经 <see cref="ISessionFacade"/> 发起的（用户自己发的为 false）。
    /// 与 <see cref="TurnId"/> 配合用更省事：「只认自己派的那一轮」直接看它。
    /// </summary>
    public bool IsPluginInitiated { get; init; }

    /// <summary>事件产生时间（Unix 毫秒）。</summary>
    public long Timestamp { get; init; }
}

/// <summary>
/// 订阅过滤。默认（<c>null</c>）= 全部会话 + 全部事件；
/// 只关心某个会话的插件（比如桌宠盯着当前会话）自己带上 <see cref="SessionId"/>。
/// </summary>
public sealed record ChatEventFilter
{
    /// <summary>只收这个会话的事件；<c>null</c> = 全部会话。</summary>
    public string? SessionId { get; init; }

    /// <summary>只收这些种类的事件；<c>null</c> = 全部种类。</summary>
    public IReadOnlyList<ChatEventKind>? Kinds { get; init; }

    /// <summary>全部会话、全部事件。</summary>
    public static ChatEventFilter All { get; } = new();

    /// <summary>这个事件是否要投递。</summary>
    public bool Matches(ChatEvent e)
    {
        if (SessionId is not null && !string.Equals(SessionId, e.SessionId, StringComparison.Ordinal))
            return false;

        if (Kinds is null) return true;

        foreach (var kind in Kinds)
            if (kind == e.Kind)
                return true;

        return false;
    }
}
