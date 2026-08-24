using System;
using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 自定义智能体蓝图（agents 表）：一份可复用的「AI 该怎么干活」配置，
/// 供工作区/会话继承。包含系统提示词、默认启用的工具/技能/MCP。
/// 权限档位不在 Agent 上，而是由工作空间持有（创建会话时再继承）。
/// </summary>
public sealed class Agent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>核心提示词：描述 AI 的角色与行为方式（不可被工作区/会话复写，仅叠加）。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>默认启用的工具 id 集合（会话创建时继承，之后可由会话二次调整/受限）。</summary>
    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>默认启用的技能集合（含 loaded / allowed 状态），会话创建时继承。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    /// <summary>默认启用的 MCP 服务器 id 集合（预留，会话创建时继承）。</summary>
    public List<string> McpServers { get; set; } = new();

    public bool IsEnabled { get; set; } = true;

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
