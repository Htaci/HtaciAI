using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HtaciAI.Services;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(HtaciAI.Tests.TestAppBuilder))]

namespace HtaciAI.Tests;

/// <summary>
/// 无头 Avalonia 宿主。给控件的 UI 级测试用（构造控件、检查控件树、模拟点击）。
///
/// 刻意<b>不用</b>应用的 <c>HtaciAI.App</c>：它在启动时会初始化数据库、存储路径等，
/// 测试里既不需要也不该触发。这里只挂一个最小 Application ——
/// 提供 FluentTheme 与图标字体这两个 XAML 必然引用的资源，别的一概不给。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    /// <summary>最小应用：只放 Styles 与图标字体资源，够 XAML 解析即可。</summary>
    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

            // 与 App.axaml 里的 {StaticResource IconFont} 同一个键。少了它，
            // 任何用到图标字形的 XAML 都会在构造时抛「找不到资源」。
            Resources["IconFont"] = new FontFamily(AppFonts.IconFontUri);

            // 代码里做等价的事：AppFonts 静态构造会把图标字体也注册进来。
            _ = AppFonts.Icons;
        }
    }
}

public class HeadlessHostTests
{
    [AvaloniaFact]
    public void 无头宿主能构造控件()
    {
        // 这一条的价值不在断言本身，而在于证明 headless 宿主、FluentTheme、
        // 图标字体资源三样都真的能起来 —— 否则后面写控件测试时会撞一堆环境问题。
        var button = new Button { Content = "hello" };
        var window = new Window { Content = button };

        window.Show();

        Assert.NotNull(button.Parent);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void 图标字体资源可以按名字取到()
    {
        var app = Application.Current;
        Assert.NotNull(app);
        Assert.True(app!.TryFindResource("IconFont", out var font));
        Assert.IsType<FontFamily>(font);
    }
}
