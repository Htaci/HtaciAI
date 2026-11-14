using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 插件会话仓储（plugin_sessions 表）。消息复用 <see cref="ChatRepository"/> 那套
/// （chat_message 的 session_id 就是本表 id），所以这里只管「一行会话配置 + 归属」。
/// </summary>
public static class PluginSessionRepository
{
    private const string Columns = """
        id, plugin_id, title, model, thinking, session_prompt, enabled_tool_ids, enabled_skills,
        mcp_servers, permission_mode, last_message_at, is_deleted, deleted_at, created_at, updated_at
        """;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<PluginSession?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM plugin_sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    /// <summary>某个插件的全部会话（不含已删除），按最近活动倒序。</summary>
    public static async Task<List<PluginSession>> GetByPluginAsync(string pluginId)
    {
        var list = new List<PluginSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM plugin_sessions
            WHERE plugin_id = $plugin AND is_deleted = 0
            ORDER BY COALESCE(last_message_at, created_at) DESC;
            """;
        cmd.Parameters.AddWithValue("$plugin", pluginId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task CreateAsync(PluginSession s)
    {
        var now = Now();
        if (s.CreatedAt == 0) s.CreatedAt = now;
        if (s.UpdatedAt == 0) s.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO plugin_sessions
                (id, plugin_id, title, model, thinking, session_prompt, enabled_tool_ids, enabled_skills,
                 mcp_servers, permission_mode, last_message_at, is_deleted, deleted_at, created_at, updated_at)
            VALUES
                ($id, $plugin_id, $title, $model, $thinking, $session_prompt, $enabled_tool_ids, $enabled_skills,
                 $mcp_servers, $permission_mode, $last_message_at, 0, NULL, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$plugin_id", s.PluginId);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$session_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 回写会话配置。<b>不写 plugin_id</b>：归属在建行那一刻定下，之后没有任何路径能改它。
    /// </summary>
    public static async Task UpdateAsync(PluginSession s)
    {
        s.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE plugin_sessions
            SET title = $title, model = $model, thinking = $thinking, session_prompt = $session_prompt,
                enabled_tool_ids = $enabled_tool_ids, enabled_skills = $enabled_skills,
                mcp_servers = $mcp_servers, permission_mode = $permission_mode,
                last_message_at = $last_message_at, updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$session_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 软删除（消息保留，与 chat_sessions 的做法一致）。
    /// 传 <paramref name="pluginId"/> 时顺带校验归属：不是这个插件的会话就一行都不改。
    /// </summary>
    public static async Task<bool> SoftDeleteAsync(string id, string? pluginId = null)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pluginId is null
            ? "UPDATE plugin_sessions SET is_deleted = 1, deleted_at = $now WHERE id = $id AND is_deleted = 0;"
            : """
              UPDATE plugin_sessions SET is_deleted = 1, deleted_at = $now
              WHERE id = $id AND plugin_id = $plugin AND is_deleted = 0;
              """;
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        if (pluginId is not null) cmd.Parameters.AddWithValue("$plugin", pluginId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static PluginSession Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        PluginId = r.GetString(1),
        Title = r.GetString(2),
        Model = r.IsDBNull(3) ? "" : r.GetString(3),
        Thinking = r.GetInt32(4),
        SystemPrompt = r.IsDBNull(5) ? null : r.GetString(5),
        EnabledToolIds = r.IsDBNull(6) ? new() : Parse<string>(r.GetString(6)),
        EnabledSkills = r.IsDBNull(7) ? new() : Parse<SessionSkill>(r.GetString(7)),
        McpServers = r.IsDBNull(8) ? new() : Parse<string>(r.GetString(8)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(9),
        LastMessageAt = r.IsDBNull(10) ? null : r.GetInt64(10),
        IsDeleted = r.GetInt32(11) != 0,
        DeletedAt = r.IsDBNull(12) ? null : r.GetInt64(12),
        CreatedAt = r.GetInt64(13),
        UpdatedAt = r.GetInt64(14),
    };

    private static string Serialize<T>(List<T> list)
    {
        try { return JsonSerializer.Serialize(list ?? new()); }
        catch { return "[]"; }
    }

    private static List<T> Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
        catch { return new(); }
    }
}
