using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 工作空间（workspaces 表，持久化）。对标旧的内存模型 <see cref="Workspace"/>，
/// 这是新的 DB 版：作为「二次配置」层——挂在 <see cref="AgentId"/> 对应的 Agent 之上，
/// 描述该工作区是干什么的，并保存默认工具/技能/MCP 与权限档位；创建会话时被继承为默认值。
/// </summary>
public sealed class WorkspaceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>项目根目录（Agent 干活的边界）。</summary>
    public string Path { get; set; } = "";

    /// <summary>默认 Agent 蓝图（活引用；会话创建时用于叠加提示词与配置）。</summary>
    public string? AgentId { get; set; }

    /// <summary>工作区系统提示词：描述该工作区是干什么的（拼接在 Agent 提示词之后）。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>默认启用的工具 id 集合（二次配置，可对 Agent 默认收紧/放开）。</summary>
    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>默认启用的技能集合（含 loaded / allowed 状态）。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    /// <summary>默认启用的 MCP 服务器 id 集合（预留）。</summary>
    public List<string> McpServers { get; set; } = new();

    /// <summary>工作空间默认工具审批档位：新建会话时作为默认值。</summary>
    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
