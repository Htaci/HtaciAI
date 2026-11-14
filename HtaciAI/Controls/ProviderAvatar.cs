using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 服务商卡片 / 列表里的头像。有 logo 就画 logo，没有就退回「名称首字 + 稳定底色」。
///
/// 两处共用（服务商列表卡片、添加服务商窗口）：头像长什么样只该有一个定义 ——
/// 同一个服务商在两个地方长得不一样，会让人怀疑是不是两个不同的东西。
/// </summary>
public static class ProviderAvatar
{
    /// <summary>无 logo 时的底色候选，按名称稳定取一个（同一个名字每次都是同一个颜色）。</summary>
    private static readonly Color[] Palette =
    {
        Color.Parse("#546E7A"), Color.Parse("#2EAF7D"), Color.Parse("#7B5EA7"),
        Color.Parse("#D97706"), Color.Parse("#4A90D9"), Color.Parse("#DC2626"),
    };

    /// <summary>画一个边长 <paramref name="size"/> 的方形头像。</summary>
    public static Control Create(string? name, string? iconName, double size)
    {
        if (VendorAvatar.CreateProviderLogo(iconName, size * 0.66) is { } logo)
        {
            // 品牌图标的留白比首字头像多得多，所以套一层浅灰底：图标本身五颜六色，
            // 直接贴在白卡片上会显得大小不一、边缘参差。
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.25),
                Background = new SolidColorBrush(Color.Parse("#F3F4F6")),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = logo,
                },
            };
        }

        var label = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        var index = (name ?? "").Aggregate(0, (acc, c) => acc + c) % Palette.Length;

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Background = new SolidColorBrush(Palette[index]),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = label,
                FontSize = size * 0.45,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
