using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Subagents;

/// <summary>
/// 把一次 <c>task</c> 派发跑成一个真实会话：新建（或复用）<see cref="SubagentSession"/>，
/// 借出它自己的 <see cref="ConversationRuntime"/>，发一条用户消息，等它跑完，取最后那条回复当报告。
///
/// <b>为什么复用 runtime 而不是自己写一套循环</b>：工具审批、失败重试、上下文压缩、
/// 残缺工具消息修复、落库顺序，全都已经在 <see cref="ConversationRuntime"/> + <see cref="ChatGateway"/> 里了。
/// 这里只做四件它们不该知道的事：解析 Agent 预设、按父会话收紧配置、<b>把中间过程挡在父上下文之外</b>、
/// 以及给报告加上会话 id 让主 Agent 能续用。
///
/// <b>刻意串行</b>：一次只跑一个子会话。并行会让多个子 Agent 同时写同一批文件，
/// 而「谁改了什么」在报告里看不出差别 —— 那种错误用户很难查。
/// </summary>
public static class SubagentRunner
{
    /// <summary>子会话默认的工具循环上限（模型能自己用 max_turns 抬，但抬不过 <see cref="HardMaxTurns"/>）。</summary>
    public const int DefaultMaxTurns = 20;

    /// <summary>上限。再大的数字只会让一轮派发没有边界感，长任务该由主 Agent 拆成多次派发。</summary>
    public const int HardMaxTurns = 100;

    /// <summary>报告正文的截断长度：主 Agent 的上下文很贵，报告该是结论而不是全文流水账。</summary>
    private const int MaxReportChars = 8000;

    public static async Task<SubagentTaskResult> RunAsync(
        SubagentRunContext ctx, SubagentTaskRequest request, CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(ctx, request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点了停止：交给网关按 interrupted 收尾，不在工具层吞掉
            throw;
        }
        catch (Exception ex)
        {
            // 子会话建不出来 / 写不进库这类问题不该让父会话整轮失败 ——
            // 主 Agent 拿到原因还能换一条路走（或直接自己做）。
            return SubagentTaskResult.Fail($"派发子 Agent 失败：{ex.Message}");
        }
    }

    private static async Task<SubagentTaskResult> RunCoreAsync(
        SubagentRunContext ctx, SubagentTaskRequest request, CancellationToken ct)
    {
        var agent = await ResolveAgentAsync(request.Agent);
        if (agent is null)
            return SubagentTaskResult.Fail(await UnknownAgentTextAsync(request.Agent));

        var (subagent, error) = string.IsNullOrWhiteSpace(request.SessionId)
            ? await CreateSessionAsync(ctx, agent, request)
            : await ReuseSessionAsync(ctx, agent, request);

        if (subagent is null)
            return SubagentTaskResult.Fail(error ?? "子会话准备失败");

        // 一次派发 = 一次「用户消息」。工具循环的上限挂在网关上一轮内生效，
        // 所以这里设完就必须在 finally 里还原 —— 不能把父会话的上限改坏。
        using var lease = ConversationRuntimeRegistry.Shared.Acquire(
            subagent.Id, () => new SubagentProfile(subagent));

        var runtime = lease.Runtime;
        runtime.Interaction = ctx.Interaction;
        await runtime.EnsureReadyAsync();

        var maxTurns = Math.Clamp(subagent.MaxTurns, 1, HardMaxTurns);
        var previousMaxRounds = runtime.Gateway.MaxToolRounds;
        runtime.Gateway.MaxToolRounds = maxTurns;

        ChatTurnResult? turn;
        try
        {
            turn = await runtime.SendAsync(new ConversationTurnRequest
            {
                // 工具用子会话行上的快照（派发时算好的交集），模型继承父会话这一轮的
                Settings = ctx.Settings with { ToolIds = subagent.EnabledToolIds },
                Text = request.Prompt,
                RequestPreview = request.Prompt,
            });
        }
        finally
        {
            runtime.Gateway.MaxToolRounds = previousMaxRounds;
        }

        if (turn is null)
            return SubagentTaskResult.Fail(
                $"子会话 {subagent.Id} 正在运行，本次派发没有开始。等它跑完再用同一个 session 追加要求。");

        var status = ResolveStatus(turn);

        await SubagentSessionRepository.UpdateProgressAsync(
            subagent.Id,
            subagent.TurnCount + 1,
            status,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var report = BuildReport(agent, subagent, turn, status, maxTurns);
        return new SubagentTaskResult(status == SubagentStatus.Ok, report, subagent.Id, status);
    }

    // ============================================================
    // 子会话：新建 / 续用
    // ============================================================

    private static async Task<(SubagentSession? Session, string? Error)> CreateSessionAsync(
        SubagentRunContext ctx, Agent agent, SubagentTaskRequest request)
    {
        var session = new SubagentSession
        {
            AgentId = agent.Id,
            AgentName = agent.Name,
            ParentSessionId = ctx.ParentSessionId,
            Title = BuildTitle(agent, request.Prompt),
            Model = ctx.Settings.Model.ModelName,
            Thinking = (int)ctx.Settings.Thinking,
            SystemPrompt = agent.SystemPrompt,
            BaseDirectory = ctx.BaseDirectory,
            // 工具与 MCP 只继承父会话本轮真正可用的那些，再按 Agent 预设收窄；
            // 技能只继承 allowed —— loaded 的 SKILL.md 全文属于父会话，抄进来等于 token 翻倍。
            EnabledToolIds = ResolveToolIds(ctx, agent),
            EnabledSkills = ResolveSkills(ctx, agent),
            McpServers = ResolveMcpServers(ctx, agent),
            // 权限档位原样继承：用户已经在父会话里表达过一次授权意图，子 Agent 不该重新问一遍。
            ToolPermissionMode = ctx.PermissionMode,
            MaxTurns = ResolveMaxTurns(request.MaxTurns),
            Status = SubagentStatus.Running,
        };

        await SubagentSessionRepository.CreateAsync(session);
        return (session, null);
    }

    private static async Task<(SubagentSession? Session, string? Error)> ReuseSessionAsync(
        SubagentRunContext ctx, Agent agent, SubagentTaskRequest request)
    {
        var id = request.SessionId!.Trim();
        var existing = await SubagentSessionRepository.GetAsync(id);
        if (existing is null || existing.IsDeleted)
            return (null, $"没有可用的子会话 {id}（可能已被删除）。不传 session 参数即可新开一个。");

        // 换了 Agent 就不许复用：那意味着上下文说的是另一个角色的判断，
        // 硬接上去只会让新 Agent 把上任的假设当成既定事实。
        if (!string.Equals(existing.AgentId, agent.Id, StringComparison.Ordinal))
            return (null,
                $"子会话 {id} 属于子 Agent「{existing.AgentName}」，不能交给「{agent.Name}」续用。要换 Agent 请新开一个。");

        if (!string.Equals(existing.ParentSessionId, ctx.ParentSessionId, StringComparison.Ordinal))
            return (null, $"子会话 {id} 不是当前会话派发出来的，不能续用。");

        // 续用时按本轮父会话重新收紧一次工具与 MCP：父会话可能刚关掉某些工具，
        // 沿用旧快照等于给子会话开了一扇用户已经关上的门。只收紧、不放开。
        existing.EnabledToolIds = ResolveToolIds(ctx, agent, existing.EnabledToolIds);
        existing.McpServers = ResolveMcpServers(ctx, agent, existing.McpServers);
        existing.ToolPermissionMode = ctx.PermissionMode;
        existing.Model = ctx.Settings.Model.ModelName;
        if (request.MaxTurns is { } turns) existing.MaxTurns = ResolveMaxTurns(turns);
        existing.Status = SubagentStatus.Running;

        await SubagentSessionRepository.UpdateAsync(existing);
        return (existing, null);
    }

    // ============================================================
    // 配置：父会话 ∩ Agent 预设
    // ============================================================

    /// <summary>
    /// 工具 = 父会话本轮可用的 ∩ Agent 预设；预设为空表示全继承。
    /// <b>永远剔掉 task 自己</b>：子 Agent 拿不到派发能力，深度天然是 1。
    /// </summary>
    private static List<string> ResolveToolIds(
        SubagentRunContext ctx, Agent agent, IReadOnlyList<string>? previous = null)
    {
        var parent = ctx.ParentToolIds
            .Where(id => !IsTaskTool(id))
            .ToHashSet(StringComparer.Ordinal);

        // 续用：先与上一次的快照取交集，再把父会话这轮新放开的部分加回来没有意义 ——
        // 子会话只在「派发它的父会话允许什么」这个范围内活动，所以这里只收紧不放开。
        if (previous is not null)
            parent.IntersectWith(previous.Where(id => !IsTaskTool(id)));

        if (agent.EnabledToolIds.Count == 0) return parent.ToList();

        return parent.Intersect(agent.EnabledToolIds, StringComparer.Ordinal).ToList();
    }

    private static bool IsTaskTool(string toolId)
        => string.Equals(ToolRegistry.Instance.ResolveById(toolId)?.Name, BuiltinTools.TaskToolName, StringComparison.Ordinal);

    /// <summary>技能 = 父会话的 allowed ∩ Agent 预设；loaded 一律不继承（全文注入太贵，且它属于父会话）。</summary>
    private static List<SessionSkill> ResolveSkills(SubagentRunContext ctx, Agent agent)
    {
        var allowed = ctx.ParentSkills
            .Where(s => s.Status == "allowed")
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (agent.EnabledSkills.Count > 0)
            allowed.IntersectWith(agent.EnabledSkills.Select(s => s.Id));

        return allowed.Select(id => new SessionSkill { Id = id, Status = "allowed" }).ToList();
    }

    private static List<string> ResolveMcpServers(
        SubagentRunContext ctx, Agent agent, IReadOnlyList<string>? previous = null)
    {
        var parent = ctx.ParentMcpServers.ToHashSet(StringComparer.Ordinal);
        if (previous is not null) parent.IntersectWith(previous);

        if (agent.McpServers.Count == 0) return parent.ToList();
        return parent.Intersect(agent.McpServers, StringComparer.Ordinal).ToList();
    }

    private static int ResolveMaxTurns(int? requested)
        => Math.Clamp(requested ?? DefaultMaxTurns, 1, HardMaxTurns);

    private static string BuildTitle(Agent agent, string prompt)
    {
        var firstLine = prompt.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";

        if (firstLine.Length > 24) firstLine = firstLine[..24] + "…";
        return string.IsNullOrEmpty(firstLine) ? agent.Name : $"{agent.Name}·{firstLine}";
    }

    // ============================================================
    // 结果
    // ============================================================

    /// <summary>
    /// 成功与否看的是「最后一条回复还带不带工具调用」。
    /// 网关在工具循环撞上限时是<b>正常返回</b>的（不抛异常），所以只能从这条消息的形状上看出来。
    /// </summary>
    private static string ResolveStatus(ChatTurnResult turn)
    {
        if (!turn.Success) return SubagentStatus.Failed;

        var truncated = turn.FinalAssistant is { } final
                        && ToolCallJson.Deserialize(final.Metadata).Count > 0;
        return truncated ? SubagentStatus.MaxTurns : SubagentStatus.Ok;
    }

    private static string BuildReport(
        Agent agent, SubagentSession session, ChatTurnResult turn, string status, int maxTurns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【子 Agent：{agent.Name}】会话 {session.Id}，状态 {status}，工具循环上限 {maxTurns}。");

        if (status == SubagentStatus.MaxTurns)
            sb.AppendLine("注意：撞上工具循环上限就停了，下面的报告可能不完整。" +
                          "续用同一个会话（传 session）说清要继续哪一步即可。");

        if (status == SubagentStatus.Failed)
            sb.AppendLine($"这一轮失败了：{turn.Error ?? "未知错误"}。下面可能还有部分输出。");

        sb.AppendLine("---");

        var body = turn.FinalAssistant?.Content;
        if (string.IsNullOrWhiteSpace(body))
        {
            if (status == SubagentStatus.Ok) sb.AppendLine("（子 Agent 没有给出正文报告。）");
        }
        else
        {
            sb.AppendLine(body.Length > MaxReportChars
                ? body[..MaxReportChars] + "\n…（报告过长，已截断）"
                : body);
        }

        sb.AppendLine("---");
        sb.AppendLine($"需要继续追问同一个子 Agent 时，传 agent=\"{agent.Name}\"、session=\"{session.Id}\"。");
        return sb.ToString();
    }

    // ============================================================
    // Agent 预设
    // ============================================================

    private static async Task<Agent?> ResolveAgentAsync(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;

        var agents = await AgentRepository.GetAllAsync();
        var key = nameOrId.Trim();

        return agents.FirstOrDefault(a => string.Equals(a.Id, key, StringComparison.Ordinal))
               ?? agents.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.Ordinal));
    }

    private static async Task<string> UnknownAgentTextAsync(string requested)
    {
        var names = (await AgentRepository.GetAllAsync())
            .Where(a => a.IsEnabled)
            .Select(a => a.Name)
            .ToList();

        return names.Count == 0
            ? $"没有名为「{requested}」的子 Agent，而且当前一个都没配置。请用户先在「子Agent」页建一个。"
            : $"没有名为「{requested}」的子 Agent。可用的是：{string.Join("、", names)}。";
    }
}
