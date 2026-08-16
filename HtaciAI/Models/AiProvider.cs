using System;

namespace HtaciAI.Models;

/// <summary>服务商使用的 API 协议类型。当前仅支持 OpenAI(Ex) API。</summary>
public enum ModelProtocol
{
    OpenAIEx,
}

/// <summary>服务商（ai_provider 表）。一个服务商包含若干模型。</summary>
public class AiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ModelProtocol Protocol { get; set; } = ModelProtocol.OpenAIEx;
    public string Endpoint { get; set; } = "";
    public string? ApiKey { get; set; }

    /// <summary>思考开关字段的写法：think / enable_thinking / none（不支持该字段）。</summary>
    public ThinkingFieldKind ThinkingField { get; set; } = ThinkingFieldKind.Think;
    public bool SupportsArrayContent { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
