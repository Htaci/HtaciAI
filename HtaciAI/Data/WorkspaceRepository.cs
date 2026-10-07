using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 工作空间仓储（workspaces 表）：读写工作空间「二次配置」。
/// agent_id 为活引用；工具/技能/MCP 以 JSON 数组文本存储（复用 ChatRepository 约定）。
/// </summary>
public static class WorkspaceRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<WorkspaceConfig>> GetAllAsync()
    {
        var list = new List<WorkspaceConfig>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, path, agent_id, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, created_at, updated_at
            FROM workspaces ORDER BY created_at ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<WorkspaceConfig?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, path, agent_id, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, created_at, updated_at
            FROM workspaces WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task CreateAsync(WorkspaceConfig w)
    {
        var now = Now();
        if (w.CreatedAt == 0) w.CreatedAt = now;
        if (w.UpdatedAt == 0) w.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces (id, name, description, path, agent_id, system_prompt, enabled_tool_ids, enabled_skills, mcp_servers, permission_mode, created_at, updated_at)
            VALUES ($id, $name, $description, $path, $agent_id, $system_prompt, $enabled_tool_ids, $enabled_skills, $mcp_servers, $permission_mode, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.Parameters.AddWithValue("$name", w.Name);
        cmd.Parameters.AddWithValue("$description", (object?)w.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)w.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent_id", (object?)w.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)w.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(w.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(w.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(w.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)w.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$created_at", w.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", w.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(WorkspaceConfig w)
    {
        w.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workspaces
            SET name = $name, description = $description, path = $path, agent_id = $agent_id,
                system_prompt = $system_prompt, enabled_tool_ids = $enabled_tool_ids,
                enabled_skills = $enabled_skills, mcp_servers = $mcp_servers,
                permission_mode = $permission_mode, updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.Parameters.AddWithValue("$name", w.Name);
        cmd.Parameters.AddWithValue("$description", (object?)w.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)w.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent_id", (object?)w.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)w.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(w.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(w.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(w.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)w.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$updated_at", w.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 删除工作空间。工作空间本身是硬删除，但它下面的会话先做软删除
    /// —— workspace_sessions.workspace_id 没有建外键，不显式处理的话这些会话会变成
    /// 孤儿行：侧栏按工作空间遍历，它们再也不会显示，消息却一直留在库里。
    /// </summary>
    public static async Task DeleteAsync(string id)
    {
        await WorkspaceSessionRepository.SoftDeleteByWorkspaceAsync(id);

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM workspaces WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static WorkspaceConfig Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? "" : r.GetString(2),
        Path = r.IsDBNull(3) ? "" : r.GetString(3),
        AgentId = r.IsDBNull(4) ? null : r.GetString(4),
        SystemPrompt = r.IsDBNull(5) ? "" : r.GetString(5),
        EnabledToolIds = r.IsDBNull(6) ? new() : ParseToolIds(r.GetString(6)),
        EnabledSkills = r.IsDBNull(7) ? new() : ParseSkills(r.GetString(7)),
        McpServers = r.IsDBNull(8) ? new() : ParseMcp(r.GetString(8)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(9),
        CreatedAt = r.GetInt64(10),
        UpdatedAt = r.GetInt64(11),
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
