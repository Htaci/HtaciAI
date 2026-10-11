using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>
/// ask_user_question 的只读「问答卡片」：工具卡展开时默认显示它，取代原本的入参 / 出参原文。
/// 与 <see cref="AskUserCard"/> 同一套外观（单选 / 多选标签、候选项、勾选态），只是不接受任何输入。
/// </summary>
public static class AskAnswerView
{
    private const string AccentHex = "#4A90D9";
    private const string TitleHex = "#1A1A2E";
    private const string SubHex = "#4A5B6D";
    private const string FaintHex = "#9CA3AF";
    private const string LineHex = "#E5E7EB";
    private const string ChipBgHex = "#F1F3F5";
    private const string SelectedBgHex = "#EEF4FB";

    /// <summary>渲染问答记录；没有问题可渲染时返回 null（调用方退回原文视图）。</summary>
    public static Control? Build(List<AskQuestion> questions, List<AskAnswer> answers)
    {
        if (questions.Count == 0) return null;

        var stack = new StackPanel { Spacing = 12 };
        for (var i = 0; i < questions.Count; i++)
        {
            var answer = answers.FirstOrDefault(a => a.Question == questions[i].Question);
            stack.Children.Add(BuildQuestion(questions[i], answer));
        }
        return stack;
    }

    private static Control BuildQuestion(AskQuestion question, AskAnswer? answer)
    {
        var block = new StackPanel { Spacing = 6 };

        var text = new TextBlock
        {
            Text = question.Question,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TitleHex)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(CreateTypeBadge(question.MultiSelect));
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        block.Children.Add(head);

        var options = new StackPanel { Spacing = 4 };
        foreach (var option in question.Options)
        {
            var selected = answer?.Selected.Contains(option.Label) == true;
            options.Children.Add(BuildOption(option, selected));
        }
        block.Children.Add(options);

        var (answerText, empty) = Describe(answer);
        block.Children.Add(new TextBlock
        {
            Text = "→ " + answerText,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse(empty ? FaintHex : TitleHex)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 0, 0, 0),
        });

        return block;
    }

    private static Control BuildOption(AskOption option, bool selected)
    {
        var texts = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = option.Label,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.Parse(selected ? TitleHex : FaintHex)),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(option.Description))
            texts.Children.Add(new TextBlock
            {
                Text = option.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(FaintHex)),
                TextWrapping = TextWrapping.Wrap,
            });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(texts);
        var mark = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse(selected ? AccentHex : "#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse(selected ? AccentHex : "#D5DAE1")),
            Child = new TextBlock
            {
                Text = "✓",
                FontSize = 10.5,
                Foreground = Brushes.White,
                IsVisible = selected,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse(selected ? SelectedBgHex : "#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse(selected ? AccentHex : LineHex)),
            Child = grid,
        };
    }

    private static Control CreateTypeBadge(bool multiSelect)
    {
        var (text, fg, bg) = multiSelect
            ? ("多选", AccentHex, "#E8F1FB")
            : ("单选", SubHex, ChipBgHex);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(bg)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fg)),
            },
        };
    }

    /// <summary>把一条作答整理成一行文字；返回是否为空（未作答）。</summary>
    private static (string Text, bool Empty) Describe(AskAnswer? answer)
    {
        if (answer is null || answer.IsEmpty) return ("未作答", true);

        var parts = new List<string>();
        if (answer.Selected.Count > 0) parts.Add(string.Join("、", answer.Selected));
        if (!string.IsNullOrWhiteSpace(answer.Custom)) parts.Add($"(自定义) {answer.Custom.Trim()}");
        return (string.Join("；", parts), false);
    }
}
