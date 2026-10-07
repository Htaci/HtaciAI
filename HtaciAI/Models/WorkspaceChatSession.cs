using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 工作区会话（workspace_sessions 表，持久化）。对标旧的内存模型 <see cref="WorkspaceSession"/>。
/// 创建时继承 Agent + 工作空间的「有效配置」，构建请求时本会话是唯一事实来源（可再单独调整工具/技能/MCP/权限）。
/// 消息复用 <see cref="chat_message"/> 表，session_id 即本会话 Id。
/// </summary>
public sealed class WorkspaceChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WorkspaceId { get; set; } = "";

    /// <summary>溯源用的 Agent 引用（提示词叠加来源；配置已按快照落到本会话）。</summary>
    public string? AgentId { get; set; }

    public string Title { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>
    /// 思考模式（<see cref="HtaciAI.Services.ThinkingMode"/> 的整型值）。
    /// 默认 Default(none)，理由同 <see cref="ChatSession.Thinking"/>。
    /// </summary>
    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;

    /// <summary>会话级追加提示词：描述本次会话是干什么的（拼接在 Agent / 工作区提示词之后，最具体）。</summary>
    public string SystemPrompt { get; set; } = "";

    public List<string> EnabledToolIds { get; set; } = new();

    public List<SessionSkill> EnabledSkills { get; set; } = new();

    public List<string> McpServers { get; set; } = new();

    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    /// <summary>输入框草稿（防意外丢失）。</summary>
    public string? Draft { get; set; }

    public long? LastMessageAt { get; set; }

    public bool IsDeleted { get; set; }

    public long? DeletedAt { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
