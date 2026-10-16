using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;

using HtaciAI.Services;

namespace HtaciAI.Controls;

/// <summary>
/// 输入框上方的附件 chip：图标 + 文字标签（图片 / 文本 / Word / 后缀名…）。
/// 悬停显示真实路径，点击在资源管理器中定位该文件，右侧 × 从待发送列表里移除。
/// </summary>
public sealed class AttachmentChip : Border
{
    private static readonly IBrush IdleBg = new SolidColorBrush(Color.Parse("#F1F3F6"));
    private static readonly IBrush HoverBg = new SolidColorBrush(Color.Parse("#E7EBF0"));

    /// <summary>
    /// <paramref name="compact"/> 用于消息气泡里的内联卡片：尺寸压到能塞进一行文字而不撑高行距。
    /// </summary>
    public AttachmentChip(ChatAttachment item, Action? onRemove, bool compact = false)
    {
        Height = compact ? 19 : 26;
        CornerRadius = new CornerRadius(compact ? 5 : 6);
        Padding = new Thickness(compact ? 6 : 8, 0, compact ? 5 : 6, 0);
        Background = IdleBg;
        BorderBrush = new SolidColorBrush(Color.Parse("#E1E5EA"));
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text = GlyphFor(item.Kind),
            FontFamily = AppFonts.Icons,
            FontSize = compact ? 11 : 12,
            Foreground = new SolidColorBrush(Color.Parse("#5B6472")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = compact ? 11 : 12,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (onRemove is not null)
            row.Children.Add(BuildRemoveButton(onRemove));

        Child = row;

        // 界面上不显示路径，但需要时悬停能看到是哪一份文件
        ToolTip.SetTip(this, item.Path);
        ToolTip.SetPlacement(this, PlacementMode.Bottom);

        PointerEntered += (_, _) =>
        {
            Background = HoverBg;

            // 文件是否存在在悬停时才查，不在构造时查：卡片是随历史消息成批建出来的，
            // 每条都做一次 File.Exists 是白白的系统调用（长会话里成百上千次），
            // 而这个信息只有真正悬停看提示的那一刻才有用。
            ToolTip.SetTip(this, BuildTooltip(item.Path));
        };

        PointerExited += (_, _) => Background = IdleBg;
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            AttachmentStore.RevealInExplorer(item.Path);
            e.Handled = true;
        };
    }

    /// <summary>
    /// 悬停提示。文件已被删除或挪走时在路径前加一行说明。
    ///
    /// <b>刻意不改卡片本身的外观</b>：路径仍然是一个合法的附件（路径在那儿，只是文件此刻不在），
    /// 常见情形是文件被临时挪走或换机之后路径失效，把它画成警告色会让人以为配置错了、
    /// 要去做点什么。放在提示里说清楚，够用且不打扰。
    /// </summary>
    internal static string BuildTooltip(string path)
    {
        try
        {
            if (File.Exists(path)) return path;
        }
        catch
        {
            // 路径本身非法（如含非法字符）或无权访问，一律按「找不到」提示 —— 结果一样
        }

        return $"文件缺失（已被删除或移走）{Environment.NewLine}{path}";
    }

    private static TextBlock BuildRemoveButton(Action onRemove)
    {
        var close = new TextBlock
        {
            Text = "\uF36A",   // Cancel（×）
            FontFamily = AppFonts.Icons,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#9AA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        close.PointerPressed += (_, e) =>
        {
            // 先于 chip 自身的「定位文件」处理，避免点 × 时把资源管理器打开
            e.Handled = true;
            onRemove();
        };
        close.PointerEntered += (_, _) => close.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        close.PointerExited += (_, _) => close.Foreground = new SolidColorBrush(Color.Parse("#9AA3AF"));
        return close;
    }

    /// <summary>种类图标（Fluent System Icons）。</summary>
    private static string GlyphFor(string kind) => kind switch
    {
        "image" => "\uF489",    // Picture
        "office" or "text" => "\uF379",   // Document
        _ => "\uF379",          // Page
    };
}
