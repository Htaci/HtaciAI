namespace HtaciAI.Services;

/// <summary>
/// 思考模式：NoThink=显式关闭（thinking: disabled），Default=不传任何思考/强度字段（模型默认），
/// Low/High/Max=开启思考（thinking: enabled）+ 对应推理强度字段。
/// </summary>
public enum ThinkingMode
{
    NoThink,
    Default,
    Low,
    High,
    Max,
}

/// <summary>客户端请求选项。thinking / 强度字段仅在明确设置时才写入请求体。</summary>
public sealed class ChatRequestOptions
{
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Default;
}
