using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 输入区的「外观外壳」回归测试：聚焦高亮与「更多功能」菜单。
///
/// 这两个都踩过一次真实的坑，而且性质都是「不报错、只是长得不对」，靠编译和肉眼都容易漏：
/// <list type="number">
///   <item>高亮被控件自身的裁剪切掉 —— 只在撑满宽度时才显现。</item>
///   <item>边框色的静态值写成了本地属性 —— Avalonia 里本地值优先级高于样式，
///         于是聚焦态那条 setter 永远是死的（同一段样式里 BoxShadow 却能生效，
///         所以只看「有没有高亮」根本发现不了）。</item>
///   <item>「更多功能」的菜单项搬过来了、菜单容器却漏了 —— 按钮点开是空的。</item>
/// </list>
/// </summary>
public class InputChromeTests
{
    private static ChatInputControl Mount()
    {
        var input = new ChatInputControl();
        new Window { Width = 500, Height = 300, Content = input }.Show();
        return input;
    }

    private static Border Shell(ChatInputControl input)
        => input.GetVisualDescendants().OfType<Border>().First(b => b.Name == "InputShell");

    private static Button MoreButton(ChatInputControl input)
        => input.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MoreBtn");

    // ---- 聚焦高亮 ----

    [AvaloniaFact]
    public void 控件自身不裁剪否则聚焦光晕会被切掉()
    {
        var input = Mount();

        // 光晕往外散 7px，而 InputShell 撑满控件宽度 —— 控件一旦裁剪，左右和上边就没了。
        Assert.False(input.ClipToBounds);
    }

    [AvaloniaFact]
    public void 控件内部没有别的裁剪层()
    {
        var input = Mount();
        var shell = Shell(input);

        // 从 InputShell 往上走到控件本身，中间任何一层开裁剪都会吃掉光晕。
        // （排查这个问题时就是靠走这条链定位到 ChatInputControl 的。）
        Visual? v = shell;
        while (v is not null && v != input)
        {
            Assert.False(v.ClipToBounds, $"{v.GetType().Name} 开了裁剪，会切掉聚焦光晕");
            v = v.GetVisualParent();
        }
    }

    [AvaloniaFact]
    public void 聚焦时边框色切到高亮蓝()
    {
        var input = Mount();
        var shell = Shell(input);

        // 静止态是浅灰
        Assert.Equal(Color.Parse("#E5E7EB"), Solid(shell.BorderBrush));

        input.FocusInput();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 聚焦后必须真的变蓝。这条断言专门防「静态值写成属性」那个坑：
        // 那种写法下这里会拿到 #E5E7EB，但同时 BoxShadow 是生效的，
        // 光看界面只会觉得「高亮有点怪」，不会想到有一条 setter 是死的。
        Assert.Equal(Color.Parse("#7EB3E8"), Solid(shell.BorderBrush));
    }

    [AvaloniaFact]
    public void 聚焦时加上外发光()
    {
        var input = Mount();
        var shell = Shell(input);

        Assert.Equal(default, shell.BoxShadow);

        input.FocusInput();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(default, shell.BoxShadow);
    }

    // ---- 「更多功能」菜单 ----

    [AvaloniaFact]
    public void 没有注入菜单项时更多按钮没有菜单()
    {
        var input = Mount();

        // 新建页就是这个状态：没有历史可压，于是按钮点开什么都不该有。
        Assert.Null(MoreButton(input).Flyout);
    }

    [AvaloniaFact]
    public void 注入菜单项后更多按钮挂上菜单()
    {
        var input = Mount();

        input.AddMoreItem("压缩上下文", () => { });

        var flyout = MoreButton(input).Flyout as MenuFlyout;
        Assert.NotNull(flyout);
        var item = Assert.Single(flyout!.Items.OfType<MenuItem>());
        Assert.Equal("压缩上下文", item.Header);
    }

    [AvaloniaFact]
    public void 注入的菜单项能触发回调()
    {
        var input = Mount();
        var clicked = 0;
        input.AddMoreItem("压缩上下文", () => clicked++);

        var flyout = (MenuFlyout)MoreButton(input).Flyout!;
        var item = flyout.Items.OfType<MenuItem>().Single();
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, clicked);
    }

    private static Color? Solid(IBrush? brush) => (brush as ISolidColorBrush)?.Color;
}
