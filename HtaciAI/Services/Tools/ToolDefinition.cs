using System.Collections.Generic;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Services.Tools;

/// <summary>工具的来源类型，决定执行时如何分派。</summary>
public enum ToolSource
{
    /// <summary>C# 进程内工具。</summary>
    Builtin,
    /// <summary>外部脚本工具（Python / Node.js）。</summary>
    Script,
    /// <summary>MCP 服务端提供的工具（<see cref="ToolDefinition.Target"/> 存所属服务 id）。</summary>
    Mcp,
    // 未来：Terminal
}

/// <summary>工具的权限等级：决定在「非自由」模式下是否需要用户确认。</summary>
public enum ToolDangerLevel
{
    /// <summary>安全：只读、无副作用（如 read / glob / grep / webfetch）。</summary>
    Safe,
    /// <summary>风险：会改动内容，通常需要前置读取（如 edit / write）。</summary>
    Risk,
    /// <summary>危险：可执行命令、影响系统（如 bash / 未指定等级的默认值）。</summary>
    Danger,
}

/// <summary>
/// 统一工具定义：既用于生成喂给 LLM 的 tools schema，也是执行路由的依据。
/// <see cref="Id"/> 为内部路由唯一键（库表主键）；<see cref="Name"/> 是发给 LLM 的调用名，二者分离。
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>内部路由唯一 id（库表主键）。</summary>
    public string Id { get; init; } = "";

    /// <summary>发给 LLM 的调用名（建议 snake_case）。</summary>
    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>入参 JSON Schema 的原始 JSON（根为 type=object）。</summary>
    public string InputSchemaJson { get; init; } = "{}";

    /// <summary>来源类型，决定执行分派。</summary>
    public ToolSource Source { get; init; } = ToolSource.Script;

    /// <summary>脚本工具：使用的运行时。</summary>
    public ScriptRuntimeKind Runtime { get; init; }

    /// <summary>脚本工具：目标脚本文件路径；MCP 工具：所属服务 id。</summary>
    public string? Target { get; init; }

    /// <summary>
    /// 所属工具集 id 集合（多对多）。只存用户自建的集合——「全部」「内置」是自动集合，不进这里；
    /// 为空表示不属于任何自建集合，但它依然出现在「全部」里。
    /// </summary>
    public IReadOnlyList<string> ToolsetIds { get; set; } = new List<string>();

    public bool Enabled { get; init; } = true;

    /// <summary>权限等级。未指定或库中缺失（旧工具）时默认 <see cref="ToolDangerLevel.Danger"/>。</summary>
    public ToolDangerLevel DangerLevel { get; init; } = ToolDangerLevel.Danger;

    /// <summary>
    /// 内部辅助工具（如 <c>invalid</c>）：可被解析/执行，但不进入 AI 工具列表、不显示在工具页。
    /// </summary>
    public bool IsInternal { get; init; }

    /// <summary>
    /// 该工具不参与权限审批。用于本身就是一次用户交互的工具（如 ask_user_question）：
    /// 在严格档位下再套一层审批框，会出现「先确认要不要提问、再回答问题」的荒谬两步。
    /// </summary>
    public bool SkipApproval { get; init; }

}
