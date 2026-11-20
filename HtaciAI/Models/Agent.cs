using System;
using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 智能体定义（agents 表）：一份可复用的「AI 该怎么干活」配置，包含系统提示词、
/// 默认启用的工具/技能/MCP，以及可选的默认模型、知识库、权限档位、工作目录。
///
/// <b>两类对象共用这张表</b>，由 <see cref="Kind"/> 区分：
/// <list type="bullet">
///   <item><b>role</b>（角色）：会话开始时选中，决定这个会话默认带哪些工具、用哪个模型。</item>
///   <item><b>subagent</b>（子 Agent）：由主 Agent 用 task 工具按名字派发，只回一份报告。</item>
/// </list>
/// 两者的字段完全一样，差别只在「谁选中它」—— 角色由用户选，子 Agent 由模型派发。
/// 早期这张表只服务子 Agent，现在两个概念合一，新增对象请显式给 <see cref="Kind"/>。
/// </summary>
public sealed class Agent
{
    /// <summary>角色：会话级选择，决定会话默认的工具/模型/知识库。</summary>
    public const string KindRole = "role";

    /// <summary>子 Agent：由 task 工具派发出去干一件完整的活。</summary>
    public const string KindSubagent = "subagent";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>类别：<see cref="KindRole"/> 或 <see cref="KindSubagent"/>。默认子 Agent（历史数据都是它）。</summary>
    public string Kind { get; set; } = KindSubagent;

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>核心提示词：描述 AI 的角色与行为方式（不可被工作区/会话复写，仅叠加）。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>默认启用的工具 id 集合（会话创建时继承，之后可由会话二次调整/受限）。</summary>
    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>默认启用的技能集合（含 loaded / allowed 状态），会话创建时继承。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    /// <summary>默认启用的 MCP 服务器 id 集合（会话创建时继承）。</summary>
    public List<string> McpServers { get; set; } = new();

    /// <summary>默认模型名；空则用全局默认。仅角色使用，子 Agent 继承父会话的模型。</summary>
    public string Model { get; set; } = "";

    /// <summary>默认知识库 id；空则无。</summary>
    public string KnowledgeBaseId { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
