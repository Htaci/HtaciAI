using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Controls;

/// <summary>
/// 输入框上方那一栏「排队中」。
///
/// 会话正在跑任务时用户又发的消息不会丢，也不会打断正在跑的那一轮：它们排在这里，
/// 等任务结束按顺序发出去；点了「引导插入」的则会插进当前这一轮的下一个工具循环边界。
///
/// 纯展示 + 转发事件：队列本身在 <see cref="ConversationRuntime"/> 里，这个控件只把它画出来。
/// 放在控件上会有一个具体后果 —— 切到别的标签页再切回来，视图被重建，排队的东西就没了。
/// </summary>
public sealed class PendingQueueControl : UserControl
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush Sub = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));

    private readonly TextBlock _title;
    private readonly TextBlock _hint;
    private readonly StackPanel _items;

    /// <summary>点「引导插入」：在下一次请求之前把这条并进当前这一轮。</summary>
    public event Action<PendingInput>? SteerRequested;

    /// <summary>点那枚「将在下一步插入」：撤回到普通排队。</summary>
    public event Action<PendingInput>? UnsteerRequested;

    /// <summary>点「编辑」：暂停自动发送，把这一条改成可编辑。</summary>
    public event Action<PendingInput>? EditRequested;

    /// <summary>编辑完成，带回改过的正文。</summary>
    public event Action<PendingInput, string>? EditCommitted;

    /// <summary>点「取消」：从队列里去掉，正文由宿主还回输入框。</summary>
    public event Action<PendingInput>? CancelRequested;

    public PendingQueueControl()
    {
        _title = new TextBlock { FontSize = 11.5, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center };
        _hint = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Faint,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Enter 排队 · Ctrl+Enter 引导",
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(_title);
        Grid.SetColumn(_hint, 1);
        header.Children.Add(_hint);

        _items = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9FAFB")),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 10),
            Child = new StackPanel { Children = { header, _items } },
        };

        IsVisible = false;
    }

    /// <summary>重画。空列表时整个栏一起隐藏 —— 留一个空框会让人以为输入框坏了。</summary>
    public void SetItems(IReadOnlyList<PendingInput> items, bool paused)
    {
        _items.Children.Clear();

        if (items.Count == 0)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _title.Text = $"排队中（{items.Count}）";

        // 暂停时把提示换成原因：否则用户看到「任务结束了但消息没发出去」，会以为卡死了。
        _hint.Text = paused ? "已暂停：正在编辑，改完继续发送" : "Enter 排队 · Ctrl+Enter 引导";
        _hint.Foreground = paused ? Warn : Faint;

        foreach (var item in items) _items.Children.Add(BuildRow(item));
    }

    private Control BuildRow(PendingInput item)
    {
        var row = new Border
        {
            Background = Brushes.White,
            BorderBrush = item.State == PendingInputState.Steer ? Accent : Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
        };

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 12.5,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = item.State == PendingInputState.Editing ? 0 : 3,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var tail = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        if (item.State == PendingInputState.Editing)
        {
            // 编辑态：正文换成可输入的框。自动发送已经因为「有东西在编辑」而暂停，
            // 所以这里不需要额外再挂一个暂停开关给用户点。
            var editor = new TextBox
            {
                Text = item.Text,
                FontSize = 12.5,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 56,
                MaxHeight = 160,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
            };

            var save = SmallButton("保存", Accent);
            save.Click += (_, _) =>
            {
                var text2 = editor.Text?.Trim() ?? "";
                if (text2.Length == 0) return;   // 空内容不提交：留着原样，用户还能继续写或取消
                EditCommitted?.Invoke(item, text2);
            };

            var cancel = SmallButton("取消", Sub);
            cancel.Click += (_, _) => CancelRequested?.Invoke(item);

            tail.Children.Add(save);
            tail.Children.Add(cancel);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(editor);
            Grid.SetColumn(tail, 1);
            grid.Children.Add(tail);
            row.Child = grid;

            // 编辑态不做「悬停才显示」：那两个按钮此刻是主操作，藏起来只会让人找不到。
            return row;
        }

        var steer = SmallButton("引导插入", Sub);
        steer.Click += (_, _) => SteerRequested?.Invoke(item);

        var edit = SmallButton("编辑", Sub);
        edit.Click += (_, _) => EditRequested?.Invoke(item);

        var drop = SmallButton("取消", Sub);
        drop.Click += (_, _) => CancelRequested?.Invoke(item);

        // 已经点过「引导插入」：那个按钮就地换成这枚蓝标，再点一下撤回。
        // 换掉按钮而不是在旁边加标记：两个控件并排表达同一个动作的两个方向，
        // 用户要先想一下哪个才是「我现在想做的」。
        tail.Children.Add(item.State == PendingInputState.Steer ? SteerBadge(item) : steer);
        tail.Children.Add(edit);
        tail.Children.Add(drop);

        // 三个按钮平时透明：常驻会把每一行撑得很吵，而它们只在想动那一条时才需要。
        // 用 Opacity 而不是 IsVisible：IsVisible 会让这一列宽度归零，鼠标一进一出，
        // 正文的换行位置就跟着跳一次 —— 正好在用户准备点按钮的时候。
        tail.Opacity = 0;

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(text);
        Grid.SetColumn(tail, 1);
        content.Children.Add(tail);
        row.Child = content;

        row.PointerEntered += (_, _) => tail.Opacity = 1;
        row.PointerExited += (_, _) => tail.Opacity = 0;

        return row;
    }

    /// <summary>
    /// 「将在下一步插入」那枚蓝标。它同时是撤回按钮 —— 点了插入之后，
    /// 用户下一步想做的几乎只有「改主意」，所以这一格就该是他的撤回入口，
    /// 而不是再让他去找另一个按钮。
    /// </summary>
    private Control SteerBadge(PendingInput item)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EAF2FC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#BBD8F5")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "将在下一步插入",
                FontSize = 10.5,
                Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        ToolTip.SetTip(badge, "再点一下取消插入，退回排队");
        badge.PointerEntered += (_, _) => badge.Background = new SolidColorBrush(Color.Parse("#DCEBFA"));
        badge.PointerExited += (_, _) => badge.Background = new SolidColorBrush(Color.Parse("#EAF2FC"));
        badge.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            UnsteerRequested?.Invoke(item);
        };

        return badge;
    }

    private static Button SmallButton(string label, IBrush foreground) => new()
    {
        Content = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
        Padding = new Thickness(6, 2, 6, 2),
        MinHeight = 0,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(4),
        Cursor = new Cursor(StandardCursorType.Hand),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
