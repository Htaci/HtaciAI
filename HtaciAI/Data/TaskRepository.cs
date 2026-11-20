using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 定时任务仓储：<c>task_defs</c>（定义）与 <c>task_runs</c>（每次运行 = 一个会话）。
///
/// <b>为什么两张表共用一个仓储</b>：它们总是一起被读写 —— 列出任务要带最近一次结果，
/// 点开运行要回查任务标题。分成两个仓储的结果是每个界面都得同时拿到两个对象，
/// 再在外面拼一遍。
///
/// 工具/技能/MCP 以 JSON 数组文本存储，沿用 <see cref="AgentRepository"/> 的序列化约定。
/// </summary>
public static class TaskRepository
{
    private const string DefColumns = """
        id, title, prompt, base_directory, model, thinking, enabled_tool_ids, enabled_skills,
        mcp_servers, permission_mode, max_turns, trigger_kind, trigger_days, trigger_time,
        once_at, next_run_at, last_run_at, is_enabled, failure_count, is_deleted, deleted_at,
        created_at, updated_at
        """;

    private const string RunColumns = """
        id, task_id, title, model, thinking, session_prompt, base_directory, enabled_tool_ids,
        enabled_skills, mcp_servers, permission_mode, max_turns, turn_count, status, error,
        summary, last_message_at, finished_at, is_deleted, deleted_at, created_at, updated_at
        """;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ============================================================
    // 定义
    // ============================================================

    /// <summary>全部未删除的任务，按创建时间正序（界面顺序稳定）。</summary>
    public static async Task<List<TaskDefinition>> GetDefinitionsAsync()
    {
        var list = new List<TaskDefinition>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {DefColumns} FROM task_defs WHERE is_deleted = 0 ORDER BY created_at ASC;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapDefinition(r));
        return list;
    }

    public static async Task<TaskDefinition?> GetDefinitionAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {DefColumns} FROM task_defs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapDefinition(r) : null;
    }

    /// <summary>到点该跑的任务（含程序关掉期间错过的）。</summary>
    public static async Task<List<TaskDefinition>> GetDueDefinitionsAsync(long nowMs)
    {
        var list = new List<TaskDefinition>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {DefColumns} FROM task_defs
            WHERE is_deleted = 0 AND is_enabled = 1
              AND next_run_at IS NOT NULL AND next_run_at <= $now
            ORDER BY next_run_at ASC;
            """;
        cmd.Parameters.AddWithValue("$now", nowMs);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapDefinition(r));
        return list;
    }

    /// <summary>所有启用任务里最近的一次触发时刻；一个都没有返回 null（调度器据此决定睡多久）。</summary>
    public static async Task<long?> GetNextRunAtAsync()
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(next_run_at) FROM task_defs
            WHERE is_deleted = 0 AND is_enabled = 1 AND next_run_at IS NOT NULL;
            """;
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>写入定义。id 已存在就整行覆盖（除 next_run_at 等调度字段由调度器维护，见下方方法）。</summary>
    public static async Task SaveDefinitionAsync(TaskDefinition d)
    {
        var now = Now();
        if (d.CreatedAt == 0) d.CreatedAt = now;
        d.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_defs
                (id, title, prompt, base_directory, model, thinking, enabled_tool_ids, enabled_skills,
                 mcp_servers, permission_mode, max_turns, trigger_kind, trigger_days, trigger_time,
                 once_at, next_run_at, last_run_at, is_enabled, failure_count, is_deleted, deleted_at,
                 created_at, updated_at)
            VALUES
                ($id, $title, $prompt, $base_directory, $model, $thinking, $enabled_tool_ids, $enabled_skills,
                 $mcp_servers, $permission_mode, $max_turns, $trigger_kind, $trigger_days, $trigger_time,
                 $once_at, $next_run_at, $last_run_at, $is_enabled, $failure_count, $is_deleted, $deleted_at,
                 $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                title = $title, prompt = $prompt, base_directory = $base_directory, model = $model,
                thinking = $thinking, enabled_tool_ids = $enabled_tool_ids,
                enabled_skills = $enabled_skills, mcp_servers = $mcp_servers,
                permission_mode = $permission_mode, max_turns = $max_turns,
                trigger_kind = $trigger_kind, trigger_days = $trigger_days, trigger_time = $trigger_time,
                once_at = $once_at, next_run_at = $next_run_at, last_run_at = $last_run_at,
                is_enabled = $is_enabled, failure_count = $failure_count,
                is_deleted = $is_deleted, deleted_at = $deleted_at, updated_at = $updated_at;
            """;
        AddDefinitionParameters(cmd, d);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 只推进调度状态：下一次触发时刻、上一次触发时刻、以及「一次性任务跑完就停用」。
    /// 调度器每轮都调它，因此不该顺带把整份配置重写一遍。
    /// </summary>
    public static async Task AdvanceAsync(string id, long? nextRunAt, long lastRunAt)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // next_run_at 为 NULL 就是「再也没有下一次」：一次性任务跑完走这条，顺手停用来保持语义一致
        cmd.CommandText = """
            UPDATE task_defs
            SET next_run_at = $next, last_run_at = $last, updated_at = $now,
                is_enabled = CASE WHEN $next IS NULL THEN 0 ELSE is_enabled END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$next", (object?)nextRunAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", lastRunAt);
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>重算下一次触发时刻（改了触发规则 / 重新启用后调用）。</summary>
    public static async Task SetNextRunAsync(string id, long? nextRunAt)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_defs SET next_run_at = $next, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$next", (object?)nextRunAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 记一次失败：连续失败计数 +1，够 <paramref name="disableAfter"/> 次就自动停用。
    /// <b>自动停用不是惩罚</b>：没人守着的任务一直失败，唯一后果是每天多一条出错提醒，
    /// 停在停用状态并在界面上写明原因，比继续安静地浪费 API 额度更有用。
    /// </summary>
    public static async Task<bool> RecordFailureAsync(string id, int disableAfter)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_defs
            SET failure_count = failure_count + 1,
                is_enabled = CASE WHEN failure_count + 1 >= $limit THEN 0 ELSE is_enabled END,
                updated_at = $now
            WHERE id = $id;
            RETURNING is_enabled;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$limit", disableAfter);
        cmd.Parameters.AddWithValue("$now", Now());
        var enabled = await cmd.ExecuteScalarAsync();
        return enabled is not null and not DBNull && Convert.ToInt32(enabled) == 0;
    }

    /// <summary>跑成功一次就把连续失败清零 —— 偶发失败不该攒到把任务停掉。</summary>
    public static async Task ResetFailuresAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_defs SET failure_count = 0 WHERE id = $id AND failure_count > 0;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 软删除定义。运行历史跟着隐藏（库里也有触发器兜底）；
    /// 消息不删 —— 与子会话同一处理：历史仍可回看，只是不再出现在列表里。
    /// </summary>
    public static async Task SoftDeleteDefinitionAsync(string id)
    {
        var now = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_defs SET is_deleted = 1, deleted_at = $now, is_enabled = 0, updated_at = $now
            WHERE id = $id;
            UPDATE task_runs SET is_deleted = 1, deleted_at = $now
            WHERE task_id = $id AND is_deleted = 0;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    // ============================================================
    // 运行记录
    // ============================================================

    public static async Task<TaskRun?> GetRunAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {RunColumns} FROM task_runs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapRun(r) : null;
    }

    /// <summary>某个任务的运行历史，新的在前。</summary>
    public static async Task<List<TaskRun>> GetRunsAsync(string taskId, int limit = 50)
    {
        var list = new List<TaskRun>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {RunColumns} FROM task_runs
            WHERE task_id = $task AND is_deleted = 0
            ORDER BY created_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapRun(r));
        return list;
    }

    public static async Task CreateRunAsync(TaskRun run)
    {
        var now = Now();
        if (run.CreatedAt == 0) run.CreatedAt = now;
        run.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_runs
                (id, task_id, title, model, thinking, session_prompt, base_directory, enabled_tool_ids,
                 enabled_skills, mcp_servers, permission_mode, max_turns, turn_count, status, error,
                 summary, last_message_at, finished_at, is_deleted, deleted_at, created_at, updated_at)
            VALUES
                ($id, $task_id, $title, $model, $thinking, $session_prompt, $base_directory, $enabled_tool_ids,
                 $enabled_skills, $mcp_servers, $permission_mode, $max_turns, $turn_count, $status, $error,
                 $summary, $last_message_at, $finished_at, 0, NULL, $created_at, $updated_at);
            """;
        AddRunParameters(cmd, run);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>只更新会话配置（<see cref="Models.TaskRun"/> 被档案写回时用）。</summary>
    public static async Task UpdateRunAsync(TaskRun run)
    {
        run.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_runs
            SET title = $title, model = $model, thinking = $thinking, session_prompt = $session_prompt,
                enabled_tool_ids = $enabled_tool_ids, enabled_skills = $enabled_skills,
                mcp_servers = $mcp_servers, permission_mode = $permission_mode, max_turns = $max_turns,
                turn_count = $turn_count, status = $status, last_message_at = $last_message_at,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        AddRunParameters(cmd, run);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>收尾：写状态、失败原因、一行摘要与结束时刻。</summary>
    public static async Task FinishRunAsync(string id, string status, string? error, string? summary)
    {
        var now = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_runs
            SET status = $status, error = $error, summary = $summary,
                finished_at = $now, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 启动时把「上次没跑完」的运行标成失败。
    /// 进程被杀 / 崩溃时那一行会一直停在 running，界面上看着像永远在跑。
    /// </summary>
    public static async Task CloseStaleRunsAsync()
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_runs
            SET status = 'failed', error = '程序在运行中被关闭，这次没有跑完。',
                finished_at = $now, updated_at = $now
            WHERE status = 'running';
            """;
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    // ============================================================
    // 映射
    // ============================================================

    private static void AddDefinitionParameters(SqliteCommand cmd, TaskDefinition d)
    {
        cmd.Parameters.AddWithValue("$id", d.Id);
        cmd.Parameters.AddWithValue("$title", d.Title);
        cmd.Parameters.AddWithValue("$prompt", d.Prompt);
        cmd.Parameters.AddWithValue("$base_directory", (object?)d.BaseDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)d.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", d.Thinking);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(d.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(d.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(d.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)d.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$max_turns", d.MaxTurns);
        cmd.Parameters.AddWithValue("$trigger_kind", d.TriggerKind);
        cmd.Parameters.AddWithValue("$trigger_days", SerializeDays(d.TriggerDays));
        cmd.Parameters.AddWithValue("$trigger_time", d.TriggerTime);
        cmd.Parameters.AddWithValue("$once_at", (object?)d.OnceAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next_run_at", (object?)d.NextRunAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_run_at", (object?)d.LastRunAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_enabled", d.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$failure_count", d.FailureCount);
        cmd.Parameters.AddWithValue("$is_deleted", d.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted_at", (object?)d.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", d.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", d.UpdatedAt);
    }

    private static void AddRunParameters(SqliteCommand cmd, TaskRun r)
    {
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$task_id", r.TaskId);
        cmd.Parameters.AddWithValue("$title", r.Title);
        cmd.Parameters.AddWithValue("$model", (object?)r.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", r.Thinking);
        cmd.Parameters.AddWithValue("$session_prompt", (object?)r.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$base_directory", (object?)r.BaseDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)Serialize(r.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)Serialize(r.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)Serialize(r.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)r.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$max_turns", r.MaxTurns);
        cmd.Parameters.AddWithValue("$turn_count", r.TurnCount);
        cmd.Parameters.AddWithValue("$status", r.Status);
        cmd.Parameters.AddWithValue("$error", (object?)r.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", (object?)r.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)r.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished_at", (object?)r.FinishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", r.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", r.UpdatedAt);
    }

    private static TaskDefinition MapDefinition(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Prompt = r.GetString(2),
        BaseDirectory = r.IsDBNull(3) ? null : r.GetString(3),
        Model = r.IsDBNull(4) ? "" : r.GetString(4),
        Thinking = r.GetInt32(5),
        EnabledToolIds = r.IsDBNull(6) ? new() : Parse<string>(r.GetString(6)),
        EnabledSkills = r.IsDBNull(7) ? new() : Parse<SessionSkill>(r.GetString(7)),
        McpServers = r.IsDBNull(8) ? new() : Parse<string>(r.GetString(8)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(9),
        MaxTurns = r.GetInt32(10),
        TriggerKind = r.GetString(11),
        TriggerDays = ParseDays(r.IsDBNull(12) ? null : r.GetString(12)),
        TriggerTime = r.IsDBNull(13) ? "09:00:00" : r.GetString(13),
        OnceAt = r.IsDBNull(14) ? null : r.GetInt64(14),
        NextRunAt = r.IsDBNull(15) ? null : r.GetInt64(15),
        LastRunAt = r.IsDBNull(16) ? null : r.GetInt64(16),
        IsEnabled = r.GetInt32(17) != 0,
        FailureCount = r.GetInt32(18),
        IsDeleted = r.GetInt32(19) != 0,
        DeletedAt = r.IsDBNull(20) ? null : r.GetInt64(20),
        CreatedAt = r.GetInt64(21),
        UpdatedAt = r.GetInt64(22),
    };

    private static TaskRun MapRun(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        TaskId = r.GetString(1),
        Title = r.GetString(2),
        Model = r.IsDBNull(3) ? "" : r.GetString(3),
        Thinking = r.GetInt32(4),
        SystemPrompt = r.IsDBNull(5) ? "" : r.GetString(5),
        BaseDirectory = r.IsDBNull(6) ? null : r.GetString(6),
        EnabledToolIds = r.IsDBNull(7) ? new() : Parse<string>(r.GetString(7)),
        EnabledSkills = r.IsDBNull(8) ? new() : Parse<SessionSkill>(r.GetString(8)),
        McpServers = r.IsDBNull(9) ? new() : Parse<string>(r.GetString(9)),
        ToolPermissionMode = (PermissionMode)r.GetInt32(10),
        MaxTurns = r.GetInt32(11),
        TurnCount = r.GetInt32(12),
        Status = r.GetString(13),
        Error = r.IsDBNull(14) ? null : r.GetString(14),
        Summary = r.IsDBNull(15) ? null : r.GetString(15),
        LastMessageAt = r.IsDBNull(16) ? null : r.GetInt64(16),
        FinishedAt = r.IsDBNull(17) ? null : r.GetInt64(17),
        IsDeleted = r.GetInt32(18) != 0,
        DeletedAt = r.IsDBNull(19) ? null : r.GetInt64(19),
        CreatedAt = r.GetInt64(20),
        UpdatedAt = r.GetInt64(21),
    };

    /// <summary>天列表存成逗号文本（1,3,5）。空列表存 NULL，与「没设过」区分开。</summary>
    private static object? SerializeDays(List<int> days)
        => days.Count == 0 ? null : string.Join(",", days);

    private static List<int> ParseDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        var list = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var value)) list.Add(value);
        return list;
    }

    private static string? Serialize<T>(List<T> list)
    {
        if (list is null || list.Count == 0) return null;
        try { return JsonSerializer.Serialize(list); }
        catch { return null; }
    }

    private static List<T> Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
        catch { return new(); }
    }
}
