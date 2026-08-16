using System;
using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 思考开关字段的写法：think（thinking:"enabled"/"disabled"）、
/// enable_thinking（enable_thinking:true/false）、none（不支持该开关字段）。
/// </summary>
public enum ThinkingFieldKind
{
    None,
    Think,
    EnableThinking,
}

/// <summary>模型（ai_model 表），隶属于某个服务商。</summary>
public class AiModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = "";

    /// <summary>请求中的模型名（模型调用 id），如 deepseek-v4-flash。</summary>
    public string CallId { get; set; } = "";

    /// <summary>模型备注名称（显示用）。</summary>
    public string DisplayName { get; set; } = "";
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsThinking { get; set; }

    /// <summary>支持的思考强度（wire 值，如 low/high/max）。</summary>
    public List<string> ThinkingStrengths { get; set; } = new();

    /// <summary>模型能力类型（vision / reasoning / tools 等）。</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>上下文窗口大小，-1 = 未知。</summary>
    public long ContextWindow { get; set; } = -1;

    /// <summary>价格（每百万 token，可空 = 未设置）。</summary>
    public double? PriceInput { get; set; }
    public double? PriceCacheHit { get; set; }
    public double? PriceOutput { get; set; }

    /// <summary>价格货币单位，默认人民币。</summary>
    public string Currency { get; set; } = "CNY";

    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
