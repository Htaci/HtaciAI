using System.Collections.Generic;
using System.Linq;
using HtaciAI.Services.Attachments;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 附件标记 <c>[agent-file:///路径]</c> 的编解码。
///
/// 这一层是整套附件渲染的基石：正文里存的是它，渲染、复制、粘贴三处都读它。
/// 出错的表现会很难查（标记被截断、路径少一段、或整段当成了普通文本），
/// 而它本身是纯字符串逻辑 —— 正好适合用测试钉死。
/// </summary>
public class AttachmentMarkerTests
{
    private const string WinPath = @"C:\Users\test\图片.png";

    // ---- 格式化 ----

    [Fact]
    public void 普通路径包成标记()
    {
        Assert.Equal("[agent-file:///C:/Users/test/图片.png]",
            AttachmentMarker.Format("C:/Users/test/图片.png"));
    }

    [Fact]
    public void 空格与中文原样保留不做百分号编码()
    {
        // 这是「最少转义」的核心取舍：格式是要给人看和复制的，
        // `%E6%B5%8B%E8%AF%95` 那样的路径没人愿意面对。
        var m = AttachmentMarker.Format("C:/我的 文档/测试 图.png");
        Assert.Contains("我的 文档", m);
        Assert.Contains("测试 图.png", m);
        Assert.DoesNotContain("%20", m);
        Assert.DoesNotContain("%E6", m);
    }

    [Fact]
    public void 百分号被转义()
    {
        Assert.Equal("[agent-file:///C:/a%25b.png]", AttachmentMarker.Format("C:/a%b.png"));
    }

    [Fact]
    public void 方括号被转义()
    {
        // 不转的话 `]` 会提前截断标记 —— 这是「结束符出现在文件名里」的情形
        Assert.Equal("[agent-file:///C:/a%5Db%5Bc.png]", AttachmentMarker.Format("C:/a]b[c.png"));
    }

    [Fact]
    public void 控制字符被转义()
    {
        Assert.Equal("[agent-file:///C:/a%0Ab.png]", AttachmentMarker.Format("C:/a\nb.png"));
    }

    // ---- 往返 ----

    [Theory]
    [InlineData("C:/Users/test/图片.png")]
    [InlineData("/Users/x/a b.txt")]
    [InlineData("C:/我的 文档/测试 图.png")]
    [InlineData("C:/a%b.png")]
    [InlineData("C:/a]b[c.png")]
    [InlineData("C:/a#b.png")]
    [InlineData("C:/a?b.png")]
    [InlineData("C:/emoji 😀/x.png")]
    [InlineData("C:/a\nb.png")]
    public void 各种路径都能往返(string path)
    {
        var spans = AttachmentMarker.Scan(AttachmentMarker.Format(path));

        var span = Assert.Single(spans);
        Assert.Equal(path, span.Path);
    }

    // ---- 扫描 ----

    [Fact]
    public void 扫描出正文中间的一处标记()
    {
        var spans = AttachmentMarker.Scan("看看这个 [agent-file:///C:/a.png] 对不对");

        var span = Assert.Single(spans);
        Assert.Equal("C:/a.png", span.Path);
        Assert.Equal("看看这个 ".Length, span.Start);   // 起始位置要准，渲染要按它切文本
        Assert.Equal("[agent-file:///C:/a.png]".Length, span.Length);
    }

    [Fact]
    public void 正文里有多处标记时全部扫出来且顺序正确()
    {
        var text = "[agent-file:///C:/a.png] 和 [agent-file:///C:/b.txt]";
        var paths = AttachmentMarker.Scan(text).Select(s => s.Path).ToList();

        Assert.Equal(new[] { "C:/a.png", "C:/b.txt" }, paths);
    }

    [Fact]
    public void 没有标记时返回空()
    {
        Assert.Empty(AttachmentMarker.Scan("就是一句普通的话"));
        Assert.Empty(AttachmentMarker.Scan(""));
        Assert.Empty(AttachmentMarker.Scan(null));
    }

    [Fact]
    public void 真file网址不会被当成附件()
    {
        // 用户在消息里贴一个从浏览器地址栏复制的本地网址是常有的事，
        // 它应当照常显示成网址 —— agent- 前缀正是为此而加。
        Assert.Empty(AttachmentMarker.Scan("file:///C:/Users/test.html 是这个"));
        Assert.False(AttachmentMarker.Contains("file:///C:/Users/test.html"));
    }

    [Fact]
    public void 未闭合的标记不当成附件()
    {
        Assert.Empty(AttachmentMarker.Scan("[agent-file:///C:/a.png 后面没了"));
    }

    [Fact]
    public void 路径跨行时视为未闭合()
    {
        // 路径不可能含换行，遇到换行说明这不是一个完整标记
        Assert.Empty(AttachmentMarker.Scan("[agent-file:///C:/a\nb.png]"));
    }

    [Fact]
    public void 转义过的结束符不会提前截断标记()
    {
        // %5D 是转义后的 `]`，扫描时必须跳过它，否则会在文件名中间断掉
        var spans = AttachmentMarker.Scan("[agent-file:///C:/a%5Db.png] 尾巴");

        var span = Assert.Single(spans);
        Assert.Equal("C:/a]b.png", span.Path);
        Assert.Equal(0, span.Start);
        Assert.Equal("[agent-file:///C:/a%5Db.png]".Length, span.Length);
    }

    [Fact]
    public void 空载荷的标记被忽略()
    {
        Assert.Empty(AttachmentMarker.Scan("[agent-file:///]"));
    }

    [Fact]
    public void 未闭合的标记不会挡住后面那个完整标记()
    {
        var text = "[agent-file:///坏掉了 [agent-file:///C:/b.png]";

        var spans = AttachmentMarker.Scan(text);

        // 第一个没闭合（里面还有个 `[`），应当跳过它继续找，最终认出第二个
        Assert.Contains(spans, s => s.Path == "C:/b.png");
    }

    // ---- 剥离（标题生成用）----

    [Fact]
    public void 剥离标记时替换成标签而保留普通文字()
    {
        var stripped = AttachmentMarker.StripMarkers("看看这个 [agent-file:///C:/a.png] 对不对");

        Assert.Equal("看看这个 图片 对不对", stripped);
    }

    [Fact]
    public void 附件打头时剥离结果以标签开头()
    {
        // 这是标题生成的关键场景：首行如果是标记，标题不该是那串标记
        var stripped = AttachmentMarker.StripMarkers("[agent-file:///C:/a.png] 帮我看看");

        Assert.StartsWith("图片", stripped);
        Assert.DoesNotContain("agent-file", stripped);
    }

    [Fact]
    public void 没有标记时剥离结果是原文()
    {
        Assert.Equal("普通文本", AttachmentMarker.StripMarkers("普通文本"));
    }

    [Fact]
    public void 多个标记都替换且不粘在一起()
    {
        var stripped = AttachmentMarker.StripMarkers(
            "[agent-file:///C:/a.png][agent-file:///C:/b.txt]");

        Assert.DoesNotContain("agent-file", stripped);
        Assert.Contains("图片", stripped);
    }
}
