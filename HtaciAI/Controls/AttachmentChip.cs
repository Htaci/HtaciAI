using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;

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
            FontFamily = new FontFamily("Segoe Fluent Icons"),
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

        PointerEntered += (_, _) => Background = HoverBg;
        PointerExited += (_, _) => Background = IdleBg;
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            AttachmentStore.RevealInExplorer(item.Path);
            e.Handled = true;
        };
    }

    private static TextBlock BuildRemoveButton(Action onRemove)
    {
        var close = new TextBlock
        {
            Text = "",   // Cancel（×）
            FontFamily = new FontFamily("Segoe Fluent Icons"),
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

    /// <summary>种类图标（Segoe Fluent Icons）。</summary>
    private static string GlyphFor(string kind) => kind switch
    {
        "image" => "",    // Picture
        "office" or "text" => "",   // Document
        _ => "",          // Page
    };
}
