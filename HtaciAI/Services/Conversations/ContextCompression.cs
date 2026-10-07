using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 上下文压缩的规则层：一条压缩记录长什么样、怎么判定、以及「发给模型的上下文」怎么截。
///
/// <b>压缩本身就是一条普通消息</b>（<c>chat_message</c> 里的一行），正文是摘要、<c>role</c> 是
/// <see cref="CompressRole"/>、<c>metadata</c> 带 <see cref="CompressFlag"/> 标记。不新建表也不加列，
/// 因为它天然需要 message 的那套东西：id、顺序号、时间、以及跟着会话一起被删除。
///
/// <b>它同时是上下文的分界线</b>：构建请求时，最后一条压缩记录之前的消息全部丢弃，
/// 从这条记录开始往后算。第二次压缩把第一次的摘要一起喂给模型，所以信息是逐轮滚存的，
/// 不会因为反复压缩而一层层衰减。
/// </summary>
public static class ContextCompression
{
    /// <summary>压缩记录的 role 取值。刻意用一个全新的值，与 user/assistant/tool 区分开。</summary>
    public const string CompressRole = "agent";

    /// <summary>metadata 里的标记键。判定时与 role 一起看（见 <see cref="IsCompression"/>）。</summary>
    public const string CompressFlag = "compress";

    /// <summary>喂给摘要模型时，单条工具结果的截断长度。</summary>
    private const int ToolOutputLimit = 2000;

    /// <summary>摘要正文在提示词里要求的分节，见 <see cref="SummarySystemPrompt"/>。</summary>
    public static string SerializeMetadata() => JsonSerializer.Serialize(new { compress = true });

    /// <summary>
    /// 是否是压缩记录。
    ///
    /// <b>role 与 metadata 标记都要满足</b>，这是刻意选的安全方向：万一标记因为任何原因丢了，
    /// 这里返回 false，于是退化成「发送完整历史」——只是贵，不会悄悄丢掉一大段上下文。
    /// 反过来（只看标记）一旦误判就会莫名其妙地少发内容，那种错很难被发现。
    /// </summary>
    public static bool IsCompression(ChatMessage message)
    {
        if (message.Role != CompressRole) return false;
        if (string.IsNullOrWhiteSpace(message.Metadata)) return false;

        try
        {
            using var document = JsonDocument.Parse(message.Metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(CompressFlag, out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
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
    /// 送给模型的上下文：最后一条压缩记录之前的全部丢弃，从它开始往后（含它自己）。
    /// 没有压缩记录时原样返回（不复制，避免每轮白拷一遍长历史）。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildWireHistory(IReadOnlyList<ChatMessage> messages)
    {
        var index = LastCompressionIndex(messages);
        if (index < 0) return messages;

        var result = new List<ChatMessage>(messages.Count - index) { ToWireMessage(messages[index]) };
        for (var i = index + 1; i < messages.Count; i++)
            result.Add(messages[i]);

        return result;
    }

    /// <summary>
    /// 该不该压缩：分界线之后至少还要有一条消息，否则压了也是白压
    /// （自动压缩每轮都会问一次，没这条判断就会反复对同一批内容发摘要请求）。
    /// </summary>
    public static bool HasCompressibleContent(IReadOnlyList<ChatMessage> messages)
        => messages.Count > LastCompressionIndex(messages) + 1;

    /// <summary>
    /// 摘要的输入范围：从最后一条压缩记录（含）到末尾。
    /// 含它自己是关键——第二轮的输入里带着第一轮的摘要，信息不会断层。
    /// </summary>
    public static IReadOnlyList<ChatMessage> SliceForSummary(IReadOnlyList<ChatMessage> messages)
    {
        var start = LastCompressionIndex(messages);
        if (start < 0) start = 0;
        return start >= messages.Count ? Array.Empty<ChatMessage>() : messages.Skip(start).ToList();
    }

    /// <summary>
    /// 压缩记录转成真正发给模型的那条消息。
    ///
    /// ⚠️ 必须把 role 换成 <c>user</c>：客户端（<c>OpenAiExClient.BuildMessages</c>）的 role 分支只认
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

    /// <summary>给摘要正文加个入出界的说明，免得模型把摘要当成用户刚说的话。</summary>
    private static string WrapSummary(string? summary)
        => "【以下是本次对话此前内容的压缩摘要，请把它当作已经发生过的对话背景，不要当成新问题】\n\n"
           + (summary ?? "").Trim()
           + "\n\n【此前对话摘要结束，其后是摘要之后新产生的消息】";

    /// <summary>
    /// 摘要请求的正文：把要压缩的消息拼成一份可读的逐条记录。
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

            // 用户消息带的附件只有路径有意义：正文里的 @图片N 标记到这里已经看不出指向谁
            var attachments = AttachmentJson.Deserialize(message.Metadata);
            if (attachments.Count > 0)
                builder.Append("（本条附带文件：")
                       .Append(string.Join("、", attachments.Select(a => a.Path)))
                       .AppendLine("）");

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 摘要提示词。分节结构是用户指定的：目标是让摘要拿掉原文之后仍然够用——
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

    private static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= limit ? text : text[..limit] + $"\n…（已截断，原文 {text.Length} 字）";
    }
}
