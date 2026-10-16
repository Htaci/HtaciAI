using HtaciAI.Services.Attachments;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services;

/// <summary>
/// 会话标题的生成规则。历史上 24 这个长度是散在两处的魔数
/// （ConversationPage.OnNewChatSend 与 ConversationView.SendMessageAsync），这里收拢成一处。
/// </summary>
public static class ConversationTitle
{
    /// <summary>标题最大长度（超出截断并追加省略号）。</summary>
    public const int MaxLength = 24;

    /// <summary>未命名会话的占位标题。</summary>
    public const string Placeholder = "新会话";

    /// <summary>按最大长度截断，超出部分用省略号代替。</summary>
    public static string Truncate(string? text, int max = MaxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>
    /// 首条消息发出时的初始标题。
    ///
    /// <list type="bullet">
    ///   <item>自动命名关闭 → 保持 <see cref="Placeholder"/>，等用户手动改名。</item>
    ///   <item>未配置命名模型 → 直接截断首条消息（这是历史行为）。</item>
    ///   <item>配置了命名模型 → 也先保持占位，等首轮 AI 回复完成后由
    ///   <c>ConversationView.ApplyAutoTitleAsync</c> 生成。</item>
    /// </list>
    ///
    /// 消息里可能带附件标记（<c>[agent-file:///…]</c>）。它们要先换成各自的标签再截断 ——
    /// 否则「附件打头、后面才是文字」的消息，标题会是一串标记，而且 24 字的长度
    /// 会被路径吃光，一点有用信息都留不下。
    /// </summary>
    public static string InitialTitle(string firstMessage)
    {
        var settings = AppSettingsStore.Current;

        if (!settings.AutoTopicNaming) return Placeholder;

        var text = AttachmentMarker.StripMarkers(firstMessage);

        if (string.IsNullOrWhiteSpace(settings.TopicNamingModelId)) return Truncate(text);

        return Placeholder;
    }

    public static bool IsPlaceholder(string? title)
        => string.IsNullOrEmpty(title) || title == Placeholder;
}
