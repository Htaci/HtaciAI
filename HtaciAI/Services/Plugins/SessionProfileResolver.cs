using System;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 按会话 id 解析出会话档案（以及它在 <see cref="SessionViewCache"/> 里的缓存键）。
///
/// 四类会话共用一套 id 空间、各占一张表，只能按「先普通、后插件、再子 Agent、最后工作空间」去查。
/// 这段判断原本长在 <c>PluginSessionFacade</c> 里；「借会话视图」（<see cref="ConversationDockHost"/>）
/// 也要用同一个判断，抄一份出去的话，加会话类型时必然漏改一处 —— 所以它只有这一个实现。
/// </summary>
internal static class SessionProfileResolver
{
    public static async Task<IConversationProfile?> ResolveAsync(string sessionId)
    {
        var resolved = await ResolveWithKeyAsync(sessionId);
        return resolved?.Profile;
    }

    /// <summary>解析档案，同时给出这个会话在缓存里的键（三种会话的键格式不同）。</summary>
    public static async Task<(string Key, IConversationProfile Profile)?> ResolveWithKeyAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        if (await ChatRepository.GetAsync(sessionId) is not null)
            return (SessionViewCache.KeyForSession(sessionId), new StandaloneProfile(sessionId));

        // 插件会话 id 带 plugin- 前缀：先按前缀挡掉，免得每个普通会话都多查一次插件表
        if (sessionId.StartsWith(PluginSession.IdPrefix, StringComparison.Ordinal) &&
            await PluginSessionRepository.GetAsync(sessionId) is { IsDeleted: false } plugin)
            return (SessionViewCache.KeyForPluginSession(sessionId), new PluginSessionProfile(plugin));

        // 子 Agent 会话 id 带 sub- 前缀，下面两次查询在 id 层面就不可能命中它
        if (await SubagentSessionRepository.GetAsync(sessionId) is { IsDeleted: false } subagent)
            return (SessionViewCache.KeyForSubagent(sessionId), new SubagentProfile(subagent));

        // 定时任务运行 id 带 task-run- 前缀：一次运行就是一个会话，点系统提醒要能直接打开它
        if (await TaskRepository.GetRunAsync(sessionId) is { IsDeleted: false } run)
            return (SessionViewCache.KeyForTaskRun(sessionId), new TaskProfile(run));

        if (await WorkspaceSessionRepository.GetAsync(sessionId) is not { } session)
            return null;

        if (await WorkspaceRepository.GetAsync(session.WorkspaceId) is not { } workspace)
            return null;

        // 顺手登记归属：工作空间节点要按它汇总「我下面还有没有会话在跑 / 在等用户」。
        // 放在这个唯一的解析点上，插件借走会话视图、从系统提醒跳进来这些路径都不会漏。
        SessionActivityStore.Bind(session.Id, workspace.Id);

        return (SessionViewCache.KeyForWorkspaceSession(workspace.Id, session.Id), new WorkspaceProfile(workspace, session));
    }
}
