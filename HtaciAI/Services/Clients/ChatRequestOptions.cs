namespace HtaciAI.Services;

using System.Collections.Generic;
using HtaciAI.Services.Tools;

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

/// <summary>客户端请求选项。thinking / 强度字段仅在明确设置时才写入请求体；Tools 非空时写入 tools 数组。</summary>
public sealed class ChatRequestOptions
{
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Default;

    /// <summary>当前激活的工具定义（去重后），非空时作为 tools 数组下发给 LLM。</summary>
    public IReadOnlyList<ToolDefinition>? Tools { get; set; }

    /// <summary>
    /// 当前模型是否支持视觉。历史里可能留着 view_image 产生的图片 tool 结果，
    /// 换到不支持视觉的模型后必须把它们降级成文本，否则整轮请求会被 API 拒绝。
    /// </summary>
    public bool SupportsVision { get; set; }
}
