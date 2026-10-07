using System.Collections.Generic;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 会话级状态投影：把「普通对话会话」与「工作空间会话」两种模型收敛成视图能用的同一种形状。
///
/// 视图只读写本对象，由 <see cref="IConversationProfile"/> 的实现负责与各自模型之间来回映射，
/// 从而避免视图里出现 `is WorkspaceProfile` 这类类型分支。
/// </summary>
public sealed class ConversationState
{
    /// <summary>会话 id（两种模型共用同一套 id 空间，消息表 <c>chat_message.session_id</c> 是多态关联）。</summary>
    public string SessionId { get; init; } = "";

    public string Title { get; set; } = "";

    /// <summary>模型调用 id（如 deepseek-v4-flash）。</summary>
    public string Model { get; set; } = "";

    /// <summary>思考模式（ThinkingMode 的整型值）。</summary>
    public int Thinking { get; set; }

    public string? SystemPrompt { get; set; }

    /// <summary>会话启用的技能（含 allowed / loaded 状态）。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    /// <summary>会话启用的工具 id。</summary>
    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>会话启用的 MCP 服务 id。发送前会确保这些服务已连接，其工具并入请求。</summary>
    public List<string> McpServers { get; set; } = new();

    public PermissionMode PermissionMode { get; set; } = PermissionMode.Normal;

    public long? LastMessageAt { get; set; }
}
