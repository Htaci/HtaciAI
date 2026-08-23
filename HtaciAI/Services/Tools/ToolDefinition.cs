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
    // 未来：Mcp、Terminal
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

    /// <summary>脚本工具：目标脚本文件路径（未来终端工具存命令模板）。</summary>
    public string? Target { get; init; }

    /// <summary>所属工具集 id 集合（多对多）。为空时视为默认集。</summary>
    public IReadOnlyList<string> ToolsetIds { get; set; } = new List<string>();

    public bool Enabled { get; init; } = true;

    /// <summary>权限等级。未指定或库中缺失（旧工具）时默认 <see cref="ToolDangerLevel.Danger"/>。</summary>
    public ToolDangerLevel DangerLevel { get; init; } = ToolDangerLevel.Danger;

    /// <summary>
    /// 内部辅助工具（如 <c>invalid</c>）：可被解析/执行，但不进入 AI 工具列表、不显示在工具页。
    /// </summary>
    public bool IsInternal { get; init; }

    /// <summary>有效所属集合：未显式指定时归入默认集。</summary>
    public IReadOnlyList<string> EffectiveToolsetIds
        => ToolsetIds.Count > 0 ? ToolsetIds : new[] { Toolset.Default.Id };
}
