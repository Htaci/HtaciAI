using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace HtaciAI;

public partial class ChatPage : UserControl
{
    public ChatPage()
    {
        InitializeComponent();
        SetupNewChatButton();
        LoadTestData();
    }

    private void SetupNewChatButton()
    {
        NewChatBtn.Cursor = new Cursor(StandardCursorType.Hand);

        NewChatShadow.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new CubicEaseOut()
            }
        };

        NewChatBtn.PointerEntered += (s, e) =>
        {
            NewChatShadow.Opacity = 1;
        };

        NewChatBtn.PointerExited += (s, e) =>
        {
            NewChatShadow.Opacity = 0;
        };

        NewChatBtn.PointerPressed += (s, e) =>
        {
            OnNewChatClick(this, e);
            e.Handled = true;
        };
    }

    /// <summary>
    /// 从数据源加载历史记录列表（后续接入 SQLine 时替换测试数据调用）
    /// </summary>
    public void LoadHistoryItems(IEnumerable<string> items)
    {
        HistoryList.Children.Clear();

        foreach (var item in items)
        {
            HistoryList.Children.Add(CreateHistoryItem(item));
        }
    }

    private void LoadTestData()
    {
        LoadHistoryItems(new[]
        {
            "关于 Avalonia 的 MVVM 模式",
            "C# 中的 async/await 最佳实践",
            "如何实现自定义控件",
            "性能优化技巧讨论",
            "项目结构设计思路",
            "与 Rust 的互操作方案",
        });
    }

    private Border CreateHistoryItem(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(12, 0)
        };

        var border = new Border
        {
            Height = 36,
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = textBlock
        };

        border.PointerEntered += (s, e) =>
        {
            border.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
            border.Cursor = new Cursor(StandardCursorType.Hand);
        };

        border.PointerExited += (s, e) =>
        {
            border.Background = Brushes.Transparent;
            border.Cursor = new Cursor(StandardCursorType.Arrow);
        };

        border.PointerPressed += (s, e) =>
        {
            // TODO: 选中历史会话，加载对应对话内容
            e.Handled = true;
        };

        return border;
    }

    private void OnNewChatClick(object? sender, RoutedEventArgs e)
    {
        // TODO: 创建新会话逻辑
    }
}
