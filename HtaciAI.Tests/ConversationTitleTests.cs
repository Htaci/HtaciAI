using HtaciAI.Services;
using HtaciAI.Services.Attachments;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 会话标题生成。重点是附件打头的那条消息：
/// 标题不该是一串标记，也不该被路径吃光 24 字的长度预算。
/// </summary>
public class ConversationTitleTests
{
    /// <summary>
    /// 取初始标题。行为取决于全局 AppSettingsStore：自动命名关闭时直接是占位标题。
    /// 下面几条断言（尤其是「以标签开头」）在占位标题下会失败，所以它们实际依赖的是
    /// 「自动命名开启 + 未配置命名模型」这条分支 —— 本机默认设置满足它，
    /// 反向验证也确认过去掉 StripMarkers 这几条会红。
    /// </summary>
    private static string Initial(string message) => ConversationTitle.InitialTitle(message);

    [Fact]
    public void 标题里不出现附件标记()
    {
        var title = Initial($"{AttachmentMarker.Format(@"C:\Users\me\一张很长名字的图片.png")} 帮我看看");

        Assert.DoesNotContain("agent-file", title);
        Assert.DoesNotContain(@"C:\Users", title);
    }

    [Fact]
    public void 附件打头时标题以标签开头()
    {
        var title = Initial($"{AttachmentMarker.Format(@"C:\Users\me\a.png")} 帮我看看这个报告");

        Assert.Contains("图片", title);
    }

    [Fact]
    public void 纯文字消息不受影响()
    {
        var title = Initial("帮我写一个快速排序");

        Assert.Equal("帮我写一个快速排序", title);
    }

    [Fact]
    public void 截断长度仍然生效()
    {
        var longText = new string('字', 100);
        var title = Initial(longText);

        Assert.True(title.Length <= ConversationTitle.MaxLength + 1);   // +1 是省略号
    }
}
