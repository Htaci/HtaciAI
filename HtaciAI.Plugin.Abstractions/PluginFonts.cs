using System;
using Avalonia;
using Avalonia.Media;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 宿主随程序打包的图标字体（Fluent System Icons，MIT）。
///
/// <b>为什么插件要用它，而不是打字面符号</b>：<c>← → ⚙ ✕</c> 这些是「字符」，
/// 字重、基线、宽度全由文本字体决定，和旁边的宿主按钮放在一起会像打错字的界面。
/// 宿主同类图标统一用这一套字形，插件跟着用才有一致的外观。
///
/// <b>为什么不能自己 new 一个 FontFamily("Segoe Fluent Icons")</b>：那是微软专有字体，
/// 只在 Windows 上有，而且宿主要求不许依赖它；自己再打包一份又是第二份字体文件。
/// 同进程里直接取宿主这份最省事，也不增体积。
///
/// <b>只能在 UI 线程上用</b>（它要读 <see cref="Application.Current"/> 的资源）。
///
/// <b>字形码位</b>：这套字体的私用区码位是算出来的，不能凭语义猜，
/// 也不要照抄 <c>Segoe Fluent Icons</c> 的码位 —— 两者毫无关系。
/// 用法：把码位写进 <c>TextBlock.Text</c>，字体用 <see cref="IconFont"/>：
/// <code>
/// new TextBlock { Text = "\uF10A", FontFamily = PluginFonts.IconFont, FontSize = 14 };
/// </code>
/// 宿主用到的码位对照见仓库里的 <c>docs/icon-glyphs.md</c>。
/// </summary>
public static class PluginFonts
{
    /// <summary>
    /// 图标字体的 avares 地址（<c>#</c> 后是字体内部族名）。
    /// <b>不要在你的界面里直接拿它 new 字体</b>，用 <see cref="IconFont"/> ——
    /// 宿主换字体文件时这个常量会变，而 <see cref="IconFont"/> 会自动跟上。
    /// </summary>
    public const string IconFontUri =
        "avares://HtaciAI/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    private static FontFamily? _iconFont;

    /// <summary>
    /// 宿主的图标字体（只解析一次）。
    ///
    /// 先取宿主 <c>App.axaml</c> 里注册的 <c>IconFont</c> 资源：那是宿主自己也在用的那一份，
    /// 宿主换字体时插件不用改任何代码。取不到时退回字体文件地址；
    /// 连字体文件都读不出来时返回 <see cref="FontFamily.Default"/> ——
    /// 那时图标会显示成方框，但界面照常画出来，不会因为一个字形炸掉整页。
    /// </summary>
    public static FontFamily IconFont => _iconFont ??= Resolve();

    private static FontFamily Resolve()
    {
        try
        {
            if (Application.Current?.TryGetResource("IconFont", null, out var value) == true &&
                value is FontFamily family)
            {
                return family;
            }
        }
        catch (Exception)
        {
            // 资源系统没就绪（极早期）或宿主没有这个资源：走下面的兜底
        }

        try
        {
            return new FontFamily(IconFontUri);
        }
        catch (Exception)
        {
            return FontFamily.Default;
        }
    }
}
