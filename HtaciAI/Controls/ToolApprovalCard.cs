using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>
/// 对话内嵌的工具审批卡片（取代原来单独弹的那个「工具权限确认」窗口）：
/// 展示工具名、危险等级与入参，由「允许执行 / 拒绝」决定是否放行。
/// 用户点完按钮后卡片原地换成只读回执，不再接受输入。
/// </summary>
public sealed class ToolApprovalCard : UserControl
{
    private const string AccentHex = "#4A90D9";
    private const string TitleHex = "#1A1A2E";
    private const string LineHex = "#E5E7EB";
    private const string ChipBgHex = "#F1F3F5";

    private readonly TaskCompletionSource<bool> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用户是否放行这次调用。作答之前一直处于等待状态。</summary>
    public Task<bool> Allowed => _done.Task;

    public ToolApprovalCard(ToolDefinition tool, ChatToolCall call)
    {
        var (levelText, levelColor) = tool.DangerLevel switch
        {
            ToolDangerLevel.Safe => ("安全", "#16A34A"),
            ToolDangerLevel.Risk => ("风险", "#D97706"),
            _ => ("危险", "#DC2626"),
        };

        var name = new TextBlock
        {
            Text = tool.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TitleHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse(levelColor)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = levelText,
                FontSize = 11,
                Foreground = Brushes.White,
            },
        });
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var args = new SelectableTextBlock
        {
            Text = FormatJson(call.Arguments),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Color.Parse("#333333")),
            TextWrapping = TextWrapping.Wrap,
        };

        var reject = CreateButton("拒绝", "#374151", ChipBgHex, ChipBgHex, () => Finish(false));
        var allow = CreateButton("允许执行", "#FFFFFF", AccentHex, AccentHex, () => Finish(true));

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                HeaderLine("工具权限确认"),
                head,
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    // 入参可能很长（写文件之类），给个上限免得卡片把整屏撑开
                    Child = new SmoothScrollViewer
                    {
                        Content = args,
                        MaxHeight = 220,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 2, 0, 0),
                    Children = { reject, allow },
                },
            },
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse(LineHex)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12),
            Child = body,
        };
    }

    private void Finish(bool allowed)
    {
        _done.TrySetResult(allowed);

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#FAFBFC")),
            BorderBrush = new SolidColorBrush(Color.Parse(LineHex)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12),
            Child = HeaderLine(allowed ? "已允许执行" : "已拒绝执行"),
        };
    }

    private static Control HeaderLine(string text)
    {
        var mark = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(AccentHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "?",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                mark,
                new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                },
            },
        };
    }

    private static Button CreateButton(string text, string fg, string bg, string border, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(fg)),
            },
            MinWidth = 76,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(bg)),
            BorderBrush = new SolidColorBrush(Color.Parse(border)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static string FormatJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(无入参)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
