using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace HtaciAI.Controls;

/// <summary>
/// 给 <see cref="MenuFlyout"/> 里的菜单行套上 App.axaml 里那份 MenuItem 主题。
///
/// 为什么必须显式指定：
/// 只在 <c>Application.Resources</c> 里写 <c>&lt;ControlTheme x:Key="{x:Type MenuItem}"&gt;</c>
/// 并不能顶掉 Fluent 自带的那个 —— 实测菜单行仍是 Fluent 的模板（箭头还是那块 8×16 楔形、
/// Padding 还是 12,8）。而 <c>MenuFlyout.ItemContainerTheme</c> 是直接赋给容器的本地值，
/// 不经过任何资源查找顺序，套上去就一定生效。
///
/// 主题本体仍写在 App.axaml（XAML 写模板比用 C# 拼控件树清楚得多），这里只负责取出来。
/// </summary>
internal static class MenuTheme
{
    /// <summary>把应用级的 MenuItem 主题套到某个菜单上；取不到主题时静默跳过（退回 Fluent 外观）。</summary>
    public static void Apply(MenuFlyout? menu)
    {
        if (menu is null) return;

        if (Application.Current?.TryGetResource(typeof(MenuItem), null, out var value) == true
            && value is ControlTheme theme)
        {
            menu.ItemContainerTheme = theme;
        }
    }
}
