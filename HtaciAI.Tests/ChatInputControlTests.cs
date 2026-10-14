using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using HtaciAI.Models;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 共用输入控件的 UI 级测试（无头宿主，见 <see cref="TestAppBuilder"/>）。
///
/// 覆盖的是「控件接线」这一层：附件加进去之后正文和 chip 栏是否都对、
/// 提交时路径有没有展开、宿主驱动的状态是否能正确切换外观。
/// 纯文本逻辑在 <see cref="AttachmentDraftTests"/> 里，这里不重复。
/// </summary>
public class ChatInputControlTests
{
    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), name);

    private static ChatAttachment Attach(string path, string label = "图片")
        => new() { Path = path, Label = label, Kind = "image" };

    /// <summary>控件放进窗口才走完整的挂载流程（样式、模板都要求有 TopLevel）。</summary>
    private static (Window Window, ChatInputControl Input) Mount()
    {
        var input = new ChatInputControl();
        var window = new Window { Content = input };
        window.Show();
        return (window, input);
    }

    [AvaloniaFact]
    public void 构造不抛异常且初始为空()
    {
        var (_, input) = Mount();

        Assert.Equal("", input.Text);
        Assert.Equal(0, input.AttachmentCount);
    }

    [AvaloniaFact]
    public void 附加文件后正文出现占位符且附件计数增加()
    {
        var (_, input) = Mount();

        input.AddAttachments(new[] { Attach(TempPath("shot.png")) });

        Assert.Equal(1, input.AttachmentCount);
        Assert.Contains("@图片1", input.Text);
        // 正文里绝不能出现绝对路径 —— 那会把句子冲散
        Assert.DoesNotContain(TempPath("shot.png"), input.Text);
    }

    [AvaloniaFact]
    public void 重复附加同一个文件不会产生第二条()
    {
        var (_, input) = Mount();
        var path = TempPath("same.png");

        input.AddAttachments(new[] { Attach(path) });
        input.AddAttachments(new[] { Attach(path) });

        Assert.Equal(1, input.AttachmentCount);
    }

    [AvaloniaFact]
    public void 附件栏在没有附件时不可见有附件时可见()
    {
        var (_, input) = Mount();
        var bar = input.GetVisualDescendants().OfType<WrapPanel>().First();

        Assert.False(bar.IsVisible);

        input.AddAttachments(new[] { Attach(TempPath("a.png")) });
        Assert.True(bar.IsVisible);
    }

    [AvaloniaFact]
    public void 提交时把占位符展开成真实路径并带上附件()
    {
        var (_, input) = Mount();
        var path = TempPath("payload.png");
        input.AddAttachments(new[] { Attach(path) });

        InputSubmit? captured = null;
        input.SendRequested += (_, s) => captured = s;

        input.Submit();

        Assert.NotNull(captured);
        Assert.Contains(path, captured!.Text);
        Assert.DoesNotContain("@图片1", captured.Text);
        var sent = Assert.Single(captured.Attachments);
        Assert.Equal(path, sent.Path);
    }

    [AvaloniaFact]
    public void 空文本且无附件时不提交()
    {
        var (_, input) = Mount();

        var raised = false;
        input.SendRequested += (_, _) => raised = true;

        input.Submit();

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void 只有附件没有文字也能提交()
    {
        var (_, input) = Mount();
        input.AddAttachments(new[] { Attach(TempPath("only.png")) });

        var raised = false;
        input.SendRequested += (_, _) => raised = true;

        input.Submit();

        Assert.True(raised);
    }

    [AvaloniaFact]
    public void 清空会同时清掉正文与附件()
    {
        var (_, input) = Mount();
        input.AddAttachments(new[] { Attach(TempPath("a.png")) });

        input.Clear();

        Assert.Equal("", input.Text);
        Assert.Equal(0, input.AttachmentCount);
    }

    // ---- 宿主驱动的状态 ----
    //
    // 这几条正是「不按页面身份判断」的验证：新建页从不调 SetRunning，
    // 于是停止形态永不出现；对话页调，就切过去。控件本身不需要知道谁在调。

    [AvaloniaFact]
    public void 默认不是运行态因此发送按钮显示发送图标()
    {
        var (_, input) = Mount();

        Assert.False(StopGlyphVisible(input));
    }

    [AvaloniaFact]
    public void 置为运行态后切换成停止图标并点亮等待环()
    {
        var (_, input) = Mount();

        input.SetRunning(true);

        Assert.True(StopGlyphVisible(input));
        Assert.True(input.GetVisualDescendants().OfType<LoadingRing>().First().IsVisible);
    }

    [AvaloniaFact]
    public void 退出运行态后恢复发送图标()
    {
        var (_, input) = Mount();

        input.SetRunning(true);
        input.SetRunning(false);

        Assert.False(StopGlyphVisible(input));
        Assert.False(input.GetVisualDescendants().OfType<LoadingRing>().First().IsVisible);
    }

    [AvaloniaFact]
    public void 传空用量会隐藏用量环()
    {
        var (_, input) = Mount();
        var ring = input.GetVisualDescendants().OfType<ContextUsageRing>().First();

        input.SetContextUsage(null, null, "");
        Assert.False(ring.IsVisible);

        input.SetContextUsage(100, 1000, "某模型");
        Assert.True(ring.IsVisible);
    }

    [AvaloniaFact]
    public void 提示行默认为隐藏显示后可手动收起()
    {
        var (_, input) = Mount();
        var hint = HintBlock(input);

        Assert.False(hint.IsVisible);

        input.ShowHint("正在压缩上下文…", InputHintKind.Info, autoHide: false);
        Assert.True(hint.IsVisible);
        Assert.Equal("正在压缩上下文…", hint.Text);

        input.HideHint();
        Assert.False(hint.IsVisible);
    }

    [AvaloniaFact]
    public void 注入的更多菜单项在运行中被禁用()
    {
        var (_, input) = Mount();

        input.AddMoreItem("压缩上下文", () => { });
        var item = Assert.Single(input.MoreItems);
        Assert.True(item.IsEnabled);

        // 生成中不许再压缩：压缩要在这一轮发出之前定下上下文
        input.SetRunning(true);
        Assert.False(item.IsEnabled);

        input.SetRunning(false);
        Assert.True(item.IsEnabled);
    }

    [AvaloniaFact]
    public void 没有可用模型时发送按钮被禁用()
    {
        var (_, input) = Mount();
        var send = input.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SendBtn");

        input.RefreshSendAvailability("请先添加模型");
        Assert.False(send.IsEnabled);
    }

    // ---- 取内部元素的小工具 ----

    private static bool StopGlyphVisible(ChatInputControl input)
        => input.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "StopGlyph")
            .Select(b => b.IsVisible)
            .FirstOrDefault();

    private static TextBlock HintBlock(ChatInputControl input)
        => input.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HintText");
}
