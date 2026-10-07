using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HtaciAI.Services.Mcp;

public enum McpLogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <param name="ServerId">所属 MCP 服务 id。</param>
/// <param name="At">本地时间，用于展示。</param>
/// <param name="Level">级别，决定着色。</param>
/// <param name="Message">已经过敏感信息掩码的文本。</param>
public sealed record McpLogEntry(string ServerId, DateTime At, McpLogLevel Level, string Message);

/// <summary>
/// MCP 的进程内日志环形缓冲。项目本身没有任何日志设施（只有 LogToTrace），
/// MCP 页的「日志」标签需要实时内容，所以单独建一个：容量固定、只留最近若干条，不会无限增长。
/// </summary>
public static partial class McpLog
{
    private const int Capacity = 500;

    private static readonly object Gate = new();
    private static readonly LinkedList<McpLogEntry> Entries = new();

    /// <summary>新日志入队时触发。可能在任意线程上，订阅方自行切回 UI 线程。</summary>
    public static event Action<McpLogEntry>? EntryAdded;

    public static void Info(string serverId, string message) => Add(serverId, McpLogLevel.Info, message);

    public static void Success(string serverId, string message) => Add(serverId, McpLogLevel.Success, message);

    public static void Warn(string serverId, string message) => Add(serverId, McpLogLevel.Warning, message);

    public static void Error(string serverId, string message) => Add(serverId, McpLogLevel.Error, message);

    /// <summary>取某个服务的日志快照（按时间正序）。serverId 传 null 取全部。</summary>
    public static List<McpLogEntry> Snapshot(string? serverId = null)
    {
        lock (Gate)
        {
            return serverId is null
                ? Entries.ToList()
                : Entries.Where(e => e.ServerId == serverId).ToList();
        }
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    private static void Add(string serverId, McpLogLevel level, string message)
    {
        var entry = new McpLogEntry(serverId, DateTime.Now, level, Mask(message));

        lock (Gate)
        {
            Entries.AddLast(entry);
            while (Entries.Count > Capacity) Entries.RemoveFirst();
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>
    /// 掩码请求头里的凭据。配置本身明文存库（与 ai_provider.api_key 的既有约定一致），
    /// 但日志会被界面展示、也可能被贴出去，至少别把密钥原样打出来。
    /// </summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 保留名字、分隔符与认证方案（Bearer 之类本身不是秘密），只把凭据换成 ***。
        // 注意不能只吃掉第一个词：`Bearer sk-xxx` 里真正要藏的是后面那个 token。
        return SensitiveHeaderRegex().Replace(text, match =>
            $"{match.Groups["name"].Value}{match.Groups["sep"].Value}{match.Groups["scheme"].Value}***");
    }

    /// <summary>匹配 <c>名字: 值</c> 形式，名字命中敏感词才处理。</summary>
    [GeneratedRegex(
        @"(?<name>(?i:authorization|api[-_]?key|x[-_]?api[-_]?key|token|access[-_]?token|secret|password))" +
        @"(?<sep>\s*[:=]\s*)" +
        @"(?<scheme>(?i:Bearer|Basic|Token)\s+)?" +
        @"(?<value>\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHeaderRegex();
}
