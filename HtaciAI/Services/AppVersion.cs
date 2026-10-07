using System;
using System.Reflection;

namespace HtaciAI.Services;

/// <summary>
/// 程序版本号的唯一读取点。真正的值写在 <c>HtaciAI.Desktop.csproj</c> 的 <c>&lt;Version&gt;</c>，
/// SDK 会把它同步到程序集信息里，这里只负责取出来。
///
/// 注意取的是 <b>入口程序集</b>（HtaciAI.Desktop），不是 HtaciAI 这个库——
/// 版本号属于「这一个发布」，跟着可执行文件走。
/// </summary>
public static class AppVersion
{
    /// <summary>当前版本，如 <c>1.0.0</c>。取不到时返回 <c>0.0.0</c>（永远不会抛）。</summary>
    public static string Current { get; } = Resolve();

    /// <summary>解析成可比较的 <see cref="Version"/>；解析失败返回 null。</summary>
    public static Version? Parsed => Parse(Current);

    /// <summary>
    /// 把版本字符串解析成 <see cref="Version"/>，容忍前导 <c>v</c>（<c>v1.2.3</c>）。
    /// 只有两段（<c>1.2</c>）也接受，缺的位补 0。
    /// </summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V')) cleaned = cleaned[1..];

        // 丢掉预发布/构建后缀：1.2.3-beta.1+abc → 1.2.3
        var cut = cleaned.IndexOfAny(['-', '+', ' ']);
        if (cut > 0) cleaned = cleaned[..cut];

        // 顺手补成两段以上，让 "1" 也能解析
        if (cleaned.Count('.') == 0) cleaned += ".0";

        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    private static string Resolve()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // SDK 会附上源码版本，形如 1.0.0+abc123；展示与比较都用不上
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            // 未指定 Build/Revision 时它们是 -1，别渲染成 1.0.-1
            if (version is not null)
                return $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
        }
        catch
        {
            // 反射失败（裁剪、单文件等）不该让程序起不来
        }

        return "0.0.0";
    }
}
