namespace HtaciAI.Services;

using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

/// <summary>
/// 思考模式：NoThink=显式关闭（thinking: disabled），Default=不传任何思考/强度字段（模型默认），
/// 其余=开启思考（thinking: enabled）+ 对应推理强度字段。
///
/// 档位要跟各家厂商的取值对齐：Minimal/Medium/XHigh 三个不是我们发明的，
/// 是厂商文档里真实存在的档位（Gemini 与 OpenAI 有 minimal/medium，
/// Anthropic 的 effort 有 xhigh）。少一个就只能把表里的档位硬映射到相邻档，
/// 用户看到的档位和实际下发的不一致。
///
/// <b>新增档位只能追加在末尾。</b>这个枚举以整数形式存在 chat_session.thinking /
/// workspace_chat_session.thinking 里（见 ChatSession.Thinking），
/// 按语义顺序插值会静默改写所有已保存会话的思考模式。
/// </summary>
public enum ThinkingMode
{
    NoThink,
    Default,
    Low,
    High,
    Max,

    // ↓ 后补的档位，值只在末尾增长
    Minimal,
    Medium,
    XHigh,
}

/// <summary>
/// 思考档位与 wire 值之间的<b>唯一</b>映射。客户端下发和选择器过滤都读这里，
/// 两处各写一份必然会在新增档位时漏改一边 —— 表现是「菜单里有这个档位，发出去却是别的」。
/// </summary>
public static class ThinkingModes
{
    /// <summary>菜单展示顺序。Off 与 Default 在前，其余按强度递增。</summary>
    public static readonly ThinkingMode[] DisplayOrder =
    {
        ThinkingMode.NoThink,
        ThinkingMode.Default,
        ThinkingMode.Minimal,
        ThinkingMode.Low,
        ThinkingMode.Medium,
        ThinkingMode.High,
        ThinkingMode.XHigh,
        ThinkingMode.Max,
    };

    /// <summary>菜单里的展示文案。强度档位直接显示 wire 值，与厂商文档一致，便于对照排查。</summary>
    public static string Label(ThinkingMode mode) => mode switch
    {
        ThinkingMode.NoThink => "NoThink",
        ThinkingMode.Default => "none",
        _ => WireValue(mode) ?? mode.ToString(),
    };

    /// <summary>
    /// 该档位对应的推理强度 wire 值。返回 null 表示<b>不下发</b>强度字段
    /// （NoThink 只写开关对象，Default 什么都不写）。
    /// </summary>
    public static string? WireValue(ThinkingMode mode) => mode switch
    {
        ThinkingMode.Minimal => "minimal",
        ThinkingMode.Low => "low",
        ThinkingMode.Medium => "medium",
        ThinkingMode.High => "high",
        ThinkingMode.XHigh => "xhigh",
        ThinkingMode.Max => "max",
        _ => null,
    };

    /// <summary>wire 值反查档位。用于把模型声明的 strengths 映射成菜单项。</summary>
    public static ThinkingMode? FromWire(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return null;
        var v = wire.Trim();

        foreach (var mode in DisplayOrder)
            if (string.Equals(WireValue(mode), v, StringComparison.OrdinalIgnoreCase))
                return mode;

        return null;
    }

    /// <summary>
    /// 该模型可选的档位。<paramref name="strengths"/> 为空时的处理是刻意的：
    /// 「模型声明支持思考但没给档位」和「我们根本不知道这个模型」都无法确定合法档位，
    /// 这时只给 NoThink 与 Default —— 前者显式关闭、后者不传任何字段，
    /// 是仅有的两个不会因为猜错档位名而被服务端拒绝的选项。
    /// </summary>
    public static List<ThinkingMode> AvailableFor(IEnumerable<string>? strengths)
    {
        var result = new List<ThinkingMode> { ThinkingMode.NoThink, ThinkingMode.Default };

        if (strengths is null) return result;

        foreach (var wire in strengths)
        {
            if (FromWire(wire) is not { } mode) continue;
            if (mode is ThinkingMode.NoThink or ThinkingMode.Default) continue;
            if (result.Contains(mode)) continue;
            result.Add(mode);
        }

        // 按展示顺序排序，保证菜单顺序稳定（strengths 在配置里的书写顺序不该影响界面）。
        result.Sort((a, b) => Array.IndexOf(DisplayOrder, a).CompareTo(Array.IndexOf(DisplayOrder, b)));
        return result;
    }
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
