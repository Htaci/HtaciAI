using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 智能对话仓储：会话与消息的读写。
/// </summary>
public static class ChatRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 会话 ----

    public static async Task<List<ChatSession>> GetAllAsync()
    {
        var list = new List<ChatSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, model, system_prompt, thinking, created_at, updated_at
            FROM chat_sessions ORDER BY updated_at DESC;
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
            SELECT id, title, model, system_prompt, thinking, created_at, updated_at
            FROM chat_sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapSession(r) : null;
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
            INSERT INTO chat_sessions (id, title, model, system_prompt, thinking, created_at, updated_at)
            VALUES ($id, $title, $model, $system_prompt, $thinking, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
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
                thinking = $thinking, updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 消息 ----

    public static async Task<List<ChatMessage>> GetBySessionAsync(string sessionId)
    {
        var list = new List<ChatMessage>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, turn_id, sequence_number, role, content, thinking,
                   tool_call_id, tool_name, status, usage_json, model_name, metadata,
                   is_deleted, deleted_at, created_at, updated_at
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
                is_deleted, deleted_at, created_at, updated_at)
            VALUES ($id, $session_id, $turn_id, $sequence_number, $role, $content,
                $thinking, $tool_call_id, $tool_name, $status, $usage_json, $model_name, $metadata,
                $is_deleted, $deleted_at, $created_at, $updated_at);
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
                model_name = $model_name, metadata = $metadata, updated_at = $updated_at
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
        CreatedAt = r.GetInt64(5),
        UpdatedAt = r.GetInt64(6),
    };

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
    };
}
