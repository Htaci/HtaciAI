using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>模型仓储（ai_model 表）。</summary>
public static class ModelRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // 新增列一律追加在末尾：Map 是按序号取值的，插在中间会让所有后续下标错位。
    private const string SelectCols = """
        SELECT id, provider_id, call_id, display_name, supports_streaming, supports_thinking,
               thinking_strengths, capabilities, context_window,
               price_input, price_cache_hit, price_output, currency,
               is_enabled, sort_order, created_at, updated_at,
               vendor, max_output_tokens, supports_array_content, temperature_supported, config_origin
        FROM ai_model
        """;

    public static async Task<List<AiModel>> GetAllAsync()
    {
        var list = new List<AiModel>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + " ORDER BY sort_order ASC, display_name ASC;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<List<AiModel>> GetByProviderAsync(string providerId)
    {
        var list = new List<AiModel>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + " WHERE provider_id = $provider_id ORDER BY sort_order ASC, display_name ASC;";
        cmd.Parameters.AddWithValue("$provider_id", providerId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<AiModel?> GetAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + " WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task<AiModel?> GetByCallIdAsync(string callId)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + " WHERE call_id = $call_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$call_id", callId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task<int> CountByProviderAsync(string providerId)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ai_model WHERE provider_id = $provider_id;";
        cmd.Parameters.AddWithValue("$provider_id", providerId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public static async Task CreateAsync(AiModel m)
    {
        var now = Now();
        if (m.CreatedAt == 0) m.CreatedAt = now;
        if (m.UpdatedAt == 0) m.UpdatedAt = now;

        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_model (id, provider_id, call_id, display_name, supports_streaming, supports_thinking,
                thinking_strengths, capabilities, context_window,
                price_input, price_cache_hit, price_output, currency,
                is_enabled, sort_order, created_at, updated_at,
                vendor, max_output_tokens, supports_array_content, temperature_supported, config_origin)
            VALUES ($id, $provider_id, $call_id, $display_name, $supports_streaming, $supports_thinking,
                $thinking_strengths, $capabilities, $context_window,
                $price_input, $price_cache_hit, $price_output, $currency,
                $is_enabled, $sort_order, $created_at, $updated_at,
                $vendor, $max_output_tokens, $supports_array_content, $temperature_supported, $config_origin);
            """;
        AddParams(cmd, m);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(AiModel m)
    {
        m.UpdatedAt = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ai_model
            SET call_id = $call_id, display_name = $display_name,
                supports_streaming = $supports_streaming, supports_thinking = $supports_thinking,
                thinking_strengths = $thinking_strengths, capabilities = $capabilities,
                context_window = $context_window,
                price_input = $price_input, price_cache_hit = $price_cache_hit, price_output = $price_output,
                currency = $currency, is_enabled = $is_enabled, sort_order = $sort_order,
                updated_at = $updated_at,
                vendor = $vendor, max_output_tokens = $max_output_tokens,
                supports_array_content = $supports_array_content,
                temperature_supported = $temperature_supported, config_origin = $config_origin
            WHERE id = $id;
            """;
        AddParams(cmd, m);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_model WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParams(SqliteCommand cmd, AiModel m)
    {
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$provider_id", m.ProviderId);
        cmd.Parameters.AddWithValue("$call_id", m.CallId);
        cmd.Parameters.AddWithValue("$display_name", m.DisplayName);
        cmd.Parameters.AddWithValue("$supports_streaming", m.SupportsStreaming ? 1 : 0);
        cmd.Parameters.AddWithValue("$supports_thinking", m.SupportsThinking ? 1 : 0);
        cmd.Parameters.AddWithValue("$thinking_strengths", (object?)ToJson(m.ThinkingStrengths) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$capabilities", (object?)ToJson(m.Capabilities) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$context_window", m.ContextWindow);
        cmd.Parameters.AddWithValue("$price_input", (object?)m.PriceInput ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$price_cache_hit", (object?)m.PriceCacheHit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$price_output", (object?)m.PriceOutput ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$currency", m.Currency);
        cmd.Parameters.AddWithValue("$is_enabled", m.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort_order", m.SortOrder);
        cmd.Parameters.AddWithValue("$created_at", m.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", m.UpdatedAt);
        cmd.Parameters.AddWithValue("$vendor", string.IsNullOrEmpty(m.Vendor) ? (object)DBNull.Value : m.Vendor);
        cmd.Parameters.AddWithValue("$max_output_tokens", m.MaxOutputTokens);
        // 三态可空：NULL 是「不知道」，不是「不支持」。这个区分是刻意的，
        // 合并成 0 之后调用方就再也分不出「服务端明确拒绝」和「我们没查过」了。
        cmd.Parameters.AddWithValue("$supports_array_content", (object?)BoolToInt(m.SupportsArrayContent) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$temperature_supported", (object?)BoolToInt(m.TemperatureSupported) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$config_origin", (int)m.ConfigOrigin);
    }

    private static int? BoolToInt(bool? value) => value switch
    {
        true => 1,
        false => 0,
        null => null,
    };

    private static AiModel Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ProviderId = r.GetString(1),
        CallId = r.GetString(2),
        DisplayName = r.GetString(3),
        SupportsStreaming = r.GetInt32(4) != 0,
        SupportsThinking = r.GetInt32(5) != 0,
        ThinkingStrengths = FromJson(r.IsDBNull(6) ? null : r.GetString(6)),
        Capabilities = FromJson(r.IsDBNull(7) ? null : r.GetString(7)),
        ContextWindow = r.GetInt64(8),
        PriceInput = r.IsDBNull(9) ? null : r.GetDouble(9),
        PriceCacheHit = r.IsDBNull(10) ? null : r.GetDouble(10),
        PriceOutput = r.IsDBNull(11) ? null : r.GetDouble(11),
        Currency = r.IsDBNull(12) ? "CNY" : r.GetString(12),
        IsEnabled = r.GetInt32(13) != 0,
        SortOrder = r.GetInt32(14),
        CreatedAt = r.GetInt64(15),
        UpdatedAt = r.GetInt64(16),
        Vendor = r.IsDBNull(17) ? "" : r.GetString(17),
        MaxOutputTokens = r.GetInt64(18),
        SupportsArrayContent = IntToBool(r.IsDBNull(19) ? null : r.GetInt32(19)),
        TemperatureSupported = IntToBool(r.IsDBNull(20) ? null : r.GetInt32(20)),
        // 超出枚举范围的脏值退回 Manual：宁可少提示，也不要把用户已经配好的模型
        // 误标成「配置缺失」而反复打扰。
        ConfigOrigin = Enum.IsDefined(typeof(ModelConfigOrigin), r.GetInt32(21))
            ? (ModelConfigOrigin)r.GetInt32(21)
            : ModelConfigOrigin.Manual,
    };

    private static bool? IntToBool(int? value) => value switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    private static string? ToJson(List<string> list)
        => list.Count == 0 ? null : JsonSerializer.Serialize(list);

    private static List<string> FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(text) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
