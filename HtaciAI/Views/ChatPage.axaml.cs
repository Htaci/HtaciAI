using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Views;

namespace HtaciAI;

/// <summary>
/// 智能对话页：左侧会话列表 + 右侧视图（未打开会话→NewChatView，打开会话→ChatView）。
/// </summary>
public partial class ChatPage : UserControl
{
    private string? _currentSessionId;
    private readonly List<ChatSession> _sessions = new();

    public ChatPage()
    {
        InitializeComponent();
        SetupNewChatButton();
        _ = LoadSessionsAsync();
        ShowNewChatView();
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

        NewChatBtn.PointerEntered += (s, e) => NewChatShadow.Opacity = 1;
        NewChatBtn.PointerExited += (s, e) => NewChatShadow.Opacity = 0;
        NewChatBtn.PointerPressed += (s, e) =>
        {
            ShowNewChatView();
            e.Handled = true;
        };
    }

    /// <summary>回到新建会话视图（右侧显示 NewChatView）。</summary>
    private void ShowNewChatView()
    {
        _currentSessionId = null;
        var view = new NewChatView();
        view.SendRequested += OnNewChatSend;
        RightPanel.Content = view;
    }

    private async void OnNewChatSend(object? sender, string text)
    {
        if (sender is not NewChatView nv) return;
        nv.SendRequested -= OnNewChatSend;
        if (string.IsNullOrWhiteSpace(text)) return;

        var session = new ChatSession
        {
            Title = text.Length > 24 ? text[..24] + "…" : text,
            Model = nv.SelectedModel?.ModelName ?? ChatConfig.Model,
            Thinking = (int)nv.ThinkingMode,
        };
        await ChatRepository.CreateAsync(session);
        _currentSessionId = session.Id;

        var view = new ChatView(session.Id);
        view.ThinkingMode = nv.ThinkingMode; // 首条消息沿用新建页选择的思考模式
        view.SelectedToolIds = nv.SelectedToolIds; // 首条消息沿用新建页激活的工具
        view.Updated += OnSessionUpdated;
        RightPanel.Content = view;
        await view.InitializeAsync();
        await view.SendMessageAsync(text);
        await LoadSessionsAsync();
    }

    private async Task OpenSession(string id)
    {
        _currentSessionId = id;
        var view = new ChatView(id);
        view.Updated += OnSessionUpdated;
        RightPanel.Content = view;
        await view.InitializeAsync();
        await LoadSessionsAsync();
    }

    private async void OnSessionUpdated() => await LoadSessionsAsync();

    // ---- 会话列表 ----

    private async Task LoadSessionsAsync()
    {
        _sessions.Clear();
        _sessions.AddRange(await ChatRepository.GetAllAsync());

        HistoryList.Children.Clear();
        foreach (var s in _sessions)
            HistoryList.Children.Add(CreateSessionItem(s));
    }

    private Border CreateSessionItem(ChatSession s)
    {
        var isActive = s.Id == _currentSessionId;

        var titleBlock = new TextBlock
        {
            Text = s.Title,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var timeBlock = new TextBlock
        {
            Text = FormatTime(s.UpdatedAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        // 删除按钮（hover 会话项时显示，替换时间）
        var delIcon = new TextBlock
        {
            Text = "", // Segoe Fluent Icons：删除（e74d）
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var delBtn = new Button
        {
            Content = delIcon,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        delBtn.PointerEntered += (_, _) => delIcon.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        delBtn.PointerExited += (_, _) => delIcon.Foreground = new SolidColorBrush(Color.Parse("#1A1A2E"));
        // 单击时先阻止事件冒泡到父级（避免误触“打开会话”），释放后再执行删除
        delBtn.PointerPressed += (_, e) => e.Handled = true;
        delBtn.Click += async (_, _) => await DeleteSessionAsync(s);

        var rightHost = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto") };
        rightHost.Children.Add(timeBlock);
        rightHost.Children.Add(delBtn);
        Grid.SetColumn(rightHost, 1);

        var item = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { titleBlock, rightHost }
            }
        };

        item.PointerEntered += (s2, e2) =>
        {
            if (!isActive) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
            timeBlock.IsVisible = false;
            delBtn.IsVisible = true;
        };
        item.PointerExited += (s2, e2) =>
        {
            if (!isActive) item.Background = Brushes.Transparent;
            timeBlock.IsVisible = true;
            delBtn.IsVisible = false;
        };
        item.PointerPressed += async (s2, e2) =>
        {
            await OpenSession(s.Id);
            e2.Handled = true;
        };

        return item;
    }

    /// <summary>软删除会话并刷新列表；若删除的是当前会话则回到新建会话视图。</summary>
    private async Task DeleteSessionAsync(ChatSession s)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, $"确定删除会话「{s.Title}」吗？", "删除会话")) return;

        try { await ChatRepository.SoftDeleteSessionAsync(s.Id); } catch { /* 忽略 */ }
        if (_currentSessionId == s.Id)
            ShowNewChatView();
        await LoadSessionsAsync();
    }

    private static string FormatTime(long ms)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var now = DateTimeOffset.Now;
        if (dt.Date == now.Date) return dt.ToString("HH:mm");
        if (dt.Date == now.AddDays(-1).Date) return "昨天";
        return dt.ToString("M/d");
    }
}
