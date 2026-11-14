using System;

namespace HtaciAI.Models;

/// <summary>
/// 一次出站 API 请求的流水记录（<c>api_requests</c> 表的一行）。
///
/// <b>粒度是「一次 HTTP 往返」</b>：自动重试的每一次尝试各占一行，
/// 一轮带工具调用的任务会有几十行。统计页的所有数字都从这里聚合。
///
/// 刻意<b>不</b>记录的东西：请求体、响应体（体积与隐私）、api_key、
/// 以及 endpoint 的 query 串（有的中转站把密钥放 URL 里）。
/// </summary>
public sealed class ApiRequest
{
    public string Id { get; set; } = "";

    /// <summary>请求种类，见 <see cref="ApiRequestKinds"/>。</summary>
    public string Kind { get; set; } = ApiRequestKinds.Chat;

    /// <summary><c>ok</c> / <c>failed</c> / <c>cancelled</c>。</summary>
    public string Status { get; set; } = ApiRequestStatus.Ok;

    /// <summary>同一轮里的第几次尝试（自动重试时递增，从 1 起）。</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>所属会话。话题命名、连接测试这类请求没有会话，为 null。</summary>
    public string? SessionId { get; set; }

    /// <summary>所属 AI 轮次。</summary>
    public string? TurnId { get; set; }

    /// <summary>这一轮要落的 assistant 消息 id（能把流水对回具体消息）。</summary>
    public string? MessageId { get; set; }

    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }

    /// <summary>实际发给 API 的模型名（调用 id）。</summary>
    public string? ModelName { get; set; }

    /// <summary>协议名（OpenAI / OpenAIResponses / Anthropic / LMStudio）。</summary>
    public string? Protocol { get; set; }

    /// <summary>目标地址，已剥掉 query 与 fragment。</summary>
    public string? Endpoint { get; set; }

    /// <summary>HTTP 状态码。成功为 200；连不上 / 超时为 null。</summary>
    public int? HttpStatus { get; set; }

    /// <summary>失败分类，见 <see cref="ApiErrorKinds"/>。成功为 null。</summary>
    public string? ErrorKind { get; set; }

    /// <summary>失败原因（转成可读中文，已截断）。成功为 null。</summary>
    public string? ErrorMessage { get; set; }

    // ---- 用量：null 表示这次没拿到，不是 0 ----

    public long? InputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? OutputTokens { get; set; }

    /// <summary>按写入当时的价格算出的金额快照（价格单位是「每百万 token」）。</summary>
    public double? Cost { get; set; }

    public string? Currency { get; set; }

    /// <summary>首字延迟（毫秒）：从发出请求到第一个分片到达。null = 一个字都没收到。</summary>
    public long? TtftMs { get; set; }

    /// <summary>整次往返耗时（毫秒）。</summary>
    public long DurationMs { get; set; }

    /// <summary>这次发出去的历史消息条数。</summary>
    public int MessageCount { get; set; }

    /// <summary>这次下发的工具个数。</summary>
    public int ToolCount { get; set; }

    /// <summary>请求发出的时刻（Unix 毫秒）。</summary>
    public long StartedAt { get; set; }
}

/// <summary>请求种类。统计页按它分组，因此取值要稳定、不要在展示层现拼字符串。</summary>
public static class ApiRequestKinds
{
    /// <summary>会话里的对话请求（含工具循环的每一轮）。</summary>
    public const string Chat = "chat";

    /// <summary>自动话题命名。</summary>
    public const string Title = "title";

    /// <summary>上下文压缩 / 回复总结。</summary>
    public const string Compress = "compress";

    /// <summary>服务商连接测试。</summary>
    public const string Probe = "probe";

    /// <summary>其它（新增调用点忘了标 kind 时的兜底）。</summary>
    public const string Other = "other";

    public static string Label(string kind) => kind switch
    {
        Chat => "对话",
        Title => "话题命名",
        Compress => "压缩/总结",
        Probe => "连接测试",
        Other => "其它",
        _ => kind,
    };
}

/// <summary>请求状态。</summary>
public static class ApiRequestStatus
{
    public const string Ok = "ok";
    public const string Failed = "failed";

    /// <summary>用户点了「停止」。不算失败，不该计入失败率。</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>失败分类。分类而不是直接把异常类型名写进库：统计页要能按「连不上 / 超时 / 限流」聚合。</summary>
public static class ApiErrorKinds
{
    public const string Network = "network";
    public const string Timeout = "timeout";
    public const string Http = "http";
    public const string Quota = "quota";
    public const string Auth = "auth";
    public const string Other = "other";

    public static string Label(string? kind) => kind switch
    {
        Network => "网络",
        Timeout => "超时",
        Http => "服务端",
        Quota => "额度",
        Auth => "鉴权",
        Other => "其它",
        _ => "—",
    };
}

/// <summary>
/// 这一次请求的上下文（谁发的、属于哪个会话/轮次）。
///
/// 由调用方在 <see cref="Services.ChatRequestOptions"/> 上挂一份，
/// 记录装饰器（<see cref="Services.ApiRecordingChatClient"/>）读它来决定怎么落库。
/// <b>可变是刻意的</b>：同一份 options 会在工具循环的每一轮里复用，
/// 轮次与消息 id 只能就地更新。
/// </summary>
public sealed class ApiCallContext
{
    public string Kind { get; set; } = ApiRequestKinds.Chat;
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public string? MessageId { get; set; }

    /// <summary>当前是第几次尝试。由 <c>RetryingChatClient</c> 在每次尝试前写。</summary>
    public int Attempt { get; set; } = 1;
}
