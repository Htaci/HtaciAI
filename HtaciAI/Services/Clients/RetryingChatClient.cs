using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 给「一次 API 往返」加上自动重试的装饰器。
///
/// <b>重试的粒度和计数：</b>一次 <see cref="StreamAsync"/> 调用是最多 5 次尝试，
/// 不是「整个任务 5 次」。一轮工具循环里每发一次请求都重新数 5 次 —— 一次任务可能有
/// 十几轮请求，每一轮都该各有自己的重试额度。也要注意它和网关的 <c>MaxToolRounds</c>
/// （工具循环上限）是两件互不相干的事，别混在一起算。
///
/// <b>只重试「来得及重来」的失败：</b>
/// <list type="bullet">
///   <item>网络层：连不上、连接被掐断、读流中断（IOException / SocketException）。</item>
///   <item>超时：HttpClient 把超时也报成 <see cref="OperationCanceledException"/>，
///         只能靠「ct 没被取消」把它和用户点「停止」区分开。</item>
///   <item>5xx / 408 / 429 限流这类服务端临时故障。</item>
/// </list>
/// <b>不重试：</b>400 / 401 / 403 / 404 / 422 这类「请求本身就是错的」，
/// 以及余额不足、内容审计等「再来一次也一样」的失败 —— 这些交给用户在错误卡上点「重试」自己决定。
///
/// <b>已经吐出内容就不重试了。</b>重试要走同一份 <see cref="ChatRoundSink"/>，
/// 而正文块、思考卡、工具卡都是<b>边收边追加</b>的，没有「回滚到上一次尝试」这种操作：
/// 硬重试会把内容写重、工具卡写重。所以一旦有任何分片到过界面，就当作不可重来，
/// 带着已生成的内容抛出去（见 <see cref="ChatStreamException"/>）。
/// </summary>
public sealed class RetryingChatClient : IChatClient
{
    /// <summary>一次 API 往返的总尝试次数（含第一次）。</summary>
    public const int MaxAttempts = 5;

    /// <summary>第 1～4 次失败后的等待时间。最后一次失败不再等 —— 等完也不会重试。</summary>
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(1400),
        TimeSpan.FromMilliseconds(3000),
        TimeSpan.FromMilliseconds(5000),
    };

    private readonly IChatClient _inner;
    private readonly Action<int, int>? _onRetry;

    /// <param name="onRetry">
    /// 即将重试时的通知（下一个尝试序号, 总尝试数）。用来在输入区显示「正在重试 2/5」——
    /// 一断连就静默地晾用户十几秒，看起来就是「程序卡死了」。
    /// </param>
    public RetryingChatClient(IChatClient inner, Action<int, int>? onRetry = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onRetry = onRetry;
    }

    public string ModelName => _inner.ModelName;

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        ChatRoundSink sink,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // 统计流水要能看出「这是同一轮的第二次尝试」：记在 options 上，由内层的
            // ApiRecordingChatClient 读走（它每次调用都会落一行，失败的尝试也留痕）。
            if (options.ApiCall is { } apiCall) apiCall.Attempt = attempt;

            // 本次尝试往界面里送过什么。emitted 决定「还能不能重来」，
            // 两个缓冲用来在放弃时把已生成的内容交还给上层（见 ChatStreamException）。
            var emitted = false;
            var content = new StringBuilder();
            var thinking = new StringBuilder();

            var watched = new ChatRoundSink
            {
                OnContent = delta =>
                {
                    emitted = true;
                    content.Append(delta);
                    sink.OnContent(delta);
                },
                OnThinking = delta =>
                {
                    emitted = true;
                    thinking.Append(delta);
                    sink.OnThinking(delta);
                },
                OnToolCall = delta =>
                {
                    emitted = true;
                    sink.OnToolCall(delta);
                },
            };

            try
            {
                return await _inner.StreamAsync(history, systemPrompt, options, watched, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户点了「停止」：一秒都别再试，直接上抛（网关按 interrupted 收尾）
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !emitted && IsTransient(ex, ct))
            {
                _onRetry?.Invoke(attempt + 1, MaxAttempts);

                // 等待也受 ct 管：用户在「等待重试」的这几秒里点停止，要能立刻停下来
                await Task.Delay(Delays[Math.Min(attempt - 1, Delays.Length - 1)], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 走到这里就是「不再重试」的失败。已收到的正文 / 思考跟着异常一起交出去，
                // 否则那条失败的 assistant 消息落库之后是空正文，用户点重试时上一轮的思考链就白想了。
                if (content.Length == 0 && thinking.Length == 0) throw;
                throw new ChatStreamException(ex, content.ToString(), thinking.ToString());
            }
        }
    }

    /// <summary>这次失败值不值得重来。判定一律看包装里的原始异常。</summary>
    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        var cause = ex is ChatStreamException broken ? broken.Cause : ex;

        // 传进来的 ct 已被取消 → 其实是「用户停止」，不是故障。
        // 这个分支在 catch 过滤器里已经被排除了，这里再挡一次是为了防住延迟期间的取消。
        if (ct.IsCancellationRequested) return false;

        // 超时：HttpClient / 我们的读取都用 TaskCanceledException 表示超时（ct 没被取消时）
        if (cause is TaskCanceledException or TimeoutException) return true;

        if (cause is HttpRequestException http)
        {
            // 没有状态码 = 压根没连上（DNS、拒绝连接、TLS 失败），属于典型可重试
            if (http.StatusCode is not { } status) return true;

            var code = (int)status;

            // 余额 / 额度用尽：服务端常用 429 或 402 报这件事，重试只会白等
            if (code is 402 or 429 && LooksLikeQuotaExhausted(http.Message)) return false;

            // 402 是「请付费」，与 400/401/403 同类：再来一次也一样
            return code is 408 or 425 or 429 || code is >= 500 and < 600;
        }

        // 读流中断：连接被掐、TLS 提前关掉等
        return cause is IOException or SocketException;
    }

    /// <summary>错误文案里出现账单/额度字样：把它当永久失败，别拿重试去等一个充不上钱的额度。</summary>
    private static bool LooksLikeQuotaExhausted(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;

        foreach (var hint in new[]
                 {
                     "insufficient", "quota", "billing", "credit",
                     "余额", "额度", "欠费", "配额",
                 })
            if (message.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
