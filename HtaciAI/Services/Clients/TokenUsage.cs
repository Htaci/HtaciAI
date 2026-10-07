using System;
using System.Text.Json;

namespace HtaciAI.Services;

/// <summary>
/// 归一化的 Token 用量。只存 input / cache_hit / output 三个原始量，
/// cache_miss 与 total 可由加减法现算，不单独落库。
/// </summary>
public sealed record TokenUsage(long InputTokens, long CacheHitTokens, long OutputTokens)
{
    public static readonly TokenUsage Empty = new(0, 0, 0);

    /// <summary>cache_miss = input − cache_hit。</summary>
    public long CacheMissTokens => Math.Max(0, InputTokens - CacheHitTokens);

    /// <summary>total = input + output。</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// 从 OpenAI 兼容的 usage 对象解析。兼容 OpenAI（prompt_tokens_details.cached_tokens）
    /// 与 DeepSeek（prompt_cache_hit_tokens）两种缓存命中结构。
    /// </summary>
    public static TokenUsage FromJson(JsonElement usage)
    {
        long input = GetLong(usage, "prompt_tokens");
        long output = GetLong(usage, "completion_tokens");
        long cacheHit = 0;

        if (usage.TryGetProperty("prompt_cache_hit_tokens", out var hit) && hit.ValueKind == JsonValueKind.Number)
            cacheHit = hit.GetInt64();

        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cacheHit = GetLong(details, "cached_tokens");

        return new TokenUsage(input, cacheHit, output);
    }

    /// <summary>
    /// 从 <c>chat_message.usage_json</c>（<see cref="ChatGateway"/> 的落库格式：
    /// input_tokens / cache_hit_tokens / output_tokens + raw）解析。不是该格式或解析失败返回 null。
    /// </summary>
    public static TokenUsage? FromStoredJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new TokenUsage(
                GetLong(root, "input_tokens"),
                GetLong(root, "cache_hit_tokens"),
                GetLong(root, "output_tokens"));
        }
        catch
        {
            return null;
        }
    }

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
}
