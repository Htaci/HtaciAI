using System;
using Avalonia;
using Avalonia.Styling;

namespace HtaciAI.Services.ModelTable;

/// <summary>
/// 开发商 → 模型头像的资源路径解析。
///
/// 图标按<b>品牌</b>画，不是按公司：智谱用 <c>glm.svg</c>、xAI 用 <c>grok.svg</c>，
/// 所以具体文件名取自 <see cref="VendorInfo.Icon"/>，没有覆盖时才退回开发商 id。
///
/// 暗色图标目前只是浅色的子集（深色适配还没做，见 App.axaml 的说明）。
/// 这里按「暗色缺失就退回浅色」处理，等真做深色主题时不会出现一半模型变成空白。
///
/// 可用文件名由生成器扫描资源目录写进 <see cref="BuiltinModelTable"/>，
/// 所以这里判断存在性是查名单，不需要访问文件系统（打包成 avares 之后也查不了）。
///
/// <b>当前 <c>Assets/icon/</c> 下的图标已全部移除</b>（原有那批来自 cherry-studio，
/// AGPL-3.0 与本项目的 MIT 冲突，清单见 <c>docs/icon-inventory.md</c>）。
/// 名单为空时这里一律返回 null，界面改为显示开发商名称文本 —— 这是预期状态，
/// 放入新图标后重跑生成器即可恢复。
/// </summary>
public static class VendorIcons
{
    private const string AssetBase = "avares://HtaciAI/Assets/icon/models";

    /// <summary>按开发商 id 解析头像资源路径；该开发商没有图标时返回 null（调用方用通用图标）。</summary>
    /// <param name="vendorId">开发商 id，来自 <c>AiModel.Vendor</c>。</param>
    /// <param name="dark">当前是否深色主题。浅色图标是全集，暗色缺失时自动退回。</param>
    public static string? Resolve(string? vendorId, bool dark)
    {
        if (string.IsNullOrWhiteSpace(vendorId)) return null;

        var vendor = ModelTableIndex.FindVendor(vendorId.Trim());

        // 表里没登记的开发商也允许有图标：用户手工填了个 id 又想让它有头像时，
        // 不该因为表里少一条就什么都不显示。
        var iconName = vendor?.Icon ?? vendorId.Trim().ToLowerInvariant();

        return ResolveByName(iconName, dark);
    }

    /// <summary>按图标名解析（不带扩展名）。</summary>
    public static string? ResolveByName(string? iconName, bool dark)
    {
        if (string.IsNullOrWhiteSpace(iconName)) return null;

        var name = iconName.Trim().ToLowerInvariant();

        if (dark && Contains(BuiltinModelTable.IconNamesDark, name))
            return $"{AssetBase}/dark/{name}.svg";

        if (Contains(BuiltinModelTable.IconNamesLight, name))
            return $"{AssetBase}/light/{name}.svg";

        // 暗色集合里有、浅色集合里没有的图标（理论上不该出现，但生成时按目录扫描，
        // 真出现了也不该解析失败）。
        if (Contains(BuiltinModelTable.IconNamesDark, name))
            return $"{AssetBase}/dark/{name}.svg";

        return null;
    }

    /// <summary>
    /// 用当前应用主题解析。便利重载 —— 应用被钉在浅色主题时（见 App.axaml）恒为浅色，
    /// 等深色适配落地后无需改动调用方。
    /// </summary>
    public static string? Resolve(string? vendorId)
        => Resolve(vendorId, Application.Current?.ActualThemeVariant == ThemeVariant.Dark);

    /// <summary>开发商展示名；表里没登记时返回 null。</summary>
    public static string? VendorName(string? vendorId)
        => string.IsNullOrWhiteSpace(vendorId) ? null : ModelTableIndex.FindVendor(vendorId.Trim())?.Name;

    private static bool Contains(string[] names, string value)
    {
        // 名单很小（几十个），线性查找比建字典更省事，也避免静态构造顺序的坑。
        foreach (var n in names)
            if (string.Equals(n, value, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
