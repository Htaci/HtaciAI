using Avalonia.Media;

namespace HtaciAI.Services;

/// <summary>
/// 内置字体的统一引用。图标字体随程序一起打包
/// （<c>Assets/Fonts/FluentSystemIcons-Regular.ttf</c>），这样在没装该字体的系统上
/// 图标字形也能正常渲染，而不是退化成方框。
///
/// <b>为什么是 Fluent System Icons 而不是 Segoe Fluent Icons</b>：后者是微软专有字体，
/// EULA 只授权「在微软平台上的程序」里使用且禁止再分发，打包进跨平台应用属于侵权。
/// Fluent System Icons 同为微软出品、观感一致，但以 <b>MIT</b> 许可发布，可自由再分发
/// （许可证全文见同目录 <c>LICENSE-FluentSystemIcons.txt</c>，MIT 要求随附，勿删）。
///
/// ⚠️ <b>图标字形码位与 Segoe 完全不通用</b>：两套字体的私用区分配毫无关系，
/// 换字体时每一处字形都必须逐个重映射，不能只改字体文件。当前用到的映射见
/// <c>docs/icon-glyphs.md</c>（码位 → 语义 → Fluent 图标名）。
///
/// 引用方式分两处，都指向同一个资源：
/// <list type="bullet">
///   <item>代码里用 <see cref="Icons"/>（一个现成的 <see cref="FontFamily"/>）；</item>
///   <item>XAML 里用 App.axaml 里的 <c>{StaticResource IconFont}</c>。</item>
/// </list>
/// 换字体文件 / 改名时只动这一处与 App.axaml 的资源，别在别处再硬编码族名字符串。
/// </summary>
public static class AppFonts
{
    /// <summary>图标字体的 avares 资源地址，<c>#</c> 后是字体内部族名。</summary>
    public const string IconFontUri =
        "avares://HtaciAI/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    /// <summary>图标字体。</summary>
    public static readonly FontFamily Icons = new(IconFontUri);
}
