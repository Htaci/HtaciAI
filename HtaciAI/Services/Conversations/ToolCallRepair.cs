using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// assistant 的 tool_calls 与 tool 结果的配对修复。
///
/// 服务端对这套配对的检查是硬性的：assistant 里出现的每个 <c>tool_call_id</c>，
/// 后面都必须有一条 <c>role=tool</c> 的消息回它，少一条整个会话就再也发不出去 ——
/// 400 <c>insufficient tool messages following tool_calls message</c>。
/// 麻烦在于这条坏消息会一直留在历史里，之后<b>每次</b>发送都重蹈覆辙，除非把那一轮删掉。
///
/// 而缺一条太容易发生了：带 tool_calls 的 assistant 消息是在执行工具<b>之前</b>落库的，
/// 这中间用户直接关掉软件，就只剩下悬空的 tool_calls
/// （「上一次跑到一半被我关掉，下一次一进这个会话就报错」就是这么来的）。
///
/// 处理方式是<b>把配不上的调用删掉</b>，而不是补一条假结果：补结果等于替模型编造一次
/// 它从没看到的工具输出，删掉只是回到「这轮没调成」这个事实。副作用也小得多 ——
/// 悬空的调用本来就没有对应的工具卡，删掉它界面上什么都不变。
/// </summary>
public static class ToolCallRepair
{
    /// <summary>
    /// 工具调用还没返回结果时的占位文案。占位行在工具执行<b>前</b>就落库，
    /// 中途被杀就停在这个状态 —— 它必须是一条合法的 tool 结果，否则历史照样发不出去。
    /// </summary>
    public const string InterruptedResult = "（工具执行被中断：应用在工具返回结果前退出）";

    /// <summary>
    /// 历史的可发送版本：悬空的 tool_calls 去掉，没人认领的 tool 结果也去掉。
    /// 配对完整时原样返回（不复制 —— 长会话每一轮都要调这里）。
    /// </summary>
    public static IReadOnlyList<ChatMessage> ForWire(IReadOnlyList<ChatMessage> messages)
    {
        var scan = Scan(messages);
        List<ChatMessage>? patched = null;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            if (message.Role == "assistant")
            {
                var calls = ToolCallJson.Deserialize(message.Metadata);
                if (calls.Count == 0)
                {
                    patched?.Add(message);
                    continue;
                }

                var kept = calls.Where(c => IsAnswered(c, scan.AnswerSet(i))).ToList();
                if (kept.Count == calls.Count)
                {
                    patched?.Add(message);
                    continue;
                }

                patched ??= new List<ChatMessage>(messages.Take(i));
                patched.Add(WithCalls(message, kept));
            }
            else if (message.Role == "tool")
            {
                // 没有 assistant 声明过它的 tool_call_id，单独发出去同样会被拒
                if (scan.Claimed.Contains(i))
                    patched?.Add(message);
                else
                    patched ??= new List<ChatMessage>(messages.Take(i));
            }
            else
            {
                patched?.Add(message);
            }
        }

        return patched ?? messages;
    }

    /// <summary>
    /// 就地修复：把悬空的 tool_calls 从 metadata 里删掉。
    /// 返回被改动过的消息（调用方负责落库），没有可修的返回空列表。
    ///
    /// 就地改而不是返回副本：调用方手上的其他列表（各种 <c>Take</c> 出来的上下文切片）
    /// 引用的是同一批对象，换新对象只会让它们各说各话。
    /// </summary>
    public static List<ChatMessage> RepairInPlace(IReadOnlyList<ChatMessage> messages)
    {
        var scan = Scan(messages);
        var touched = new List<ChatMessage>();

        foreach (var index in scan.AssistantIndexes)
        {
            var message = messages[index];
            var calls = ToolCallJson.Deserialize(message.Metadata);
            var kept = calls.Where(c => IsAnswered(c, scan.AnswerSet(index))).ToList();
            if (kept.Count == calls.Count) continue;

            message.Metadata = kept.Count == 0 ? null : ToolCallJson.Serialize(kept);
            touched.Add(message);
        }

        return touched;
    }

    /// <summary>
    /// 这条 assistant 消息的副本，只把 metadata 里的 <c>tool_calls</c> 换成 <paramref name="calls"/>
    /// （一个不剩就摘掉这个键）。<see cref="ForWire"/> 用它作为发出去的版本，不动调用方手里的原对象。
    ///
    /// <b>只动 tool_calls、其他键一律保留</b>：metadata 是共享的，回复总结（<c>turn_summary</c>）
    /// 就挂在同一行上。整体重建 metadata 会让「配对残缺的那一轮」的总结在发出的上下文里凭空消失。
    /// </summary>
    private static ChatMessage WithCalls(ChatMessage source, List<ChatToolCall> calls) => new()
    {
        Id = source.Id,
        SessionId = source.SessionId,
        TurnId = source.TurnId,
        SequenceNumber = source.SequenceNumber,
        Role = source.Role,
        Content = source.Content,
        Thinking = source.Thinking,
        ToolCallId = source.ToolCallId,
        ToolName = source.ToolName,
        Status = source.Status,
        UsageJson = source.UsageJson,
        ModelName = source.ModelName,
        Metadata = MergeToolCalls(source.Metadata, calls),
        DurationMs = source.DurationMs,
        IsDeleted = source.IsDeleted,
        DeletedAt = source.DeletedAt,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    /// <summary>替换 metadata 里的 <c>tool_calls</c>，其余键原样保留（见 <see cref="WithCalls"/>）。</summary>
    private static string? MergeToolCalls(string? metadata, List<ChatToolCall> calls)
    {
        JsonObject? root = null;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            try { root = JsonNode.Parse(metadata) as JsonObject; }
            catch (JsonException) { root = null; }
        }

        if (calls.Count == 0)
        {
            if (root is null) return null;

            root.Remove("tool_calls");
            return root.Count == 0 ? null : root.ToJsonString();
        }

        root ??= new JsonObject();
        root["tool_calls"] = JsonSerializer.SerializeToNode(calls);
        return root.ToJsonString();
    }

    /// <summary>这个调用有没有结果回它。id 为空一律算配不上 —— 那种行本来也发不出去（见 BuildMessages）。</summary>
    private static bool IsAnswered(ChatToolCall call, HashSet<string> answered)
        => !string.IsNullOrEmpty(call.Id) && answered.Contains(call.Id);

    /// <summary>
    /// 扫一遍历史，得出「每条带 tool_calls 的 assistant 收到了哪些 id 的回复」以及
    /// 「哪些 tool 行确实被认领了」。两次修复共用这一遍结果。
    /// </summary>
    private static ScanResult Scan(IReadOnlyList<ChatMessage> messages)
    {
        var scan = new ScanResult();

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != "assistant") continue;

            var calls = ToolCallJson.Deserialize(messages[i].Metadata);
            if (calls.Count == 0) continue;

            // 回复只认<b>紧跟在后面</b>的连续 tool 段：中间夹了别的角色，服务端就不认这段配对了
            // （它的措辞就是 "must be followed by"）。已删除的 tool 行不算数，跳过但不截断这一段。
            var answered = new HashSet<string>(StringComparer.Ordinal);
            for (var j = i + 1; j < messages.Count && messages[j].Role == "tool"; j++)
            {
                if (messages[j].IsDeleted) continue;

                var id = messages[j].ToolCallId;
                // 重复的 id 只认第一条：第二条同一个 tool_call_id 的回复服务端也会拒
                if (!string.IsNullOrEmpty(id) && answered.Add(id))
                    scan.Claimed.Add(j);
            }

            scan.Answers[i] = answered;
            scan.AssistantIndexes.Add(i);
        }

        return scan;
    }

    private sealed class ScanResult
    {
        /// <summary>assistant 下标 → 它收到的 tool_call_id 集合。</summary>
        public Dictionary<int, HashSet<string>> Answers { get; } = new();

        /// <summary>带 tool_calls 的 assistant 下标，按顺序。</summary>
        public List<int> AssistantIndexes { get; } = new();

        /// <summary>被某条 assistant 认领了的 tool 行下标。</summary>
        public HashSet<int> Claimed { get; } = new();

        public HashSet<string> AnswerSet(int index)
            => Answers.TryGetValue(index, out var set) ? set : Empty;

        private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);
    }
}
