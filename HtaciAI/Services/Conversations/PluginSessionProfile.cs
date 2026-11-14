using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 插件会话档案：插件通过 <c>ISessionFacade.CreateSessionAsync</c> 给自己开的独立会话区。
///
/// 与 <see cref="SubagentProfile"/> 一样，差别只有三处：会话行来自 <c>plugin_sessions</c>、
/// 工具基准目录是<b>插件自己的数据目录</b>、system 提示词带上「这条会话是哪个插件开的」。
/// 消息、压缩、工具修复、落库全部复用现有那套，本类不碰业务逻辑。
///
/// <b>为什么基准目录是插件数据目录</b>：插件会话没有父会话可继承（它不属于任何用户会话），
/// 回落到进程当前目录等于让插件借 AI 的手在程序目录里写文件。插件数据目录既是它的，
/// 又和插件 <c>IPluginStorage</c> 看到的是同一块地方，出问题也好定位。
/// </summary>
public sealed class PluginSessionProfile : IConversationProfile
{
    private readonly string _sessionId;
    private PluginSession _session;

    public PluginSessionProfile(PluginSession session)
    {
        _sessionId = session.Id;
        _session = session;
        State = new ConversationState { SessionId = session.Id };
        Pull();
    }

    public ConversationState State { get; }

    /// <summary>通知标题的前缀位置：显示成「插件·id · 会话标题」，用户能分清这是插件在后台跑的活。</summary>
    public string? WorkspaceName
        => string.IsNullOrWhiteSpace(_session.PluginId) ? "插件" : "插件·" + _session.PluginId;

    public string BaseDirectory
        => Path.Combine(AppPaths.PluginData, _session.PluginId);

    /// <summary>插件会话没有输入框（内容由插件发），所以没有草稿。</summary>
    public IDraftStore? Draft => null;

    public async Task LoadAsync()
    {
        // 读不到就保留内存里这份（可能是刚建出来还没落库），别把配置清空
        _session = await PluginSessionRepository.GetAsync(_sessionId) ?? _session;
        Pull();
    }

    public async Task SaveAsync()
    {
        Push();
        await PluginSessionRepository.UpdateAsync(_session);
    }

    /// <summary>
    /// 插件会话的 system 提示词。
    ///
    /// 刻意比主会话短：这条会话的上下文由插件自己组织，写一堆「用户偏好」既用不上、
    /// 又会和插件注入的内容打架。留下的是「身份 + 安全底线 + 工具规矩 + 环境」。
    /// </summary>
    public string BuildSystemPrompt(string modelId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是 HtaciAI（Agent），一款由 赫塔奇智能科技有限公司 研发的智能AI助手/智能体。");
        sb.AppendLine($"当前这条会话是插件「{_session.PluginId}」在宿主里开出来的独立会话，不是用户手动新建的对话。");

        sb.AppendLine("""
            遵循原则：
            - 严格遵守中国法律法规，拒绝涉及色情、暴力、政治敏感、违法犯罪等不安全内容。
            - 简洁、直接、切题；除非用户要求，否则不要使用 emoji。
            - 工具按用户权限模式执行，需要审批的会转给用户确认；被拒就换一种做法，不要重复同一个调用。
            """);

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {modelId}，当前工作目录：{BaseDirectory}");

        if (!string.IsNullOrWhiteSpace(State.SystemPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("#========会话提示词========#");
            sb.AppendLine(State.SystemPrompt);
        }

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

        return sb.ToString();
    }

    // ---- 模型 ↔ 状态映射 ----

    private void Pull()
    {
        State.Title = _session.Title;
        State.Model = _session.Model;
        State.Thinking = _session.Thinking;
        State.SystemPrompt = _session.SystemPrompt ?? "";
        State.EnabledSkills = _session.EnabledSkills;
        State.EnabledToolIds = _session.EnabledToolIds;
        State.McpServers = _session.McpServers;
        State.PermissionMode = _session.ToolPermissionMode;
        State.LastMessageAt = _session.LastMessageAt;
    }

    /// <summary>
    /// 只回写本档案负责的字段。归属（plugin_id）与删除标记由仓储/门面自己维护，这里不覆盖。
    /// </summary>
    private void Push()
    {
        _session.Title = State.Title;
        _session.Model = State.Model;
        _session.Thinking = State.Thinking;
        _session.SystemPrompt = State.SystemPrompt;
        _session.EnabledToolIds = State.EnabledToolIds;
        _session.EnabledSkills = State.EnabledSkills;
        _session.McpServers = State.McpServers;
        _session.ToolPermissionMode = State.PermissionMode;
        _session.LastMessageAt = State.LastMessageAt;
    }
}
