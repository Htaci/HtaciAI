using System.Collections.Generic;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 「当前上下文占了多少 token」的<b>唯一一份口径</b>，宿主界面与插件接口都调它。
///
/// <b>为什么必须只有一份</b>：界面上的用量环、自动压缩的阈值判定、插件报给用户的百分比，
/// 三处一旦各写一遍，迟早会有一个地方跟着另一处的改动漂走 —— 用户看到的就是
/// 「界面说 62%，机器人说 48%」，而两边都"对"。
///
/// 口径取<b>最新一条有 usage 的来源</b>，两种二选一：
/// <list type="bullet">
///   <item>普通 assistant 消息：无状态 API 的最后一次输入已包含之前所有轮的输入与输出，
///   所以 <c>total</c> 就是当前占用；</item>
///   <item>压缩记录：它之前的历史已经不再进入上下文，<b>它自己才是新的上下文起点</b>，
///   所以取摘要的输出 token —— 它的输入是「被压掉的全部原文」，比压缩前还大，不能拿来当占用。</item>
/// </list>
/// </summary>
public static class ContextUsage
{
    /// <summary>
    /// 按全量历史算占用。历史从旧到新排列（与 <c>ConversationRuntime.Messages</c> 一致）。
    /// </summary>
    public static bool TryCompute(
        IReadOnlyList<ChatMessage> messages, long contextWindow, out long used, out long total)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (TryUse(messages[i], contextWindow, out used, out total))
                return true;

        used = 0;
        total = contextWindow;
        return false;
    }

    /// <summary>
    /// 按「只带 usage 的消息、从新到旧」算占用。给只读库的调用方用：
    /// 它们不加载全文本，只把有 usage_json 的那些行按倒序拿出来。
    /// </summary>
    public static bool TryComputeFromLatest(
        IEnumerable<ChatMessage> newestFirst, long contextWindow, out long used, out long total)
    {
        foreach (var message in newestFirst)
            if (TryUse(message, contextWindow, out used, out total))
                return true;

        used = 0;
        total = contextWindow;
        return false;
    }

    private static bool TryUse(ChatMessage m, long contextWindow, out long used, out long total)
    {
        used = 0;
        total = contextWindow;
        if (total <= 0) return false;

        if (string.IsNullOrWhiteSpace(m.UsageJson)) return false;
        if (TokenUsage.FromStoredJson(m.UsageJson) is not { } usage) return false;

        if (ContextCompression.IsCompression(m))
        {
            // 服务商没回 usage（少数兼容实现如此）时按摘要字数估一个：
            // 绝不能因此退回压缩前那个大数，那会让用量环一直顶在阈值上、每次发送都再压一遍
            var summaryTokens = usage.OutputTokens > 0
                ? usage.OutputTokens
                : ContextCompression.EstimateTokens(m.Content);

            if (summaryTokens <= 0) return false;

            used = summaryTokens;
            return true;
        }

        if (m.Role != "assistant") return false;

        used = usage.TotalTokens;
        return true;
    }
}
