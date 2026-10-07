using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// MCP 连接的生命周期与工具注册。进程内单例：一个服务端只连一次，发现到的工具注册进全局
/// <see cref="ToolRegistry"/>，之后就和内置工具走同一条解析/执行链路。
///
/// <b>必须显式 <see cref="Invalidate"/></b>：注册表是全局的，配置改了或服务删了之后，
/// 残留的旧工具仍会被会话解析到，调用必然失败。
/// </summary>
public sealed class McpConnectionManager
{
    /// <summary>
    /// 连接阶段的最长等待。协议里那个 60s 是「调用」的耐心，不能拿它卡住用户发消息——
    /// 连不上就跳过，让对话照常进行。
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 连接失败后的冷却期。没有它的话，一个连不上的服务会让<b>每一条</b>消息都白等 10 秒超时——
    /// 会话是「发消息前确保连接」的，必须避免反复重试。
    /// </summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);

    public static McpConnectionManager Instance { get; } = new();

    private readonly ConcurrentDictionary<string, McpConnection> _connections = new();
    private readonly ConcurrentDictionary<string, string> _lastErrors = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpConnectionManager() { }

    /// <summary>
    /// 会话入口：确保这些服务已启动并连上。未启动的会顺手启动（用户要的就是这个——
    /// 会话里选了某个 MCP，不该因为它没开着就静默失效）。单个失败只记日志、不抛出。
    /// </summary>
    public async Task EnsureToolsAsync(IEnumerable<string> serverIds, CancellationToken ct)
    {
        foreach (var id in serverIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            await EnsureConnectedAsync(id, ct, autoStart: true);
    }

    /// <param name="autoStart">服务处于「未启动」时是否顺手启动它。</param>
    /// <param name="ignoreCooldown">用户手动点「启动/连接」时为 true，跳过失败冷却立刻重试。</param>
    public async Task<bool> EnsureConnectedAsync(
        string serverId, CancellationToken ct, bool autoStart = false, bool ignoreCooldown = false)
    {
        if (_connections.TryGetValue(serverId, out var established))
        {
            if (established.Client.IsAlive) return true;

            // 通道已经死了（stdio 子进程退出、sse 长流断开）。丢掉它走下面的重连 ——
            // 否则每一轮都会拿这条死连接去撞墙，错误一直重复到用户手动重连为止。
            McpLog.Warn(serverId, "连接已断开，正在重新建立");
            Invalidate(serverId);
        }

        if (!ignoreCooldown && IsCoolingDown(serverId)) return false;

        await _gate.WaitAsync(ct);
        try
        {
            // 等锁期间可能已被别的调用连上
            if (_connections.TryGetValue(serverId, out var raced) && raced.Client.IsAlive) return true;

            var config = await McpServerRepository.GetAsync(serverId);
            if (config is null)
            {
                McpLog.Error(serverId, "服务配置不存在，可能已被删除");
                _lastErrors[serverId] = "服务配置不存在";
                return false;
            }

            if (!config.Enabled)
            {
                if (!autoStart)
                {
                    _lastErrors[serverId] = "服务未启动";
                    return false;
                }

                config.Enabled = true;
                await McpServerRepository.UpdateAsync(config);
                McpLog.Info(serverId, "会话使用了此服务，已自动启动");
            }

            if (McpTransportInfo.Validate(config) is { } problem)
            {
                McpLog.Error(serverId, problem);
                _lastErrors[serverId] = problem;
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);

            _lastAttempts[serverId] = DateTime.UtcNow;

            var client = new McpClient(config);
            try
            {
                await client.InitializeAsync(timeout.Token);
                var tools = await client.ListToolsAsync(timeout.Token);

                var connection = new McpConnection(client, tools);
                RegisterTools(connection, config);
                _connections[serverId] = connection;
                _lastErrors.TryRemove(serverId, out _);
                _lastAttempts.TryRemove(serverId, out _);
                return true;
            }
            catch (Exception ex)
            {
                // 失败时也要收掉：stdio 可能已经把子进程拉起来了（握手失败但进程还在）
                try { client.Dispose(); } catch { /* 释放路径上的异常不往上抛 */ }

                var message = Describe(ex);
                McpLog.Error(serverId, $"连接失败：{message}");
                _lastErrors[serverId] = message;
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止：注销工具、丢弃连接，并把「启动」标记写回 false。</summary>
    public async Task StopAsync(string serverId)
    {
        Invalidate(serverId);
        _lastErrors.TryRemove(serverId, out _);
        _lastAttempts.TryRemove(serverId, out _);

        var config = await McpServerRepository.GetAsync(serverId);
        if (config is null || !config.Enabled) return;

        config.Enabled = false;
        await McpServerRepository.UpdateAsync(config);
        McpLog.Info(serverId, "服务已停止");
    }

    /// <summary>配置变更或服务删除后调用：注销其工具并丢弃连接，下次使用会重新握手。</summary>
    public void Invalidate(string serverId)
    {
        // 配置改了，之前的失败冷却不该再拦住重试
        _lastAttempts.TryRemove(serverId, out _);

        if (!_connections.TryRemove(serverId, out var connection)) return;

        foreach (var id in connection.RegisteredIds)
            ToolRegistry.Instance.Unregister(id);

        // 必须真的释放传输：stdio 不释放就会留下孤儿进程，sse 不释放会长占着一条连接
        try { connection.Client.Dispose(); }
        catch { /* 释放失败也要继续把连接丢弃 */ }

        McpLog.Info(serverId, "连接已失效，相关工具已注销");
    }

    /// <summary>
    /// 退出应用前收掉所有连接。目前只有 stdio 需要（会留子进程），但统一收一遍更省心。
    /// 由 <c>App</c> 挂在 <c>ShutdownRequested</c> 上，见那边的注释。
    /// </summary>
    public void ShutdownAll()
    {
        foreach (var key in _connections.Keys.ToList())
            Invalidate(key);
    }

    public bool IsConnected(string serverId) => _connections.ContainsKey(serverId);

    /// <summary>最近一次连接失败的原因；连上或停止后清空。“已启动但没连上”就靠它给出黄点的悬浮说明。</summary>
    public string? GetLastError(string serverId)
        => _lastErrors.TryGetValue(serverId, out var error) ? error : null;

    private bool IsCoolingDown(string serverId)
        => _lastAttempts.TryGetValue(serverId, out var at) && DateTime.UtcNow - at < RetryCooldown;

    public McpClient? GetClient(string serverId)
        => _connections.TryGetValue(serverId, out var connection) ? connection.Client : null;

    /// <summary>已连接时返回发现的工具，否则 null（用于「工具」标签区分「没连」和「连了但没工具」）。</summary>
    public IReadOnlyList<McpToolInfo>? GetTools(string serverId)
        => _connections.TryGetValue(serverId, out var connection) ? connection.Tools : null;

    /// <summary>MCP 工具的 id 规则。服务 id 是 32 位十六进制，不含冒号，所以可以按 ':' 反解。</summary>
    public static string BuildToolId(string serverId, string toolName) => $"mcp:{serverId}:{toolName}";

    /// <summary>从工具 id 反解远端工具名。</summary>
    public static string RemoteName(string toolId)
    {
        var parts = toolId.Split(':', 3);
        return parts.Length == 3 ? parts[2] : toolId;
    }

    /// <summary>
    /// id 里带服务 id 保证唯一；<b>Name</b> 撞车时加服务名前缀——Name 才是发给 LLM 的调用名，
    /// 两个服务暴露同名工具时必须区分开。
    /// </summary>
    private static void RegisterTools(McpConnection connection, McpServerConfig config)
    {
        foreach (var tool in connection.Tools)
        {
            var id = BuildToolId(config.Id, tool.Name);
            var name = tool.Name;

            if (ToolRegistry.Instance.ResolveByName(name) is { } existing && existing.Id != id)
            {
                name = $"{Sanitize(config.Name)}_{tool.Name}";
                McpLog.Warn(config.Id, $"工具名 {tool.Name} 与已有工具冲突，改用 {name}");
            }

            ToolRegistry.Instance.Register(new ToolDefinition
            {
                Id = id,
                Name = name,
                Description = string.IsNullOrWhiteSpace(tool.Description)
                    ? $"来自 MCP 服务「{config.Name}」的工具"
                    : tool.Description,
                InputSchemaJson = tool.InputSchemaJson,
                Source = ToolSource.Mcp,
                Target = config.Id,
                DangerLevel = ToolDangerLevel.Danger,
            });

            connection.RegisteredIds.Add(id);
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        var result = new string(chars).Trim('_');
        return result.Length == 0 ? "mcp" : result;
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => $"超时（{ConnectTimeout.TotalSeconds:0} 秒）",
        McpException => ex.Message,
        _ => ex.Message,
    };

    /// <summary>一个已建立的服务端连接及其注册出去的工具 id。</summary>
    private sealed class McpConnection(McpClient client, List<McpToolInfo> tools)
    {
        public McpClient Client { get; } = client;
        public List<McpToolInfo> Tools { get; } = tools;
        public List<string> RegisteredIds { get; } = new();
    }
}
