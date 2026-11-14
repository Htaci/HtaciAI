using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services;

/// <summary>
/// 给任意 <see cref="IChatClient"/> 加上「记一行 API 流水」的装饰器。
///
/// <b>为什么做成装饰器而不是在网关里记</b>：发请求的地方不止一处 —— 网关的对话循环、
/// 上下文压缩、话题命名、连接测试都直接调 <c>IChatClient.StreamAsync</c>，
/// 只有贴在客户端上才能一处不漏。它由 <see cref="ModelCatalog.BuildClient"/> 统一包上，
/// 所以任何从模型目录拿到的客户端都自带记录能力。
///
/// <b>位置在重试装饰器之内</b>：<c>RetryingChatClient</c> 包在它外面，
/// 于是自动重试的每一次尝试各记一行（attempt 由重试装饰器写在
/// <see cref="ApiCallContext.Attempt"/> 上），失败的尝试同样留痕。
///
/// <b>记录是旁路</b>：统计关闭、写库失败、格式异常，一律不影响正在进行的对话。
/// </summary>
public sealed class ApiRecordingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ModelDetails _details;

    public ApiRecordingChatClient(IChatClient inner, ModelDetails details)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _details = details;
    }

    public string ModelName => _inner.ModelName;

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        ChatRoundSink sink,
        CancellationToken ct)
    {
        // 统计关掉时连计时都不做，直接透传
        if (!AppSettingsStore.Current.ApiRequestLogEnabled)
            return await _inner.StreamAsync(history, systemPrompt, options, sink, ct);

        var call = options.ApiCall;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stopwatch = Stopwatch.StartNew();

        // 首字时刻：任何一类分片第一次到达都算「已经开始出东西了」
        long? ttft = null;
        void MarkFirstChunk()
        {
            ttft ??= stopwatch.ElapsedMilliseconds;
        }

        var watched = new ChatRoundSink
        {
            OnContent = delta => { MarkFirstChunk(); sink.OnContent(delta); },
            OnThinking = delta => { MarkFirstChunk(); sink.OnThinking(delta); },
            OnToolCall = delta => { MarkFirstChunk(); sink.OnToolCall(delta); },
        };

        try
        {
            var result = await _inner.StreamAsync(history, systemPrompt, options, watched, ct);

            _ = RecordAsync(phone =>
            {
                phone.Status = ApiRequestStatus.Ok;
                phone.HttpStatus = 200;
                phone.InputTokens = result.Usage.InputTokens;
                phone.CacheHitTokens = result.Usage.CacheHitTokens;
                phone.OutputTokens = result.Usage.OutputTokens;
                phone.Cost = ComputeCost(result.Usage);
            });

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点了「停止」：单独记一类，别混进失败率
            _ = RecordAsync(row =>
            {
                row.Status = ApiRequestStatus.Cancelled;
                row.ErrorKind = ApiErrorKinds.Other;
            });
            throw;
        }
        catch (Exception ex)
        {
            var (kind, status) = Classify(ex);
            _ = RecordAsync(row =>
            {
                row.Status = ApiRequestStatus.Failed;
                row.ErrorKind = kind;
                row.HttpStatus = status;
                row.ErrorMessage = Truncate(Describe(ex), 500);
            });
            throw;
        }

        void Fill(ApiRequest row)
        {
            row.Id = Guid.NewGuid().ToString("N");
            row.Kind = call?.Kind ?? ApiRequestKinds.Other;
            row.Attempt = Math.Max(1, call?.Attempt ?? 1);
            row.SessionId = call?.SessionId;
            row.TurnId = call?.TurnId;
            row.MessageId = call?.MessageId;

            row.ProviderName = NullIfEmpty(_details.ProviderName);
            row.ModelId = NullIfEmpty(_details.ModelId);
            row.ModelName = NullIfEmpty(_details.ModelName);
            row.Protocol = _details.Protocol.ToString();
            row.Endpoint = SanitizeEndpoint(_details.Endpoint);

            row.DurationMs = stopwatch.ElapsedMilliseconds;
            row.TtftMs = ttft;
            row.MessageCount = history.Count;
            row.ToolCount = options.Tools?.Count ?? 0;
            row.StartedAt = startedAt;
            row.Currency = _details.Currency;
        }

        Task RecordAsync(Action<ApiRequest> fill)
        {
            var row = new ApiRequest();
            try
            {
                Fill(row);
                fill(row);
            }
            catch
            {
                // 装填出错就当这次没记，别把异常带进流式路径
                return Task.CompletedTask;
            }

            // 落库不 await：流式回调里任何额外的同步等待都会直接反映成界面卡顿。
            // InsertAsync 内部自己吞异常，这里不会产生未观察的任务。
            // 用 Task.Run 换到线程池：不然 await 的续体会回到调用方的 UI 同步上下文，
            // 每条请求都往 UI 线程排一次 SQLite 写。
            return Task.Run(() => ApiRequestRepository.InsertAsync(row));
        }
    }

    // ---- 成本 ----

    /// <summary>
    /// 按写入当时的模型价格算一次金额快照（价格单位是「每百万 token」）。
    /// 价格一个都没填时返回 null —— 那比报一个假的 0 更诚实。
    /// 缓存命中价没填时退回输入价（宁可高估，也不要凭空给出一个好看的便宜数）。
    /// </summary>
    private double? ComputeCost(TokenUsage usage)
    {
        var priceIn = _details.PriceInput;
        var priceHit = _details.PriceCacheHit ?? priceIn;
        var priceOut = _details.PriceOutput;

        if (priceIn is null && priceOut is null) return null;

        var cacheHit = Math.Min(usage.CacheHitTokens, usage.InputTokens);
        var cacheMiss = Math.Max(0, usage.InputTokens - cacheHit);

        double cost = 0;
        if (priceIn is { } pin) cost += cacheMiss * pin / 1_000_000d;
        if (priceHit is { } phit) cost += cacheHit * phit / 1_000_000d;
        if (priceOut is { } pout) cost += usage.OutputTokens * pout / 1_000_000d;
        return cost;
    }

    // ---- 失败分类 ----

    /// <summary>
    /// 把异常归到统计页能聚合的几类上。<paramref name="status"/> 是能读到的 HTTP 状态码。
    /// 一律看原始异常：外层包装（如 <see cref="ChatStreamException"/>）不携带这些信息。
    /// </summary>
    private static (string Kind, int? Status) Classify(Exception ex)
    {
        var cause = ex is ChatStreamException broken ? broken.Cause : ex;

        if (cause is HttpRequestException http)
        {
            var status = (int?)http.StatusCode;
            if (status is null) return (ApiErrorKinds.Network, null);
            if (status is 401 or 403) return (ApiErrorKinds.Auth, status);
            if (status is 402) return (ApiErrorKinds.Quota, status);
            if (status is 429 && LooksLikeQuota(http.Message)) return (ApiErrorKinds.Quota, status);
            return (ApiErrorKinds.Http, status);
        }

        if (cause is TaskCanceledException or TimeoutException) return (ApiErrorKinds.Timeout, null);
        if (cause is System.IO.IOException or System.Net.Sockets.SocketException)
            return (ApiErrorKinds.Network, null);

        return (ApiErrorKinds.Other, null);
    }

    private static bool LooksLikeQuota(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var hint in new[] { "insufficient", "quota", "billing", "credit", "余额", "额度", "欠费", "配额" })
            if (message.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 错误文案：优先取 HTTP 状态与异常消息，去掉一层包装类的名字。
    /// 不落完整调用栈 —— 那是日志的事，统计表只放能一眼看懂的原因。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var cause = ex is ChatStreamException broken ? broken.Cause : ex;

        if (cause is HttpRequestException http)
        {
            var status = http.StatusCode is { } s ? $"HTTP {(int)s} " : "";
            return string.IsNullOrWhiteSpace(http.Message) ? $"{status}请求失败" : $"{status}{http.Message}";
        }

        return cause.GetType().Name + "：" + cause.Message;
    }

    // ---- 小工具 ----

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 地址落库前剥掉 query 与 fragment：有的中转站把 api_key 塞在 URL 里，
    /// 统计表不该成为第二份密钥副本。解析失败就整条不记。
    /// </summary>
    private static string? SanitizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : endpoint;
    }
}
