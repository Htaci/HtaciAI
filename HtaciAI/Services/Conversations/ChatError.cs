using System;
using System.Text;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 请求出错在消息流里的落地方式：一条 <c>role = "error"</c> 的消息，
/// 正文是「简短原因 + 完整详情」两段（见 <see cref="BuildContent(Exception)"/>），
/// 界面据此拆出错误卡的标题与可展开的详情（见 <c>Controls/ErrorCard</c>）。
///
/// 这是与 user / assistant / tool 并列的第四个 role，但它<b>永远不会发给模型</b>：
/// <c>OpenAiChatClient.BuildMessages</c> 只认那三种，其余静默跳过。它纯粹是留给人看的排查记录。
/// </summary>
public static class ChatError
{
    /// <summary>错误记录的 role 取值。</summary>
    public const string Role = "error";

    /// <summary>详情段的分隔标题。拆分「标题」与「详情」只认它，改这里就够。</summary>
    private const string DetailHeader = "—— 错误详情 ——";

    /// <summary>简短原因的长度上限：更长的异常消息在卡片上只会被截断，存全也没有用。</summary>
    private const int SummaryLimit = 200;

    public static bool IsError(ChatMessage message) => message.Role == Role;

    /// <summary>构造一条错误记录。时间戳交给落库时补（见 <c>ChatRepository.InsertAsync</c>）。</summary>
    public static ChatMessage Create(
        string sessionId, string turnId, int sequenceNumber, string? modelName, Exception ex)
        => new()
        {
            SessionId = sessionId,
            TurnId = turnId,
            SequenceNumber = sequenceNumber,
            Role = Role,
            Content = BuildContent(ex),
            Status = "failed",
            ModelName = modelName,
        };

    /// <summary>正文：首行简短原因，空行之后是详情段。</summary>
    public static string BuildContent(Exception ex) => BuildContent(Summarize(ex), Describe(ex));

    /// <summary>正文：首行简短原因，空行之后是详情段（详情为空时只有一行）。</summary>
    public static string BuildContent(string summary, string detail)
    {
        var text = new StringBuilder(summary.Trim());
        if (!string.IsNullOrWhiteSpace(detail))
            text.Append("\n\n").Append(DetailHeader).Append('\n').Append(detail.Trim());
        return text.ToString();
    }

    /// <summary>
    /// 卡片标题用的简短原因：异常消息里第一行有内容的文本。
    /// HTTP 失败的消息是多行的（首行状态码、其后是服务端返回体），取首行正好得到「401 Unauthorized」。
    /// </summary>
    public static string Summarize(Exception ex)
    {
        var line = FirstLine(ex.Message);
        if (line.Length == 0) return ex.GetType().Name;
        return line.Length <= SummaryLimit ? line : line[..SummaryLimit] + "…";
    }

    /// <summary>完整详情：异常类型、异常消息、逐层内部异常、调用栈。</summary>
    public static string Describe(Exception ex)
    {
        var text = new StringBuilder();
        text.Append("异常类型：").AppendLine(ex.GetType().FullName);
        text.Append("异常消息：").AppendLine(ex.Message.TrimEnd());

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text.AppendLine();
            text.Append("内部异常：").AppendLine(inner.GetType().FullName);
            text.AppendLine(inner.Message.TrimEnd());
        }

        // 调用栈是「方便排查」的关键：没有它，只知道失败了，不知道从哪儿失败
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            text.AppendLine();
            text.AppendLine("调用栈：");
            text.AppendLine(ex.StackTrace.TrimEnd());
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>从已落库的正文里取简短原因（首行）。</summary>
    public static string Summary(string? content) => FirstLine(content);

    /// <summary>从已落库的正文里取详情段；没有详情段时返回整段内容。</summary>
    public static string Detail(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        var index = content.IndexOf(DetailHeader, StringComparison.Ordinal);
        return index < 0 ? content.Trim() : content[(index + DetailHeader.Length)..].Trim();
    }

    /// <summary>第一行非空文本；没有则空串。</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return "";
    }
}
