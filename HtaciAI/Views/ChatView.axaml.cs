using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;

namespace HtaciAI.Views;

/// <summary>
/// 会话对话视图：消息列表（ScrollViewer>StackPanel）+ 底部输入框 + DeepSeek 流式回复。
/// </summary>
public partial class ChatView : UserControl
{
    private readonly string? _sessionId;
    private ChatSession _session = new();
    private readonly List<ChatMessage> _messages = new();
    private bool _busy;

    /// <summary>会话标题/时间变化时触发，供 ChatPage 刷新列表。</summary>
    public event Action? Updated;

    /// <summary>无参构造函数，供 XAML 预览器/设计器使用（无会话 ID，显示加载失败提示）。</summary>
    public ChatView() : this(null) { }

    /// <param name="sessionId">为 null 时作为占位视图（工作空间页/预览器用法），显示加载失败提示，不读写数据库。</param>
    public ChatView(string? sessionId)
    {
        InitializeComponent();
        _sessionId = sessionId;
        if (_sessionId is null)
            ShowLoadFailed();
    }

    /// <summary>从库加载会话与历史消息并渲染。</summary>
    public async Task InitializeAsync()
    {
        if (_sessionId is null)
        {
            ShowLoadFailed();
            return;
        }
        try
        {
            _session = await ChatRepository.GetAsync(_sessionId)
                       ?? new ChatSession { Id = _sessionId, Title = "会话" };
            _messages.Clear();
            _messages.AddRange(await ChatRepository.GetBySessionAsync(_sessionId));
            MessagesPanel.Children.Clear();
            foreach (var m in _messages)
                AddBubble(m.Role, m.Content);
            ScrollToEnd();
        }
        catch (Exception ex)
        {
            ShowLoadFailed("加载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 无会话 ID 或加载异常时展示错误提示，并禁用输入，避免渲染空白/崩溃。
    /// </summary>
    private void ShowLoadFailed(string message = "加载失败，无会话ID")
    {
        MessagesPanel.Children.Clear();
        MessagesPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 0),
        });
        InputBox.IsEnabled = false;
        SendBtn.IsEnabled = false;
    }

    public async Task SendMessageAsync(string text)
    {
        if (_sessionId is null || _busy || string.IsNullOrWhiteSpace(text)) return;
        _busy = true;
        SendBtn.IsEnabled = false;
        InputBox.Text = "";

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var turnId = Guid.NewGuid().ToString("N");

        var userMsg = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = _sessionId,
            TurnId = turnId,
            SequenceNumber = await ChatRepository.GetNextSequenceAsync(_sessionId),
            Role = "user",
            Content = text,
            Status = "completed",
            ModelName = ChatConfig.Model,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _messages.Add(userMsg);
        AddBubble("user", text);
        await ChatRepository.InsertAsync(userMsg);

        // 首条消息确定会话标题，并刷新 updated_at
        if (string.IsNullOrEmpty(_session.Title))
            _session.Title = text.Length > 24 ? text[..24] + "…" : text;
        _session.Model = ChatConfig.Model;
        await ChatRepository.UpdateAsync(_session);

        var assistantMsg = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = _sessionId,
            TurnId = turnId,
            SequenceNumber = await ChatRepository.GetNextSequenceAsync(_sessionId),
            Role = "assistant",
            Status = "completed",
            ModelName = ChatConfig.Model,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var (bubble, block) = CreateBubble("assistant");
        MessagesPanel.Children.Add(bubble);
        ScrollToEnd();

        try
        {
            var sb = new StringBuilder();
            var (content, usageJson) = await DeepSeekClient.StreamChatAsync(
                _messages, _session.SystemPrompt,
                delta =>
                {
                    sb.Append(delta);
                    block.Text = sb.ToString();
                    ScrollToEnd();
                },
                CancellationToken.None);

            assistantMsg.Content = content;
            assistantMsg.UsageJson = string.IsNullOrEmpty(usageJson) ? null : usageJson;
            assistantMsg.Status = "completed";
        }
        catch (Exception ex)
        {
            assistantMsg.Status = "failed";
            block.Text = "请求失败：" + ex.Message;
        }

        await ChatRepository.InsertAsync(assistantMsg);
        _messages.Add(assistantMsg);

        _busy = false;
        SendBtn.IsEnabled = true;
        Updated?.Invoke();
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
        => _ = SendFromInputAsync();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = SendFromInputAsync();
        }
    }

    private async Task SendFromInputAsync()
    {
        var text = InputBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            await SendMessageAsync(text);
    }

    private (Border Bubble, TextBlock Block) CreateBubble(string role)
    {
        var isUser = role == "user";
        var block = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse(isUser ? "#FFFFFF" : "#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
        };
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse(isUser ? "#4A90D9" : "#F1F3F5")),
            Padding = new Thickness(12, 10),
            MaxWidth = 560,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(isUser ? 80 : 0, 0, isUser ? 0 : 80, 0),
            Child = block,
        };
        return (bubble, block);
    }

    private void AddBubble(string role, string? content)
    {
        var (bubble, block) = CreateBubble(role);
        block.Text = content ?? "";
        MessagesPanel.Children.Add(bubble);
    }

    private void ScrollToEnd()
    {
        MessagesScroll.ScrollToEnd();
    }
}
