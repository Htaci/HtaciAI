using System;
using System.Collections.Generic;
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
using Avalonia.Threading;

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

    /// <summary>
    /// 从卡片外部作答（目前只有系统提醒上的「拒绝 / 同意 / 全部同意」按钮用）。
    /// 已经答过就什么都不做：按钮与卡片是两条并行的作答入口，谁先点谁算数。
    /// </summary>
    public void Respond(bool allowed)
    {
        if (_done.Task.IsCompleted) return;

        if (Dispatcher.UIThread.CheckAccess()) Finish(allowed);
        else Dispatcher.UIThread.Post(() => Finish(allowed));
    }

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
            Text = tool.Display,
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

        // 有别名时把原始调用名跟在后面：审批是「谁在动你的文件」的最后一关，
        // 用户需要能对上模型实际调用的那个名字（对日志、搜索工具都靠它）。
        if (tool.DisplayName.Length == 0)
        {
            Grid.SetColumn(name, 1);
            head.Children.Add(name);
        }
        else
        {
            name.Margin = new Thickness(8, 0, 0, 0);
            name.TextWrapping = TextWrapping.NoWrap;
            var nameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    name,
                    new TextBlock
                    {
                        Text = $"（{tool.Name}）",
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 2, 0, 0),
                    },
                },
            };
            Grid.SetColumn(nameRow, 1);
            head.Children.Add(nameRow);
        }

        // 自定义块 + 原生选择：入参是多行 JSON，交给框架自己选区（拖选/复制开箱可用），
        // 卡片内部独立选择，不与轮内的渲染态选区互相干扰。
        // 长文本字段（脚本 / 技能正文）不放在这里，见下面的 ExtractLongText —— 它们要按真实换行单独展示。
        var args = new CustomSelectableTextBlock
        {
            NativeSelectionEnabled = true,
            Text = FormatJson(call.Arguments, omit: LongTextFields),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Color.Parse("#333333")),
            TextWrapping = TextWrapping.Wrap,
        };

        var reject = CreateButton("拒绝", "#374151", ChipBgHex, ChipBgHex, () => Finish(false));
        var allow = CreateButton("允许执行", "#FFFFFF", AccentHex, AccentHex, () => Finish(true));

        // 入参里可能有整段脚本 / 技能正文（create_tool / create_skill）。这一段是整个审批的核心：
        // 用户批准的是「会写进磁盘的这段代码」，不是「创建工具」这个动作。
        // 所以它不能跟在 JSON 里糊成一坨（那里面换行全是 \n 转义），要按真实换行单独摆出来。
        var longText = ExtractLongText(call.Arguments);

        var argsHost = new StackPanel
        {
            Spacing = 10,
            Children =
            {
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
            },
        };

        if (longText is { } code)
        {
            argsHost.Children.Add(new TextBlock
            {
                Text = code.Label,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                Margin = new Thickness(2, 2, 0, 0),
            });
            argsHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new SmoothScrollViewer
                {
                    Content = new CustomSelectableTextBlock
                    {
                        NativeSelectionEnabled = true,
                        Text = code.Text,
                        FontSize = 12,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(Color.Parse("#333333")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    MaxHeight = 320,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            });
        }

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                HeaderLine("工具权限确认"),
                head,
                argsHost,
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

    /// <summary>
    /// 会整段写进磁盘的入参字段。它们不该混在 JSON 缩进里展示 ——
    /// 一段脚本在 JSON 里换行全是 <c>\n</c> 转义，用户根本没法审。
    /// </summary>
    private static readonly string[] LongTextFields = { "script", "body" };

    /// <summary>
    /// 从入参里取出「要落盘的那段内容」并按真实换行交出去。
    /// 返回 null 表示这是一次普通工具调用（没有长文本字段）。
    /// </summary>
    private static (string Label, string Text)? ExtractLongText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var field in LongTextFields)
            {
                if (!doc.RootElement.TryGetProperty(field, out var value) ||
                    value.ValueKind != JsonValueKind.String) continue;

                var text = value.GetString() ?? "";
                if (text.Trim().Length == 0) continue;

                var label = field == "script" ? "即将写入的脚本" : "即将写入的技能正文";
                return (label, text);
            }
            return null;
        }
        catch
        {
            // 入参不是合法 JSON：本来也没法挑出长文本，交给上面那段 JSON 原文展示
            return null;
        }
    }

    /// <summary>
    /// 格式化入参 JSON，可排除若干字段（它们已经单独展示了）。
    /// 排除后如果没有别的字段，返回一句占位，免得卡片上出现一个空框。
    /// </summary>
    private static string FormatJson(string? json, string[]? omit = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(无入参)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;

            if (omit is null)
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });

            // 只保留剩下的标量字段：嵌套对象原样带上，没必要为它做深层裁剪
            var rest = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(omit, prop.Name) >= 0) continue;
                rest[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }

            return rest.Count == 0
                ? "(无其他参数)"
                : JsonSerializer.Serialize(rest, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
