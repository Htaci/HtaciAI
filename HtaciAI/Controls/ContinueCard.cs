using System;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Controls;

/// <summary>
/// 对话内嵌的「等你操作完再继续」卡片（ask_user_continue 工具的界面）。
///
/// <b>与提问卡片的差别</b>：提问要的是「选一个答案」，这张卡要的是「你去做一件事，
/// 做完告诉我」。所以它没有选项、没有输入框，只有写清楚要做什么的正文和两个按钮。
///
/// <b>为什么需要它而不是让模型直接问</b>：AI 经常需要人做它做不了的事 —— 扫码、输验证码、
/// 在手机上确认、去柜台签字。这些动辄几分钟，模型如果只是「问一句然后等」，
/// 用户看不出这一轮停在哪、也分不清该点哪里继续。
/// 一张明确的卡片把「现在轮到你了」这件事变成看得见的界面。
/// </summary>
public sealed class ContinueCard : Avalonia.Controls.UserControl
{
    private const string AccentHex = "#4A90D9";
    private const string TitleHex = "#1A1A2E";
    private const string SubHex = "#4A5B6D";
    private const string FaintHex = "#9CA3AF";
    private const string LineHex = "#E5E7EB";
    private const string ChipBgHex = "#F1F3F5";
    private const string CardBgHex = "#FFFFFF";

    private readonly TaskCompletionSource<bool?> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用户点了「已完成」为 true，「取消」为 false。等待期间不完成。</summary>
    public Task<bool?> Answer => _done.Task;

    public ContinueCard(string title, string instructions)
    {
        var body = new Avalonia.Controls.StackPanel { Spacing = 10 };

        body.Children.Add(BuildHeader(title));
        body.Children.Add(BuildBody(instructions));
        body.Children.Add(BuildFooter());

        Content = new Avalonia.Controls.Border
        {
            CornerRadius = new Avalonia.CornerRadius(10),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(CardBgHex)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(LineHex)),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(14, 12),
            Child = body,
        };
    }

    private static Avalonia.Controls.Control BuildHeader(string title)
    {
        // 用「暂停」符号而不是问号：这张卡不是提问，是「轮到你了」。
        var mark = new Avalonia.Controls.Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F59E0B")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new Avalonia.Controls.TextBlock
            {
                Text = "!",
                FontSize = 10.5,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Avalonia.Media.Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };

        var titleText = new Avalonia.Controls.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "需要你操作后继续" : title,
            FontSize = 13.5,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(TitleHex)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
        };

        return new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { mark, titleText },
        };
    }

    private static Avalonia.Controls.Control BuildBody(string instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            return new Avalonia.Controls.TextBlock { IsVisible = false };

        // 正文按行拆开，空行当段间距。这里刻意不做 Markdown：
        // 一张要求「照着做」的卡片，过度排版反而让人找不到重点。
        var panel = new Avalonia.Controls.StackPanel { Spacing = 4 };

        foreach (var raw in instructions.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                panel.Children.Add(new Avalonia.Controls.Border { Height = 4 });
                continue;
            }

            panel.Children.Add(new Avalonia.Controls.TextBlock
            {
                Text = line,
                FontSize = 12.5,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(SubHex)),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        return new Avalonia.Controls.Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(ChipBgHex)),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12, 10),
            Child = panel,
        };
    }

    private Avalonia.Controls.Control BuildFooter()
    {
        var cancel = CreateTextButton("取消，不做这个", () => _done.TrySetResult(false));
        var confirm = CreatePrimaryButton("我已做完，继续", () => _done.TrySetResult(true));

        var grid = new Avalonia.Controls.Grid
        {
            ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };
        grid.Children.Add(cancel);
        Avalonia.Controls.Grid.SetColumn(confirm, 1);
        grid.Children.Add(confirm);
        return grid;
    }

    private static Avalonia.Controls.Button CreateTextButton(string text, Action onClick)
    {
        var block = new Avalonia.Controls.TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(FaintHex)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var btn = new Avalonia.Controls.Button
        {
            Content = block,
            Height = 30,
            Padding = new Avalonia.Thickness(8, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        btn.PointerEntered += (_, _) => block.Foreground =
            new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#374151"));
        btn.PointerExited += (_, _) => block.Foreground =
            new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(FaintHex));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Avalonia.Controls.Button CreatePrimaryButton(string text, Action onClick)
    {
        var block = new Avalonia.Controls.TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = Avalonia.Media.Brushes.White,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var btn = new Avalonia.Controls.Button
        {
            Content = block,
            MinWidth = 116,
            Height = 30,
            Padding = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(AccentHex)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(AccentHex)),
            BorderThickness = new Avalonia.Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        btn.Click += (_, _) => onClick();
        return btn;
    }
}
