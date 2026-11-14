using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 子 Agent 会话档案：一次 <c>task</c> 派发对应一个这样的会话。
///
/// 它与 <see cref="StandaloneProfile"/> 在「怎么读写会话状态」上几乎一样，差别只有三处：
/// 会话行来自 <c>subagent_sessions</c>、工具基准目录取父会话的快照、system 提示词是
/// 「基础身份 + Agent 角色 + 派发规则 + 输出格式」。
/// <b>刻意不抽公共基类</b>：抽出来会变成三层继承，而这三处差异恰恰是读代码时最需要一眼看到的东西。
///
/// 消息、上下文压缩、工具调用修复、持久化全部复用现有那套（<c>chat_message</c> + <c>ConversationRuntime</c>），
/// 本类一行业务逻辑都不碰。
/// </summary>
public sealed class SubagentProfile : IConversationProfile
{
    private readonly string _sessionId;
    private SubagentSession _session;

    public SubagentProfile(SubagentSession session)
    {
        _sessionId = session.Id;
        _session = session;
        State = new ConversationState { SessionId = session.Id };
        Pull();
    }

    public ConversationState State { get; }

    /// <summary>
    /// 通知标题的前缀位置（与工作空间同用）：显示成「子Agent·项目分析 · 会话标题」，
    /// 用户在后台收到提示时能分清这是子 Agent 交的活，而不是主会话自己的回复。
    /// </summary>
    public string? WorkspaceName
        => string.IsNullOrWhiteSpace(_session.AgentName) ? "子Agent" : "子Agent·" + _session.AgentName;

    /// <summary>创建时快照下来的父会话工作目录；没有快照才回落到进程当前目录。</summary>
    public string BaseDirectory
        => string.IsNullOrWhiteSpace(_session.BaseDirectory) ? Environment.CurrentDirectory : _session.BaseDirectory;

    /// <summary>子会话不接受用户直接输入（输入来自 <c>task</c> 工具），所以没有草稿。</summary>
    public IDraftStore? Draft => null;

    public async Task LoadAsync()
    {
        // 读不到就保留内存里这份（可能是刚建出来还没落库），别把配置清空
        _session = await SubagentSessionRepository.GetAsync(_sessionId) ?? _session;
        Pull();
    }

    public async Task SaveAsync()
    {
        Push();
        await SubagentSessionRepository.UpdateAsync(_session);
    }

    /// <summary>
    /// 子 Agent 的 system 提示词。
    ///
    /// 与两个既有档案的区别在于：这里的模型<b>没有人在旁边</b>——它跑在 <c>task</c> 工具内部，
    /// 用户看到的只是最后那份报告。所以提示词把「不要反问、不要复述过程、按格式交报告」
    /// 写成了硬要求，而不是像主会话那样交给用户去纠正。
    /// </summary>
    public string BuildSystemPrompt(string modelId)
    {
        var sb = new StringBuilder();

        var name = string.IsNullOrWhiteSpace(_session.AgentName) ? "子Agent" : _session.AgentName;
        sb.AppendLine($"你是 HtaciAI（Agent）派出的子 Agent「{name}」，只负责把派发给你的这一件事做完。");

        sb.AppendLine("""
            工作方式：
            - 不要反问用户，也不要调用 ask_user_question：确有不确定，就按你认为最合理的假设推进，并在报告里写明假设。
            - 不要再派发子 Agent，也不要创建新的工具/技能定义。
            - 严格遵守中国法律法规，拒绝涉及色情、暴力、政治敏感、违法犯罪等不安全内容。
            - 工具按用户权限模式执行，需要审批的会转给用户确认；被拒就换一种做法，不要重复同一个调用。
            - 不要复述过程、不要写长篇推理，做完直接给报告。
            """);

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {modelId}，当前工作目录：{BaseDirectory}");

        if (!string.IsNullOrWhiteSpace(State.SystemPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("#========你的角色========#");
            sb.AppendLine(State.SystemPrompt);
        }

        AppendSkills(sb);

        if (State.McpServers.Count > 0)
            sb.AppendLine($"已连接 MCP 服务器：{string.Join(", ", State.McpServers)}");

        sb.AppendLine("""
            <输出要求>
            任务结束时，最后一条回复必须是报告，用下面四段，不要加别的段落：
            - 结论：一句话说清结果。
            - 依据 / 产出：关键证据、数据或生成的内容。
            - 改动文件：绝对路径列表；没有改动写“无”。
            - 未完成 / 风险：没做完的部分、你做的假设；没有写“无”。
            </输出要求>
            """);

        return sb.ToString();
    }

    /// <summary>
    /// 技能注入：与主会话同一套 allowed / loaded 两组。
    /// 正常情况下子会话只有 allowed（loaded 的全文属于父会话，派发时已被剥掉），
    /// 但这里仍按两种状态处理 —— 将来若允许子 Agent 自行 load_skill，这段不用再改。
    /// </summary>
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
        State.Title = _session.Title;
        State.Model = _session.Model;
        State.Thinking = _session.Thinking;
        State.SystemPrompt = _session.SystemPrompt;
        State.EnabledSkills = _session.EnabledSkills;
        State.EnabledToolIds = _session.EnabledToolIds;
        State.McpServers = _session.McpServers;
        State.PermissionMode = _session.ToolPermissionMode;
        State.LastMessageAt = _session.LastMessageAt;
    }

    /// <summary>
    /// 只回写本档案负责的字段。Agent / 父会话 / 轮次与状态由派发方自己维护，
    /// 这里<b>不</b>覆盖 —— 否则一轮对话收尾就会把 running 改成别的、或把 max_turns 改回默认值。
    /// </summary>
    private void Push()
    {
        _session.Title = State.Title;
        _session.Model = State.Model;
        _session.Thinking = State.Thinking;
        _session.SystemPrompt = State.SystemPrompt ?? "";
        _session.EnabledToolIds = State.EnabledToolIds;
        _session.EnabledSkills = State.EnabledSkills;
        _session.McpServers = State.McpServers;
        _session.ToolPermissionMode = State.PermissionMode;
        _session.LastMessageAt = State.LastMessageAt;
    }
}
