using System;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 向宿主的工具注册表登记工具（需要声明 <see cref="PluginCapabilities.ToolsProvide"/>）。
///
/// 登记之后：
/// <list type="bullet">
///   <item>模型在「工具列表」里能看见它（会话启用了这个工具时）；</item>
///   <item>用户在「工具」页能看见它，并能调它的危险等级（<b>与内置工具走同一套审批</b>）；</item>
///   <item>插件被禁用时自动注销，会话里选着的 id 也自然解析不到了。</item>
/// </list>
///
/// <b>能返回什么、不能返回什么</b>：工具结果就是一段文本（会作为 <c>tool</c> 消息进上下文），
/// 所以内容要当作「给模型看的输出」来写 —— 长输出请自己截断，别把整份文件塞回去。
///
/// <b>超时</b>：单次执行默认 5 分钟封顶（与内置工具一致），超时与「用户点停止」都会把
/// 如实的结果写回给模型，不会卡住整轮对话。既然取消令牌会传进来，<b>请把它传下去</b>。
///
/// <b>线程</b>：<see cref="PluginToolHandler"/> 在后台线程上调用，<b>不要</b>在里面直接改控件。
/// </summary>
public interface IToolRegistryApi
{
    /// <summary>
    /// 登记一个工具。返回的 <see cref="IDisposable"/> 注销它（插件被禁用时宿主也会注销）。
    /// </summary>
    /// <param name="definition">工具名、说明、入参 schema、危险等级。</param>
    /// <param name="execute">实际执行体。</param>
    IDisposable Register(PluginToolDefinition definition, PluginToolHandler execute);
}

/// <summary>
/// 插件的工具执行体。返回 <see cref="PluginToolResult"/>；
/// <b>抛异常也可以</b>（宿主会把它写成一条失败结果回给模型，不会让进程出事）。
/// </summary>
public delegate Task<PluginToolResult> PluginToolHandler(PluginToolCall call, CancellationToken ct);

/// <summary>
/// 一个插件工具的定义。<b>宿主会对这些字段做校验</b>：名字不合规、schema 不是合法 JSON、
/// 与已有工具重名，都会被拒绝并记日志（而不是悄悄登记一个调不到的工具）。
/// </summary>
public sealed class PluginToolDefinition
{
    /// <summary>
    /// 调用名（发给模型的名字）。规则：<c>^[a-z][a-z0-9_]{1,63}$</c>。
    /// 与内置工具或别的插件重名会被拒绝 —— 名字是模型唯一的调用入口，重名等于随机命中一个。
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>给模型看的说明。<b>它决定模型会不会用这个工具</b>，请写清「做什么、什么时候用」。</summary>
    public string Description { get; init; } = "";

    /// <summary>入参 JSON Schema（根必须是 <c>type=object</c>）。无参数时给 <c>{"type":"object","properties":{}}</c>。</summary>
    public string InputSchemaJson { get; init; } = "{\"type\":\"object\",\"properties\":{}}";

    /// <summary>
    /// 危险等级。默认 <see cref="PluginToolDangerLevel.Safe"/>：
    /// 让人自己选「这个插件有破坏性」比默认把没声明的工具当危险、每步都要确认要友好。
    /// 用户可以在工具页把它改高，宿主的审批闸门按改后的等级走。
    /// </summary>
    public PluginToolDangerLevel DangerLevel { get; init; } = PluginToolDangerLevel.Safe;
}

/// <summary>工具危险等级。语义与宿主界面上的徽标一致。</summary>
public enum PluginToolDangerLevel
{
    /// <summary>安全：只读、无副作用（不会触发审批，除非用户把档位设成严格）。</summary>
    Safe,

    /// <summary>风险：会改动内容。</summary>
    Risk,

    /// <summary>危险：执行命令、影响系统。</summary>
    Danger,
}

/// <summary>一次工具调用的入参。<b>按需解析</b>，宿主不替你解析（这样你有完全的控制权，包括怎么处理坏 JSON）。</summary>
public sealed class PluginToolCall
{
    /// <summary>模型给的原始参数 JSON 字符串（可能是空串，也可能是坏 JSON）。</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>被调用的工具名。</summary>
    public required string Name { get; init; }
}

/// <summary>工具执行结果。</summary>
public sealed class PluginToolResult
{
    /// <summary>是否成功。<c>false</c> 时 <see cref="Content"/> 应当是「为什么失败」。</summary>
    public bool Success { get; init; } = true;

    /// <summary>结果文本（进上下文）。</summary>
    public string Content { get; init; } = "";

    public static PluginToolResult Ok(string content) => new() { Success = true, Content = content };

    public static PluginToolResult Fail(string reason) => new() { Success = false, Content = reason };
}
