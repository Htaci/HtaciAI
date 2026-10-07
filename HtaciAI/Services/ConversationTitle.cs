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
    /// </summary>
    public static string InitialTitle(string firstMessage)
    {
        var settings = AppSettingsStore.Current;

        if (!settings.AutoTopicNaming) return Placeholder;
        if (string.IsNullOrWhiteSpace(settings.TopicNamingModelId)) return Truncate(firstMessage);

        return Placeholder;
    }

    public static bool IsPlaceholder(string? title)
        => string.IsNullOrEmpty(title) || title == Placeholder;
}
