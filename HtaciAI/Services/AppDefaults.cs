using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;
using HtaciAI.Services.KnowledgeBase;

namespace HtaciAI.Services;

/// <summary>
/// 「默认配置」落到具体值的地方。设置里的 <c>null</c> 表示「用内置默认行为」，
/// 这里负责把 null 解析成真实列表；空列表则原样返回（用户显式清空，语义不同）。
/// </summary>
public static class AppDefaults
{
    /// <summary>默认开启的工具 id。设置里为 null 时 = 全部可见内置工具。</summary>
    public static List<string> ResolveToolIds()
    {
        var configured = AppSettingsStore.Current.DefaultToolIds;
        return configured is not null
            ? new List<string>(configured)
            : BuiltinTools.GetVisible().Select(t => t.Id).ToList();
    }

    /// <summary>默认开启的技能。null / 空 = 普通会话默认不开启（AI 仍可用 load_skill 主动加载）。</summary>
    public static List<SessionSkill> ResolveSkills()
        => AppSettingsStore.Current.DefaultSkills is { } skills ? new List<SessionSkill>(skills) : new();

    /// <summary>默认开启的 MCP 服务 id。null / 空 = 不启用任何 MCP。</summary>
    public static List<string> ResolveMcpServerIds()
        => AppSettingsStore.Current.DefaultMcpServerIds is { } ids ? new List<string>(ids) : new();

    /// <summary>
    /// 一次用户消息内的工具调用轮数上限。设置里 ≤ 0 = 不限，这里换算成
    /// <see cref="int.MaxValue"/> 交给 <see cref="ChatGateway.MaxToolRounds"/>，
    /// 让「不限」在循环条件里自然成立，不用在上层再写分支。
    /// </summary>
    public static int ResolveMaxToolRounds()
    {
        var configured = AppSettingsStore.Current.MaxToolRounds;
        return configured <= 0 ? int.MaxValue : configured;
    }

    /// <summary>会话窗口首屏渲染的轮数，以及滚到顶部时每次追加的轮数。</summary>
    public static int ResolveHistoryBatchTurns()
    {
        var configured = AppSettingsStore.Current.HistoryInitialTurns;
        return configured <= 0 ? 10 : configured;
    }

    /// <summary>
    /// 新会话的默认模型。设置里指定了就用它；没指定则回退「上次使用的模型」
    /// （普通会话与工作区会话取更近的那次）；都没有（或解析不出来）就返回 null，
    /// 由调用方把会话的模型留空 —— 发送侧会拦下并提示去配置，不再兜底任何内置模型。
    /// </summary>
    public static async Task<ModelDetails?> ResolveDefaultModelAsync()
    {
        var configured = AppSettingsStore.Current.DefaultModelId;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var details = await ModelCatalog.ResolveAsync(configured);
            if (details is not null) return details;
        }

        var lastName = await GetLastUsedModelNameAsync();
        return string.IsNullOrWhiteSpace(lastName) ? null : await ModelCatalog.ResolveAsync(lastName);
    }

    /// <summary>
    /// 带首选模型的版本（工作空间默认模型）。<paramref name="preferredId"/> 解析得出来就用它，
    /// 否则（未设置 / 已被停用或删除）回落到无参重载的通用规则。
    /// </summary>
    public static async Task<ModelDetails?> ResolveDefaultModelAsync(string? preferredId)
    {
        if (!string.IsNullOrWhiteSpace(preferredId) &&
            await ModelCatalog.ResolveAsync(preferredId) is { } preferred)
            return preferred;

        return await ResolveDefaultModelAsync();
    }

    private static async Task<string?> GetLastUsedModelNameAsync()
    {
        var plain = await ChatRepository.GetLastUsedModelAsync();
        var workspace = await WorkspaceSessionRepository.GetLastUsedModelAsync();

        if (plain is null) return workspace?.Model;
        if (workspace is null) return plain.Value.Model;

        return plain.Value.At >= workspace.Value.At ? plain.Value.Model : workspace.Value.Model;
    }

    // ============================================================
    // 知识库
    // ============================================================

    /// <summary>
    /// 新建会话 / 打开会话时该用哪个知识库。顺序与模型那套一致：
    /// 会话自己选的 → 工作空间默认 → 全局默认 → 上次用过的 → 不指定。
    ///
    /// <b>每一层都验一遍库还在不在</b>：知识库被删掉之后，会话上留着的 id 就是悬空的，
    /// 直接返回它会让「选了知识库却查不到东西」变成无法解释的现象。
    /// </summary>
    public static async Task<string?> ResolveDefaultKnowledgeBaseIdAsync(
        string? preferredId, string? workspaceDefaultId = null)
    {
        foreach (var candidate in new[] { preferredId, workspaceDefaultId, AppSettingsStore.Current.DefaultKnowledgeBaseId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (await KnowledgeBaseRepository.GetAsync(candidate) is not null) return candidate;
        }

        var lastUsed = await GetLastUsedKnowledgeBaseIdAsync();
        if (!string.IsNullOrWhiteSpace(lastUsed) &&
            await KnowledgeBaseRepository.GetAsync(lastUsed) is not null)
            return lastUsed;

        return null;
    }

    /// <summary>
    /// 上次在会话里选过的知识库。「上次用过的」不额外存一份设置 ——
    /// 会话表上就有答案，多存一处只会多一个会不同步的地方。
    /// </summary>
    public static async Task<string?> GetLastUsedKnowledgeBaseIdAsync()
    {
        foreach (var session in await ChatRepository.GetAllAsync())
        {
            if (!string.IsNullOrWhiteSpace(session.KnowledgeBaseId)) return session.KnowledgeBaseId;
        }
        return null;
    }

    /// <summary>
    /// 向量库用的嵌入模型。设置里选的解析不出来（被删/被停用）就当没配，
    /// 界面据此提示去配置，而不是悄悄退回某个模型 —— 换模型意味着向量空间变了，
    /// 拿旧索引继续检索只会给出更差的结果。
    /// </summary>
    public static async Task<ModelDetails?> ResolveEmbeddingModelAsync()
    {
        var id = AppSettingsStore.Current.EmbeddingModelId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var model = await ModelCatalog.ResolveAsync(id);
        if (model is null) return null;

        return EmbeddingClient.CheckUsable(model) is null ? model : null;
    }
}
