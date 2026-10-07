using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>服务商仓储（ai_provider 表）。</summary>
public static class ProviderRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<AiProvider>> GetAllAsync()
    {
        var list = new List<AiProvider>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, protocol, endpoint, api_key, thinking_field,
                   supports_array_content, supports_streaming, is_enabled, sort_order,
                   created_at, updated_at
            FROM ai_provider
            ORDER BY sort_order ASC, name ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<AiProvider?> GetAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, protocol, endpoint, api_key, thinking_field,
                   supports_array_content, supports_streaming, is_enabled, sort_order,
                   created_at, updated_at
            FROM ai_provider WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task CreateAsync(AiProvider p)
    {
        var now = Now();
        if (p.CreatedAt == 0) p.CreatedAt = now;
        if (p.UpdatedAt == 0) p.UpdatedAt = now;

        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_provider (id, name, description, protocol, endpoint, api_key, thinking_field,
                supports_array_content, supports_streaming, is_enabled, sort_order, created_at, updated_at)
            VALUES ($id, $name, $description, $protocol, $endpoint, $api_key, $thinking_field,
                $supports_array_content, $supports_streaming, $is_enabled, $sort_order, $created_at, $updated_at);
            """;
        AddParams(cmd, p);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(AiProvider p)
    {
        p.UpdatedAt = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ai_provider
            SET name = $name, description = $description, protocol = $protocol, endpoint = $endpoint,
                api_key = $api_key, thinking_field = $thinking_field,
                supports_array_content = $supports_array_content, supports_streaming = $supports_streaming,
                is_enabled = $is_enabled, sort_order = $sort_order, updated_at = $updated_at
            WHERE id = $id;
            """;
        AddParams(cmd, p);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>删除服务商及其下全部模型（手动级联 + 外键双保险）。</summary>
    public static async Task DeleteAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM ai_model WHERE provider_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM ai_provider WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static void AddParams(SqliteCommand cmd, AiProvider p)
    {
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$description", (object?)p.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$protocol", MapProtocol(p.Protocol));
        cmd.Parameters.AddWithValue("$endpoint", p.Endpoint);
        cmd.Parameters.AddWithValue("$api_key", (object?)p.ApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking_field", MapThinkingField(p.ThinkingField));
        cmd.Parameters.AddWithValue("$supports_array_content", p.SupportsArrayContent ? 1 : 0);
        cmd.Parameters.AddWithValue("$supports_streaming", p.SupportsStreaming ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_enabled", p.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort_order", p.SortOrder);
        cmd.Parameters.AddWithValue("$created_at", p.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", p.UpdatedAt);
    }

    private static AiProvider Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        Protocol = ParseProtocol(r.GetString(3)),
        Endpoint = r.GetString(4),
        ApiKey = r.IsDBNull(5) ? null : r.GetString(5),
        ThinkingField = ParseThinkingField(r.GetString(6)),
        SupportsArrayContent = r.GetInt32(7) != 0,
        SupportsStreaming = r.GetInt32(8) != 0,
        IsEnabled = r.GetInt32(9) != 0,
        SortOrder = r.GetInt32(10),
        CreatedAt = r.GetInt64(11),
        UpdatedAt = r.GetInt64(12),
    };

    private static string MapProtocol(ModelProtocol p) => p switch
    {
        ModelProtocol.OpenAIEx => "OpenAIEx",
        ModelProtocol.LMStudio => "LMStudio",
        _ => "OpenAIEx",
    };

    private static ModelProtocol ParseProtocol(string s) => s switch
    {
        "LMStudio" => ModelProtocol.LMStudio,
        "OpenAIEx" => ModelProtocol.OpenAIEx,
        _ => ModelProtocol.OpenAIEx,
    };

    private static string MapThinkingField(ThinkingFieldKind k) => k switch
    {
        ThinkingFieldKind.Think => "think",
        ThinkingFieldKind.EnableThinking => "enable_thinking",
        ThinkingFieldKind.ReasoningEffort => "reasoning_effort",
        _ => "none",
    };

    private static ThinkingFieldKind ParseThinkingField(string s) => s switch
    {
        "think" => ThinkingFieldKind.Think,
        "enable_thinking" => ThinkingFieldKind.EnableThinking,
        "reasoning_effort" => ThinkingFieldKind.ReasoningEffort,
        _ => ThinkingFieldKind.None,
    };
}
