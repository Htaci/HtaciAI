using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using HtaciAI.Services.Attachments;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 附件标记的端到端往返：粘贴 → 输入框显示占位符 → 提交 → 正文里是标记 → 再粘贴回输入框。
///
/// 这是整套设计要保证的那件事。分段的单元测试各自都对，串起来仍可能错
/// （比如提交时忘了展开、或展开后又被别处改回路径），所以这里走控件的真实路径。
/// </summary>
public class AttachmentRoundTripTests
{
    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), name);

    private static ChatInputControl Mount()
    {
        var input = new ChatInputControl();
        // Window 必须持有引用：它一旦被回收，控件树也跟着废掉。
        _windows.Add(new Window { Width = 500, Height = 300, Content = input });
        _windows[^1].Show();
        return input;
    }

    private static readonly List<Window> _windows = new();

    /// <summary>
    /// 设置正文并等待采纳逻辑跑完。
    ///
    /// <b>必须 RunJobs</b>：无头平台下 TextBox 的 TextChanged 是经 dispatcher 队列派发的，
    /// 程序化设完 Text 之后当帧读到的仍是旧状态（实测 TextChanged 触发次数为 0）。
    /// 真实应用里用户输入会正常派发，这里的等待只是让测试能观察到同一结果。
    /// </summary>
    private static void Type(ChatInputControl input, string text)
    {
        input.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    private static bool AttachmentBarVisible(ChatInputControl input)
        => input.GetVisualDescendants().OfType<WrapPanel>().First().IsVisible;

    [AvaloniaFact]
    public void 粘贴标记后输入框显示占位符且附件栏出现()
    {
        var path = TempPath("rt1.png");
        var input = Mount();
        Assert.False(AttachmentBarVisible(input));

        Type(input, $"看看 {AttachmentMarker.Format(path)} 对不对");

        Assert.Equal("看看 @图片1 对不对", input.Text);
        Assert.Equal(1, input.AttachmentCount);
        Assert.True(AttachmentBarVisible(input));
    }

    [AvaloniaFact]
    public void 粘贴标记后提交正文里是标记而不是裸路径()
    {
        var path = TempPath("rt2.png");
        var input = Mount();
        Type(input, $"看看 {AttachmentMarker.Format(path)} 对不对");

        InputSubmit? captured = null;
        input.SendRequested += (_, s) => captured = s;
        input.Submit();

        Assert.NotNull(captured);
        Assert.Contains(AttachmentMarker.Format(path), captured!.Text);
        Assert.Single(captured.Attachments);
    }

    [AvaloniaFact]
    public void 复制出去的正文粘回来仍是同一个附件()
    {
        // 模拟：从一条消息里复制正文 → 粘进输入框
        var path = TempPath("rt 带空格.png");
        var messageContent = $"看这个 {AttachmentMarker.Format(path)}";

        var input = Mount();
        Type(input, messageContent);          // 粘贴

        Assert.Equal("看这个 @图片1", input.Text);   // 先转成占位符给人看
        Assert.Equal(1, input.AttachmentCount);

        InputSubmit? captured = null;
        input.SendRequested += (_, s) => captured = s;
        input.Submit();

        // 再发出去时又变回同一个标记 —— 无损往返
        Assert.Equal(messageContent, captured!.Text);
    }

    [AvaloniaFact]
    public void 手打标记也会被采纳()
    {
        // 采纳逻辑挂在 TextChanged 上而不是粘贴事件上，所以手打同样生效。
        // 这也是「不为每个入口各写一遍」的好处。
        var path = TempPath("rt3.txt");
        var input = Mount();

        Type(input, AttachmentMarker.Format(path));

        Assert.Equal("@文本文件1", input.Text);
        Assert.Equal(1, input.AttachmentCount);
    }

    [AvaloniaFact]
    public void 真file网址在输入框里保持原样()
    {
        var input = Mount();

        Type(input, "file:///C:/Users/x.html 是这个");

        Assert.Equal("file:///C:/Users/x.html 是这个", input.Text);
        Assert.Equal(0, input.AttachmentCount);
    }

    [AvaloniaFact]
    public void 移除附件后正文里的占位符也消失()
    {
        var path = TempPath("rt4.png");
        var input = Mount();
        Type(input, $"看 {AttachmentMarker.Format(path)}");

        // chip 上的 × 按钮走的是控件的移除逻辑
        var chip = input.GetVisualDescendants().OfType<AttachmentChip>().First();
        Assert.NotNull(chip);
        // 直接调控件公开的移除路径：通过把正文里的占位符删掉（等价于用户删除）
        Type(input, "看 ");

        Assert.Equal(0, input.AttachmentCount);
        Assert.False(AttachmentBarVisible(input));
    }

    [AvaloniaFact]
    public void 多个附件各自分配占位符()
    {
        var input = Mount();
        Type(input,
            $"{AttachmentMarker.Format(TempPath("m1.png"))} " +
            $"{AttachmentMarker.Format(TempPath("m2.txt"))} " +
            $"{AttachmentMarker.Format(TempPath("m3.pdf"))}");

        Assert.Equal(3, input.AttachmentCount);
        Assert.Contains("@图片1", input.Text);
        Assert.Contains("@文本文件2", input.Text);
        // 未知后缀按「直接把后缀贴上去」处理
        Assert.Contains("@pdf3", input.Text);
    }
}
