using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 按会话 id 解析出会话档案（以及它在 <see cref="SessionViewCache"/> 里的缓存键）。
///
/// 普通对话与工作空间会话共用一套 id 空间，但存在两张表里，只能按「先普通、后工作空间」去查。
/// 这段判断原本长在 <c>PluginSessionFacade</c> 里；「借会话视图」（<see cref="ConversationDockHost"/>）
/// 也要用同一个判断，抄一份出去的话，将来加第三种会话类型必然漏改一处。
/// </summary>
internal static class SessionProfileResolver
{
    public static async Task<IConversationProfile?> ResolveAsync(string sessionId)
    {
        var resolved = await ResolveWithKeyAsync(sessionId);
        return resolved?.Profile;
    }

    /// <summary>解析档案，同时给出这个会话在缓存里的键（普通对话 / 工作空间会话的键格式不同）。</summary>
    public static async Task<(string Key, IConversationProfile Profile)?> ResolveWithKeyAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        if (await ChatRepository.GetAsync(sessionId) is not null)
            return (SessionViewCache.KeyForSession(sessionId), new StandaloneProfile(sessionId));

        if (await WorkspaceSessionRepository.GetAsync(sessionId) is not { } session)
            return null;

        if (await WorkspaceRepository.GetAsync(session.WorkspaceId) is not { } workspace)
            return null;

        return (SessionViewCache.KeyForWorkspaceSession(workspace.Id, session.Id), new WorkspaceProfile(workspace, session));
    }
}
