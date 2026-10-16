using System.IO;
using System.Linq;
using System.Text.Json;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 附件草稿逻辑。覆盖的是「难复现、易写错」的部分：token 碰撞、光标处整体删除、
/// 孤儿清理、路径展开、草稿序列化/恢复。这些走界面点极难验证 ——
/// 出问题的表现往往是「偶尔多留一个 @图片1」或「光标跳了」，靠手测基本抓不住。
/// </summary>
public class AttachmentDraftTests
{
    private static ChatAttachment Attach(string path, string label = "图片")
        => new() { Path = path, Label = label, Kind = "image" };

    /// <summary>用真实存在的临时路径，避免依赖文件系统状态。</summary>
    private static string TempPath(string name)
        => Path.Combine(Path.GetTempPath(), name);

    private static AttachmentDraft DraftWith(params string[] paths)
    {
        var draft = new AttachmentDraft();
        var text = "";
        var caret = 0;
        foreach (var p in paths)
        {
            Assert.True(draft.TryAdd(Attach(p), text, caret, out var edit));
            text = edit.Text;
            caret = edit.Caret;
        }

        return draft;
    }

    // ---- 添加 ----

    [Fact]
    public void 添加附件会在正文里插入占位符而不是路径()
    {
        var draft = new AttachmentDraft();
        var added = draft.TryAdd(Attach(@"C:\分 享\很长的名字.png"), "看看这个", 4, out var edit);

        Assert.True(added);
        Assert.Equal("看看这个 @图片1 ", edit.Text);
        // 关键：正文里绝不能出现绝对路径 —— 路径会把句子冲散，用户也无法调整它的位置
        Assert.DoesNotContain(@"C:\分 享", edit.Text);
        Assert.Equal(edit.Text.Length, edit.Caret);
    }

    [Fact]
    public void 前面已是空白时不再补空格()
    {
        var draft = new AttachmentDraft();
        draft.TryAdd(Attach(TempPath("a.png")), "看 ", 2, out var edit);
        Assert.Equal("看 @图片1 ", edit.Text);
    }

    [Fact]
    public void 同一个路径不会被附加两次()
    {
        var draft = DraftWith(TempPath("dup.png"));

        var again = draft.TryAdd(Attach(TempPath("dup.png")), "x", 1, out var edit);

        Assert.False(again);
        Assert.Equal("x", edit.Text);          // 正文不该被动过
        Assert.Equal(1, draft.Count);
    }

    [Fact]
    public void 路径比较不区分大小写()
    {
        var draft = DraftWith(@"C:\Pics\Shot.PNG");
        Assert.True(draft.HasPath(@"c:\pics\shot.png"));
    }

    [Fact]
    public void 多个附件的占位符按序号递增()
    {
        var draft = DraftWith(TempPath("1.png"), TempPath("2.png"), TempPath("3.png"));

        Assert.Equal(new[] { "@图片1", "@图片2", "@图片3" }, draft.Items.Select(i => i.Token));
    }

    // ---- token 碰撞 ----
    //
    // 用户手打的正文里可能本来就有「@图片1」这样的字。占位符必须避开它，
    // 否则展开时会把用户的原文一起替换成路径。

    [Fact]
    public void 占位符会避开正文里已有的同名字符串()
    {
        var draft = new AttachmentDraft();
        draft.TryAdd(Attach(TempPath("x.png")), "请参考 @图片1 这个说法", 0, out var edit);

        Assert.Contains("@图片2", edit.Text);
        Assert.Equal("@图片2", draft.Items.Single().Token);
    }

    [Fact]
    public void 占位符会避开已有附件占用的序号()
    {
        var draft = DraftWith(TempPath("a.png"));
        var text = "@图片1 已被占用";
        draft.TryAdd(Attach(TempPath("b.png")), text, text.Length, out var edit);

        Assert.Equal("@图片2", draft.Items.Last().Token);
    }

    // ---- 光标处的整体删除 ----

    [Fact]
    public void 退格键在光标紧邻占位符末尾时整体删除()
    {
        var draft = DraftWith(TempPath("a.png"));      // 正文 "@图片1 "
        var text = draft.Items.Single().Token + "后面";
        var caret = draft.Items.Single().Token.Length;

        Assert.True(draft.TryDeleteBefore(text, caret, caret, caret, out var edit));
        Assert.DoesNotContain("@图片1", edit.Text);
        Assert.Empty(draft.Items);
        Assert.Equal(0, edit.Caret);
    }

    [Fact]
    public void 退格键在光标不紧邻占位符时不接管()
    {
        var draft = DraftWith(TempPath("a.png"));
        var text = draft.Items.Single().Token + "后面";

        Assert.False(draft.TryDeleteBefore(text, text.Length, text.Length, text.Length, out _));
    }

    [Fact]
    public void 有选区时前后删除都不接管()
    {
        var draft = DraftWith(TempPath("a.png"));
        var text = draft.Items.Single().Token;

        // 有选区时应当走 TextBox 自己的删除逻辑，否则会删掉选区之外的东西
        Assert.False(draft.TryDeleteBefore(text, text.Length, 0, 3, out _));
        Assert.False(draft.TryDeleteAfter(text, 0, 0, 3, out _));
        Assert.Equal(1, draft.Count);
    }

    [Fact]
    public void 删除键在光标紧邻占位符开头时整体删除并吞掉尾随空格()
    {
        var draft = DraftWith(TempPath("a.png"));
        var text = draft.Items.Single().Token + " 尾巴";

        Assert.True(draft.TryDeleteAfter(text, 0, 0, 0, out var edit));
        Assert.Equal("尾巴", edit.Text);
        Assert.Empty(draft.Items);
    }

    // ---- 孤儿清理 ----

    [Fact]
    public void 正文里占位符被手工删掉后附件随之清掉()
    {
        var draft = DraftWith(TempPath("a.png"), TempPath("b.png"));
        var token1 = draft.Items[0].Token;

        // 模拟用户选中删除了第一个占位符
        var text = $"只剩第二个 {draft.Items[1].Token} ";

        Assert.True(draft.Prune(text));
        Assert.Single(draft.Items);
        Assert.DoesNotContain(draft.Items, i => i.Token == token1);
    }

    [Fact]
    public void 正文没变时孤儿清理不动任何东西()
    {
        var draft = DraftWith(TempPath("a.png"));
        var text = draft.Items.Single().Token + " ";

        Assert.False(draft.Prune(text));
        Assert.Equal(1, draft.Count);
    }

    // ---- 路径展开 ----

    [Fact]
    public void 展开把占位符换成真实路径且保留位置()
    {
        var path = TempPath("a.png");
        var draft = DraftWith(path);
        var token = draft.Items.Single().Token;

        var expanded = draft.Expand($"前面 {token} 后面");

        Assert.Contains(path, expanded);
        Assert.DoesNotContain(token, expanded);
        // 位置信息要保住：模型看到的是「图片在句子中间」
        Assert.StartsWith("前面 ", expanded);
        Assert.EndsWith(" 后面", expanded);
    }

    [Fact]
    public void 没有附件时展开是恒等变换()
    {
        var draft = new AttachmentDraft();
        Assert.Equal("原样返回", draft.Expand("原样返回"));
    }

    [Fact]
    public void 移除附件同时从正文里摘掉占位符()
    {
        var draft = DraftWith(TempPath("a.png"));
        var item = draft.Items.Single();
        var text = $"前面 {item.Token} 后面";

        var edit = draft.Remove(item, text, text.Length);

        Assert.Empty(draft.Items);
        Assert.DoesNotContain(item.Token, edit.Text);
        Assert.Contains("前面", edit.Text);
        Assert.Contains("后面", edit.Text);
    }

    // ---- 草稿序列化 ----

    /// <summary>按草稿实际落库的形状拼一份 JSON（与 AttachmentDraft 内部的私有 record 同形）。</summary>
    private static string RawDraft(string text, params (string Token, string Path)[] attachments)
        => JsonSerializer.Serialize(new
        {
            Text = text,
            Attachments = attachments.Select(a => new { a.Token, a.Path }).ToList(),
        });

    [Fact]
    public void 无附件时草稿就是纯文本()
    {
        var draft = new AttachmentDraft();
        Assert.Equal("就是原话", draft.Serialize("就是原话"));
    }

    [Fact]
    public void 带附件的草稿能完整往返()
    {
        var path = TempPath("往返.png");
        var draft = DraftWith(path);
        var text = $"看看 {draft.Items.Single().Token} ";

        var restored = new AttachmentDraft();
        var edit = restored.Restore(draft.Serialize(text));

        Assert.Equal(text, edit.Text);
        var item = Assert.Single(restored.Items);
        Assert.Equal(path, item.Info.Path);
        Assert.Equal(draft.Items.Single().Token, item.Token);
    }

    [Fact]
    public void 纯文本草稿按原文恢复()
    {
        var draft = new AttachmentDraft();
        var edit = draft.Restore("一段没发出去的话");

        Assert.Equal("一段没发出去的话", edit.Text);
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void 空草稿恢复成空输入()
    {
        var edit = new AttachmentDraft().Restore(null);
        Assert.Equal("", edit.Text);
        Assert.Equal(0, edit.Caret);
    }

    [Fact]
    public void 损坏的JSON按纯文本恢复而不是丢弃正文()
    {
        // 用户可能写了很久的内容。JSON 坏了宁可少两个附件，也绝不能把正文弄丢。
        const string broken = "{\"text\":\"没写完";
        var draft = new AttachmentDraft();
        var edit = draft.Restore(broken);

        Assert.Equal(broken, edit.Text);
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void 路径非法的附件被丢掉但正文保住()
    {
        var draft = new AttachmentDraft();
        var edit = draft.Restore(RawDraft("看看 @图片1 ", ("@图片1", "")));

        Assert.Equal("看看 @图片1 ", edit.Text);
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void 恢复后正文与附件对不上时以正文为准()
    {
        // 库里存着两个附件，但正文里只有一个占位符（比如上次落库的时序出了问题）：
        // 以正文为准，多出来的附件应当被清掉，否则 chip 栏会显示一个正文里根本不存在的东西。
        var draft = new AttachmentDraft();
        draft.Restore(RawDraft(
            "@图片1 ",
            ("@图片1", TempPath("keep.png")),
            ("@图片9", TempPath("stale.png"))));

        var item = Assert.Single(draft.Items);
        Assert.Equal("@图片1", item.Token);
    }

    [Fact]
    public void Clear清空附件但保留正文()
    {
        var draft = DraftWith(TempPath("a.png"));
        draft.Clear();

        Assert.True(draft.IsEmpty);
        Assert.Equal("发送后正文由调用方处理", draft.Expand("发送后正文由调用方处理"));
    }

    // ---- 采纳正文里的标记（粘贴带标记的文本） ----
    //
    // 注意这里的路径一律用 Path.GetTempPath() 拼出来，不写死 "C:/..."：
    // AttachmentStore.FromPath 会走 Path.GetFullPath，而 POSIX 上 "C:/a.png"
    // 不是绝对路径，会被解析成「当前目录下的 C:/a.png」，测试就测不到东西了。

    private static string MarkerFor(string path) => AttachmentMarker.Format(path);

    [Fact]
    public void 粘贴标记时换成占位符并登记为附件()
    {
        var path = TempPath("adopt1.png");
        var draft = new AttachmentDraft();

        var adopted = draft.TryAdoptMarkers($"看看这个 {MarkerFor(path)} 对不对", out var edit);

        Assert.True(adopted);
        Assert.DoesNotContain("agent-file", edit.Text);
        Assert.Contains("@图片1", edit.Text);
        var item = Assert.Single(draft.Items);
        Assert.Equal(path, item.Info.Path);
    }

    [Fact]
    public void 粘贴的标记前后补上空白分隔()
    {
        var path = TempPath("adopt2.png");
        var draft = new AttachmentDraft();

        // 标记原本紧贴着文字，换成占位符后必须能分开读
        draft.TryAdoptMarkers($"看看{MarkerFor(path)}这个", out var edit);

        Assert.Equal("看看 @图片1 这个", edit.Text);
    }

    [Fact]
    public void 同一个文件出现两次时复用同一个占位符()
    {
        var path = TempPath("adopt3.png");
        var marker = MarkerFor(path);
        var draft = new AttachmentDraft();

        draft.TryAdoptMarkers($"{marker} 和 {marker}", out var edit);

        // 语义上是同一个附件，只登记一次
        Assert.Single(draft.Items);
        var token = draft.Items.Single().Token;
        Assert.Equal(2, edit.Text.Split(token).Length - 1);
    }

    [Fact]
    public void 没有标记时采纳是空操作()
    {
        var draft = new AttachmentDraft();
        var adopted = draft.TryAdoptMarkers("普通文本", out var edit);

        Assert.False(adopted);
        Assert.Equal("普通文本", edit.Text);
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void 真的file网址不会被采纳()
    {
        var draft = new AttachmentDraft();
        var adopted = draft.TryAdoptMarkers("file:///C:/x.html 是这个", out var edit);

        Assert.False(adopted);
        Assert.Equal("file:///C:/x.html 是这个", edit.Text);
        Assert.Empty(draft.Items);
    }

    [Fact]
    public void 采纳后的正文再展开应当还原成原来的标记()
    {
        // 往返：粘贴标记 → 变成占位符 → 发送时又变回同一个标记。
        // 这条是整套设计成立的关键 —— 复制出去再粘回来必须无损。
        var path = TempPath("adopt 带空格.png");
        var original = MarkerFor(path);

        var draft = new AttachmentDraft();
        draft.TryAdoptMarkers($"看 {original}", out var edit);

        Assert.Equal($"看 {original}", draft.Expand(edit.Text));
    }

    [Fact]
    public void 采纳多个不同标记时各自登记()
    {
        var draft = new AttachmentDraft();
        draft.TryAdoptMarkers(
            $"{MarkerFor(TempPath("adoptA.png"))} {MarkerFor(TempPath("adoptB.txt"))}",
            out var edit);

        Assert.Equal(2, draft.Count);
        Assert.Contains("@图片1", edit.Text);
        Assert.Contains("文本文件", edit.Text);
    }
}
