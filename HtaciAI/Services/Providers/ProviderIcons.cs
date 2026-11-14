using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Styling;
using HtaciAI.Services.ModelTable;

namespace HtaciAI.Services.Providers;

/// <summary>
/// 服务商图标 → 资源路径解析（<c>Assets/icon/providers/</c>）。
///
/// 与 <see cref="VendorIcons"/> 是同一套路子，差别只在资源目录：那边是<b>模型品牌</b>，
/// 这边是<b>服务商 / API 平台</b>，两套目录里会有同名文件（deepseek 之类），不能混用。
/// 可用文件名由生成器扫描目录写进 <see cref="BuiltinModelTable"/>，
/// 所以判断存在性是查名单，不访问文件系统（打包成 avares 之后也查不了）。
/// </summary>
public static class ProviderIcons
{
    private const string AssetBase = "avares://HtaciAI/Assets/icon/providers";

    /// <summary>
    /// 全部可用的图标名（浅色那一套就是全集，深色缺的会自动回退到浅色）。
    /// 供「服务商设置」里的图标下拉使用 —— 用户能换的只能是真实存在的文件，
    /// 让他在输入框里手打文件名只会打出一堆解析不到的图标名。
    /// </summary>
    public static IReadOnlyList<string> Available => BuiltinModelTable.ProviderIconNamesLight;

    /// <summary>按图标名解析资源路径；没有这个图标时返回 null（调用方退回「首字 + 底色」头像）。</summary>
    public static string? Resolve(string? iconName)
        => Resolve(iconName, Application.Current?.ActualThemeVariant == ThemeVariant.Dark);

    public static string? Resolve(string? iconName, bool dark)
    {
        if (string.IsNullOrWhiteSpace(iconName)) return null;

        var name = iconName.Trim().ToLowerInvariant();

        if (dark && Contains(BuiltinModelTable.ProviderIconNamesDark, name))
            return $"{AssetBase}/dark/{name}.svg";

        if (Contains(BuiltinModelTable.ProviderIconNamesLight, name))
            return $"{AssetBase}/light/{name}.svg";

        // 深色集合里有、浅色里没有的图标（理论上不该出现）：宁可发一个存在的那份。
        if (Contains(BuiltinModelTable.ProviderIconNamesDark, name))
            return $"{AssetBase}/dark/{name}.svg";

        return null;
    }

    private static bool Contains(string[] names, string value)
    {
        foreach (var n in names)
            if (string.Equals(n, value, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
