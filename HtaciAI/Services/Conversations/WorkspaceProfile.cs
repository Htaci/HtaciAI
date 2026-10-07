using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 工作空间会话档案：持有工作目录与工作空间层，system 提示词走
/// <see cref="WorkspaceAgentPipeline.BuildSystemPrompt"/> 的拼装，并有草稿持久化。
///
/// <see cref="Models.WorkspaceConfig"/> 只在本类内部使用、不对外暴露，
/// 以免视图里出现类型分支。
/// </summary>
public sealed class WorkspaceProfile : IConversationProfile
{
    private readonly WorkspaceConfig _workspace;
    private readonly WorkspaceChatSession _session;

    public WorkspaceProfile(WorkspaceConfig workspace, WorkspaceChatSession session)
    {
        _workspace = workspace;
        _session = session;
        State = new ConversationState { SessionId = session.Id };
        Draft = new WorkspaceDraftStore(session);
        Pull();
    }

    public ConversationState State { get; }

    /// <summary>工具基准目录 = 工作空间根目录；未配置时回退进程当前目录（与改造前一致）。</summary>
    public string BaseDirectory
        => string.IsNullOrWhiteSpace(_workspace.Path) ? Environment.CurrentDirectory : _workspace.Path;

    public IDraftStore? Draft { get; }

    /// <summary>会话对象由页面加载后传入，此处只需保持与状态同步。</summary>
    public Task LoadAsync()
    {
        Pull();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        Push();
        await WorkspaceSessionRepository.UpdateAsync(_session);
    }

    /// <summary>
    /// system 提示词（静态身份 → 工作空间 → 会话 → 技能 → MCP）。
    /// ⚠️ 结构与普通对话那套**不同**（无 <c>#========</c> 包裹、环境行含工作目录），不能合流。
    /// </summary>
    public string BuildSystemPrompt(string modelId)
    {
        // pipeline 直接读会话模型上的字段，先同步一次状态，避免提示词取到旧值
        Push();
        return WorkspaceAgentPipeline.BuildSystemPrompt(_workspace, _session, modelId, BaseDirectory);
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

    /// <summary>只回写本档案负责的字段，其余（WorkspaceId/AgentId/时间戳等）原样保留。</summary>
    private void Push()
    {
        _session.Title = State.Title;
        _session.Model = State.Model;
        _session.Thinking = State.Thinking;
        _session.SystemPrompt = State.SystemPrompt ?? "";
        _session.EnabledSkills = State.EnabledSkills;
        _session.EnabledToolIds = State.EnabledToolIds;
        _session.McpServers = State.McpServers;
        _session.ToolPermissionMode = State.PermissionMode;
        _session.LastMessageAt = State.LastMessageAt;
    }

    /// <summary>工作空间会话的草稿存储（普通对话没有对应列，因此它是可选的）。</summary>
    private sealed class WorkspaceDraftStore : IDraftStore
    {
        private readonly WorkspaceChatSession _session;

        public WorkspaceDraftStore(WorkspaceChatSession session) => _session = session;

        public string? Load() => _session.Draft;

        public async Task SaveAsync(string draft)
        {
            _session.Draft = draft;
            await WorkspaceSessionRepository.SaveDraftAsync(_session.Id, draft);
        }
    }
}
