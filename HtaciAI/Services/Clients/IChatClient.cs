using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>模型客户端统一接口：一次调用 = 一次 API 往返，不包含工具循环与持久化。</summary>
public interface IChatClient
{
    /// <summary>发送给 API 的模型名（用于落库/展示）。</summary>
    string ModelName { get; }

    /// <summary>
    /// 流式对话。system 作为独立参数传入（由客户端在拼请求时前置成 system 消息），
    /// 以兼容 Anthropic 这类不支持 system-in-messages 的协议。
    ///
    /// 三类分片（正文 / 思考 / 工具调用）都通过 <paramref name="sink"/> 实时回调出去，
    /// 返回值则是这一整轮的汇总结果。界面依赖工具调用的回调做「参数正在接收」的实时反馈，
    /// 所以客户端<b>不能</b>把 tool_calls 攒到最后一次性返回。
    /// </summary>
    Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        ChatRoundSink sink,
        CancellationToken ct);
}
