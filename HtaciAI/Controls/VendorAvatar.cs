using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using HtaciAI.Services.ModelTable;
using HtaciAI.Services.Providers;

namespace HtaciAI.Controls;

/// <summary>
/// 开发商 / 服务商头像：按 <c>Assets/icon/models/</c>（模型品牌）或 <c>Assets/icon/providers/</c>
/// （服务商平台）下的 SVG 画 logo，没有图标时返回 null 由调用方决定退路。
///
/// 为什么单独一个类：SVG 渲染要走 <see cref="SvgSource"/> + <see cref="SvgImage"/>，
/// 而 <see cref="SvgSource"/> 解析一次就是一次文件读取 + XML 解析。设置页里模型行动辄几十条、
/// 还会随滚动反复重建，不做缓存的话光解析就能把列表拖慢。所以这里按资源路径缓存。
///
/// 主题：浅色/深色是两套文件，路径由 <see cref="VendorIcons.Resolve(string)"/> 按当前主题决定。
/// 本应用钉死浅色（见 App.axaml），所以实际都取 <c>light/</c>；深色适配落地后这里无需改动。
/// </summary>
public static class VendorAvatar
{
    /// <summary>资源路径 → 已解析的图形。null 表示这个路径解析失败过，不必反复重试。</summary>
    private static readonly Dictionary<string, SvgSource?> Sources = new();

    /// <summary>
    /// 造一个开发商 logo。该开发商没有图标时返回 null ——
    /// 调用方据此决定是留空还是退回通用头像，而不是拿到一个空白方块。
    /// </summary>
    /// <param name="vendorId">开发商 id（<c>AiModel.Vendor</c>）。</param>
    /// <param name="size">边长（正方形）。</param>
    public static Control? CreateLogo(string? vendorId, double size)
    {
        if (VendorIcons.Resolve(vendorId) is not { } path) return null;
        if (Load(path) is not { } source) return null;

        return new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = new SvgImage { Source = source },
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// 造一个服务商 logo（资源取自 <c>Assets/icon/providers/</c>）。
    /// 没有图标时返回 null，调用方退回首字头像 —— 服务商卡片本来就有那套退路。
    /// </summary>
    /// <param name="iconName">图标名（<c>AiProvider.Icon</c>，不带扩展名）。</param>
    /// <param name="size">边长（正方形）。</param>
    public static Control? CreateProviderLogo(string? iconName, double size)
    {
        if (ProviderIcons.Resolve(iconName) is not { } path) return null;
        if (Load(path) is not { } source) return null;

        return new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = new SvgImage { Source = source },
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static SvgSource? Load(string path)
    {
        if (Sources.TryGetValue(path, out var cached)) return cached;

        SvgSource? source;
        try
        {
            // baseUri 传 null：path 本身就是完整的 avares:// 地址
            source = SvgSource.Load(path, null);
        }
        catch
        {
            // 图标是外部资源，坏一个文件不该让整个列表崩掉 —— 当成「没有图标」处理
            source = null;
        }

        Sources[path] = source;
        return source;
    }
}
