using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件接口里的「上下文用量」取数：<b>只读库</b>，不 Acquire 运行时 ——
/// 那会把整个会话的历史读进内存，而插件问用量时往往一次问一批会话。
///
/// 口径不在这里：真正「占多少」的判断在 <see cref="ContextUsage"/>，
/// 那是界面用量环与自动压缩共用的同一份实现。
/// </summary>
internal static class PluginUsageReader
{
    /// <summary>
    /// 批量算「上下文占用 + 窗口」。一次查库取回所有 usage 行；
    /// 窗口按模型 id 缓存（同一批会话往往共用一个模型，不必逐个查目录）。
    /// </summary>
    /// <param name="modelOf">会话 id → 会话存档里的模型 id（可为空 = 走宿主默认）。</param>
    public static async Task<Dictionary<string, (long Used, long Window)>> ReadContextUsageAsync(
        IReadOnlyList<string> sessionIds, Func<string, string> modelOf, CancellationToken ct)
    {
        var result = new Dictionary<string, (long Used, long Window)>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;

        var rows = await ChatRepository.GetUsageMessagesAsync(sessionIds);
        var windows = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var sessionId in sessionIds)
        {
            if (result.ContainsKey(sessionId)) continue;

            ct.ThrowIfCancellationRequested();

            var modelId = modelOf(sessionId) ?? "";
            if (!windows.TryGetValue(modelId, out var window))
            {
                window = (await ResolveModelDetailsAsync(modelId))?.ContextWindow ?? -1;
                windows[modelId] = window;
            }

            rows.TryGetValue(sessionId, out var messages);
            var used = messages is not null &&
                       ContextUsage.TryComputeFromLatest(messages, window, out var computed, out _)
                ? computed
                : 0;

            result[sessionId] = (used, window);
        }

        return result;
    }

    /// <summary>
    /// 只读库地解析一个模型：存档里的那个 → 宿主默认模型。
    /// 与 <c>PluginSessionFacade</c> 发消息时用的回退是同一套。
    /// </summary>
    public static async Task<ModelDetails?> ResolveModelDetailsAsync(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && await ModelCatalog.ResolveAsync(modelId) is { } saved)
            return saved;

        return await AppDefaults.ResolveDefaultModelAsync();
    }
}
