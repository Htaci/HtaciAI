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
    /// </summary>
    Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        Action<string> onContent,
        Action<string>? onThinking,
        CancellationToken ct);
}
