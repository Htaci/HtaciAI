using System;
using System.Collections.Generic;
using System.Text.Json;
using HtaciAI.Models;

namespace HtaciAI.Services.Tools;

/// <summary>本轮对某个文件的改动类型。</summary>
public enum FileChangeKind
{
    /// <summary>新建的文件。</summary>
    Added,

    /// <summary>已有文件被改写。</summary>
    Modified,

    /// <summary>文件被删除。</summary>
    Deleted,
}

/// <summary>本轮回复里对某一个文件的改动。</summary>
public sealed record FileChange(string Path, FileChangeKind Kind);

/// <summary>
/// 从工具结果里反推「这一轮改了哪些文件」，供会话末尾的文件卡片渲染。
///
/// <b>只认工具结果的文字，不另记状态</b>：历史重绘（关掉会话再打开）时手上只有消息，
/// 另立一份内存状态的话，实时轮看得见、重开就没了。
/// write / edit 的成功回执里本来就带着完整路径（见 <see cref="BuiltinToolExecutor"/>），
/// 按回执前缀就能判定类型，不需要改库表结构。
/// </summary>
public static class FileChanges
{
    /// <param name="messages">一轮回复的全部消息。</param>
    /// <param name="findArguments">按 tool_call_id 反查入参 JSON 的回调（删除类工具只有入参里有路径）。</param>
    public static List<FileChange> Collect(IEnumerable<ChatMessage> messages, Func<string?, string> findArguments)
    {
        var result = new List<FileChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in messages)
        {
            if (m.Role != "tool" || m.Status == "failed") continue;
            if (Parse(m, findArguments(m.ToolCallId)) is not { } change) continue;

            // 同一个文件改了多次只留一条，类型取第一次的（新建之后又改，仍然算「新增」）
            if (seen.Add(change.Path)) result.Add(change);
        }
        return result;
    }

    private static FileChange? Parse(ChatMessage m, string argumentsJson)
    {
        var name = m.ToolName ?? "";
        var content = m.Content ?? "";

        if (name == "write")
        {
            if (TakePath(content, "已创建文件：") is { } created) return new FileChange(created, FileChangeKind.Added);
            if (TakePath(content, "已覆盖文件：") is { } overwritten) return new FileChange(overwritten, FileChangeKind.Modified);
            return null;
        }

        if (name == "edit")
        {
            // 回执形如「已替换 2 处。文件：D:\...」；失败回执（未找到文本 / 文件不存在）不含这段
            if (!content.StartsWith("已替换", StringComparison.Ordinal)) return null;
            if (TakePath(content, "文件：") is { } edited) return new FileChange(edited, FileChangeKind.Modified);
            return null;
        }

        // 删除：内置工具里没有删除，能走到这里的是 MCP / 自建工具，按名字认
        if (LooksLikeDelete(name) && TryGetPathArg(argumentsJson) is { } removed)
            return new FileChange(removed, FileChangeKind.Deleted);

        return null;
    }

    /// <summary>取回执里最后一个前缀之后的路径（回执末尾就是完整路径）。</summary>
    private static string? TakePath(string content, string prefix)
    {
        var idx = content.LastIndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        var path = content[(idx + prefix.Length)..].Trim();
        return path.Length == 0 ? null : path;
    }

    private static bool LooksLikeDelete(string toolName)
    {
        var n = toolName.ToLowerInvariant();
        return n.Contains("delete") || n.Contains("remove") || n.Contains("unlink") || n.Contains("rmdir");
    }

    /// <summary>入参里的路径：filePath 优先，其次 path / target。</summary>
    private static string? TryGetPathArg(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "filePath", "path", "target" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var path = v.GetString();
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
        }
        catch
        {
            // 入参不是合法 JSON（流式截断等）：当作没有路径
        }
        return null;
    }
}
