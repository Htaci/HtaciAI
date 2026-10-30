using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 服务商仓储（ai_provider 表 + 它的端点表 ai_provider_endpoint）。
///
/// 两张表当成一个整体来读写：端点没有独立生命期，脱离服务商没有意义，所以不单开仓储。
/// 保存时总是先把默认端点同步到 ai_provider.protocol / endpoint 再落库 —— 这两列是
/// <b>默认端点的镜像</b>，请求层只读它们；不同步就会出现「界面上的默认端点」和
/// 「实际请求打的位置」不一致，那种问题查起来极费劲。
/// </summary>
public static class ProviderRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<AiProvider>> GetAllAsync()
    {
        var list = new List<AiProvider>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using (var cmd = conn.CreateCommand())
        {
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
        }

        await LoadEndpointsAsync(conn, list);
        return list;
    }

    public static async Task<AiProvider?> GetAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        AiProvider? provider;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, name, description, protocol, endpoint, api_key, thinking_field,
                       supports_array_content, supports_streaming, is_enabled, sort_order,
                       created_at, updated_at
                FROM ai_provider WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            provider = await r.ReadAsync() ? Map(r) : null;
        }

        if (provider is null) return null;
        await LoadEndpointsAsync(conn, new List<AiProvider> { provider });
        return provider;
    }

    public static async Task CreateAsync(AiProvider p)
    {
        var now = Now();
        if (p.CreatedAt == 0) p.CreatedAt = now;
        if (p.UpdatedAt == 0) p.UpdatedAt = now;

        p.SyncDefaultEndpoint();

        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        await using var tx = await conn.BeginTransactionAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO ai_provider (id, name, description, protocol, endpoint, api_key, thinking_field,
                    supports_array_content, supports_streaming, is_enabled, sort_order, created_at, updated_at)
                VALUES ($id, $name, $description, $protocol, $endpoint, $api_key, $thinking_field,
                    $supports_array_content, $supports_streaming, $is_enabled, $sort_order, $created_at, $updated_at);
                """;
            AddParams(cmd, p);
            await cmd.ExecuteNonQueryAsync();
        }

        await SyncEndpointsAsync(conn, (SqliteTransaction)tx, p);
        await tx.CommitAsync();
    }

    public static async Task UpdateAsync(AiProvider p)
    {
        p.UpdatedAt = Now();
        p.SyncDefaultEndpoint();

        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        await using var tx = await conn.BeginTransactionAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
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

        await SyncEndpointsAsync(conn, (SqliteTransaction)tx, p);
        await tx.CommitAsync();
    }

    /// <summary>删除服务商及其下全部模型与端点（手动级联 + 外键双保险）。</summary>
    public static async Task DeleteAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        foreach (var sql in new[]
                 {
                     "DELETE FROM ai_model WHERE provider_id = $id;",
                     "DELETE FROM ai_provider_endpoint WHERE provider_id = $id;",
                     "DELETE FROM ai_provider WHERE id = $id;",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---- 端点 ----

    /// <summary>一次查出所有端点再按服务商分组，避免每个服务商一次往返。</summary>
    private static async Task LoadEndpointsAsync(SqliteConnection conn, List<AiProvider> providers)
    {
        if (providers.Count == 0) return;

        var byId = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, provider_id, protocol, endpoint, is_default, sort_order, created_at, updated_at
            FROM ai_provider_endpoint
            ORDER BY sort_order ASC, created_at ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var e = new AiProviderEndpoint
            {
                Id = r.GetString(0),
                ProviderId = r.GetString(1),
                Protocol = ParseProtocol(r.GetString(2)),
                Endpoint = r.GetString(3),
                IsDefault = r.GetInt32(4) != 0,
                SortOrder = r.GetInt32(5),
                CreatedAt = r.GetInt64(6),
                UpdatedAt = r.GetInt64(7),
            };
            if (byId.TryGetValue(e.ProviderId, out var owner))
                owner.Endpoints.Add(e);
        }
    }

    /// <summary>
    /// 把内存里的端点列表同步进库：删掉已被移除的、写入新增与改动的，顺带重排 sort_order。
    /// 用 upsert 而不是「全删再插」，是为了保住新建时间（端点顺序按它兜底）。
    /// </summary>
    private static async Task SyncEndpointsAsync(SqliteConnection conn, SqliteTransaction tx, AiProvider p)
    {
        var keep = p.Endpoints.Select(e => e.Id).ToList();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = keep.Count == 0
                ? "DELETE FROM ai_provider_endpoint WHERE provider_id = $pid;"
                : $"DELETE FROM ai_provider_endpoint WHERE provider_id = $pid AND id NOT IN ({string.Join(",", keep.Select((_, i) => "$k" + i))});";
            del.Parameters.AddWithValue("$pid", p.Id);
            for (var i = 0; i < keep.Count; i++)
                del.Parameters.AddWithValue("$k" + i, keep[i]);
            await del.ExecuteNonQueryAsync();
        }

        var now = Now();
        for (var i = 0; i < p.Endpoints.Count; i++)
        {
            var e = p.Endpoints[i];
            e.ProviderId = p.Id;
            e.SortOrder = i;
            if (e.CreatedAt == 0) e.CreatedAt = now;
            e.UpdatedAt = now;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ai_provider_endpoint
                    (id, provider_id, protocol, endpoint, is_default, sort_order, created_at, updated_at)
                VALUES ($id, $pid, $protocol, $endpoint, $is_default, $sort_order, $created_at, $updated_at)
                ON CONFLICT(id) DO UPDATE SET
                    protocol = $protocol, endpoint = $endpoint, is_default = $is_default,
                    sort_order = $sort_order, updated_at = $updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.Parameters.AddWithValue("$pid", p.Id);
            cmd.Parameters.AddWithValue("$protocol", MapProtocol(e.Protocol));
            cmd.Parameters.AddWithValue("$endpoint", e.Endpoint);
            cmd.Parameters.AddWithValue("$is_default", e.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$sort_order", e.SortOrder);
            cmd.Parameters.AddWithValue("$created_at", e.CreatedAt);
            cmd.Parameters.AddWithValue("$updated_at", e.UpdatedAt);
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
        ModelProtocol.OpenAIResponses => "OpenAIResponses",
        ModelProtocol.LMStudio => "LMStudio",
        _ => "OpenAI",
    };

    private static ModelProtocol ParseProtocol(string s) => s switch
    {
        "OpenAIResponses" => ModelProtocol.OpenAIResponses,
        "LMStudio" => ModelProtocol.LMStudio,
        // "OpenAIEx" 是改名前的写法，老库里存着它，必须还能读出来 —— 否则那些服务商会静默退回默认协议。
        "OpenAIEx" or "OpenAI" => ModelProtocol.OpenAI,
        _ => ModelProtocol.OpenAI,
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
