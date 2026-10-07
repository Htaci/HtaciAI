using System.Collections.Generic;

namespace HtaciAI.Services;

/// <summary>一次流式对话（单个 API 往返）的统一结果，各客户端返回结构一致。</summary>
public sealed record ChatStreamResult(
    string Content,
    string Thinking,
    string Role,
    string ToolCallId,
    IReadOnlyList<ChatToolCall> ToolCalls,
    TokenUsage Usage,
    string RawUsageJson);
