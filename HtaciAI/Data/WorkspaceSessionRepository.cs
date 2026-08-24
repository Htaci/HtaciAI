using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 工作区会话仓储（workspace_sessions 表）：读写工作区内的智能体会话。
/// 消息复用 chat_message 表（session_id 即本会话 Id）；本会话保存「有效配置」，构建请求时只读它。
/// </summary>
public static class WorkspaceSessionRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<WorkspaceChatSession>> GetByWorkspaceAsync(string workspaceId)
    {
        var list = new List<WorkspaceChatSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace_id, agent_id, title, model, thinking, session_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, draft, last_message_at, created_at, updated_at
            FROM workspace_sessions
            WHERE workspace_id = $workspace_id AND is_deleted = 0
            ORDER BY COALESCE(last_message_at, created_at) DESC;
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<WorkspaceChatSession?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace_id, agent_id, title, model, thinking, session_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, draft, last_message_at, created_at, updated_at
            FROM workspace_sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task CreateAsync(WorkspaceChatSession s)
    {
        var now = Now();
        if (s.CreatedAt == 0) s.CreatedAt = now;
        if (s.UpdatedAt == 0) s.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspace_sessions
                (id, workspace_id, agent_id, title, model, thinking, session_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, draft, last_message_at, created_at, updated_at)
            VALUES
                ($id, $workspace_id, $agent_id, $title, $model, $thinking, $session_prompt, $enabled_tool_ids, $enabled_skills, $mcp_servers, $permission_mode, $draft, $last_message_at, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$workspace_id", s.WorkspaceId);
        cmd.Parameters.AddWithValue("$agent_id", (object?)s.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$session_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$draft", (object?)s.Draft ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(WorkspaceChatSession s)
    {
        s.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workspace_sessions
            SET title = $title, model = $model, thinking = $thinking, session_prompt = $session_prompt,
                enabled_tool_ids = $enabled_tool_ids, enabled_skills = $enabled_skills,
                mcp_servers = $mcp_servers, permission_mode = $permission_mode, draft = $draft,
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
        cmd.Parameters.AddWithValue("$draft", (object?)s.Draft ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>更新会话草稿（单客户端无并发，防抖写入可忽略）。</summary>
    public static async Task SaveDraftAsync(string id, string draft)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workspace_sessions SET draft = $draft, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$draft", draft);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SoftDeleteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workspace_sessions SET is_deleted = 1, deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static WorkspaceChatSession Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        WorkspaceId = r.GetString(1),
        AgentId = r.IsDBNull(2) ? null : r.GetString(2),
        Title = r.GetString(3),
        Model = r.IsDBNull(4) ? "" : r.GetString(4),
        Thinking = r.GetInt32(5),
        SystemPrompt = r.IsDBNull(6) ? "" : r.GetString(6),
        EnabledToolIds = r.IsDBNull(7) ? new() : ParseToolIds(r.GetString(7)),
        EnabledSkills = r.IsDBNull(8) ? new() : ParseSkills(r.GetString(8)),
        McpServers = r.IsDBNull(9) ? new() : ParseMcp(r.GetString(9)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(10),
        Draft = r.IsDBNull(11) ? null : r.GetString(11),
        LastMessageAt = r.IsDBNull(12) ? null : r.GetInt64(12),
        CreatedAt = r.GetInt64(13),
        UpdatedAt = r.GetInt64(14),
    };

    private static string Serialize<T>(List<T> list)
    {
        try { return JsonSerializer.Serialize(list ?? new()); }
        catch { return "[]"; }
    }

    private static List<string> ParseToolIds(string? json)
        => string.IsNullOrWhiteSpace(json) ? new() : TryParse<string>(json);

    private static List<SessionSkill> ParseSkills(string? json)
        => string.IsNullOrWhiteSpace(json) ? new() : TryParse<SessionSkill>(json);

    private static List<string> ParseMcp(string? json)
        => string.IsNullOrWhiteSpace(json) ? new() : TryParse<string>(json);

    private static List<T> TryParse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
        catch { return new(); }
    }
}
