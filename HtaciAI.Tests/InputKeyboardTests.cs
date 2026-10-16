using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HtaciAI.Controls;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 输入框的键盘行为。这里用无头宿主**模拟真实按键**，而不是直接构造事件参数 ——
/// 要验的问题恰好取决于真实的处理顺序（TextBox 的类处理器 vs 我们注册的实例处理器），
/// 自己 RaiseEvent 会把这个顺序绕过去，测了等于没测。
///
/// 踩过的坑：回车发送时正文末尾带了一个换行。
/// <c>AcceptsReturn=True</c> 的 TextBox 会在自己的 OnKeyDown（类处理器）里先把换行插进正文，
/// 而「回车就发送」原先注册在<b>冒泡</b>阶段 —— 等它拿到文本时换行已经在了。
/// 表现是消息气泡里多出一个空行。
/// </summary>
public class InputKeyboardTests
{
    private static (Window Window, ChatInputControl Input) Mount()
    {
        var input = new ChatInputControl();
        var window = new Window { Width = 500, Height = 300, Content = input };
        window.Show();
        input.FocusInput();
        Dispatcher.UIThread.RunJobs();
        return (window, input);
    }

    [AvaloniaFact]
    public void 回车发送的正文末尾不应带换行()
    {
        var (window, input) = Mount();
        input.Text = "你好";

        InputSubmit? captured = null;
        input.SendRequested += (_, s) => captured = s;

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(captured);
        // 断言的是「没有尾巴上的换行」，不是「整体等于原文」——
        // 这样即使以后对首尾空白做了别的规范化，这条测试仍然盯得住它该盯的东西。
        Assert.Equal("你好", captured!.Text.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\n', captured.Text);
    }

    [AvaloniaFact]
    public void 回车发送后输入框里也不该留下换行()
    {
        var (window, input) = Mount();
        input.Text = "只有一行";

        input.SendRequested += (_, _) => { };
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        // 正文本身就不该被插入换行。发送失败时宿主不会清空输入框，
        // 这时残留的换行会一直留在用户眼前（而且要不了几行就顶到 MaxHeight）。
        Assert.DoesNotContain('\n', input.Text);
    }

    [AvaloniaFact]
    public void 只按回车不发送空内容()
    {
        var (_, input) = Mount();
        input.Text = "";

        var raised = false;
        input.SendRequested += (_, _) => raised = true;
        input.Submit();

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void Shift加回车是换行而不是发送()
    {
        var (window, input) = Mount();
        input.Text = "第一行";

        var raised = false;
        input.SendRequested += (_, _) => raised = true;

        // 把光标放到末尾，Shift+Enter 应当插入换行
        input.FocusInput();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(raised);
        Assert.Contains('\n', input.Text);   // 换行是本意，必须保留
    }

    [AvaloniaFact]
    public void 生成中按回车既不发送也不插入换行()
    {
        var (window, input) = Mount();
        input.Text = "生成中";

        var raised = false;
        input.SendRequested += (_, _) => raised = true;

        input.SetRunning(true);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        // 运行中回车不当作「停止」（那颗按钮的语义切换只对鼠标点击成立），
        // 但也不该顺手往正文里插一个换行。
        Assert.False(raised);
        Assert.DoesNotContain('\n', input.Text);
    }
}
