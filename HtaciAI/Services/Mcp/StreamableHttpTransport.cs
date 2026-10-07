using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// <c>streamableHttp</c> 传输：每个请求一次 POST，响应体按 Content-Type 可能是 JSON，
/// 也可能是一条 SSE 流（服务端在处理过程中推出来的）。
///
/// 这是 MCP 现在推荐的远程传输，也是本项目最早实现的那个；
/// 与另外两个传输不同，它<b>没有长连接</b>，会话状态（Mcp-Session-Id）跟着每次请求走。
/// </summary>
public sealed class StreamableHttpTransport : McpTransportBase
{
    /// <summary>
    /// 超时统一由每个请求的 CTS 控制，所以不能设 HttpClient 的全局超时。
    /// 注意这里是 <see cref="System.Threading.Timeout"/>：基类有个同名的实例属性（本传输的超时时长），
    /// 不写全会出现「字段初始值设定项不能引用实例成员」。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private string? _sessionId;
    private bool _disposed;

    public StreamableHttpTransport(McpServerConfig config) : base(config) { }

    /// <summary>无长连接可建，直接可用。</summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>每次请求都是一次独立的 POST，没有「连着还是断了」的状态。</summary>
    public override bool IsAlive => !_disposed;

    public override async Task<string> SendAsync(string payload, int? expectedId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Config.Url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        // streamableHttp 要求客户端两种响应都能收
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpClient.ProtocolVersion);
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        foreach (var (key, value) in Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        CaptureSessionId(response);

        if (!response.IsSuccessStatusCode)
            throw new McpException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        // 202 = 通知类请求的正常应答，没有响应体
        if (expectedId is null || response.StatusCode == HttpStatusCode.Accepted)
            return "";

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        return mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? await ReadMatchingFrameAsync(response, expectedId.Value, cts.Token)
            : await response.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>
    /// 读 SSE 流直到出现 id 匹配的那条消息。服务端可能先推通知/心跳，忽略即可。
    /// 命中后立即返回，调用方释放 response 时会关掉流——服务端不主动断开也不会挂住。
    /// </summary>
    private async Task<string> ReadMatchingFrameAsync(HttpResponseMessage response, int expectedId, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var (_, data) in SseReader.ReadAsync(reader, ct))
        {
            if (data.Length == 0) continue;
            if (McpTransportBase.TryReadId(data, out var id) && id == expectedId) return data;

            McpLog.Info(Config.Id, "收到服务端推送，已忽略");
        }

        throw new McpException("SSE 流结束前没有收到匹配的响应");
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Mcp-Session-Id", out var values)) return;

        var value = values.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value)) _sessionId = value;
    }

    public override void Dispose()
    {
        // 无长连接、无子进程：HttpClient 是进程内共享的，不在这里释放
        _disposed = true;
    }
}
