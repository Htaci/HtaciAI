using System.Collections.Generic;
using HtaciAI.Models;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Subagents;

/// <summary>主 Agent 通过 <c>task</c> 工具提出的一次派发。</summary>
/// <param name="Agent">子 Agent 的名称或 id。</param>
/// <param name="Prompt">任务描述（子会话的第一条用户消息；续用时就追加在后面）。</param>
/// <param name="SessionId">要续用的子会话 id；<b>null = 新开</b>。</param>
/// <param name="MaxTurns">本轮工具循环上限；null = 用子会话行里的值。</param>
public sealed record SubagentTaskRequest(
    string Agent,
    string Prompt,
    string? SessionId = null,
    int? MaxTurns = null);

/// <summary>派发结果。<see cref="Text"/> 是回给主 Agent 的<b>全部</b>内容：报告 + 会话 id。</summary>
public sealed record SubagentTaskResult(
    bool Success,
    string Text,
    string? SessionId = null,
    string? Status = null)
{
    public static SubagentTaskResult Fail(string text) => new(false, text);
}

/// <summary>
/// 派发时可继承的父会话信息。
///
/// <b>为什么是快照而不是指向 runtime 的引用</b>：子任务跑起来之后，父会话可能已经被关掉、
/// 甚至换成了另一个档案；子会话只该按「派发那一刻父会话是什么样」来继承。
/// </summary>
public sealed class SubagentRunContext
{
    public required string ParentSessionId { get; init; }

    /// <summary>父会话的工具基准目录，落到子会话行上（子会话可能比父界面活得久）。</summary>
    public required string BaseDirectory { get; init; }

    /// <summary>父会话本轮实际发给模型的工具 id（已解析、已含 MCP），不是存档里的原始列表。</summary>
    public required IReadOnlyList<string> ParentToolIds { get; init; }

    public required List<SessionSkill> ParentSkills { get; init; }

    public required List<string> ParentMcpServers { get; init; }

    public required PermissionMode PermissionMode { get; init; }

    /// <summary>本轮模型与客户端：子 Agent 继承父会话的模型。</summary>
    public required TurnSettings Settings { get; init; }

    /// <summary>审批与提问仍然交给父会话的宿主 —— 这是「继承权限」唯一说得通的落点。</summary>
    public IInteractionHost? Interaction { get; init; }
}
