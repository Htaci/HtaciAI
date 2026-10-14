using System;

namespace HtaciAI.Services.ModelTable;

/// <summary>
/// 三态布尔。配置文件里写 <c>yes</c> / <c>no</c> / <c>unknown</c>，省略等同于 <c>unknown</c>。
///
/// 单独用一个三态而不是 <c>bool</c>，是因为「表里没写」和「表里写了不支持」必须区分开：
/// 前者意味着我们<b>不知道</b>，调用方要往保守方向取值；后者才是确定的事实。
/// </summary>
public enum TriState : byte
{
    /// <summary>未知（表里没写，或显式写了 unknown）。调用方一律按最保守的方式处理。</summary>
    Unknown = 0,
    No = 1,
    Yes = 2,
}

/// <summary>
/// 模型开发商 —— 谁的模型，而不是谁在转发。
///
/// 这个字段决定模型头像：DeepSeek 的模型就算由火山引擎转发，头像也应该是 DeepSeek。
/// 所以它是从模型 id 反推出来的，与用户配置的服务商无关。
/// </summary>
public sealed class VendorInfo
{
    /// <summary>开发商标识（如 <c>deepseek</c>、<c>zhipu</c>）。</summary>
    public string Id { get; init; } = "";

    /// <summary>展示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 图标文件名（<c>Assets/icon/models/*</c> 下不带扩展名）。为空时用 <see cref="Id"/>。
    ///
    /// 留这个覆盖是因为图标是按<b>品牌</b>而不是按公司画的：智谱的图标叫 <c>glm</c>、
    /// xAI 的叫 <c>grok</c>，硬把开发商标识改成品牌名反而不好认。
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// 从模型 id 反推该开发商的正则（锚定开头，如 <c>^deepseek</c>）。
    /// 为空表示这个开发商只能通过模型条目的显式 vendor 字段命中，不参与反推。
    /// </summary>
    public string? Match { get; init; }
}

/// <summary>一条内置模型配置。字段与 ai_model 表一一对应，加匹配用的别名。</summary>
public sealed class ModelInfo
{
    /// <summary>模型调用 id（请求里下发的那个名字）。既是对外键也是主键。</summary>
    public string Id { get; init; } = "";

    /// <summary>开发商 id，对应 <see cref="VendorInfo.Id"/>。</summary>
    public string Vendor { get; init; } = "";

    /// <summary>备注名称。为空则调用方退回使用 <see cref="Id"/>。</summary>
    public string? Name { get; init; }

    /// <summary>别名 —— 中转站/聚合商的其它写法，参与匹配但不入库。</summary>
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>上下文窗口。-1 = 未知。</summary>
    public long ContextWindow { get; init; } = -1;

    /// <summary>最大输出 token。-1 = 未知。</summary>
    public long MaxOutputTokens { get; init; } = -1;

    /// <summary>能力（wire 值，见 <see cref="HtaciAI.Models.ModelCapabilities"/>）。</summary>
    public string[] Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>是否接受数组格式的 message content（多模态消息）。</summary>
    public TriState ArrayContent { get; init; } = TriState.Unknown;

    /// <summary>是否支持思考模式。</summary>
    public bool SupportsThinking { get; init; }

    /// <summary>支持的思考强度（wire 值，如 minimal/low/medium/high/xhigh/max）。</summary>
    public string[] ThinkingStrengths { get; init; } = Array.Empty<string>();

    /// <summary>是否接受 temperature 参数。</summary>
    public TriState Temperature { get; init; } = TriState.Unknown;

    /// <summary>
    /// 该条目的数据来源（官方文档 URL）。纯维护线索，不参与匹配 ——
    /// 留这一栏是为了日后能核对「这个数字当初是从哪抄来的」，避免变成无法追溯的传说。
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// 维护备注：数字的含糊之处、文档自相矛盾的地方、某档位为何缺省。
    /// 同样不参与匹配。这条针对的正是「查的时候发现文档只写了个 1M」这类情况 ——
    /// 不记下来，过半年就没人知道这个数字是怎么来的了。
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>匹配结果的三态。</summary>
public enum TableMatchKind
{
    /// <summary>表和模型 id 都对不上，什么都没匹配到。</summary>
    None = 0,

    /// <summary>只反推出了开发商。图标和归属对得上，但能力/上下文等配置缺失 → 挂黄叹号。</summary>
    VendorOnly = 1,

    /// <summary>精确命中了模型条目，配置完整。</summary>
    Model = 2,
}

/// <summary>一次匹配的结果。</summary>
/// <param name="Kind">匹配到的层级。</param>
/// <param name="Model">命中的模型条目，未命中为 null。</param>
/// <param name="Vendor">反推出的开发商，两级都没命中为 null。</param>
public sealed record TableMatch(TableMatchKind Kind, ModelInfo? Model, VendorInfo? Vendor)
{
    /// <summary>什么都没匹配到。</summary>
    public static readonly TableMatch None = new(TableMatchKind.None, null, null);
}
