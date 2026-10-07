using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 普通对话档案：没有工作目录、没有 Agent / 工作空间层。
/// 工具基准目录为进程当前目录；system 提示词为单层，用户提示词非空时用
/// <c>#========用户提示词开始/结束========#</c> 包裹，且环境行不含工作目录。
/// </summary>
public sealed class StandaloneProfile : IConversationProfile
{
    private readonly string _sessionId;
    private ChatSession _session;

    public StandaloneProfile(string sessionId)
    {
        _sessionId = sessionId;
        _session = new ChatSession { Id = sessionId };
        State = new ConversationState { SessionId = sessionId };
        // 用委托而不是直接持有 _session：LoadAsync 会把 _session 换成刚从库里读回来的新对象
        Draft = new ChatDraftStore(() => _session);
    }

    public ConversationState State { get; }

    /// <summary>普通对话没有工作目录，工具相对路径基于进程当前目录（与改造前 ChatView 一致）。</summary>
    public string BaseDirectory => Environment.CurrentDirectory;

    public IDraftStore? Draft { get; }

    public async Task LoadAsync()
    {
        // 会话不存在时沿用占位对象而不是报错 —— 保持改造前 ChatView.InitializeAsync 的行为
        _session = await ChatRepository.GetAsync(_sessionId)
                   ?? new ChatSession { Id = _sessionId, Title = "会话" };
        Pull();
    }

    public async Task SaveAsync()
    {
        Push();
        await ChatRepository.UpdateAsync(_session);
    }

    /// <summary>
    /// 单层 system 提示词。
    /// ⚠️ 原样搬运自改造前的 ChatView.BuildSystemPrompt()，**不能**与工作空间那套三层拼装合流：
    /// 这里独有的 <c>#========</c> 包裹标记，以及环境行**不含**工作目录，都是既有行为。
    /// </summary>
    public string BuildSystemPrompt(string modelId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是 HtaciAI（Agent），一款由 赫塔奇智能科技有限公司 研发的智能AI助手/智能体，可以完成办公协助、项目开发、答疑解惑、代理操作等很多工作。");

        sb.AppendLine("遵循原则：\r\n\r\n" +
            "严格遵守中国法律法规，拒绝回答涉及色情、暴力、政治敏感、违法犯罪等不安全内容，履行 AI 安全规范。\r\n\r\n" +
            "默认语气风格：简洁、直接、切题。除非用户要求，否则不要使用 emoji。" +
            "如无用户要求，尽量用 4 行以内的文字回答，不要加无关的前言后语，如果用户提示词中有明确其他语气设定，或用户喜欢其他语气风格，则按照用户要求。\r\n\r\n" +
            "主动程度：只在被要求时主动，不要擅自行动吓到用户，如果不确定用户是否有让开始行动时，则询问用户是否要开始，直到用户明确指示开始。\r\n\r\n" +
            "遇到敏感问题时，统一回复：“我无法回答该问题，请换个问题试试吧。”");

        sb.AppendLine("工具按用户权限模式执行，未自动允许的调用会请求用户批准或拒绝。\r\n" +
            "工具被拒后应调整，若不明白拒绝原因，用 ask_user_question 询问。\r\n" +
            "用户消息里出现的文件路径是用户附加的文件：图片用 view_image 查看内容后再回答，其他文件用 read 读取。");

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {modelId}");

        // 只有用户真的给这个会话设了提示词才插入包裹标记：绝大多数会话是空的，
        // 空包裹等于每轮白送两行无意义的标记。
        if (!string.IsNullOrWhiteSpace(State.SystemPrompt))
        {
            sb.AppendLine("\r\n\r\n#========用户提示词开始========#\r\n\r\n");
            sb.AppendLine(State.SystemPrompt);
            sb.AppendLine("\r\n\r\n#========用户提示词结束========#\r\n\r\n");
        }

        var loadedIds = State.EnabledSkills.Where(s => s.Status == "loaded").Select(s => s.Id).ToHashSet();
        var allowedIds = State.EnabledSkills.Where(s => s.Status == "allowed").Select(s => s.Id).ToHashSet();

        // 1) 允许自动使用的技能：仅元数据
        if (allowedIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下是可用技能（仅元数据，完整内容按需加载）：");
            foreach (var skill in SkillRegistry.Instance.GetByIds(allowedIds))
                sb.AppendLine($"{skill.Name}: {skill.Description}");
        }

        // 2) 已加载的技能：SKILL.md 全文注入
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
        State.SystemPrompt = _session.SystemPrompt;
        State.EnabledSkills = _session.EnabledSkills;
        State.EnabledToolIds = _session.EnabledToolIds;
        State.McpServers = _session.McpServers;
        State.PermissionMode = _session.ToolPermissionMode;
        State.LastMessageAt = _session.LastMessageAt;
    }

    private void Push()
    {
        _session.Title = State.Title;
        _session.Model = State.Model;
        _session.Thinking = State.Thinking;
        _session.SystemPrompt = State.SystemPrompt;
        _session.EnabledSkills = State.EnabledSkills;
        _session.EnabledToolIds = State.EnabledToolIds;
        _session.McpServers = State.McpServers;
        _session.ToolPermissionMode = State.PermissionMode;
        _session.LastMessageAt = State.LastMessageAt;
    }

    /// <summary>普通会话的草稿存储：读当前会话对象上的 Draft，落库只写 draft 列。</summary>
    private sealed class ChatDraftStore : IDraftStore
    {
        private readonly Func<ChatSession> _current;

        public ChatDraftStore(Func<ChatSession> current) => _current = current;

        public string? Load() => _current().Draft;

        public async Task SaveAsync(string draft)
        {
            var session = _current();
            session.Draft = draft;
            await ChatRepository.SaveDraftAsync(session.Id, draft);
        }
    }
}
