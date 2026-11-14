using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// API 请求流水（<c>api_requests</c>）的读写与聚合。
///
/// <b>不做预汇总表</b>：一行 = 一次往返，一年重度使用也就几十万行、几十 MB，
/// 自带索引足够 SQLite 现算。省下的复杂度换来的是「随时能改口径」。
/// 保留策略见 <see cref="PurgeAsync"/>（默认 365 天，0 = 永久）。
/// </summary>
public static class ApiRequestRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 写入 ----

    /// <summary>
    /// 落一行流水。<b>失败一律吞掉</b>：统计是旁路，它出问题时绝不能影响正在进行的对话。
    /// </summary>
    public static async Task<bool> InsertAsync(ApiRequest row)
    {
        try
        {
            await using var conn = DatabaseService.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO api_requests (
                    id, kind, status, attempt, session_id, turn_id, message_id,
                    provider_name, model_id, model_name, protocol, endpoint,
                    http_status, error_kind, error_message,
                    input_tokens, cache_hit_tokens, output_tokens, cost, currency,
                    ttft_ms, duration_ms, message_count, tool_count, started_at)
                VALUES (
                    $id, $kind, $status, $attempt, $sessionId, $turnId, $messageId,
                    $provider, $modelId, $modelName, $protocol, $endpoint,
                    $httpStatus, $errorKind, $errorMessage,
                    $input, $cacheHit, $output, $cost, $currency,
                    $ttft, $duration, $messageCount, $toolCount, $startedAt);
                """;

            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$kind", row.Kind);
            cmd.Parameters.AddWithValue("$status", row.Status);
            cmd.Parameters.AddWithValue("$attempt", row.Attempt);
            cmd.Parameters.AddWithValue("$sessionId", (object?)row.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$turnId", (object?)row.TurnId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$messageId", (object?)row.MessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$provider", (object?)row.ProviderName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$modelId", (object?)row.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$modelName", (object?)row.ModelName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$protocol", (object?)row.Protocol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$endpoint", (object?)row.Endpoint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$httpStatus", (object?)row.HttpStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorKind", (object?)row.ErrorKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorMessage", (object?)row.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$input", (object?)row.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cacheHit", (object?)row.CacheHitTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$output", (object?)row.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cost", (object?)row.Cost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$currency", (object?)row.Currency ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ttft", (object?)row.TtftMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", row.DurationMs);
            cmd.Parameters.AddWithValue("$messageCount", row.MessageCount);
            cmd.Parameters.AddWithValue("$toolCount", row.ToolCount);
            cmd.Parameters.AddWithValue("$startedAt", row.StartedAt);

            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- 清理 ----

    /// <summary>
    /// 删掉 <paramref name="days"/> 天以前的流水。<paramref name="days"/> ≤ 0 = 永久保留，什么都不删。
    /// 返回删除的行数。
    /// </summary>
    public static async Task<int> PurgeAsync(int days)
    {
        if (days <= 0) return 0;

        var cutoff = Now() - (long)days * 86_400_000;

        try
        {
            await using var conn = DatabaseService.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_requests WHERE started_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>清空全部流水（统计页「清空记录」用）。返回删除的行数。</summary>
    public static async Task<int> ClearAsync()
    {
        try
        {
            await using var conn = DatabaseService.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_requests;";
            return await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            return 0;
        }
    }

    // ---- 聚合 ----

    /// <summary>一个时间区间的总览。<c>From</c>/<c>To</c> 都是 Unix 毫秒，To 为 null 表示「到现在」。</summary>
    public sealed record Summary(
        int Total,
        int OkCount,
        int FailedCount,
        int CancelledCount,
        long InputTokens,
        long CacheHitTokens,
        long OutputTokens,
        double Cost,
        double? AvgDurationMs,
        double? AvgTtftMs)
    {
        /// <summary>失败率只算「失败 / 有效请求」—— 用户点停止的不算失败。</summary>
        public double FailureRate => Total <= 0 ? 0 : (double)FailedCount / Total;

        public string? Currency { get; init; }
    }

    public static async Task<Summary> GetSummaryAsync(long from, long? to)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN status = 'ok' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'cancelled' THEN 1 ELSE 0 END),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_hit_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(cost), 0),
                AVG(duration_ms),
                AVG(ttft_ms),
                MAX(currency)
            FROM api_requests
            WHERE started_at >= $from AND ($to IS NULL OR started_at < $to);
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return new Summary(0, 0, 0, 0, 0, 0, 0, 0, null, null);

        return new Summary(
            r.GetInt32(0),
            r.IsDBNull(1) ? 0 : r.GetInt32(1),
            r.IsDBNull(2) ? 0 : r.GetInt32(2),
            r.IsDBNull(3) ? 0 : r.GetInt32(3),
            r.GetInt64(4),
            r.GetInt64(5),
            r.GetInt64(6),
            r.GetDouble(7),
            r.IsDBNull(8) ? null : r.GetDouble(8),
            r.IsDBNull(9) ? null : r.GetDouble(9))
        {
            Currency = r.IsDBNull(10) ? null : r.GetString(10),
        };
    }

    /// <summary>按天聚合，用于趋势图。日期按<b>本地时区</b>切（用户看的是自己那天）。</summary>
    public sealed record DayBucket(string Day, int Count, long InputTokens, long OutputTokens, double Cost, int Failed);

    public static async Task<List<DayBucket>> GetDailyAsync(long from, long? to)
    {
        var list = new List<DayBucket>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        // strftime 的 unixepoch → localtime：SQLite 直接给出本地日期，避免在 C# 里再换算一遍
        cmd.CommandText = """
            SELECT
                date(started_at / 1000, 'unixepoch', 'localtime') AS day,
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(cost), 0),
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)
            FROM api_requests
            WHERE started_at >= $from AND ($to IS NULL OR started_at < $to)
            GROUP BY day
            ORDER BY day;
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new DayBucket(
                r.GetString(0),
                r.GetInt32(1),
                r.GetInt64(2),
                r.GetInt64(3),
                r.GetDouble(4),
                r.IsDBNull(5) ? 0 : r.GetInt32(5)));
        return list;
    }

    /// <summary>按模型聚合。</summary>
    public sealed record ModelBucket(
        string? ModelName, string? ProviderName, int Count, long InputTokens, long CacheHitTokens,
        long OutputTokens, double Cost, double? AvgDurationMs, int Failed);

    public static async Task<List<ModelBucket>> GetByModelAsync(long from, long? to)
    {
        var list = new List<ModelBucket>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                model_name,
                MAX(provider_name),
                COUNT(*),
                COALESCE(SUM(input_tokens), 0),
                COALESCE(SUM(cache_hit_tokens), 0),
                COALESCE(SUM(output_tokens), 0),
                COALESCE(SUM(cost), 0),
                AVG(duration_ms),
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)
            FROM api_requests
            WHERE started_at >= $from AND ($to IS NULL OR started_at < $to)
            GROUP BY model_name
            ORDER BY COUNT(*) DESC;
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ModelBucket(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt32(2),
                r.GetInt64(3),
                r.GetInt64(4),
                r.GetInt64(5),
                r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? 0 : r.GetInt32(8)));
        return list;
    }

    /// <summary>按请求种类聚合。</summary>
    public sealed record KindBucket(string Kind, int Count, long InputTokens, long OutputTokens, double Cost);

    public static async Task<List<KindBucket>> GetByKindAsync(long from, long? to)
    {
        var list = new List<KindBucket>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, COUNT(*),
                   COALESCE(SUM(input_tokens), 0),
                   COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(cost), 0)
            FROM api_requests
            WHERE started_at >= $from AND ($to IS NULL OR started_at < $to)
            GROUP BY kind
            ORDER BY COUNT(*) DESC;
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KindBucket(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4)));
        return list;
    }

    /// <summary>最近的请求明细（新的在前）。</summary>
    public static async Task<List<ApiRequest>> GetRecentAsync(long from, long? to, int limit = 200)
    {
        var list = new List<ApiRequest>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, status, attempt, session_id, turn_id, message_id,
                   provider_name, model_id, model_name, protocol, endpoint,
                   http_status, error_kind, error_message,
                   input_tokens, cache_hit_tokens, output_tokens, cost, currency,
                   ttft_ms, duration_ms, message_count, tool_count, started_at
            FROM api_requests
            WHERE started_at >= $from AND ($to IS NULL OR started_at < $to)
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ApiRequest
            {
                Id = r.GetString(0),
                Kind = r.GetString(1),
                Status = r.GetString(2),
                Attempt = r.GetInt32(3),
                SessionId = r.IsDBNull(4) ? null : r.GetString(4),
                TurnId = r.IsDBNull(5) ? null : r.GetString(5),
                MessageId = r.IsDBNull(6) ? null : r.GetString(6),
                ProviderName = r.IsDBNull(7) ? null : r.GetString(7),
                ModelId = r.IsDBNull(8) ? null : r.GetString(8),
                ModelName = r.IsDBNull(9) ? null : r.GetString(9),
                Protocol = r.IsDBNull(10) ? null : r.GetString(10),
                Endpoint = r.IsDBNull(11) ? null : r.GetString(11),
                HttpStatus = r.IsDBNull(12) ? null : r.GetInt32(12),
                ErrorKind = r.IsDBNull(13) ? null : r.GetString(13),
                ErrorMessage = r.IsDBNull(14) ? null : r.GetString(14),
                InputTokens = r.IsDBNull(15) ? null : r.GetInt64(15),
                CacheHitTokens = r.IsDBNull(16) ? null : r.GetInt64(16),
                OutputTokens = r.IsDBNull(17) ? null : r.GetInt64(17),
                Cost = r.IsDBNull(18) ? null : r.GetDouble(18),
                Currency = r.IsDBNull(19) ? null : r.GetString(19),
                TtftMs = r.IsDBNull(20) ? null : r.GetInt64(20),
                DurationMs = r.GetInt64(21),
                MessageCount = r.GetInt32(22),
                ToolCount = r.GetInt32(23),
                StartedAt = r.GetInt64(24),
            });
        return list;
    }
}
