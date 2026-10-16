using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 平台相关的按键手势与附件卡片的悬停提示。
///
/// <b>覆盖范围的诚实说明。</b><see cref="OperatingSystem.IsMacOS"/> 在本机为 true，
/// 所以下面的断言跑的是 macOS 分支（Meta 生效）。Windows/Linux 分支
/// <b>没有被执行到</b> —— 那条路径只能靠「同一份代码、同一个表达式」的对称性保证。
/// 好在实现里只有一处平台判断（<see cref="InputGestures.PrimaryCommand"/>），
/// 两个平台共用它，不存在「一边改了一边没改」的空间。
/// </summary>
public class InputGestureTests
{
    private static bool IsMac => OperatingSystem.IsMacOS();

    private static KeyEventArgs Args(Key key, KeyModifiers modifiers)
        => new() { Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent };

    // ---- 修饰键判断 ----

    [Fact]
    public void 主命令修饰键随平台()
    {
        Assert.Equal(IsMac ? KeyModifiers.Meta : KeyModifiers.Control, InputGestures.PrimaryCommand);
    }

    [Fact]
    public void 粘贴手势认主命令修饰键()
    {
        var modifiers = IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.True(InputGestures.IsPaste(Args(Key.V, modifiers)));
    }

    [Fact]
    public void 粘贴手势在mac上也认Ctrl()
    {
        // 跨平台用户的肌肉记忆不该失效 —— 别的平台 Ctrl 是唯一的粘贴键。
        // （这条在非 mac 上恒真：那时 Ctrl 本来就是主修饰键。）
        Assert.True(InputGestures.IsPaste(Args(Key.V, KeyModifiers.Control)));
    }

    [Fact]
    public void 单独的V不是粘贴手势()
    {
        Assert.False(InputGestures.IsPaste(Args(Key.V, KeyModifiers.None)));
    }

    [Fact]
    public void 其他按键不是粘贴手势()
    {
        Assert.False(InputGestures.IsPaste(Args(Key.C, InputGestures.PrimaryCommand)));
        Assert.False(InputGestures.IsPaste(Args(Key.Enter, InputGestures.PrimaryCommand)));
    }

    [Fact]
    public void 非mac平台不接受Meta()
    {
        // Win+V 是 Windows 的剪贴板历史，抢过来会让那个功能打不开。
        // 这条在 mac 上无意义（mac 的 Meta 就是主修饰键），所以只在非 mac 上断言。
        if (IsMac) return;

        Assert.False(InputGestures.IsPaste(Args(Key.V, KeyModifiers.Meta)));
    }

    // ---- 附件卡片的悬停提示 ----

    [Fact]
    public void 文件存在时提示只显示路径()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            Assert.Equal(path, AttachmentChip.BuildTooltip(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void 文件缺失时提示里说明缺失并保留路径()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "绝对不存在的文件_zzz.png");

        var tip = AttachmentChip.BuildTooltip(missing);

        Assert.Contains("文件缺失", tip);
        // 路径必须还在：用户要能看出原本指的是哪个文件
        Assert.Contains(missing, tip);
    }

    [Fact]
    public void 非法路径不抛异常只提示缺失()
    {
        // 路径里带非法字符时 File.Exists 会抛，不该让它冒出去（会在构建卡片时炸掉整段历史渲染）
        var tip = AttachmentChip.BuildTooltip("\0非法\0");

        Assert.Contains("文件缺失", tip);
    }

    // ---- 端到端：mac 上 Cmd+V 走的是附件粘贴分支 ----

    [AvaloniaFact]
    public void mac上按Cmd加V会走粘贴分支而不是纯文本粘贴()
    {
        if (!IsMac) return;   // 只能在 mac 上验证 macOS 分支

        var input = new ChatInputControl();
        var window = new Window { Width = 500, Height = 300, Content = input };
        window.Show();
        input.FocusInput();
        Dispatcher.UIThread.RunJobs();

        // 必须抛在内部的 TextBox 上：处理程序注册在那里（隧道阶段）。
        // 抛在控件本身不行 —— 隧道事件从根往下走，不会「往下」传到子元素。
        var box = input.GetVisualDescendants().OfType<TextBox>().First();

        // 剪贴板在无头环境下没有内容，所以这里验的是「事件被我们截住了」：
        // 截住才会置 Handled。若修饰键判断不对，事件会落到 TextBox 默认行为上。
        var e = Args(Key.V, KeyModifiers.Meta);
        box.RaiseEvent(e);

        Assert.True(e.Handled, "Cmd+V 应当被粘贴分支截住（文本仍会正常粘贴，只是走了我们的路径）");

        // 对照组：单独的 V 不该被我们截住，否则等于普通打字都被吃掉
        var plain = Args(Key.V, KeyModifiers.None);
        box.RaiseEvent(plain);
        Assert.False(plain.Handled, "单独的 V 是普通输入，不应被粘贴分支截住");
    }
}
