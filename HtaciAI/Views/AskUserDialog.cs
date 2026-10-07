using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Tools;
using HtaciAI.Controls;

namespace HtaciAI.Views;

/// <summary>
/// 「向用户提问」对话框（程序化构建）：渲染 ask_user_question 工具带来的问题列表。
///
/// 每题按单选 / 多选渲染候选项，并始终附一个自定义输入框 —— 用户可以不碰选项直接写答案。
/// 单选时选项与自定义输入互斥（选了选项就清空输入框，反之亦然），多选时两者叠加。
/// 返回 null 表示用户取消（点「取消」或关闭窗口）。
/// </summary>
public static class AskUserDialog
{
    private const string Accent = "#4A90D9";
    private const string AccentDim = "#C9D6E4";
    private const string TitleFg = "#1A1A2E";
    private const string SubFg = "#9CA3AF";
    private const string LineFg = "#E5E7EB";
    private const string SelectedBg = "#EEF4FB";

    /// <summary>一道题的作答状态（仅对话框内部使用）。</summary>
    private sealed class QuestionState
    {
        public AskQuestion Q { get; init; } = new();

        /// <summary>已选中的选项 label。</summary>
        public HashSet<string> Selected { get; } = new(StringComparer.Ordinal);

        public TextBox CustomBox { get; set; } = null!;

        /// <summary>每个选项行的重绘动作。</summary>
        public List<Action> RefreshRows { get; } = new();

        /// <summary>作答变化时通知对话框（用于刷新提交按钮可用态）。</summary>
        public Action? Changed { get; set; }

        public bool HasAnswer => Selected.Count > 0 || !string.IsNullOrWhiteSpace(CustomBox.Text);
    }

    public static async Task<List<AskAnswer>?> ShowAsync(Window? owner, List<AskQuestion> questions)
    {
        var states = questions.Select(q => new QuestionState { Q = q }).ToList();

        var body = new StackPanel { Spacing = 20 };
        foreach (var st in states)
            body.Children.Add(BuildQuestion(st));

        var scroll = new SmoothScrollViewer()
        {
            Content = body,
            MaxHeight = 430,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(18, 46, 18, 0),
        };

        var cancelBtn = BuildButton("取消", "#374151", "#E5E7EB", "#E5E7EB");
        var submitBtn = BuildButton("提交", "#FFFFFF", Accent, Accent);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, submitBtn },
            Margin = new Thickness(18, 16, 18, 16),
        };

        var dlg = new Window
        {
            Title = "需要你的回答",
            Width = 470,
            MaxHeight = 680,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            WindowDecorations = WindowDecorations.BorderOnly,
        };

        // 自绘标题栏（系统标题栏已去掉），承担拖拽
        var titleBar = new Border
        {
            Height = 40,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "需要你的回答",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        titleBar.PointerPressed += (_, e) => dlg.BeginMoveDrag(e);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        grid.Children.Add(new StackPanel { Children = { titleBar, scroll } });
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        dlg.Content = grid;

        void RefreshSubmit()
        {
            var ready = states.Any(s => s.HasAnswer);
            submitBtn.IsEnabled = ready;
            var brush = new SolidColorBrush(Color.Parse(ready ? Accent : AccentDim));
            submitBtn.Background = brush;
            submitBtn.BorderBrush = brush;
        }

        foreach (var st in states)
            st.Changed = RefreshSubmit;
        RefreshSubmit();

        List<AskAnswer>? result = null;
        cancelBtn.Click += (_, _) => dlg.Close();
        submitBtn.Click += (_, _) =>
        {
            // 按选项原始顺序取选中项，而不是用户点击顺序
            result = states.Select(st => new AskAnswer
            {
                Question = st.Q.Question,
                Selected = st.Q.Options.Where(o => st.Selected.Contains(o.Label)).Select(o => o.Label).ToList(),
                Custom = string.IsNullOrWhiteSpace(st.CustomBox.Text) ? null : st.CustomBox.Text!.Trim(),
            }).ToList();
            dlg.Close();
        };

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();

        return result;
    }

    private static Control BuildQuestion(QuestionState st)
    {
        var block = new StackPanel { Spacing = 8 };

        if (!string.IsNullOrWhiteSpace(st.Q.Header))
        {
            block.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#EEF1F6")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = st.Q.Header,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#4A5B6D")),
                },
            });
        }

        block.Children.Add(new TextBlock
        {
            Text = st.Q.Question,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TitleFg)),
            TextWrapping = TextWrapping.Wrap,
        });

        var options = new StackPanel { Spacing = 6 };
        foreach (var option in st.Q.Options)
            options.Children.Add(BuildOption(st, option));
        block.Children.Add(options);

        var box = new TextBox
        {
            Height = 32,
            FontSize = 12.5,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "自定义回答（不选上面的选项，直接写在这里）",
            Margin = new Thickness(0, 2, 0, 0),
        };
        box.TextChanged += (_, _) =>
        {
            // 单选下一旦开始写自定义答案，就取消已选项，避免「选了一个又写了一个」的歧义。
            // 注意：上面点选项时会清空本框，那次 TextChanged 的文本为空，不会走到这里。
            if (!st.Q.MultiSelect && !string.IsNullOrWhiteSpace(box.Text))
            {
                st.Selected.Clear();
                foreach (var refresh in st.RefreshRows) refresh();
            }
            st.Changed?.Invoke();
        };
        st.CustomBox = box;
        block.Children.Add(box);

        return block;
    }

    private static Control BuildOption(QuestionState st, AskOption option)
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
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse(TitleFg)),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(option.Description))
            texts.Children.Add(new TextBlock
            {
                Text = option.Description,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse(SubFg)),
                TextWrapping = TextWrapping.Wrap,
            });

        var check = new TextBlock
        {
            Text = "",   // CheckMark
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(Accent)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(texts);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        void Refresh()
        {
            var on = st.Selected.Contains(option.Label);
            row.Background = new SolidColorBrush(Color.Parse(on ? SelectedBg : "#FFFFFF"));
            row.BorderBrush = new SolidColorBrush(Color.Parse(on ? Accent : LineFg));
            check.IsVisible = on;
        }

        st.RefreshRows.Add(Refresh);
        Refresh();

        row.PointerEntered += (_, _) =>
        {
            if (!st.Selected.Contains(option.Label))
                row.Background = new SolidColorBrush(Color.Parse("#F3F4F6"));
        };
        row.PointerExited += (_, _) => Refresh();
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (st.Q.MultiSelect)
            {
                if (!st.Selected.Add(option.Label))
                    st.Selected.Remove(option.Label);
            }
            else
            {
                st.Selected.Clear();
                st.Selected.Add(option.Label);
                st.CustomBox.Text = "";   // 单选：选项与自定义输入互斥
            }
            foreach (var refresh in st.RefreshRows) refresh();
            st.Changed?.Invoke();
        };

        return row;
    }

    private static Button BuildButton(string text, string fg, string accent, string bg) => new()
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
        Padding = new Thickness(0, 0),
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.Parse(bg)),
        BorderBrush = new SolidColorBrush(Color.Parse(accent)),
        BorderThickness = new Thickness(1),
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
