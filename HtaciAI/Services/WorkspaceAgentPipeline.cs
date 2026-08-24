using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HtaciAI.Models;
using HtaciAI.Services.Skills;

namespace HtaciAI.Services;

/// <summary>
/// 工作区智能体管道：把「Agent → 工作空间 → 会话」三层配置串起来。
///  - <see cref="SeedWorkspaceFromAgent"/>：新建工作空间时，从 Agent 拉取默认工具/技能/MCP；
///  - <see cref="CreateSessionDefaults"/>：新建会话时，计算「Agent ⊕ 工作空间」的有效配置作为默认；
///  - <see cref="BuildSystemPrompt"/>：按三层顺序组装请求 system 提示词。
/// 会话创建后，构建请求只读会话本身（快照模型），从而支持本会话单独收紧/放开工具、技能、MCP 与权限。
/// </summary>
public static class WorkspaceAgentPipeline
{
    /// <summary>把 Agent 的默认工具/技能/MCP 作为种子写入工作空间（仅当工作空间该项为空时，保留用户二次配置）。</summary>
    public static void SeedWorkspaceFromAgent(WorkspaceConfig ws, Agent agent)
    {
        if (ws.EnabledToolIds.Count == 0)
            ws.EnabledToolIds = agent.EnabledToolIds.ToList();
        if (ws.EnabledSkills.Count == 0)
            ws.EnabledSkills = agent.EnabledSkills.Select(CloneSkill).ToList();
        if (ws.McpServers.Count == 0)
            ws.McpServers = agent.McpServers.ToList();
    }

    /// <summary>
    /// 计算新建会话的默认配置：工作空间已有的工具/技能/MCP 优先（二次配置），否则回落到 Agent 默认。
    /// 权限档位取工作空间默认值（Agent 本身不持有权限）。
    /// </summary>
    public static WorkspaceChatSession CreateSessionDefaults(WorkspaceConfig ws, Agent? agent)
    {
        var tools = ws.EnabledToolIds.Count > 0 ? ws.EnabledToolIds : (agent?.EnabledToolIds ?? new());
        var skills = ws.EnabledSkills.Count > 0 ? ws.EnabledSkills : (agent?.EnabledSkills ?? new());
        var mcp = ws.McpServers.Count > 0 ? ws.McpServers : (agent?.McpServers ?? new());

        return new WorkspaceChatSession
        {
            WorkspaceId = ws.Id,
            AgentId = ws.AgentId ?? agent?.Id,
            Title = "新会话",
            EnabledToolIds = tools.ToList(),
            EnabledSkills = skills.Select(CloneSkill).ToList(),
            McpServers = mcp.ToList(),
            ToolPermissionMode = ws.ToolPermissionMode,
            // Model / Thinking 由会话创建方后续按需设置
        };
    }

    /// <summary>
    /// 组装请求 system 提示词，顺序：静态身份前缀（含系统环境/模型 id/工作目录）→ Agent → 工作空间 → 会话追加 → 技能 → MCP。
    /// </summary>
    public static string BuildSystemPrompt(
        Agent? agent,
        WorkspaceConfig? workspace,
        WorkspaceChatSession session,
        string modelId,
        string workspaceDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是 HtaciAI（Agent），一款由 赫塔奇智能科技有限公司 研发的智能AI助手/智能体，可以完成办公协助、项目开发、答疑解惑、代理操作等很多工作。");

        sb.AppendLine("遵循原则：\r\n\r\n" +
            "严格遵守中国法律法规，拒绝回答涉及色情、暴力、政治敏感、违法犯罪等不安全内容，履行 AI 安全规范。\r\n\r\n" +
            "默认语气风格：简洁、直接、切题。除非用户要求，否则不要使用 emoji。" +
            "如无用户要求，尽量用 4 行以内的文字回答，不要加无关的前言后语，如果用户提示词中有明确其他语气设定，或用户喜欢其他语气风格，则按照用户要求。\r\n\r\n" +
            "主动程度：只在被要求时主动，不要擅自行动吓到用户，如果不确定用户是否有让开始行动时，则询问用户是否要开始，直到用户明确指示开始。\r\n\r\n" +
            "遇到敏感问题时，统一回复：“我无法回答该问题，请换个问题试试吧。”");

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {modelId}，当前工作目录：{(string.IsNullOrWhiteSpace(workspaceDir) ? "/" : workspaceDir)}");

        if (agent is not null && !string.IsNullOrWhiteSpace(agent.SystemPrompt))
            sb.AppendLine(agent.SystemPrompt);

        if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.SystemPrompt))
            sb.AppendLine(workspace.SystemPrompt);

        if (!string.IsNullOrWhiteSpace(session.SystemPrompt))
            sb.AppendLine(session.SystemPrompt);

        AppendSkills(sb, session.EnabledSkills);

        if (session.McpServers.Count > 0)
            sb.AppendLine($"已连接 MCP 服务器：{string.Join(", ", session.McpServers)}");

        return sb.ToString();
    }

    // ---- 技能注入（与 ChatView.BuildSystemPrompt 相同的 allowed / loaded 两组） ----

    private static void AppendSkills(StringBuilder sb, List<SessionSkill> enabled)
    {
        var loadedIds = enabled.Where(s => s.Status == "loaded").Select(s => s.Id).ToHashSet();
        var allowedIds = enabled.Where(s => s.Status == "allowed").Select(s => s.Id).ToHashSet();

        if (allowedIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下是可用技能（仅元数据，完整内容按需加载）：");
            foreach (var skill in SkillRegistry.Instance.GetByIds(allowedIds))
                sb.AppendLine($"{skill.Name}: {skill.Description}");
        }

        foreach (var skill in SkillRegistry.Instance.GetByIds(loadedIds))
        {
            sb.AppendLine();
            sb.AppendLine($"<skill name=\"{skill.Name}\">");
            sb.AppendLine(skill.Body);
            sb.AppendLine("</skill>");
        }
    }

    private static SessionSkill CloneSkill(SessionSkill s) => new() { Id = s.Id, Status = s.Status };
}
