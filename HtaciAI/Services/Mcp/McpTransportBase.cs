using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// MCP 的传输层：只负责「把一个 JSON-RPC 报文发出去、把响应报文拿回来」。
/// 协议本身（握手、tools/list、tools/call、信封校验）在 <see cref="McpClient"/> 里，与传输无关。
///
/// 三种传输分成两类：
/// <list type="bullet">
///   <item><b>一来一回</b>（streamableHttp）：发一个 POST，响应就在这次响应体里。<see cref="StreamableHttpTransport"/></item>
///   <item><b>长连接 + 按 id 配对</b>（sse 的长流、stdio 的管道）：请求和响应走在不同的时间线上，
///     需要一个后台读循环把响应按 id 派发给等待方。<see cref="ChannelTransportBase"/> 把这套共用逻辑收在一处。</item>
/// </list>
/// </summary>
public abstract class McpTransportBase : IMcpTransport
{
    protected McpTransportBase(Models.McpServerConfig config)
    {
        Config = config;
        Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 600));
        Headers = McpConfigParsing.ParseKeyValues(config.HeadersRaw);
    }

    protected Models.McpServerConfig Config { get; }

    /// <summary>单次请求的等待上限（来自配置，已夹到 1–600 秒）。</summary>
    protected TimeSpan Timeout { get; }

    /// <summary>自定义请求头（stdio 用不到，sse / streamableHttp 用）。</summary>
    protected Dictionary<string, string> Headers { get; }

    public abstract Task StartAsync(CancellationToken ct);

    /// <param name="expectedId">null 表示这是通知，不解析响应体。</param>
    /// <returns>响应报文的原始 JSON 文本；通知返回空串。</returns>
    public abstract Task<string> SendAsync(string payload, int? expectedId, CancellationToken ct);

    /// <summary>
    /// 通道是否还能用。stdio 的子进程退出、sse 的长流断开之后这里会变 false，
    /// 好让连接管理器把它丢掉并重新握手，而不是一直拿一条死连接去撞墙。
    /// </summary>
    public abstract bool IsAlive { get; }

    public abstract void Dispose();

    /// <summary>从报文里取 JSON-RPC 的 id。取不到（服务端推送的通知）返回 false。</summary>
    protected static bool TryReadId(string json, out int id)
    {
        id = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadId(document.RootElement, out id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>同上，但接收已经解析好的根元素（读循环里已经解析过一次，不必重复解析）。</summary>
    protected static bool TryReadId(JsonElement root, out int id)
    {
        id = 0;
        if (!root.TryGetProperty("id", out var value)) return false;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out id),
            JsonValueKind.String => int.TryParse(value.GetString(), out id),
            _ => false,
        };
    }
}

/// <summary>传输层抽象。见 <see cref="McpTransportBase"/> 的说明。</summary>
public interface IMcpTransport : IDisposable
{
    /// <summary>建立通道。streamableHttp 无需准备，返回已完成的任务。</summary>
    Task StartAsync(CancellationToken ct);

    /// <param name="expectedId">null 表示通知：只发不收。</param>
    Task<string> SendAsync(string payload, int? expectedId, CancellationToken ct);

    /// <summary>通道是否还能用。断开之后连接管理器会把它丢掉并重新握手。</summary>
    bool IsAlive { get; }
}

/// <summary>
/// 「长连接 + 按 id 配对」两类传输的共用部分：把响应按 id 派发给对应的等待方，
/// 并在通道断掉时让所有等待方立刻失败（而不是各自干等到超时）。
///
/// 子类只需要实现 <see cref="WriteAsync"/>（怎么写出去），并在读循环里把收到的每条完整报文交给 <see cref="Dispatch"/>。
/// </summary>
public abstract class ChannelTransportBase : McpTransportBase
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();

    /// <summary>通道断掉的原因；非 null 之后所有请求直接失败。</summary>
    private volatile string? _fault;

    /// <summary>已释放。子类在 Dispose 里置位，供 <see cref="IsAlive"/> 用。</summary>
    protected bool Disposed { get; set; }

    protected ChannelTransportBase(Models.McpServerConfig config) : base(config) { }

    /// <summary>长连接断了（<see cref="Fault"/> 被调用过）就当作不可用。</summary>
    public override bool IsAlive => !Disposed && _fault is null;

    /// <summary>把一条报文写给对端（写管道 / POST 到 endpoint）。</summary>
    protected abstract Task WriteAsync(string payload, CancellationToken ct);

    /// <summary>断线标记：让后续请求直接失败，并唤醒所有正在等待的请求。</summary>
    protected void Fault(string reason)
    {
        _fault = reason;

        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var waiter))
                waiter.TrySetException(new McpException(reason));
    }

    /// <summary>读循环收到一条完整报文时调用：按 id 派发给等待方，无主的当作服务端推送忽略。</summary>
    protected void Dispatch(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // 报文压根不是合法 JSON。**不能**当成「服务端推送」悄悄忽略：那样调用方只会看到
            // 一句「等待响应超时」，完全查不到真正的毛病在服务端那边。这里留一条明确的警告。
            McpLog.Warn(Config.Id, $"收到不是合法 JSON 的报文，已忽略：{ex.Message}");
            return;
        }

        if (!TryReadId(root, out var id))
        {
            McpLog.Info(Config.Id, "收到服务端推送，已忽略");
            return;
        }

        if (_pending.TryGetValue(id, out var waiter))
            waiter.TrySetResult(json);
    }

    public sealed override async Task<string> SendAsync(string payload, int? expectedId, CancellationToken ct)
    {
        if (_fault is { } fault) throw new McpException(fault);

        TaskCompletionSource<string>? waiter = null;
        if (expectedId is { } id)
        {
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
        }

        try
        {
            await WriteAsync(payload, ct);
            if (waiter is null) return "";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            try
            {
                return await waiter.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpException($"等待响应超时（{Timeout.TotalSeconds:0} 秒）");
            }
        }
        finally
        {
            if (expectedId is { } key) _pending.TryRemove(key, out _);
        }
    }
}
