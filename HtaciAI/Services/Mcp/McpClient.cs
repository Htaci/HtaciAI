using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services;

namespace HtaciAI.Services.Mcp;

/// <summary>MCP 协议层的失败。消息面向用户，可直接展示在「工具」标签页或工具结果里。</summary>
public sealed class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

/// <summary>tools/list 返回的一条远端工具描述。</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

/// <summary>
/// MCP 客户端（JSON-RPC 2.0 协议层）：握手、tools/list 翻页、tools/call、信封校验。
///
/// <b>不含任何传输细节</b>——报文怎么送出去由 <see cref="IMcpTransport"/> 决定，
/// 三种传输（streamableHttp / sse / stdio）的差异全在那边，这里只认「发一个报文、拿回一个报文」。
///
/// 一个客户端对应一个服务端连接，持有传输资源（stdio 的子进程、sse 的长流），
/// 因此<b>用完必须 Dispose</b>。
/// </summary>
public sealed class McpClient : IDisposable
{
    /// <summary>协议版本。服务端会在 initialize 响应里回它支持的那个，这里不做版本协商取舍。</summary>
    public const string ProtocolVersion = "2025-03-26";

    private readonly McpServerConfig _config;
    private readonly IMcpTransport _transport;

    private int _nextId;

    public McpClient(McpServerConfig config)
    {
        _config = config;
        _transport = CreateTransport(config);
    }

    private static IMcpTransport CreateTransport(McpServerConfig config) => config.Transport switch
    {
        McpTransport.Stdio => new StdioTransport(config),
        McpTransport.Sse => new SseTransport(config),
        _ => new StreamableHttpTransport(config),
    };

    public string ServerId => _config.Id;

    public string ServerName => _config.Name;

    /// <summary>
    /// 通道是否还活着。stdio 的子进程退出、sse 的长流断开之后会变 false，
    /// 连接管理器据此把死连接丢掉并重新握手（<see cref="McpConnectionManager.EnsureConnectedAsync"/>）。
    /// </summary>
    public bool IsAlive => _transport.IsAlive;

    /// <summary>initialize 握手 + notifications/initialized。返回服务端自报名。</summary>
    public async Task<string> InitializeAsync(CancellationToken ct)
    {
        McpLog.Info(ServerId, $"开始握手 {DescribeTarget()}（超时 {_config.TimeoutSeconds}s）");

        // 建立通道要先于一切：stdio 要拉起子进程，sse 要建立长流并拿到 endpoint
        await _transport.StartAsync(ct);

        using var document = await SendRequestAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "HtaciAI", version = AppVersion.Current },
        }, ct);

        var root = document.RootElement;
        var serverName = root.TryGetProperty("serverInfo", out var info) &&
                         info.TryGetProperty("name", out var name)
            ? name.GetString() ?? "未命名服务"
            : "未命名服务";
        var protocol = root.TryGetProperty("protocolVersion", out var version)
            ? version.GetString() ?? "?"
            : "?";

        await SendNotificationAsync("notifications/initialized", ct);

        McpLog.Success(ServerId, $"握手完成：{serverName}（协议 {protocol}）");

        return serverName;
    }

    /// <summary>日志里怎么称呼这个服务端：stdio 说命令，另外两个说地址。</summary>
    private string DescribeTarget() => _config.Transport == McpTransport.Stdio
        ? $"{_config.Command}（stdio）"
        : $"{_config.Url}（{McpTransportInfo.Wire(_config.Transport)}）";

    /// <summary>释放底层通道：stdio 会收掉子进程，sse 会关掉长流。</summary>
    public void Dispose() => _transport.Dispose();

    /// <summary>tools/list，自动翻页。</summary>
    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;

        do
        {
            using var document = await SendRequestAsync(
                "tools/list",
                cursor is null ? new { } : new { cursor },
                ct);

            var root = document.RootElement;

            if (root.TryGetProperty("tools", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;

                    var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var schema = item.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";

                    tools.Add(new McpToolInfo(name, description, schema));
                }
            }

            cursor = root.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(cursor));

        McpLog.Success(ServerId, $"发现 {tools.Count} 个工具");
        return tools;
    }

    /// <summary>tools/call。返回拼接后的文本结果；服务端标记 isError 时抛 <see cref="McpException"/>。</summary>
    public async Task<string> CallToolAsync(string toolName, string? argumentsJson, CancellationToken ct)
    {
        McpLog.Info(ServerId, $"调用工具 {toolName}");

        using var document = await SendRequestAsync(
            "tools/call",
            new { name = toolName, arguments = ParseArguments(argumentsJson) },
            ct);

        var root = document.RootElement;
        var text = ExtractText(root);

        if (root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new McpException(string.IsNullOrWhiteSpace(text) ? "工具返回了错误" : text);

        McpLog.Success(ServerId, $"工具 {toolName} 返回 {text.Length} 字符");
        return text;
    }

    // ---- JSON-RPC 收发 ----

    private async Task<JsonDocument> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });

        var body = await _transport.SendAsync(payload, id, ct);
        return ParseEnvelope(body);
    }

    private async Task SendNotificationAsync(string method, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method });
        await _transport.SendAsync(payload, null, ct);
    }

    /// <summary>校验 JSON-RPC 信封，取出 result 部分。</summary>
    private static JsonDocument ParseEnvelope(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new McpException("服务端返回了空响应");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new McpException($"响应不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                throw new McpException($"服务端错误：{message}");
            }

            if (!root.TryGetProperty("result", out var result))
                throw new McpException("响应里没有 result 字段");

            return JsonDocument.Parse(result.GetRawText());
        }
    }

    // ---- 解析辅助 ----

    /// <summary>MCP 的 tools/call 结果是 content 数组，把文本块拼起来，非文本块给个占位说明。</summary>
    private static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.GetRawText();

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            switch (block.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "text":
                    if (block.TryGetProperty("text", out var text)) builder.Append(text.GetString());
                    break;
                case "image":
                    builder.Append("[图片结果]");
                    break;
                case "resource":
                    builder.Append("[资源结果]");
                    break;
                default:
                    builder.Append(block.GetRawText());
                    break;
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private static JsonElement ParseArguments(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

}
