using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// <c>plugin.json</c> 的内存形态。插件目录下必须有它，宿主靠它判断「这是个插件」。
///
/// 字段全部用显式 <see cref="JsonPropertyNameAttribute"/> 绑定，而不是靠宿主的命名策略：
/// 这份 DTO 是跨项目的公开契约，插件作者可能用任何方式生成这个文件，
/// 不能把「宿主恰好用了 camelCase 策略」当成协议的一部分。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>
    /// 插件唯一标识。<b>它就是目录名</b>，也是状态文件与私有数据目录的键。
    /// 允许字母、数字、<c>.</c>、<c>_</c>、<c>-</c>，不接受路径分隔符。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>给人看的名字。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>要求的最低宿主版本（如 <c>1.0.0</c>）。高于当前版本时拒绝加载。</summary>
    [JsonPropertyName("minHostVersion")]
    public string MinHostVersion { get; set; } = "";

    /// <summary>入口程序集文件名（相对插件目录），如 <c>HtaciAI.Pet.dll</c>。</summary>
    [JsonPropertyName("entry")]
    public string Entry { get; set; } = "";

    /// <summary>
    /// 实现 <see cref="IPlugin"/> 的类型全名，如 <c>HtaciAI.Pet.PetPlugin</c>。
    /// 写全名而不是靠「找唯一实现」：一个程序集里可以有多份辅助类型，猜错比报错难查。
    /// </summary>
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "";

    /// <summary>
    /// 声明要用哪些能力（见 <see cref="PluginCapabilities"/>）。宿主据此注入服务，
    /// 也据此在设置页把这些能力摊给用户看 —— **声明是给用户看的，不是安全边界**。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();
}

/// <summary>
/// 能力标识常量。字符串用 <c>点分</c> 形式，好处是新增能力不会与已有前缀冲突。
/// </summary>
public static class PluginCapabilities
{
    /// <summary>参与上下文构建（往 system 提示词追加内容）。</summary>
    public const string PromptContribute = "prompt.contribute";

    /// <summary>观察会话事件（流式增量、工具调用、轮次完成）。</summary>
    public const string ChatObserve = "chat.observe";

    /// <summary>主动发起一轮、取消、改权限档位。</summary>
    public const string ChatControl = "chat.control";

    /// <summary>创建独立窗口。</summary>
    public const string UiWindow = "ui.window";

    /// <summary>往宿主页面的插槽里插入控件、新标签页入口卡片。</summary>
    public const string UiSlot = "ui.slot";

    /// <summary>向工具注册表注册工具。</summary>
    public const string ToolsProvide = "tools.provide";

    /// <summary>已知能力的全集，供设置页把「声明了什么」翻译成人话。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PromptContribute, ChatObserve, ChatControl, UiWindow, UiSlot, ToolsProvide,
    };

    /// <summary>把能力标识翻译成中文短语；不认识的返回 null（设置页原样显示标识）。</summary>
    public static string? Describe(string capability) => capability switch
    {
        PromptContribute => "参与上下文构建",
        ChatObserve => "观察对话内容",
        ChatControl => "主动发起对话",
        UiWindow => "创建自己的窗口",
        UiSlot => "在界面中插入内容",
        ToolsProvide => "提供工具",
        _ => null,
    };
}
