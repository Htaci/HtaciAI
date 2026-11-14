using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HtaciAI.Models;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 上下文的规则层：两种压缩各自长什么样、怎么判定、以及「发给模型的上下文」怎么截。
///
/// <b>它们是两件事，不要混</b>：
/// <list type="bullet">
///   <item><b>上下文压缩</b>（<see cref="CompressRole"/>）是一次性的整体压缩，<b>同时是分界线</b>：
///   最后一条压缩记录之前的消息全部丢弃，从它开始往后算。第二次压缩把第一次的摘要一起喂给模型，
///   信息逐轮滚存，不会因为反复压缩而一层层衰减。它<b>独占一条消息</b>。</item>
///   <item><b>回复总结</b>是<b>轮内</b>的原地替换：某一轮的 AI 回复（含几十次工具调用）压成一段摘要，
///   构建请求时用它替代那一轮的原文。它<b>不是分界线</b>，所以绝不能借用 <see cref="CompressFlag"/>
///   的标记，否则第一条总结就会把之前的历史全砍掉。</item>
/// </list>
///
/// <b>回复总结不占一行，而是挂在那一轮最后一条消息的 metadata 上</b>（见
/// <see cref="SummaryMetadataKey"/>）。这不是存储上的小聪明，是为了避开一个真实的事故：
/// 总结单占一行就要<b>新申请一个序号</b>，而序号是「查最大再插」两次 I/O —— 一轮任务正在
/// 落工具结果时插一条总结，两边会拿到同一个序号，撞上 <c>UNIQUE(session_id, sequence_number)</c>，
/// 整个工具循环当场抛异常断在那里。改成写已有行的 metadata 之后，总结是纯 UPDATE，
/// 不申请序号，并发写多轮总结也不会互相踩。
///
/// 顺带解决的问题：重新总结不再需要「先软删旧行再插新行」（直接覆写）；
/// 总结跟着那一轮的消息走，删轮次时自动一起消失，不需要额外的一致性处理。
/// </summary>
public static class ContextCompression
{
    /// <summary>压缩记录的 role 取值。刻意用一个全新的值，与 user/assistant/tool 区分开。</summary>
    public const string CompressRole = "agent";

    /// <summary>metadata 里的标记键。判定时与 role 一起看（见 <see cref="IsCompression"/>）。</summary>
    public const string CompressFlag = "compress";

    /// <summary>
    /// 回复总结在 metadata 里的键。值就是总结正文本身。
    ///
    /// 挂在这一轮<b>最后一条</b>消息上（最后一条可能是 tool —— 被打断的轮次末尾就是工具行，
    /// 那种轮次同样需要总结）：这样「一条消息归谁」不需要额外记账，删轮次时它自然跟着走。
    /// </summary>
    public const string SummaryMetadataKey = "turn_summary";

    /// <summary>
    /// 一条回复总结最多保留多少字符。
    ///
    /// 这个值要足够大：一轮几十次工具调用的回复，真正的信息（改了哪些文件、哪些函数、
    /// 错误原文、下一步）写全就是几千字。卡在 6000 时大轮次的总结会被硬切在半句上，
    /// 读起来像是模型没写完 —— 而截断点之后恰恰是最新的进度与待办，最不该丢。
    /// 20000 字约 15000 token，比「一轮原本占十万」仍然小一个量级。
    /// </summary>
    public const int SummaryCharLimit = 20000;

    /// <summary>喂给摘要模型时，单条工具结果的截断长度。</summary>
    private const int ToolOutputLimit = 2000;

    /// <summary>压缩记录在 metadata 里的标记。</summary>
    public static string SerializeMetadata() => JsonSerializer.Serialize(new { compress = true });

    /// <summary>
    /// 是否是压缩记录（整体压缩，同时是上下文的分界线）。
    ///
    /// <b>role 与 metadata 标记都要满足</b>，这是刻意选的安全方向：万一标记因为任何原因丢了，
    /// 这里返回 false，于是退化成「发送完整历史」——只是贵，不会悄悄丢掉一大段上下文。
    /// 反过来（只看标记）一旦误判就会莫名其妙地少发内容，那种错很难被发现。
    /// </summary>
    public static bool IsCompression(ChatMessage message) => HasMarker(message, CompressRole, CompressFlag);

    /// <summary>
    /// 这条消息身上有没有那一轮的回复总结。
    ///
    /// <b>先读后写的合并入口见 <see cref="MergeSummaryMetadata"/></b>：metadata 是共享的，
    /// assistant 行里已经有 <c>tool_calls</c>、tool 行里可能有图片路径，整体覆盖会把它们抹掉。
    /// </summary>
    public static bool TryGetTurnSummary(ChatMessage message, out string summary)
    {
        summary = "";

        var metadata = ReadMetadataObject(message.Metadata);
        if (metadata is null) return false;

        if (!metadata.TryGetValue(SummaryMetadataKey, out var value)) return false;
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return false;

        summary = text;
        return true;
    }

    /// <summary>
    /// 把总结写进 <paramref name="metadata"/>，<b>保留其中原有的其他键</b>。
    ///
    /// 整体重建 metadata 是这条路上最容易犯的错：assistant 行的 <c>tool_calls</c> 一被抹掉，
    /// 下一轮请求就会因为「有 tool_calls 却没有 tool 回它」被服务端 400 拒掉 ——
    /// 而症状是「这个会话突然发不出去了」，很难联想到是总结干的。
    /// </summary>
    public static string MergeSummaryMetadata(string? metadata, string summary)
    {
        var source = ReadMetadataObject(metadata) ?? new Dictionary<string, object?>();
        source[SummaryMetadataKey] = summary;
        return JsonSerializer.Serialize(source);
    }

    private static Dictionary<string, object?>? ReadMetadataObject(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;

        try
        {
            using var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => property.Value.TryGetInt64(out var number)
                        ? number
                        : property.Value.GetDouble(),
                    JsonValueKind.Array => property.Value.GetRawText(),
                    // 嵌套对象也按原始 JSON 存回去：本类只关心自己的键，别人的结构一个字节都不动
                    _ => property.Value.GetRawText(),
                };

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasMarker(ChatMessage message, string role, string flag)
    {
        if (message.Role != role) return false;
        if (string.IsNullOrWhiteSpace(message.Metadata)) return false;

        try
        {
            using var document = JsonDocument.Parse(message.Metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(flag, out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>最后一条压缩记录的下标；没有则 -1。</summary>
    public static int LastCompressionIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (IsCompression(messages[i]))
                return i;

        return -1;
    }

    /// <summary>
    /// 发给这一轮请求时，末尾保留几轮原文。
    ///
    /// <b>功能关掉就是「一直全发」</b>（<see cref="int.MaxValue"/> 轮），此时一条总结都不会被换上。
    /// 打开后按设置保留最近 N 轮，更早的轮换成它自己的总结。
    ///
    /// <b>空闲降级</b>：距最后一条消息已经超过空闲时间，说明服务端的提示词缓存早已过期，
    /// 这时「保留最近几轮原文」只换来一次实实在在的缓存写入，不如干脆只发总结（0 轮）。
    /// 缓存还在有效期内时不动窗口 —— 前缀不变才可能继续命中。
    /// </summary>
    public static int ResolveKeepTurns(IReadOnlyList<ChatMessage> messages)
    {
        var settings = AppSettingsStore.Current;
        if (!settings.TurnSummaryEnabled) return int.MaxValue;

        var keep = Math.Max(0, settings.TurnSummaryKeepTurns);
        var idle = settings.TurnSummaryIdleMinutes;
        if (idle <= 0 || keep <= 0 || messages.Count == 0) return keep;

        var last = messages[^1].CreatedAt;
        if (last > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last > idle * 60_000L)
            return 0;

        return keep;
    }

    /// <summary>
    /// 送给模型的上下文，做两件事（顺序不能反）：
    /// <list type="number">
    ///   <item>整体压缩的分界线：最后一条压缩记录之前的全部丢弃。</item>
    ///   <item>轮内替换：从最后一条压缩记录往后数，<b>最近 <paramref name="keepRecentTurns"/> 轮</b>原样发送；
    ///   更早的轮如果已经有总结，就把「它的 user 消息 + 一条 assistant 总结」发出去，其余过程全部省掉。</item>
    /// </list>
    ///
    /// <b>user 消息永远原样发送</b>：用户自己说过的话不该被二手转述。
    /// <b>没有总结的轮也原样发送</b>：只留下「用户问了、没人回答」会让模型以为那件事还没做。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildWireHistory(
        IReadOnlyList<ChatMessage> messages, int keepRecentTurns)
    {
        var start = LastCompressionIndex(messages);
        if (start < 0) start = 0;

        // 这一轮要发多少条原文，取决于切片里有几轮：「保留最近 3 轮」得先知道最后一轮是第几轮
        var roundCount = 0;
        for (var i = start; i < messages.Count; i++)
            if (messages[i].Role == "user")
                roundCount++;

        // 前 firstKept 轮是「可以换成总结」的，之后的是「原样发送」的
        var firstKept = roundCount - Math.Max(0, keepRecentTurns);

        // 总结挂在那一轮最后一条消息的 metadata 上，所以要先扫一遍把「轮 → 总结」建起来：
        // 边走边取是不行的 —— 轮到那条消息时，这一轮前面的消息已经发完了。
        var summaries = CollectTurnSummaries(messages, start);

        var result = new List<ChatMessage>(messages.Count);
        var round = -1;
        var roundTurnId = "";
        var replaceable = false;
        var summaryEmitted = false;

        for (var i = start; i < messages.Count; i++)
        {
            var m = messages[i];

            if (m.Role == "user")
            {
                round++;
                roundTurnId = "";
                replaceable = round < firstKept;
                summaryEmitted = false;
                result.Add(m);
                continue;
            }

            // 第一条 user 之前的内容（压缩记录 / 孤立的历史）：原样带上，不做轮内替换
            if (round < 0)
            {
                result.Add(IsCompression(m) ? ToWireMessage(m) : m);
                continue;
            }

            // 一轮的 turn_id 取它第一条 AI 消息的
            if (roundTurnId.Length == 0) roundTurnId = m.TurnId;

            if (!replaceable)
            {
                result.Add(IsCompression(m) ? ToWireMessage(m) : m);
                continue;
            }

            if (summaries.TryGetValue(roundTurnId, out var summary))
            {
                if (!summaryEmitted)
                {
                    result.Add(ToWireSummary(m, summary));
                    summaryEmitted = true;
                }

                continue;
            }

            // 没有总结：这一轮退回「原样发送」，后面的消息也跟着原样走
            replaceable = false;
            result.Add(IsCompression(m) ? ToWireMessage(m) : m);
        }

        return result;
    }

    /// <summary>把「轮 turn_id → 该轮的总结正文」扫出来。同一个键出现多次时后面的覆盖前面的（不该发生）。</summary>
    private static Dictionary<string, string> CollectTurnSummaries(
        IReadOnlyList<ChatMessage> messages, int start)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = start; i < messages.Count; i++)
            if (TryGetTurnSummary(messages[i], out var summary))
                summaries[messages[i].TurnId] = summary;

        return summaries;
    }

    /// <summary>
    /// 某一轮有没有总结，有就取出来给界面用（操作栏上的「已总结」）。
    /// 一轮只可能有一条消息带总结（写在最后一条上），所以命中即可返回。
    /// </summary>
    public static string? FindTurnSummary(IEnumerable<ChatMessage> turnMessages)
    {
        foreach (var message in turnMessages)
            if (TryGetTurnSummary(message, out var summary))
                return summary;

        return null;
    }

    /// <summary>
    /// 该不该整体压缩：分界线之后至少还要有一条消息，否则压了也是白压
    /// （自动压缩每轮都会问一次，没这条判断就会反复对同一批内容发摘要请求）。
    ///
    /// <paramref name="keepRecentTurns"/> 是「保留最近 N 轮原文」，见 <see cref="SliceForSummary"/>：
    /// 保留窗口吃掉全部可压内容时这里返回 <c>false</c> —— 那种情况压了等于没压。
    /// </summary>
    public static bool HasCompressibleContent(IReadOnlyList<ChatMessage> messages, int keepRecentTurns = 0)
    {
        var slice = SliceForSummary(messages, keepRecentTurns);
        // 只有压缩记录本身时压了也是白压（第一轮压缩没有记录，所以那个 1 只在有记录时扣）
        return slice.Count > (LastCompressionIndex(messages) >= 0 ? 1 : 0);
    }

    /// <summary>
    /// 整体压缩的输入范围：从最后一条压缩记录（含）到末尾。
    /// 含它自己是关键——第二轮的输入里带着第一轮的摘要，信息不会断层。
    ///
    /// <paramref name="keepRecentTurns"/> &gt; 0 时末尾那段<b>不进摘要、原样留着</b>：
    /// 压缩过去之后，最近几轮的原文仍然逐字发给模型（工具调用、报错原文、文件路径都在），
    /// 只有更早的部分变成摘要。切点落在一条 user 消息上，不会把一轮劈成两半。
    ///
    /// 轮数不够（可压内容本来就不足 N 轮）时返回空：宁可什么都不压，
    /// 也不要把半轮历史塞进摘要再从原文里删掉。
    /// </summary>
    public static IReadOnlyList<ChatMessage> SliceForSummary(
        IReadOnlyList<ChatMessage> messages, int keepRecentTurns = 0)
    {
        var start = LastCompressionIndex(messages);
        if (start < 0) start = 0;
        if (start >= messages.Count) return Array.Empty<ChatMessage>();

        var end = KeptWindowStart(messages, start, keepRecentTurns);
        if (end <= start) return Array.Empty<ChatMessage>();

        return messages.Skip(start).Take(end - start).ToList();
    }

    /// <summary>
    /// 保留窗口的第一条消息下标（也就是摘要能切到的位置）。
    ///
    /// 从末尾往回数第 <paramref name="keepRecentTurns"/> 条 user 消息就是窗口的起点：
    /// 一条 user 消息 = 一轮的开头，所以切在这里不会出现「有 tool 结果没有它的调用」那种半轮。
    /// 轮数不足或 <paramref name="keepRecentTurns"/> &lt;= 0 时返回末尾（整段都可压）。
    /// </summary>
    private static int KeptWindowStart(
        IReadOnlyList<ChatMessage> messages, int from, int keepRecentTurns)
    {
        if (keepRecentTurns <= 0) return messages.Count;

        var seen = 0;
        for (var i = messages.Count - 1; i >= from; i--)
        {
            if (messages[i].Role != "user") continue;
            if (++seen >= keepRecentTurns) return i;
        }

        return from;   // 轮数不够：可压内容全都落在保留窗口里
    }

    /// <summary>
    /// 压缩记录转成真正发给模型的那条消息。
    ///
    /// ⚠️ 必须把 role 换成 <c>user</c>：客户端（<c>OpenAiChatClient.BuildMessages</c>）的 role 分支只认
    /// user / assistant / tool，<see cref="CompressRole"/> 会<b>被静默丢弃</b>——摘要等于白生成。
    /// </summary>
    private static ChatMessage ToWireMessage(ChatMessage source) => new()
    {
        Id = source.Id,
        SessionId = source.SessionId,
        TurnId = source.TurnId,
        SequenceNumber = source.SequenceNumber,
        Role = "user",
        Content = WrapSummary(source.Content),
        Status = source.Status,
        ModelName = source.ModelName,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        // 刻意不带 metadata：那是「这是压缩记录」的内部标记，没必要发到线上
    };

    /// <summary>
    /// 回复总结转成发给模型的那条消息：<b>role 是 assistant</b>。
    ///
    /// 它替代的本来就是「AI 那一轮说过的话」，换成 user 会读成「用户又提了一个任务」——
    /// 上下文会变成 user 提问、user 复述，模型分不清自己是在哪一侧。
    ///
    /// <paramref name="template"/> 只借 id / turn_id / 时间这些标识，正文与 metadata 一律不用
    /// （那条消息自身的内容已经被这段总结替代了）。
    /// </summary>
    private static ChatMessage ToWireSummary(ChatMessage template, string summary) => new()
    {
        Id = template.Id,
        SessionId = template.SessionId,
        TurnId = template.TurnId,
        SequenceNumber = template.SequenceNumber,
        Role = "assistant",
        Content = WrapTurnSummary(summary),
        Status = template.Status,
        ModelName = template.ModelName,
        CreatedAt = template.CreatedAt,
        UpdatedAt = template.UpdatedAt,
    };

    /// <summary>给整体压缩的摘要加个入出界的说明，免得模型把摘要当成用户刚说的话。</summary>
    private static string WrapSummary(string? summary)
        => "【以下是本次对话此前内容的压缩摘要，请把它当作已经发生过的对话背景，不要当成新问题】\n\n"
           + (summary ?? "").Trim()
           + "\n\n【此前对话摘要结束，其后是摘要之后新产生的消息】";

    /// <summary>
    /// 给回复总结加上「这是二手信息」的说明。这句提示不是装饰：
    /// 摘要里写着「读取了 X.cs」，模型会以为自己还记得那个文件，于是凭印象改代码而不是重读一遍。
    /// 明确要求「细节不在上下文里」之后，这种自信的错误才会变成一次正常的重读。
    /// </summary>
    private static string WrapTurnSummary(string? summary)
        => "【以下是你此前一次回复的自动总结：那次回复的完整过程（工具调用与输出）已不在上下文中。"
           + "其中的文件名、函数名、行号只是线索，动手前请重新读取原文，不要凭这段摘要臆测具体内容】\n\n"
           + (summary ?? "").Trim();

    /// <summary>
    /// 整体压缩的请求正文：把要压缩的消息拼成一份可读的逐条记录。
    /// 工具结果单独截断（它们动辄上万字，全塞进去等于没压缩），但保留工具名。
    /// </summary>
    public static string BuildSummaryRequest(IReadOnlyList<ChatMessage> slice)
    {
        var builder = new StringBuilder();
        builder.AppendLine("以下是需要你压缩的对话内容（按时间顺序）：");
        builder.AppendLine();

        foreach (var message in slice)
        {
            var label = message.Role switch
            {
                "user" => "用户",
                "assistant" => "助手",
                CompressRole => "【上一轮压缩摘要】",
                "tool" => $"工具结果（{message.ToolName}）",
                _ => message.Role,
            };

            builder.Append("== ").Append(label).AppendLine(" ==");

            var content = message.Role == "tool"
                ? Truncate(message.Content, ToolOutputLimit)
                : message.Content;

            builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "（无正文）" : content.Trim());

            // 附件的路径不用另行补充：正文里存的就是 [agent-file:///…] 标记，路径随正文一起
            // 进摘要，原本在这里从 metadata 里捞一遍纯属重复。
            // 而且那样还漏 —— 用户手打的标记在 metadata 里没有，只有正文里有。

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 回复总结的请求正文：一轮用户消息 + 那一轮的 AI 回复（正文与工具结果）。
    ///
    /// 用户那句话要带上：摘要里最要紧的是「这一轮在干什么」，
    /// 而这件事只写在用户消息里 —— AI 自己的过程叙述常常看不出任务目标。
    /// </summary>
    public static string BuildTurnSummaryRequest(string? userText, IReadOnlyList<ChatMessage> turnMessages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("以下是需要你总结的一次 AI 回复（按时间顺序）：");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(userText))
        {
            builder.AppendLine("== 用户这次的要求 ==");
            builder.AppendLine(userText.Trim());
            builder.AppendLine();
        }

        foreach (var message in turnMessages)
        {
            var label = message.Role == "tool"
                ? $"工具结果（{message.ToolName}）"
                : "助手";

            builder.Append("== ").Append(label).AppendLine(" ==");

            var content = message.Role == "tool"
                ? Truncate(message.Content, ToolOutputLimit)
                : message.Content;

            builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "（无正文）" : content.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 整体压缩的提示词。分节结构是用户指定的：目标是让摘要拿掉原文之后仍然够用——
    /// 尤其是「用户明确要求过什么」「已经定下来的结论」「改过哪些文件」「还没做完的事」，
    /// 这四类信息丢了之后继续对话最容易出岔子。
    /// </summary>
    public const string SummarySystemPrompt =
        """
        你的任务是把一段对话压缩成一份详细摘要，重点关注用户明确提出的要求和已经做过的事情。
        摘要必须包含以下分节：
        1. 用户的核心请求与意图
        2. 涉及的关键概念与决策
        3. 涉及的文件、代码或数据（有路径就写全路径，并说明它被改成了什么样）
        4. 出现过的错误与修复方式
        5. 问题解决过程与已达成的结论
        6. 用户说过的所有要求（逐条列出，包括语气偏好、格式偏好、明确的禁止事项）
        7. 待办事项
        8. 当前进行到哪一步
        9. 下一步该做什么（可以引用最近对话里的原话）

        要求：
        - 用中文输出，纯文本，不要用 Markdown 标题符号（#）之外的装饰。
        - 忠于原文，不要添加原文中没有的结论；不确定的地方写「不确定」。
        - 某节确实没有内容时写「无」，不要省略这一节。
        - 直接输出摘要正文，不要写「好的」「以下是摘要」这类前言后语。
        """;

    /// <summary>
    /// 回复总结的提示词。
    ///
    /// <b>要求「照抄标识符」是刻意的</b>：换成总结之后最容易丢的从来不是「做了什么」，
    /// 而是函数名、变量名、文件全路径、错误原文、行号 —— 那些东西写错一个字就用不了，
    /// 而「改了 X 的解析逻辑」这种话本身是记不错也查不了的。
    /// </summary>
    public const string TurnSummarySystemPrompt =
        """
        你的任务是总结「AI 助手的一次回复」：这次回复里它读过哪些文件、执行过哪些命令、
        得出了什么结论、改了哪些东西、做到哪一步、还剩什么没做。这段总结之后会替代这次回复的
        全部过程记录进入后续上下文，所以它必须让后来的自己「知道发生过什么」，而不是复述过程。

        必须包含以下分节：
        1. 这次回复的任务目标（用用户的原话概括）
        2. 做过的操作：读过 / 写过的文件（写全路径）、执行过的关键命令及其结论
        3. 结论与已确认的事实（原文里已经验证过的，不是推测）
        4. 改动明细：每个被修改的文件路径 + 改成什么样（改了哪个函数/类/配置项）
        5. 出现过的错误与修复方式（错误信息尽量照抄原文）
        6. 当前进度：已完成 / 未完成 / 卡在哪里
        7. 下一步建议
        8. 文件存根：每个涉及的文件一行「路径 — 一句话作用 — 本次是否修改」

        要求：
        - 用中文输出，纯文本。
        - 文件名、函数名、类名、变量名、路径、错误信息、关键数值一律照抄原文，不要改写或省略。
        - 不要写「助手读取了文件」这种空话；要写「读了哪个文件的哪一部分、看到了什么」。
        - 忠于原文，不要添加原文中没有的结论；不确定的地方写「不确定」。
        - 某节确实没有内容时写「无」，不要省略这一节。
        - <b>宁长勿短</b>：这次回复做过的事全都写进去，不要为了简短省略具体文件与函数名。
          步骤很多时按阶段罗列，每阶段一行，不要概括成「做了一系列修改」。
        - 先把 1~8 节写完；确实写不下时，优先保留第 4、6、7 节（改动、进度、下一步）。
        - 不要写任何前言后语，直接输出总结正文。
        """;

    /// <summary>
    /// 功能打开时追加到 system 提示词里的一段说明。
    /// 关着时返回空串 —— 提示词与升级前完全一致，行为不会因为多出这段话而变化。
    /// </summary>
    public static string BuildSystemDirective()
    {
        if (!AppSettingsStore.Current.TurnSummaryEnabled) return "";

        return """
            上下文里可能出现以「【以下是你此前一次回复的自动总结」开头的 assistant 消息，
            那是框架自动生成的历史回复摘要，原本的工具调用与输出已经不在上下文里。
            看到这类消息时：把它当作「确实发生过的背景」继续工作，但其中的文件内容、函数体、
            具体数值都只是线索 —— 需要依赖细节时，必须重新读取原文或重新执行工具确认，
            不要凭摘要里的描述直接改写文件。
            """;
    }

    /// <summary>
    /// 一轮 AI 回复有多大（按会真正发回模型的那些正文估算）。
    /// 阈值判断用它：只有一次小工具调用的轮次不需要总结 —— 省下的还没总结花掉的多。
    /// 思考内容不算：它不随请求发回服务端（见 <c>OpenAiChatClient.BuildMessages</c>）。
    /// </summary>
    public static long EstimateTurnTokens(IEnumerable<ChatMessage> turnMessages)
    {
        long total = 0;
        foreach (var message in turnMessages)
        {
            if (message.Role is not ("assistant" or "tool")) continue;
            total += EstimateTokens(message.Content);
        }

        return total;
    }

    /// <summary>把摘要裁到 <see cref="SummaryCharLimit"/> 以内，超了留个说明。</summary>
    public static string ClampSummary(string? summary)
    {
        var text = (summary ?? "").Trim();
        return text.Length <= SummaryCharLimit
            ? text
            : text[..SummaryCharLimit] + "\n\n…（总结已超过长度上限，后面部分未能保留）";
    }

    /// <summary>
    /// 粗略估算一段文本的 token 数，<b>只在服务商没回 usage 时兜底</b>。
    /// 中文字约占 0.75 个 token，按字数取这个偏保守的比例即可，不追求准确。
    /// </summary>
    public static long EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (long)Math.Ceiling(text.Length * 0.75);

    private static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= limit ? text : text[..limit] + $"\n…（已截断，原文 {text.Length} 字）";
    }
}
