using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 智能对话仓储：会话与消息的读写。
/// </summary>
public static class ChatRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 会话 ----

    /// <summary>
    /// 全部未删除会话，按<b>真实对话时间</b>倒序（最近发过消息的在前）。
    ///
    /// ⚠️ 排序刻意用 <c>COALESCE(last_message_at, created_at)</c> 而<b>不是</b> <c>updated_at</c>：
    /// <c>updated_at</c> 会被任何一次写库抬高——改模型/技能/工具/MCP/权限、改名，甚至只是
    /// <b>打开会话</b>（打开会恢复草稿，草稿列一变就会写一次库）。用它排序的结果是「点开看一眼
    /// 就会跳到列表最前面」，而用户要的是「只有发了新请求才算最近用过」。
    /// <c>last_message_at</c> 全仓只在真正发请求时写，正是这个语义；没有消息的会话退回创建时间，
    /// 于是刚建出来的空会话仍然排在最前（这是对的，它确实是新的）。
    /// </summary>
    public static async Task<List<ChatSession>> GetAllAsync()
    {
        var list = new List<ChatSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, model, system_prompt, thinking, is_deleted, deleted_at, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, draft, mcp_servers
            FROM chat_sessions WHERE is_deleted = 0
            ORDER BY COALESCE(last_message_at, created_at) DESC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapSession(r));
        return list;
    }

    public static async Task<ChatSession?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, model, system_prompt, thinking, is_deleted, deleted_at, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, draft, mcp_servers
            FROM chat_sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapSession(r) : null;
    }

    /// <summary>
    /// 最近一次用过的模型名与使用时间，供「新会话默认模型 = 上次使用的模型」使用。
    /// 排序口径与 <see cref="GetAllAsync"/> 一致（只看真实对话时间），否则
    /// 「上次使用的模型」会被「随手点开某个会话」这种动作污染。
    /// 没有可用会话时返回 null。
    /// </summary>
    public static async Task<(string Model, long At)?> GetLastUsedModelAsync()
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT model, COALESCE(last_message_at, created_at) AS at FROM chat_sessions
            WHERE is_deleted = 0 AND model IS NOT NULL AND model <> ''
            ORDER BY at DESC
            LIMIT 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    public static async Task CreateAsync(ChatSession s)
    {
        var now = Now();
        if (s.CreatedAt == 0) s.CreatedAt = now;
        if (s.UpdatedAt == 0) s.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_sessions (id, title, model, system_prompt, thinking, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, mcp_servers)
            VALUES ($id, $title, $model, $system_prompt, $thinking, $created_at, $updated_at, $enabled_skills, $enabled_tool_ids, $last_message_at, $permission_mode, $mcp_servers);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeStringList(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeStringList(s.McpServers) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(ChatSession s)
    {
        s.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_sessions
            SET title = $title, model = $model, system_prompt = $system_prompt,
                thinking = $thinking, enabled_skills = $enabled_skills,
                enabled_tool_ids = $enabled_tool_ids, last_message_at = $last_message_at,
                permission_mode = $permission_mode, mcp_servers = $mcp_servers,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeStringList(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeStringList(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 只更新草稿列，不碰其余字段（<b>尤其不碰 updated_at</b>）。
    /// 与 <see cref="UpdateAsync"/> 分开是刻意的：整表更新会把会话对象上的旧 draft 写回去，
    /// 冲掉「刚敲进去、还没来得及同步到会话对象」的内容。
    ///
    /// 为什么不动 updated_at：草稿是「打字过程」而不是「会话活动」。它会随着每次输入落库，
    /// 而且在<b>打开会话恢复草稿</b>时也会被触发一次——早期版本顺带写了 updated_at，
    /// 于是「点开看一眼」就改写了会话记录。排序列已经不依赖 updated_at 了（见 <see cref="GetAllAsync"/>），
    /// 这里再断开一次，免得将来又有人拿 updated_at 当「最近使用」用。
    /// </summary>
    public static async Task SaveDraftAsync(string id, string draft)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET draft = $draft WHERE id = $id;";
        cmd.Parameters.AddWithValue("$draft", draft);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>软删除会话：标记 is_deleted=1 并记录删除时间（消息保留，便于删除会话管理页恢复）。</summary>
    public static async Task SoftDeleteSessionAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET is_deleted = 1, deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 消息 ----

    /// <summary>软删除一轮（AI 回复）：标记该 turn_id 下所有消息为已删除。</summary>
    public static async Task SoftDeleteTurnAsync(string sessionId, string turnId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_message SET is_deleted = 1, deleted_at = $now WHERE session_id = $session_id AND turn_id = $turn_id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$turn_id", turnId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>软删除单条消息（用户消息）。</summary>
    public static async Task SoftDeleteMessageAsync(string sessionId, string messageId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_message SET is_deleted = 1, deleted_at = $now WHERE session_id = $session_id AND id = $message_id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$message_id", messageId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<ChatMessage>> GetBySessionAsync(string sessionId)
    {
        var list = new List<ChatMessage>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, turn_id, sequence_number, role, content, thinking,
                   tool_call_id, tool_name, status, usage_json, model_name, metadata,
                   is_deleted, deleted_at, created_at, updated_at, duration_ms
            FROM chat_message
            WHERE session_id = $session_id AND is_deleted = 0
            ORDER BY sequence_number ASC;
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapMessage(r));
        return list;
    }

    public static async Task<int> GetNextSequenceAsync(string sessionId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM chat_message WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// 下一个 turn_id（会话内递增整数 1、2、3…）。用户与 AI 各占一个新 turn，
    /// 因此每次 ChatAsync 会被调用两次（先用户后 AI）。历史数据为 GUID 时 CAST 为 0，忽略不计。
    /// </summary>
    public static async Task<string> GetNextTurnIdAsync(string sessionId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(CAST(turn_id AS INTEGER)), 0) + 1
            FROM chat_message WHERE session_id = $session_id;
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result).ToString();
    }

    public static async Task InsertAsync(ChatMessage m)
    {
        var now = Now();
        if (m.CreatedAt == 0) m.CreatedAt = now;
        if (m.UpdatedAt == 0) m.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_message (id, session_id, turn_id, sequence_number, role, content,
                thinking, tool_call_id, tool_name, status, usage_json, model_name, metadata,
                is_deleted, deleted_at, created_at, updated_at, duration_ms)
            VALUES ($id, $session_id, $turn_id, $sequence_number, $role, $content,
                $thinking, $tool_call_id, $tool_name, $status, $usage_json, $model_name, $metadata,
                $is_deleted, $deleted_at, $created_at, $updated_at, $duration_ms);
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$session_id", m.SessionId);
        cmd.Parameters.AddWithValue("$turn_id", m.TurnId);
        cmd.Parameters.AddWithValue("$sequence_number", m.SequenceNumber);
        cmd.Parameters.AddWithValue("$role", m.Role);
        cmd.Parameters.AddWithValue("$content", (object?)m.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", (object?)m.Thinking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_call_id", (object?)m.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_name", (object?)m.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", m.Status);
        cmd.Parameters.AddWithValue("$usage_json", (object?)m.UsageJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model_name", (object?)m.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)m.Metadata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_deleted", m.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted_at", (object?)m.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", m.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", m.UpdatedAt);
        cmd.Parameters.AddWithValue("$duration_ms", (object?)m.DurationMs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 流式结束后回写：正文、推理内容、状态、用量。
    /// </summary>
    public static async Task UpdateMessageAsync(ChatMessage m)
    {
        m.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_message
            SET content = $content, thinking = $thinking, tool_call_id = $tool_call_id,
                tool_name = $tool_name, status = $status, usage_json = $usage_json,
                model_name = $model_name, metadata = $metadata, duration_ms = $duration_ms,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$content", (object?)m.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", (object?)m.Thinking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_call_id", (object?)m.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_name", (object?)m.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", m.Status);
        cmd.Parameters.AddWithValue("$usage_json", (object?)m.UsageJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model_name", (object?)m.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)m.Metadata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration_ms", (object?)m.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", m.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 映射 ----

    private static ChatSession MapSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Model = r.IsDBNull(2) ? "" : r.GetString(2),
        SystemPrompt = r.IsDBNull(3) ? null : r.GetString(3),
        Thinking = r.GetInt32(4),
        IsDeleted = r.GetInt32(5) != 0,
        DeletedAt = r.IsDBNull(6) ? null : r.GetInt64(6),
        CreatedAt = r.GetInt64(7),
        UpdatedAt = r.GetInt64(8),
        EnabledSkills = r.IsDBNull(9) ? new() : ParseSkills(r.GetString(9)),
        EnabledToolIds = r.IsDBNull(10) ? new() : ParseStringList(r.GetString(10)),
        LastMessageAt = r.IsDBNull(11) ? null : r.GetInt64(11),
        ToolPermissionMode = (PermissionMode)r.GetInt32(12),
        Draft = r.IsDBNull(13) ? null : r.GetString(13),
        McpServers = r.IsDBNull(14) ? new() : ParseStringList(r.GetString(14)),
    };

    /// <summary>把会话启用技能序列化为 JSON 数组文本（供 enabled_skills 列存储）。</summary>
    private static string SerializeSkills(List<SessionSkill> skills)
    {
        try { return JsonSerializer.Serialize(skills ?? new()); }
        catch { return "[]"; }
    }

    /// <summary>从 JSON 数组文本解析会话启用技能；空/损坏时返回空列表。</summary>
    private static List<SessionSkill> ParseSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<SessionSkill>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>把字符串列表序列化为 JSON 数组文本（enabled_tool_ids / mcp_servers 共用）。</summary>
    private static string SerializeStringList(List<string> values)
    {
        try { return JsonSerializer.Serialize(values ?? new()); }
        catch { return "[]"; }
    }

    /// <summary>从 JSON 数组文本解析字符串列表；空/损坏时返回空列表。</summary>
    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static ChatMessage MapMessage(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        SessionId = r.GetString(1),
        TurnId = r.GetString(2),
        SequenceNumber = r.GetInt32(3),
        Role = r.GetString(4),
        Content = r.IsDBNull(5) ? null : r.GetString(5),
        Thinking = r.IsDBNull(6) ? null : r.GetString(6),
        ToolCallId = r.IsDBNull(7) ? null : r.GetString(7),
        ToolName = r.IsDBNull(8) ? null : r.GetString(8),
        Status = r.GetString(9),
        UsageJson = r.IsDBNull(10) ? null : r.GetString(10),
        ModelName = r.IsDBNull(11) ? null : r.GetString(11),
        Metadata = r.IsDBNull(12) ? null : r.GetString(12),
        IsDeleted = r.GetInt32(13) != 0,
        DeletedAt = r.IsDBNull(14) ? null : r.GetInt64(14),
        CreatedAt = r.GetInt64(15),
        UpdatedAt = r.GetInt64(16),
        DurationMs = r.IsDBNull(17) ? null : r.GetInt64(17),
    };
}
