using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using HtaciAI.Services.Tools;

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
    private readonly IToolExecutor _toolExecutor = new ToolExecutorDispatcher();
    private bool _busy;

    /// <summary>当前正在流式渲染的 turn：同一 turn 只渲染一次模型头，多轮工具调用的内容追加到同一根。</summary>
    private string? _activeTurnId;
    private StackPanel? _activeTurnRoot;

    /// <summary>当前思考模式：透传模型选择器，供外部（新建会话流程）初始化。</summary>
    public ThinkingMode ThinkingMode
    {
        get => ModelSelector.ThinkingMode;
        set => ModelSelector.ThinkingMode = value;
    }

    /// <summary>当前激活的工具 id 集合：透传工具选择器，供外部（新建会话流程）初始化。</summary>
    public IReadOnlyList<string> SelectedToolIds
    {
        get => ToolSelector.SelectedToolIds;
        set => ToolSelector.SelectedToolIds = value;
    }

    /// <summary>把注册表中的工具集与已启用工具注入工具选择器。</summary>
    private void LoadTools()
    {
        var registry = ToolRegistry.Instance;
        ToolSelector.Toolsets = registry.GetToolsets();
        ToolSelector.Tools = registry.GetEnabled();
    }

    /// <summary>解析当前激活的工具定义（去重后）；无激活工具时返回 null（不发送 tools）。</summary>
    private IReadOnlyList<ToolDefinition>? ResolveActiveTools()
    {
        if (ToolSelector.SelectedToolIds.Count == 0) return null;
        return ToolRegistry.Instance.ResolveByIds(ToolSelector.SelectedToolIds);
    }

    /// <summary>会话标题/时间变化时触发，供 ChatPage 刷新列表。</summary>
    public event Action? Updated;

    /// <summary>无参构造函数，供 XAML 预览器/设计器使用（无会话 ID，显示加载失败提示）。</summary>
    public ChatView() : this(null) { }

    /// <param name="sessionId">为 null 时作为占位视图（工作空间页/预览器用法），显示加载失败提示，不读写数据库。</param>
    public ChatView(string? sessionId)
    {
        InitializeComponent();
        _sessionId = sessionId;
        LoadTools();
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
            // 模型列表：加载已启用的模型（含服务商），为空时控件内部回退内置默认模型
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
            // 恢复会话记录的模型选中态（按调用 id 匹配）
            if (!string.IsNullOrEmpty(_session.Model))
            {
                var saved = ModelSelector.Models.FirstOrDefault(m => m.ModelName == _session.Model);
                if (saved is not null)
                    ModelSelector.SelectedModel = saved;
            }
            _messages.Clear();
            _messages.AddRange(await ChatRepository.GetBySessionAsync(_sessionId));
            RenderHistory();
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
        // 记录会话使用的模型调用 id（内置模型为 ChatConfig.Model）
        var selected = ModelSelector.SelectedModel;
        _session.Model = selected?.ModelName ?? ChatConfig.Model;
        await ChatRepository.UpdateAsync(_session);

        // 统一入口：网关负责 turn 管理、工具循环、消息落库与流式 UI 回调。
        // 按当前选中模型解析客户端（未配置/不可用时回退内置默认模型），确保所选模型被真正调用。
        var client = await _gateway.ResolveClientAsync(selected?.ModelId);

        await _gateway.ChatAsync(
            _sessionId,
            _session.SystemPrompt,
            _messages,
            text,
            new ChatRequestOptions
            {
                Thinking = ModelSelector.ThinkingMode,
                Tools = ResolveActiveTools(),
            },
            client,
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
        if (m.Role == "user")
            AddMessage(m);
        else if (m.Role == "tool")
            AddToolCallCard(m);
    }

    /// <summary>
    /// 按 turn 分组渲染历史：同一 turn 的多个 assistant / tool 消息（多轮工具调用共享同一 turn_id）
    /// 归为一组，只渲染一次模型头。user 消息单独成组。
    /// </summary>
    private void RenderHistory()
    {
        MessagesPanel.Children.Clear();

        var groups = new List<(string? TurnId, List<ChatMessage> Msgs)>();
        foreach (var m in _messages)
        {
            if (m.Role == "user")
            {
                groups.Add((null, new List<ChatMessage> { m }));
            }
            else if (m.Role is "assistant" or "tool")
            {
                var key = m.TurnId;
                var last = groups.Count > 0 ? groups[^1] : default;
                if (last.TurnId is not null && last.TurnId == key)
                    last.Msgs.Add(m);
                else
                    groups.Add((key, new List<ChatMessage> { m }));
            }
        }

        foreach (var (_, msgs) in groups)
            RenderTurnBlock(msgs);
    }

    /// <summary>渲染一组消息：user 单条气泡；assistant 起头则渲染模型头 + 思考卡 + 工具卡 + 正文。</summary>
    private void RenderTurnBlock(List<ChatMessage> msgs)
    {
        var first = msgs[0];
        if (first.Role == "user")
        {
            var (root, block) = CreateUserElement();
            block.Text = first.Content ?? "";
            MessagesPanel.Children.Add(root);
            return;
        }

        var (displayName, providerName) = ModelInfo(first.ModelName ?? ChatConfig.Model);
        var rootPanel = new StackPanel
        {
            Margin = new Thickness(20, 2, 20, 2),
            Spacing = 10,
        };
        rootPanel.Children.Add(CreateModelHeader(displayName, providerName, first.CreatedAt));

        foreach (var m in msgs)
        {
            if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                {
                    var card = new ThinkingCard();
                    card.SetText(m.Thinking);
                    card.Show();
                    rootPanel.Children.Add(card.Root);
                }
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    rootPanel.Children.Add(new TextBlock
                    {
                        Text = m.Content,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 22,
                    });
                }
            }
            else if (m.Role == "tool")
            {
                var input = FindToolCallInput(m.ToolCallId);
                var card = new ToolCallCard();
                card.SetData(m.ToolName ?? "工具", input, m.Content ?? "");
                rootPanel.Children.Add(card.Root);
            }
        }

        MessagesPanel.Children.Add(rootPanel);
    }

    /// <summary>实时：工具结果以可折叠工具卡追加到当前 turn 根（无根时兜底追加到消息面板）。</summary>
    private void AddToolCallCard(ChatMessage toolMsg)
    {
        var input = FindToolCallInput(toolMsg.ToolCallId);
        var card = new ToolCallCard();
        card.SetData(toolMsg.ToolName ?? "工具", input, toolMsg.Content ?? "");

        if (_activeTurnRoot is not null)
            _activeTurnRoot.Children.Add(card.Root);
        else
            MessagesPanel.Children.Add(card.Root);
        ScrollToEnd();
    }

    /// <summary>按 tool_call_id 反查输入参数（来自对应 assistant 消息的 metadata 里的 tool_calls）。</summary>
    private string FindToolCallInput(string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)) return "{}";
        foreach (var m in _messages)
        {
            if (m.Role != "assistant") continue;
            var calls = ToolCallJson.Deserialize(m.Metadata);
            var c = calls.FirstOrDefault(x => x.Id == toolCallId);
            if (c is not null) return c.Arguments ?? "{}";
        }
        return "{}";
    }

    /// <summary>网关回调：新一轮 assistant 开始流式时创建 UI 元素并返回流式回调。</summary>
    private ChatRoundSink BeginAssistantRound(ChatMessage assistantMsg)
    {
        var (displayName, providerName) = ModelInfo(assistantMsg.ModelName ?? ChatConfig.Model);

        // 同一 turn（含多轮工具调用）只渲染一次模型头；新 turn 或首次时新建根容器
        StackPanel root;
        if (assistantMsg.TurnId is not null && assistantMsg.TurnId == _activeTurnId && _activeTurnRoot is not null)
        {
            root = _activeTurnRoot;
        }
        else
        {
            root = new StackPanel
            {
                Margin = new Thickness(20, 2, 20, 2),
                Spacing = 10,
            };
            root.Children.Add(CreateModelHeader(displayName, providerName, assistantMsg.CreatedAt));
            MessagesPanel.Children.Add(root);
            ScrollToEnd();
            _activeTurnId = assistantMsg.TurnId;
            _activeTurnRoot = root;
        }

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
            IsVisible = false, // 纯工具轮次无正文时不占位
        };
        root.Children.Add(body);

        var contentSb = new StringBuilder();
        return new ChatRoundSink
        {
            OnContent = delta =>
            {
                contentSb.Append(delta);
                body.Text = contentSb.ToString();
                body.IsVisible = true;
                ScrollToEnd();
            },
            OnThinking = delta =>
            {
                // 首个思考分片到达时让卡片显示，之后持续累积文本
                card.Show();
                card.Append(delta);
                ScrollToEnd();
            },
            OnFailed = message =>
            {
                body.Text = "请求失败：" + message;
                body.IsVisible = true;
            },
        };
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

    /// <summary>按消息渲染：实时 user 气泡；历史加载走 RenderHistory 按 turn 分组，工具结果走 ToolCallCard。</summary>
    private void AddMessage(ChatMessage m)
    {
        switch (m.Role)
        {
            case "user":
                var (userRoot, userBlock) = CreateUserElement();
                userBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(userRoot);
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

    /// <summary>模型信息头：左侧灰色圆角头像占位，右侧竖排「模型名称 | 服务商」+ 小字灰色时间。</summary>
    private Control CreateModelHeader(string modelName, string providerName, long createdAt)
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
            Text = $"{modelName} | {providerName}",
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

    /// <summary>根据模型调用 id 解析展示信息（显示名 + 服务商名）；无匹配时回退 call id + Htaci。</summary>
    private (string DisplayName, string ProviderName) ModelInfo(string callId)
    {
        if (string.IsNullOrEmpty(callId)) callId = ChatConfig.Model;
        var m = ModelSelector.Models.FirstOrDefault(x => x.ModelName == callId);
        return m is not null
            ? (m.DisplayName, m.ProviderName)
            : (callId, "Htaci");
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

    /// <summary>
    /// 可折叠的工具调用卡片：折叠态显示「{工具名} 工具」，展开显示「输入 / 输出」两段。
    /// 交互与样式对齐 ThinkingCard。
    /// </summary>
    private sealed class ToolCallCard
    {
        private const double ExpandedMaxHeight = 300;
        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

        private readonly ScrollViewer _contentHost;
        private readonly RotateTransform _chevron;
        private readonly Border _header;
        private readonly TextBlock _titleBlock;
        private readonly TextBlock _inputBlock;
        private readonly TextBlock _outputBlock;
        private bool _expanded;

        /// <summary>卡片根容器（用于控制整体显隐）。</summary>
        public Border Root { get; }

        public ToolCallCard()
        {
            // —— 头部：工具图标 + 「{工具名} 工具」 + 右侧箭头 ——
            var icon = new TextBlock
            {
                Text = "", // Segoe Fluent Icons：扳手（工具）
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            _titleBlock = new TextBlock
            {
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
            Grid.SetColumn(_titleBlock, 1);
            headerGrid.Children.Add(_titleBlock);
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

            // —— 展开内容：输入 / 输出 两段 ——
            _inputBlock = new TextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                FontFamily = new FontFamily("Consolas"),
            };
            _outputBlock = new TextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            };

            var contentStack = new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(5, 14, 5, 14),
            };
            contentStack.Children.Add(MakeLabel("输入"));
            contentStack.Children.Add(_inputBlock);
            contentStack.Children.Add(MakeLabel("输出"));
            contentStack.Children.Add(_outputBlock);

            _contentHost = new ScrollViewer
            {
                Content = contentStack,
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

        public void SetData(string toolName, string inputJson, string output)
        {
            _titleBlock.Text = $"{toolName} 工具";
            _inputBlock.Text = FormatJson(inputJson);
            _outputBlock.Text = string.IsNullOrWhiteSpace(output) ? "（无输出）" : output;
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            _contentHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
            _contentHost.Opacity = _expanded ? 1 : 0;
            _chevron.Angle = _expanded ? 90 : 0;
        }

        private static TextBlock MakeLabel(string text) => new()
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
        };

        private static string FormatJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }
    }
}
