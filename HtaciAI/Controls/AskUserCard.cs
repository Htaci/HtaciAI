using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>
/// 对话内嵌的「向用户提问」卡片（ask_user_question 工具的界面）。
///
/// 一题一页：多题时顶部是 `‹ 1/2 ›` 翻页加关闭，底部按钮按位置显示「下一题 / 提交」。
/// 单选还是多选由界面自己标注 —— 模型不必也没必要在题干里写「（可多选）」。
/// 单选可再点一次已选项取消选择；单选下选项与自定义回答互斥，多选下两者并存。
/// 作答（或取消）之后由调用方把卡片从界面上撤掉：这次询问的记录留在工具卡里。
/// </summary>
public sealed class AskUserCard : UserControl
{
    private const string AccentHex = "#4A90D9";
    private const string TitleHex = "#1A1A2E";
    private const string SubHex = "#4A5B6D";
    private const string FaintHex = "#9CA3AF";
    private const string LineHex = "#E5E7EB";
    private const string ChipBgHex = "#F1F3F5";
    private const string SelectedBgHex = "#EEF4FB";
    private const string HoverBgHex = "#F5F6F8";
    private const string CardBgHex = "#FFFFFF";

    /// <summary>一道题的作答状态。</summary>
    private sealed class QuestionState
    {
        public AskQuestion Q { get; init; } = new();

        /// <summary>已选中的选项 label。</summary>
        public HashSet<string> Selected { get; } = new(StringComparer.Ordinal);

        public TextBox Custom { get; set; } = null!;

        /// <summary>每个选项行的重绘动作。</summary>
        public List<Action> RefreshRows { get; } = new();

        /// <summary>该题在翻页容器里的那一页。</summary>
        public Control Page { get; set; } = null!;

        public AskAnswer ToAnswer() => new()
        {
            Question = Q.Question,
            // 按选项原始顺序取，而不是用户点击顺序
            Selected = Q.Options.Where(o => Selected.Contains(o.Label)).Select(o => o.Label).ToList(),
            Custom = string.IsNullOrWhiteSpace(Custom.Text) ? null : Custom.Text!.Trim(),
        };
    }

    private readonly List<QuestionState> _states;
    private readonly TaskCompletionSource<List<AskAnswer>?> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StackPanel _pageHost = new() { Spacing = 8 };
    private readonly TextBlock _pageLabel = new()
    {
        FontSize = 11.5,
        Foreground = new SolidColorBrush(Color.Parse(FaintHex)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0),
    };

    private Button? _prevBtn;
    private Button? _nextBtn;
    private Button? _primaryBtn;
    private int _index;

    /// <summary>作答结果；用户关掉这次询问时为 null。作答之前一直处于等待状态。</summary>
    public Task<List<AskAnswer>?> Answer => _done.Task;

    public AskUserCard(List<AskQuestion> questions)
    {
        _states = questions.Select(q => new QuestionState { Q = q }).ToList();

        foreach (var st in _states)
            st.Page = BuildPage(st);

        var body = new StackPanel
        {
            Spacing = 10,
            Children = { BuildHeader(), _pageHost, BuildFooter() },
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse(CardBgHex)),
            BorderBrush = new SolidColorBrush(Color.Parse(LineHex)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12),
            Child = body,
        };

        ShowPage(0);
    }

    // ---- 头部：标题 + 翻页 + 关闭 ----

    private Control BuildHeader()
    {
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_states.Count > 1)
        {
            _prevBtn = CreateIconButton("‹", "上一题", () => ShowPage(_index - 1));
            _nextBtn = CreateIconButton("›", "下一题", () => ShowPage(_index + 1));
            right.Children.Add(_prevBtn);
            right.Children.Add(_pageLabel);
            right.Children.Add(_nextBtn);
        }
        right.Children.Add(CreateIconButton("✕", "关闭，不作答", Cancel));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(HeaderLine("需要你的回答"));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
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

    private Control BuildFooter()
    {
        _primaryBtn = CreatePrimaryButton("下一题", Advance);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        grid.Children.Add(CreateTextButton("跳过本题", SkipCurrent));
        Grid.SetColumn(_primaryBtn, 1);
        grid.Children.Add(_primaryBtn);
        return grid;
    }

    // ---- 一页 = 一道题 ----

    private Control BuildPage(QuestionState st)
    {
        var block = new StackPanel { Spacing = 8 };

        if (!string.IsNullOrWhiteSpace(st.Q.Header))
        {
            block.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(ChipBgHex)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = st.Q.Header,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse(SubHex)),
                },
            });
        }

        var question = new TextBlock
        {
            Text = st.Q.Question,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TitleHex)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(CreateTypeBadge(st.Q.MultiSelect));
        Grid.SetColumn(question, 1);
        head.Children.Add(question);
        block.Children.Add(head);

        var options = new StackPanel { Spacing = 6 };
        for (var i = 0; i < st.Q.Options.Count; i++)
            options.Children.Add(BuildOption(st, st.Q.Options[i], i + 1));
        block.Children.Add(options);

        var box = new TextBox
        {
            Height = 32,
            FontSize = 12.5,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = st.Q.MultiSelect
                ? "输入您的回答…（可与上面的选项同时提交）"
                : "输入您的回答…（可跳过选项直接写）",
        };
        box.TextChanged += (_, _) =>
        {
            // 单选下开始写自定义答案就算放弃所选选项；上面点选项时会清空本框，
            // 那次 TextChanged 的文本为空，不会走到这里。
            if (!st.Q.MultiSelect && !string.IsNullOrWhiteSpace(box.Text) && st.Selected.Count > 0)
            {
                st.Selected.Clear();
                RefreshRows(st);
            }
        };
        st.Custom = box;
        block.Children.Add(box);

        return block;
    }

    private Control BuildOption(QuestionState st, AskOption option, int number)
    {
        var numberText = new TextBlock
        {
            Text = number.ToString(CultureInfo.InvariantCulture),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse(ChipBgHex)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = numberText,
        };

        var texts = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = option.Label,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse(TitleHex)),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(option.Description))
            texts.Children.Add(new TextBlock
            {
                Text = option.Description,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse(FaintHex)),
                TextWrapping = TextWrapping.Wrap,
            });

        // 勾选标记只在多选出现：单选靠整行高亮表达，多选必须让「可以多选」这件事看得见
        var mark = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = "✓",
                FontSize = 10.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(badge);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(mark, 2);
        grid.Children.Add(mark);

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
            row.Background = new SolidColorBrush(Color.Parse(on ? SelectedBgHex : CardBgHex));
            row.BorderBrush = new SolidColorBrush(Color.Parse(on ? AccentHex : LineHex));
            badge.Background = new SolidColorBrush(Color.Parse(on ? AccentHex : ChipBgHex));
            numberText.Foreground = new SolidColorBrush(Color.Parse(on ? "#FFFFFF" : SubHex));
            mark.IsVisible = st.Q.MultiSelect;
            mark.Background = new SolidColorBrush(Color.Parse(on ? AccentHex : CardBgHex));
            mark.BorderBrush = new SolidColorBrush(Color.Parse(on ? AccentHex : "#D5DAE1"));
            if (mark.Child is TextBlock tick) tick.IsVisible = on;
        }

        st.RefreshRows.Add(Refresh);
        Refresh();

        row.PointerEntered += (_, _) =>
        {
            if (!st.Selected.Contains(option.Label))
                row.Background = new SolidColorBrush(Color.Parse(HoverBgHex));
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
            else if (!st.Selected.Remove(option.Label))
            {
                // 再点一次已选项 = 取消选择（这一题就可以留空不作答）
                st.Selected.Clear();
                st.Selected.Add(option.Label);
                st.Custom.Text = "";   // 单选：选项与自定义回答互斥
            }
            RefreshRows(st);
        };

        return row;
    }

    private static void RefreshRows(QuestionState st)
    {
        foreach (var refresh in st.RefreshRows) refresh();
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

    // ---- 翻页 / 作答 ----

    private void ShowPage(int index)
    {
        if (_states.Count == 0) return;
        _index = Math.Clamp(index, 0, _states.Count - 1);

        _pageHost.Children.Clear();
        _pageHost.Children.Add(_states[_index].Page);

        if (_states.Count > 1)
        {
            _pageLabel.Text = $"{_index + 1} / {_states.Count}";
            if (_prevBtn is not null) _prevBtn.IsEnabled = _index > 0;
            if (_nextBtn is not null) _nextBtn.IsEnabled = _index < _states.Count - 1;
        }
        if (_primaryBtn?.Content is TextBlock label)
            label.Text = _index == _states.Count - 1 ? "提交" : "下一题";
    }

    /// <summary>底部主按钮：最后一题是「提交」，否则翻到下一题。</summary>
    private void Advance()
    {
        if (_index >= _states.Count - 1) Submit();
        else ShowPage(_index + 1);
    }

    /// <summary>跳过本题：清空这题的作答（提交时按「未作答」反馈给模型），能翻页就翻下一页。</summary>
    private void SkipCurrent()
    {
        var st = _states[_index];
        st.Selected.Clear();
        st.Custom.Text = "";
        RefreshRows(st);

        Advance();
    }

    private void Submit() => _done.TrySetResult(_states.Select(s => s.ToAnswer()).ToList());

    private void Cancel() => _done.TrySetResult(null);

    // ---- 按钮 ----

    private static Button CreateIconButton(string glyph, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(FaintHex)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = text,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.PointerEntered += (_, _) => text.Foreground = new SolidColorBrush(Color.Parse("#374151"));
        btn.PointerExited += (_, _) => text.Foreground = new SolidColorBrush(Color.Parse(FaintHex));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Button CreateTextButton(string text, Action onClick)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse(FaintHex)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = block,
            Height = 30,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.PointerEntered += (_, _) => block.Foreground = new SolidColorBrush(Color.Parse("#374151"));
        btn.PointerExited += (_, _) => block.Foreground = new SolidColorBrush(Color.Parse(FaintHex));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Button CreatePrimaryButton(string text, Action onClick)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = block,
            MinWidth = 76,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(AccentHex)),
            BorderBrush = new SolidColorBrush(Color.Parse(AccentHex)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
