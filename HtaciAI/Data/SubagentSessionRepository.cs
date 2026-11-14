using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 子 Agent 会话仓储（subagent_sessions 表）。
/// 消息复用 <see cref="chat_message"/> 表，因此这里只管「一行会话配置 + 运行状态」，
/// 不碰消息读写。
/// </summary>
public static class SubagentSessionRepository
{
    private const string Columns = """
        id, agent_id, agent_name, parent_session_id, title, model, thinking, session_prompt,
        base_directory, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode,
        max_turns, turn_count, status, last_message_at, created_at, updated_at
        """;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<SubagentSession?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM subagent_sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    /// <summary>某个父会话派发过的全部子会话（含已结束的，供主 Agent 挑一个续用）。</summary>
    public static async Task<List<SubagentSession>> GetByParentAsync(string parentSessionId)
    {
        var list = new List<SubagentSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM subagent_sessions
            WHERE parent_session_id = $parent AND is_deleted = 0
            ORDER BY COALESCE(last_message_at, created_at) DESC;
            """;
        cmd.Parameters.AddWithValue("$parent", parentSessionId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task CreateAsync(SubagentSession s)
    {
        var now = Now();
        if (s.CreatedAt == 0) s.CreatedAt = now;
        if (s.UpdatedAt == 0) s.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO subagent_sessions
                (id, agent_id, agent_name, parent_session_id, title, model, thinking, session_prompt,
                 base_directory, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode,
                 max_turns, turn_count, status, last_message_at, created_at, updated_at)
            VALUES
                ($id, $agent_id, $agent_name, $parent, $title, $model, $thinking, $session_prompt,
                 $base_directory, $enabled_tool_ids, $enabled_skills, $mcp_servers, $permission_mode,
                 $max_turns, $turn_count, $status, $last_message_at, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$agent_id", s.AgentId);
        cmd.Parameters.AddWithValue("$agent_name", s.AgentName);
        cmd.Parameters.AddWithValue("$parent", s.ParentSessionId);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$session_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$base_directory", (object?)s.BaseDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$max_turns", s.MaxTurns);
        cmd.Parameters.AddWithValue("$turn_count", s.TurnCount);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(SubagentSession s)
    {
        s.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE subagent_sessions
            SET title = $title, model = $model, thinking = $thinking, session_prompt = $session_prompt,
                enabled_tool_ids = $enabled_tool_ids, enabled_skills = $enabled_skills,
                mcp_servers = $mcp_servers, permission_mode = $permission_mode,
                max_turns = $max_turns, turn_count = $turn_count, status = $status,
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
        cmd.Parameters.AddWithValue("$max_turns", s.MaxTurns);
        cmd.Parameters.AddWithValue("$turn_count", s.TurnCount);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>只推进轮次计数与状态：子会话每轮都要写，不该顺带把整份配置重写一遍。</summary>
    public static async Task UpdateProgressAsync(string id, int turnCount, string status, long? lastMessageAt)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE subagent_sessions
            SET turn_count = $turn_count, status = $status,
                last_message_at = COALESCE($last_message_at, last_message_at), updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$turn_count", turnCount);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)lastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SoftDeleteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE subagent_sessions SET is_deleted = 1, deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 父会话被软删除时级联（库里也有同名触发器兜底，这里给需要显式调用的代码路径用）。
    /// 消息不删：<c>chat_message.is_deleted</c> 由父会话的删除路径统一处理，子会话只是隐藏。
    /// </summary>
    public static async Task SoftDeleteByParentAsync(string parentSessionId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE subagent_sessions
            SET is_deleted = 1, deleted_at = $now
            WHERE parent_session_id = $parent AND is_deleted = 0;
            """;
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$parent", parentSessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static SubagentSession Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        AgentId = r.GetString(1),
        AgentName = r.GetString(2),
        ParentSessionId = r.GetString(3),
        Title = r.GetString(4),
        Model = r.IsDBNull(5) ? "" : r.GetString(5),
        Thinking = r.GetInt32(6),
        SystemPrompt = r.IsDBNull(7) ? "" : r.GetString(7),
        BaseDirectory = r.IsDBNull(8) ? null : r.GetString(8),
        EnabledToolIds = r.IsDBNull(9) ? new() : Parse<string>(r.GetString(9)),
        EnabledSkills = r.IsDBNull(10) ? new() : Parse<SessionSkill>(r.GetString(10)),
        McpServers = r.IsDBNull(11) ? new() : Parse<string>(r.GetString(11)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(12),
        MaxTurns = r.GetInt32(13),
        TurnCount = r.GetInt32(14),
        Status = r.GetString(15),
        LastMessageAt = r.IsDBNull(16) ? null : r.GetInt64(16),
        CreatedAt = r.GetInt64(17),
        UpdatedAt = r.GetInt64(18),
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
