using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 定时任务的运行档案：一次触发对应一个这样的会话（一行 <see cref="TaskRun"/>）。
///
/// 与 <see cref="SubagentProfile"/> 的差别只有两处：会话行来自 <c>task_runs</c>，
/// 以及 system 提示词换成「无人值守」那一套。
///
/// <b>「无人值守」是整个功能最关键的一段提示词</b>：它跑在闹钟上，没有人在旁边。
/// 会话里默认那套「不确定就问」在这里是错的 —— 问出去没人答，等同卡死等超时。
/// 所以要求它：按最合理的假设推进、把假设写进报告、需要审批的调用直接当拒绝处理。
///
/// 消息、上下文压缩、工具调用修复、持久化全部复用现有那套（<c>chat_message</c> + <c>ConversationRuntime</c>）。
/// </summary>
public sealed class TaskProfile : IConversationProfile
{
    private readonly string _runId;
    private TaskRun _run;

    public TaskProfile(TaskRun run)
    {
        _runId = run.Id;
        _run = run;
        State = new ConversationState { SessionId = run.Id };
        Pull();
    }

    public ConversationState State { get; }

    /// <summary>通知标题的前缀位置：显示成「定时任务 · 每日备份」，与普通会话区分开。</summary>
    public string? WorkspaceName => "定时任务";

    /// <summary>工具基准目录取触发时的快照；没有才回落到进程当前目录。</summary>
    public string BaseDirectory
        => string.IsNullOrWhiteSpace(_run.BaseDirectory) ? Environment.CurrentDirectory : _run.BaseDirectory;

    /// <summary>定时任务的输入来自闹钟，不由用户直接敲，所以没有草稿。</summary>
    public IDraftStore? Draft => null;

    public async Task LoadAsync()
    {
        // 读不到就保留内存里这份（可能是刚建出来还没落库），别把配置清空
        _run = await TaskRepository.GetRunAsync(_runId) ?? _run;
        Pull();
    }

    public async Task SaveAsync()
    {
        Push();
        await TaskRepository.UpdateRunAsync(_run);
    }

    public string BuildSystemPrompt(string modelId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是 HtaciAI（Agent），这次是由「定时任务」按预定的时间自动发起的，没有用户在旁边看着。");

        sb.AppendLine("""
            工作方式：
            - 不要反问、不要讨价还价、不要调用 ask_user_question：没人会回答。确有不确定，就按你认为最合理的假设推进，并在结尾写明这个假设。
            - 需要用户批准的调用会被当成「被拒绝」处理。遇到就换一种不需要审批的做法；换不了就停下，把卡在哪一步写清楚。
            - 不要派发子 Agent、不要创建工具或技能定义，也不要修改定时任务本身。
            - 严格遵守中国法律法规，拒绝涉及色情、暴力、政治敏感、违法犯罪等不安全内容。
            - 结论先行，不要复述过程、不要写长篇推理。
            """);

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {modelId}，当前工作目录：{BaseDirectory}");

        if (!string.IsNullOrWhiteSpace(State.SystemPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("#========任务说明========#");
            sb.AppendLine(State.SystemPrompt);
        }

        AppendSkills(sb);

        if (State.McpServers.Count > 0)
            sb.AppendLine($"已连接 MCP 服务器：{string.Join(", ", State.McpServers)}");

        sb.AppendLine("""
            <输出要求>
            最后一条回复就是这次运行的结果，用下面三段，不要加别的段落：
            - 结论：一句话说清这次做成了什么、或者没做成的原因。
            - 依据 / 产出：关键数据、文件路径或生成的内容。
            - 假设 / 风险：你不得不做的假设、下次需要人看一眼的地方；没有写“无”。
            </输出要求>
            """);

        return sb.ToString();
    }

    private void AppendSkills(StringBuilder sb)
    {
        var allowedIds = State.EnabledSkills.Where(s => s.Status == "allowed").Select(s => s.Id).ToHashSet();
        var loadedIds = State.EnabledSkills.Where(s => s.Status == "loaded").Select(s => s.Id).ToHashSet();

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

    // ---- 模型 ↔ 状态映射 ----

    private void Pull()
    {
        State.Title = _run.Title;
        State.Model = _run.Model;
        State.Thinking = _run.Thinking;
        State.SystemPrompt = _run.SystemPrompt;
        State.EnabledSkills = _run.EnabledSkills;
        State.EnabledToolIds = _run.EnabledToolIds;
        State.McpServers = _run.McpServers;
        State.PermissionMode = _run.ToolPermissionMode;
        State.LastMessageAt = _run.LastMessageAt;
    }

    /// <summary>
    /// 只回写本档案负责的字段。状态 / 轮次 / 摘要 / 结束时刻由执行方维护（<see cref="TaskRepository.FinishRunAsync"/>），
    /// 这里<b>不</b>覆盖 —— 否则一轮对话收尾就会把已经写好的 ok 又改回 running。
    /// </summary>
    private void Push()
    {
        _run.Title = State.Title;
        _run.Model = State.Model;
        _run.Thinking = State.Thinking;
        _run.SystemPrompt = State.SystemPrompt ?? "";
        _run.EnabledToolIds = State.EnabledToolIds;
        _run.EnabledSkills = State.EnabledSkills;
        _run.McpServers = State.McpServers;
        _run.ToolPermissionMode = State.PermissionMode;
        _run.LastMessageAt = State.LastMessageAt;
    }
}
