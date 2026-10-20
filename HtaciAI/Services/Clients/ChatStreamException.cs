using System;

namespace HtaciAI.Services;

/// <summary>
/// 流式请求中途失败时抛出的包装异常：除了原始异常，还带着<b>已经收到的</b>正文与思考。
///
/// 为什么要带这部分内容：客户端是边读 SSE 边回调 <see cref="ChatRoundSink"/> 的，进度全在
/// 界面控件里；异常一旦抛出去，客户端方法里那几个 <c>StringBuilder</c> 就没了 ——
/// 落库的那条 assistant 消息只剩空正文，用户点「重试」后，上一轮想出来的思考链彻底消失，
/// 只能从零再想一遍。把这个部分内容交出来，网关就能把它写进那条失败的 assistant 消息里，
/// 界面重开也还在。
///
/// <see cref="Exception.Message"/> 直接取内层异常的消息：错误卡的标题取的是消息首行
/// （见 <c>ChatError.Summarize</c>），套一层自己的文案只会把「HTTP 502」这种关键信息挤掉。
/// </summary>
public sealed class ChatStreamException : Exception
{
    public ChatStreamException(Exception cause, string partialContent, string partialThinking)
        : base(cause.Message, cause)
    {
        PartialContent = partialContent ?? "";
        PartialThinking = partialThinking ?? "";
    }

    /// <summary>失败前已经收到的正文（可能为空）。</summary>
    public string PartialContent { get; }

    /// <summary>失败前已经收到的思考内容（可能为空）。</summary>
    public string PartialThinking { get; }

    /// <summary>原始异常。落错误记录要的是它 —— 用户关心的是「为什么失败」，不是这层包装。</summary>
    public Exception Cause => InnerException ?? this;
}
