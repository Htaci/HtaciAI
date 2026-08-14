using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
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
/// AI 消息为三段式：模型信息头（头像/名称|服务商/时间）+ 可折叠思考卡片 + 正文。
/// </summary>
public partial class ChatView : UserControl
{
    private readonly string? _sessionId;
    private ChatSession _session = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly ChatGateway _gateway = new();
    private readonly IToolExecutor _toolExecutor = new StubToolExecutor();
    private ThinkingMode _thinkingMode = ThinkingMode.Default;
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
                AddMessage(m);
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

        // 首条消息确定会话标题，并刷新 updated_at
        if (string.IsNullOrEmpty(_session.Title))
            _session.Title = text.Length > 24 ? text[..24] + "…" : text;
        _session.Model = ChatConfig.Model;
        await ChatRepository.UpdateAsync(_session);

        // 统一入口：网关负责 turn 管理、工具循环、消息落库与流式 UI 回调
        await _gateway.ChatAsync(
            _sessionId,
            _session.SystemPrompt,
            _messages,
            text,
            new ChatRequestOptions { Thinking = _thinkingMode },
            _toolExecutor,
            PersistMessage,
            BeginAssistantRound,
            CancellationToken.None);

        _busy = false;
        SendBtn.IsEnabled = true;
        Updated?.Invoke();
    }

    /// <summary>网关回调：把一条新消息落库并加入内存列表（assistant 的 UI 已在 BeginAssistantRound 中渲染）。</summary>
    private async Task PersistMessage(ChatMessage m)
    {
        await ChatRepository.InsertAsync(m);
        _messages.Add(m);
        if (m.Role is "user" or "tool")
            AddMessage(m);
    }

    /// <summary>网关回调：新一轮 assistant 开始流式时创建 UI 元素并返回流式回调。</summary>
    private ChatRoundSink BeginAssistantRound(ChatMessage assistantMsg)
    {
        var (root, body, card) = CreateAssistantElement(assistantMsg.ModelName ?? ChatConfig.Model, assistantMsg.CreatedAt);
        MessagesPanel.Children.Add(root);
        ScrollToEnd();

        var contentSb = new StringBuilder();
        return new ChatRoundSink
        {
            OnContent = delta =>
            {
                contentSb.Append(delta);
                body.Text = contentSb.ToString();
                ScrollToEnd();
            },
            OnThinking = delta =>
            {
                // 首个思考分片到达时让卡片显示，之后持续累积文本
                card.Show();
                card.Append(delta);
                ScrollToEnd();
            },
            OnFailed = message => body.Text = "请求失败：" + message,
        };
    }

    /// <summary>思考模式菜单：NoThink/none/low/high/max → ThinkingMode。</summary>
    private void OnThinkingMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        _thinkingMode = (mi.Header?.ToString()) switch
        {
            "NoThink" => ThinkingMode.NoThink,
            "none" => ThinkingMode.Default,
            "low" => ThinkingMode.Low,
            "high" => ThinkingMode.High,
            "max" => ThinkingMode.Max,
            _ => ThinkingMode.Default,
        };
        if (ThinkingLabel is not null)
            ThinkingLabel.Text = mi.Header?.ToString() ?? "";
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

    /// <summary>按消息渲染：用户用气泡，AI 用「模型头 + 思考卡片 + 正文」三段式，工具角色预留。</summary>
    private void AddMessage(ChatMessage m)
    {
        switch (m.Role)
        {
            case "user":
                var (userRoot, userBlock) = CreateUserElement();
                userBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(userRoot);
                break;

            case "assistant":
                var (assistantRoot, body, card) = CreateAssistantElement(m.ModelName ?? ChatConfig.Model, m.CreatedAt);
                body.Text = m.Content ?? "";
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                {
                    card.SetText(m.Thinking);
                    card.Show();
                }
                MessagesPanel.Children.Add(assistantRoot);
                break;

            case "tool":
                // 工具结果：预留角色样式，暂与普通文本一致，后续按需定制
                var (toolRoot, toolBlock) = CreatePlainBlock("#6B7280", 13);
                toolBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(toolRoot);
                break;

            default:
                var (plainRoot, plainBlock) = CreatePlainBlock("#1A1A2E", 14);
                plainBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(plainRoot);
                break;
        }
    }

    /// <summary>用户消息：蓝色气泡，靠右。</summary>
    private (Control Root, TextBlock Block) CreateUserElement()
    {
        var block = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            TextWrapping = TextWrapping.Wrap,
        };
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#4A90D9")),
            Padding = new Thickness(12, 10),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(80, 0, 20, 0),
            Child = block,
        };
        return (bubble, block);
    }

    /// <summary>
    /// AI 消息三段式：模型信息头 + 思考卡片（默认隐藏，收到思考内容后显示）+ 正文。
    /// </summary>
    private (StackPanel Root, TextBlock Body, ThinkingCard Card) CreateAssistantElement(string modelName, long createdAt)
    {
        var root = new StackPanel
        {
            Margin = new Thickness(20, 2, 20, 2),
            Spacing = 10,
        };
        root.Children.Add(CreateModelHeader(modelName, createdAt));

        var card = new ThinkingCard();
        card.Root.IsVisible = false;
        root.Children.Add(card.Root);

        var body = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        };
        root.Children.Add(body);

        return (root, body, card);
    }

    /// <summary>模型信息头：左侧灰色圆角头像占位，右侧竖排「模型名称 | 服务商」+ 小字灰色时间。</summary>
    private Control CreateModelHeader(string modelName, long createdAt)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 2),
        };

        var avatar = new Border
        {
            Width = 35,
            Height = 35,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-45, 0, 10, 0),
            Child = new TextBlock
            {
                Text = "Ht",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var name = new TextBlock
        {
            Text = $"{modelName} | Htaci",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var time = new TextBlock
        {
            Text = FormatTime(createdAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, time },
        };
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        return grid;
    }

    private static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToLocalTime()
            .ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private (Control Root, TextBlock Block) CreatePlainBlock(string color, double fontSize)
    {
        var block = new TextBlock
        {
            Text = "",
            FontSize = fontSize,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 2, 20, 2),
        };
        return (block, block);
    }

    private void ScrollToEnd()
    {
        MessagesScroll.ScrollToEnd();
    }

    /// <summary>
    /// 可折叠的「已深度思考」推理卡片。点击头部展开/收起，高度与箭头均有过渡动画，
    /// 头部悬停时背景轻微加深。思考内容较多时内容区自动出现极细灰色滚动条。
    /// </summary>
    private sealed class ThinkingCard
    {
        private const double ExpandedMaxHeight = 300;
        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

        private readonly ScrollViewer _contentHost;
        private readonly RotateTransform _chevron;
        private readonly Border _header;
        private bool _expanded;

        /// <summary>卡片根容器（用于控制整体显隐）。</summary>
        public Border Root { get; }

        /// <summary>思考内容文本块。</summary>
        public TextBlock ContentBlock { get; }

        public ThinkingCard()
        {
            // —— 头部：灯泡图标 + 「已深度思考」 + 右侧箭头 ——
            var icon = CreateLightbulbIcon(new SolidColorBrush(Color.Parse("#333333")));

            var title = new TextBlock
            {
                Text = "已深度思考",
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#1F1F1F")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.Parse("#C5C5C7")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _chevron = new RotateTransform();
            chevron.RenderTransform = _chevron;
            chevron.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _chevron.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = RotateTransform.AngleProperty,
                    Duration = AnimDuration,
                    Easing = new CubicEaseInOut(),
                },
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(18, 0, 18, 0),
            };
            var iconHost = new Border
            {
                Child = icon,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(iconHost, 0);
            headerGrid.Children.Add(iconHost);
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);
            Grid.SetColumn(chevron, 2);
            headerGrid.Children.Add(chevron);

            _header = new Border
            {
                Child = headerGrid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Height = 46,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _header.PointerEntered += (_, _) => _header.Background = new SolidColorBrush(Color.Parse("#99F0F0F0"));
            _header.PointerExited += (_, _) => _header.Background = Brushes.Transparent;
            _header.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(_header).Properties.IsLeftButtonPressed)
                    Toggle();
            };

            // —— 内容区：可展开的滚动容器 ——
            ContentBlock = new TextBlock
            {
                Text = "",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#666666")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24,
                Margin = new Thickness(5, 18, 5, 18),
            };

            _contentHost = new ScrollViewer
            {
                Content = ContentBlock,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 0,
                Margin = new Thickness(18, 0, 18, 0),
                Classes = { "thin-scrollbar" },
                Opacity = 0,
            };
            _contentHost.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Layoutable.MaxHeightProperty,
                    Duration = AnimDuration,
                    Easing = new CubicEaseInOut(),
                },
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = AnimDuration,
                    Easing = new CubicEaseInOut(),
                },
            };

            // —— 卡片容器 ——
            Root = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#00F7F8FA")),
                BorderBrush = new SolidColorBrush(Color.Parse("#ECEEF1")),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Children = { _header, _contentHost },
                },
            };
        }

        public void SetText(string text) => ContentBlock.Text = text;

        public void Append(string text) => ContentBlock.Text += text;

        /// <summary>首次收到思考内容时调用：让卡片显示出来（保持折叠态）。</summary>
        public void Show()
        {
            if (Root.IsVisible) return;
            Root.IsVisible = true;
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            _contentHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
            _contentHost.Opacity = _expanded ? 1 : 0;
            _chevron.Angle = _expanded ? 90 : 0;
        }

        /// <summary>描边风格的灯泡图标（Feather "lightbulb"），用于卡片头部。</summary>
        private static Control CreateLightbulbIcon(Brush brush)
        {
            var icon = new Grid { Width = 16, Height = 16 };
            void AddFigure(string data)
            {
                icon.Children.Add(new Path
                {
                    Data = Geometry.Parse(data),
                    Stroke = brush,
                    StrokeThickness = 1.5,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stretch = Stretch.Uniform,
                });
            }
            AddFigure("M9 18h6");
            AddFigure("M10 22h4");
            AddFigure("M15.09 14c.18-.98.65-1.74 1.41-2.5A4.65 4.65 0 0 0 18 8 6 6 0 0 0 6 8c0 1 .23 2.23 1.5 3.5.76.76 1.23 1.52 1.41 2.5");
            return icon;
        }
    }
}
