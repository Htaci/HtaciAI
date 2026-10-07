using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 智能体仓储（agents 表）：读写自定义 Agent 蓝图。工具/技能/MCP 以 JSON 数组文本存储，
/// 复用 ChatRepository 的序列化约定（enabled_tool_ids 为 string 数组、enabled_skills 为含 id/status 的对象数组）。
/// </summary>
public static class AgentRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<Agent>> GetAllAsync()
    {
        var list = new List<Agent>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, is_enabled, created_at, updated_at
            FROM agents ORDER BY created_at ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<Agent?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, is_enabled, created_at, updated_at
            FROM agents WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task CreateAsync(Agent a)
    {
        var now = Now();
        if (a.CreatedAt == 0) a.CreatedAt = now;
        if (a.UpdatedAt == 0) a.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agents (id, name, description, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, is_enabled, created_at, updated_at)
            VALUES ($id, $name, $description, $system_prompt, $enabled_tool_ids, $enabled_skills, $mcp_servers, $is_enabled, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$name", a.Name);
        cmd.Parameters.AddWithValue("$description", (object?)a.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)a.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeToolIds(a.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(a.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeMcp(a.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_enabled", a.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at", a.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", a.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(Agent a)
    {
        a.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agents
            SET name = $name, description = $description, system_prompt = $system_prompt,
                enabled_tool_ids = $enabled_tool_ids, enabled_skills = $enabled_skills,
                mcp_servers = $mcp_servers, is_enabled = $is_enabled, updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$name", a.Name);
        cmd.Parameters.AddWithValue("$description", (object?)a.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)a.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeToolIds(a.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(a.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeMcp(a.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_enabled", a.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated_at", a.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agents WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 映射 ----

    private static Agent Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? "" : r.GetString(2),
        SystemPrompt = r.IsDBNull(3) ? "" : r.GetString(3),
        EnabledToolIds = r.IsDBNull(4) ? new() : ParseToolIds(r.GetString(4)),
        EnabledSkills = r.IsDBNull(5) ? new() : ParseSkills(r.GetString(5)),
        McpServers = r.IsDBNull(6) ? new() : ParseMcp(r.GetString(6)),
        IsEnabled = r.GetInt32(7) != 0,
        CreatedAt = r.GetInt64(8),
        UpdatedAt = r.GetInt64(9),
    };

    private static string SerializeToolIds(List<string> ids)
    {
        try { return JsonSerializer.Serialize(ids ?? new()); }
        catch { return "[]"; }
    }

    private static List<string> ParseToolIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string SerializeSkills(List<SessionSkill> skills)
    {
        try { return JsonSerializer.Serialize(skills ?? new()); }
        catch { return "[]"; }
    }

    private static List<SessionSkill> ParseSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<SessionSkill>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string SerializeMcp(List<string> mcp)
    {
        try { return JsonSerializer.Serialize(mcp ?? new()); }
        catch { return "[]"; }
    }

    private static List<string> ParseMcp(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
