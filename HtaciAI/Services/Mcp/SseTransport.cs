using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// <c>sse</c> 传输（旧版 HTTP+SSE，协议 2024-11-05 那一版）。
///
/// 与 streamableHttp 的根本差别是<b>响应不走 POST 的响应体</b>：
/// <list type="number">
///   <item>客户端 <c>GET</c> 服务地址，建立一条 <c>text/event-stream</c> 长流；</item>
///   <item>服务端第一件事就是推一条 <c>event: endpoint</c>，告诉客户端「请求请 POST 到哪里」；</item>
///   <item>之后所有 JSON-RPC 都 POST 到那个 endpoint（正常返回 202 空体），
///     而<b>真正的响应报文从那条长流上回来</b>，靠 id 配对。</item>
/// </list>
///
/// 正因为响应是异步回来的，它继承 <see cref="ChannelTransportBase"/>：后台读循环负责派发，
/// 请求方在 <c>TaskCompletionSource</c> 上等。
/// </summary>
public sealed class SseTransport : ChannelTransportBase
{
    /// <summary>同上：全限定的 <c>System.Threading.Timeout</c>，避开基类的同名实例属性。</summary>
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly CancellationTokenSource _streamCts = new();
    private readonly TaskCompletionSource<string> _endpointReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HttpResponseMessage? _response;
    private string? _endpoint;

    public SseTransport(McpServerConfig config) : base(config) { }

    public override async Task StartAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Config.Url);
        request.Headers.Accept.ParseAdd("text/event-stream");
        foreach (var (key, value) in Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        _response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!_response.IsSuccessStatusCode)
            throw new McpException($"HTTP {(int)_response.StatusCode} {_response.ReasonPhrase}");

        _ = Task.Run(() => ReadLoopAsync(_streamCts.Token), CancellationToken.None);

        // 等服务端把 endpoint 推过来；超时或流提前断掉都会在这里抛出
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        _endpoint = await _endpointReady.Task.WaitAsync(timeout.Token);

        McpLog.Success(Config.Id, $"SSE 通道已建立：{_endpoint}");
    }

    protected override async Task WriteAsync(string payload, CancellationToken ct)
    {
        if (_endpoint is null) throw new McpException("SSE 通道尚未建立完成");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"POST 到 SSE endpoint 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = await _response!.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            await foreach (var (eventName, data) in SseReader.ReadAsync(reader, ct))
            {
                if (data.Length == 0) continue;

                if (string.Equals(eventName, "endpoint", StringComparison.OrdinalIgnoreCase))
                {
                    _endpointReady.TrySetResult(ResolveEndpoint(Config.Url, data));
                    continue;
                }

                Dispatch(data);
            }

            _endpointReady.TrySetException(new McpException("SSE 长流在建立完成前就结束了"));
            Fault("SSE 长流已被服务端关闭");
        }
        catch (OperationCanceledException)
        {
            // 自己 Dispose 时取消的，正常路径
        }
        catch (Exception ex)
        {
            _endpointReady.TrySetException(new McpException($"建立 SSE 通道失败：{ex.Message}"));
            Fault($"SSE 连接中断：{ex.Message}");
        }
    }

    /// <summary>
    /// endpoint 可能是相对地址（多数实现给的就是 <c>/messages?sessionId=…</c>），按服务地址拼成绝对地址。
    /// 公开出来是为了能单独验证这段拼接——它是 SSE 传输里最容易出错的一步。
    /// </summary>
    public static string ResolveEndpoint(string baseUrl, string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return new Uri(new Uri(baseUrl), endpoint).ToString();
    }

    /// <summary>可重入：连接失效与握手失败两条路径都可能释放同一个传输。</summary>
    public override void Dispose()
    {
        if (Disposed) return;
        Disposed = true;

        try { _streamCts.Cancel(); } catch (ObjectDisposedException) { /* 已经释放过 */ }

        _response?.Dispose();
        _response = null;
        _streamCts.Dispose();
    }
}
