using System;
using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 思考开关字段的写法：
/// think（{"thinking":{"type":"enabled"/"disabled"}}，DeepSeek OpenAI 兼容格式）、
/// enable_thinking（enable_thinking:true/false）、
/// reasoning_effort（LM Studio 等：仅用顶层 reasoning_effort 控制强度，无开关对象）、
/// none（不支持该开关字段，仅能走模型默认）。
/// </summary>
public enum ThinkingFieldKind
{
    None,
    Think,
    EnableThinking,
    ReasoningEffort,
}

/// <summary>
/// 这一行模型配置「是怎么来的」。决定设置页挂不挂提示，以及界面要不要让用户去补配置。
///
/// 值<b>只增不改</b>：它以整数形式落在 ai_model.config_origin 里，调整顺序会改写所有已存行的含义。
/// </summary>
public enum ModelConfigOrigin
{
    /// <summary>内置表和开发商反推都没命中，什么都不知道。界面上挂灰问号。</summary>
    Unmatched = 0,

    /// <summary>只反推出了开发商（头像对得上），能力/上下文等配置缺失。界面上挂黄叹号。</summary>
    VendorOnly = 1,

    /// <summary>精确命中了内置模型表，配置完整。</summary>
    Builtin = 2,

    /// <summary>用户自己填的，或在内置配置基础上改过。内置表不再对它有话可说。</summary>
    Manual = 3,
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

    /// <summary>
    /// 开发商 id（谁的模型，不是谁在转发）。决定模型头像，见 <c>Services/ModelTable</c>。
    /// 空 = 没认出来，界面用通用图标。
    /// </summary>
    public string Vendor { get; set; } = "";

    /// <summary>支持的思考强度（wire 值，如 low/high/max）。</summary>
    public List<string> ThinkingStrengths { get; set; } = new();

    /// <summary>模型能力类型（vision / reasoning / tools 等）。</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>上下文窗口大小，-1 = 未知。</summary>
    public long ContextWindow { get; set; } = -1;

    /// <summary>
    /// 最大输出 token，-1 = 未知。
    /// 目前仅作记录与展示 —— 请求体里还没有下发 max_tokens 的地方。
    /// </summary>
    public long MaxOutputTokens { get; set; } = -1;

    /// <summary>
    /// 是否接受数组格式的 message content（多模态消息）。
    /// null = 未知，此时跟随服务商（<see cref="AiProvider.SupportsArrayContent"/>) 的设置。
    /// 未知按「不支持」处理：猜「支持」会让不支持的服务端直接拒绝整个请求。
    /// </summary>
    public bool? SupportsArrayContent { get; set; }

    /// <summary>
    /// 是否接受 temperature 参数。null = 未知，按「不下发」处理。
    /// 有些模型（如 DeepSeek 的思考模式）收下这个参数既不报错也不生效，
    /// 静默失效比报错更难查，所以这条要如实记录、界面上置灰。
    /// </summary>
    public bool? TemperatureSupported { get; set; }

    /// <summary>这行配置的来源，决定界面提示。见 <see cref="ModelConfigOrigin"/>。</summary>
    public ModelConfigOrigin ConfigOrigin { get; set; } = ModelConfigOrigin.Manual;

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
