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
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 工作区会话专属聊天视图：渲染层与 <see cref="ChatView"/> 完全一致
/// （可折叠思考卡、输入/输出工具卡、模型头、复制/编辑/删除 + Tokens 汇总），
/// 但作为单独维护的变体——读写 workspace_sessions、用 <see cref="WorkspaceAgentPipeline"/>
/// 组装三层提示词、会话直接创建、输入草稿防抖落库。消息仍复用 chat_message 表。
/// </summary>
public partial class WorkspaceChatView : UserControl
{
    private readonly WorkspaceConfig _workspace;
    private readonly Agent? _agent;
    private WorkspaceChatSession _session;
    private readonly List<ChatMessage> _messages = new();
    private readonly ChatGateway _gateway = new();
    private readonly ToolExecutorDispatcher _toolExecutor = new();
    private bool _busy;
    private bool _skillEventsHooked;
    private bool _toolEventsHooked;
    private CancellationTokenSource? _draftCts;

    private string? _activeTurnId;
    private StackPanel? _activeTurnRoot;
    private readonly List<Control> _turnSteps = new();
    private int _turnToolCount;
    private int _turnThinkingCount;
    private Control? _turnLastBody;
    private readonly SelectionManager _selectionManager = new();

    public WorkspaceChatView(WorkspaceConfig workspace, Agent? agent, WorkspaceChatSession session)
    {
        InitializeComponent();
        _workspace = workspace;
        _agent = agent;
        _session = session;

        // 消息列表跨块文本选择：统一挂一颗 SelectionManager 到 MessagesPanel
        _selectionManager.AttachHost(MessagesPanel);

        ConfigureToolExecutor();
        InitPermissionMode();
        LoadTools();
        LoadSkills();
        LoadToolsSelection();
        RestoreDraft();
        // 回车发送 / Shift+Enter 换行（TextBox 内部处理 Enter 时事件已 handled）
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDown, handledEventsToo: true);
        InputBox.TextChanged += (_, _) => _ = DebouncedSaveDraftAsync();
        _ = LoadModelsAsync();
        _ = LoadMessagesAsync();
    }

    /// <summary>会话对象（供外部读取）。</summary>
    public WorkspaceChatSession Session => _session;

    /// <summary>当前思考模式：透传模型选择器。</summary>
    public ThinkingMode ThinkingMode
    {
        get => ModelSelector.ThinkingMode;
        set => ModelSelector.ThinkingMode = value;
    }

    // ---- 工具 / 技能选择器 ----

    private void LoadTools()
    {
        var registry = ToolRegistry.Instance;
        ToolSelector.Toolsets = registry.GetToolsets();
        ToolSelector.Tools = registry.GetEnabled();
    }

    private void LoadSkills()
    {
        SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();
        SkillSelector.SelectedSkills = _session.EnabledSkills;
        if (_skillEventsHooked) return;
        _skillEventsHooked = true;
        SkillSelector.SelectionChanged += async (_, _) => await SyncSkillsToSessionAsync();
    }

    private async Task SyncSkillsToSessionAsync()
    {
        _session.EnabledSkills = SkillSelector.SelectedSkills.ToList();
        await PersistSessionAsync();
    }

    private void LoadToolsSelection()
    {
        if (_session.EnabledToolIds.Count > 0)
            ToolSelector.SelectedToolIds = _session.EnabledToolIds;
        if (_toolEventsHooked) return;
        _toolEventsHooked = true;
        ToolSelector.SelectionChanged += async (_, _) => await SyncToolsToSessionAsync();
    }

    private async Task SyncToolsToSessionAsync()
    {
        _session.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        await PersistSessionAsync();
    }

    private IReadOnlyList<ToolDefinition>? ResolveActiveTools()
    {
        if (ToolSelector.SelectedToolIds.Count == 0) return null;
        return ToolRegistry.Instance.ResolveByIds(ToolSelector.SelectedToolIds);
    }

    // ---- 工具执行器 ----

    private void ConfigureToolExecutor()
    {
        _toolExecutor.Context = new BuiltinToolContext
        {
            BaseDirectory = string.IsNullOrWhiteSpace(_workspace.Path) ? Environment.CurrentDirectory : _workspace.Path,
            GetSessionSkills = () => Task.FromResult<List<SessionSkill>?>(_session.EnabledSkills),
            SaveSessionSkills = async skills =>
            {
                _session.EnabledSkills = skills;
                await PersistSessionAsync();
                SkillSelector.SelectedSkills = skills;
            },
        };
        _toolExecutor.ApprovalGate = async (call, tool) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            return await ConfirmDialog.ShowToolApprovalAsync(owner, tool, call);
        };
        _toolExecutor.Mode = _session.ToolPermissionMode;
    }

    private async Task PersistSessionAsync()
    {
        try { await WorkspaceSessionRepository.UpdateAsync(_session); } catch { /* 忽略 */ }
    }

    // ---- 权限模式（会话级盾牌图标） ----

    private MenuFlyout? PermMenu => PermModeBtn.Flyout as MenuFlyout;

    private void InitPermissionMode()
    {
        if (PermMenu is not { } menu) return;
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Tag is not string tag) continue;
            mi.ToggleType = MenuItemToggleType.CheckBox;
            mi.Click += (_, _) => _ = SetPermissionModeAsync(ParseMode(tag));
        }
        ApplyPermissionMode();
    }

    private async Task SetPermissionModeAsync(PermissionMode mode)
    {
        _session.ToolPermissionMode = mode;
        _toolExecutor.Mode = mode;
        ApplyPermissionMode();
        await PersistSessionAsync();
    }

    private void ApplyPermissionMode()
    {
        var mode = _session.ToolPermissionMode;
        _toolExecutor.Mode = mode;

        if (PermMenu is { } menu)
            foreach (var item in menu.Items)
                if (item is MenuItem mi && mi.Tag is string tag)
                    mi.IsChecked = ParseMode(tag) == mode;

        var color = mode switch
        {
            PermissionMode.Strict => "#DC2626", // 红
            PermissionMode.Normal => "#4A90D9", // 蓝
            PermissionMode.Loose => "#D97706",  // 橙
            PermissionMode.Free => "#16A34A",   // 绿
            _ => "#9CA3AF",
        };
        PermModeIcon.Foreground = new SolidColorBrush(Color.Parse(color));
        ToolTip.SetTip(PermModeBtn, ModeTip(mode));
    }

    private static string ModeTip(PermissionMode mode) => mode switch
    {
        PermissionMode.Strict => "权限模式：严格（所有工具需确认）",
        PermissionMode.Normal => "权限模式：普通（安全工具自动通过）",
        PermissionMode.Loose => "权限模式：宽松（安全/风险工具自动通过）",
        PermissionMode.Free => "权限模式：自由（所有工具免确认）",
        _ => "",
    };

    private static PermissionMode ParseMode(string tag) => tag switch
    {
        "Strict" => PermissionMode.Strict,
        "Normal" => PermissionMode.Normal,
        "Loose" => PermissionMode.Loose,
        "Free" => PermissionMode.Free,
        _ => PermissionMode.Normal,
    };

    // ---- 加载模型与消息 ----

    private async Task LoadModelsAsync()
    {
        try
        {
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
            if (!string.IsNullOrEmpty(_session.Model))
            {
                var saved = ModelSelector.Models.FirstOrDefault(m => m.ModelName == _session.Model);
                if (saved is not null)
                    ModelSelector.SelectedModel = saved;
            }
        }
        catch
        {
            // 数据库未就绪时保持内置默认模型
        }
        // 恢复会话保存的思考模式
        ModelSelector.ThinkingMode = (ThinkingMode)_session.Thinking;
    }

    private async Task LoadMessagesAsync()
    {
        _messages.Clear();
        _messages.AddRange(await ChatRepository.GetBySessionAsync(_session.Id));
        RenderHistory();
        ScrollToEnd();
    }

    /// <summary>外部触发：会话标题/时间变化后由 WorkspacePage 刷新列表。返回是否已变更（仅供通知）。</summary>
    public event Action? Updated;

    // ---- 发送 ----

    public async Task SendMessageAsync(string text)
    {
        if (_busy || string.IsNullOrWhiteSpace(text)) return;
        _busy = true;
        SendBtn.IsEnabled = false;
        InputBox.Text = "";

        if (string.IsNullOrEmpty(_session.Title))
            _session.Title = text.Length > 24 ? text[..24] + "…" : text;
        var selected = ModelSelector.SelectedModel;
        _session.Model = selected?.ModelName ?? ChatConfig.Model;
        _session.Thinking = (int)ModelSelector.ThinkingMode;
        _session.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        _session.LastMessageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistSessionAsync();

        var client = await _gateway.ResolveClientAsync(selected?.ModelId);
        var system = WorkspaceAgentPipeline.BuildSystemPrompt(_agent, _workspace, _session, _session.Model, _workspace.Path);

        await _gateway.ChatAsync(
            _session.Id,
            system,
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

        if (_activeTurnId is not null && _activeTurnRoot is not null)
        {
            // 回复结束：一轮内有多次思考 / 工具调用时，把步骤收进单个可折叠卡片（最后正文保留在外）
            TurnActivityCard.TryWrap(_activeTurnRoot, _turnSteps, _turnToolCount, _turnThinkingCount, _turnLastBody);

            var turnAssistants = _messages
                .Where(m => m.Role == "assistant" && m.TurnId == _activeTurnId)
                .ToList();
            _activeTurnRoot.Children.Add(CreateAiReplyOps(_activeTurnId, turnAssistants));
            ScrollToEnd();
        }

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

    // ---- 历史渲染（与 ChatView 一致） ----

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

        foreach (var (turnId, msgs) in groups)
            RenderTurnBlock(turnId, msgs);

        _activeTurnId = null;
        _activeTurnRoot = null;
    }

    private void RenderTurnBlock(string? turnId, List<ChatMessage> msgs)
    {
        var first = msgs[0];
        if (first.Role == "user")
        {
            MessagesPanel.Children.Add(CreateUserMessageBlock(first));
            return;
        }

        var (displayName, providerName) = ModelInfo(first.ModelName ?? ChatConfig.Model);
        var rootPanel = new StackPanel
        {
            Margin = new Thickness(20, 2, 20, 2),
            Spacing = 10,
        };
        rootPanel.Children.Add(CreateModelHeader(displayName, providerName, first.CreatedAt));

        var steps = new List<Control>();
        var toolCount = 0;
        var thinkingCount = 0;
        Control? lastBody = null;
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
                    steps.Add(card.Root);
                    thinkingCount++;
                }
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    var body = new StackPanel();
                    var parser = new MarkdownStreamParser(body);
                    parser.AppendText(m.Content);
                    parser.Complete();
                    rootPanel.Children.Add(body);
                    steps.Add(body);
                    lastBody = body;
                }
            }
            else if (m.Role == "tool")
            {
                var input = FindToolCallInput(m.ToolCallId);
                var card = new ToolCallCard();
                card.SetData(m.ToolName ?? "工具", input, m.Content ?? "");
                rootPanel.Children.Add(card.Root);
                steps.Add(card.Root);
                toolCount++;
            }
        }

        // 历史记录同样聚合：多次思考/工具调用收进单个卡片，最终正文留在卡片外
        TurnActivityCard.TryWrap(rootPanel, steps, toolCount, thinkingCount, lastBody);

        if (turnId is not null)
            rootPanel.Children.Add(CreateAiReplyOps(turnId, msgs.Where(m => m.Role == "assistant").ToList()));

        MessagesPanel.Children.Add(rootPanel);
    }

    /// <summary>实时：工具结果以可折叠工具卡追加到当前 turn 根（无根时兜底追加到消息面板）。</summary>
    private void AddToolCallCard(ChatMessage toolMsg)
    {
        var input = FindToolCallInput(toolMsg.ToolCallId);
        var card = new ToolCallCard();
        card.SetData(toolMsg.ToolName ?? "工具", input, toolMsg.Content ?? "");

        _turnToolCount++;
        if (_activeTurnRoot is not null)
            _activeTurnRoot.Children.Add(card.Root);
        else
            MessagesPanel.Children.Add(card.Root);
        _turnSteps.Add(card.Root);
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

    // ---- 消息操作（复制 / 编辑 / 删除）与 Tokens 汇总 ----

    private static (long Input, long Cache, long Output) ParseUsage(ChatMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.UsageJson)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(m.UsageJson);
            var root = doc.RootElement;
            var input = root.TryGetProperty("input_tokens", out var a) ? a.GetInt64() : 0;
            var cache = root.TryGetProperty("cache_hit_tokens", out var b) ? b.GetInt64() : 0;
            var output = root.TryGetProperty("output_tokens", out var c) ? c.GetInt64() : 0;
            return (input, cache, output);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private Button CreateGlyphButton(string glyph, string tooltip, Action onClick, string hoverColor = "#374151")
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = icon,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.PointerEntered += (_, _) => icon.Foreground = new SolidColorBrush(Color.Parse(hoverColor));
        btn.PointerExited += (_, _) => icon.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Control CreateAiReplyOps(string turnId, IReadOnlyList<ChatMessage> assistantMsgs)
    {
        long input = 0, cache = 0, output = 0;
        var copyText = new StringBuilder();
        foreach (var m in assistantMsgs)
        {
            var t = ParseUsage(m);
            input += t.Input;
            cache += t.Cache;
            output += t.Output;
            if (!string.IsNullOrWhiteSpace(m.Content))
                copyText.AppendLine(m.Content.TrimEnd());
        }
        var total = input + output;

        var copyBtn = CreateGlyphButton("", "复制", async () => await CopyText(copyText.ToString()));
        var editBtn = CreateGlyphButton("", "编辑", () => { /* TODO: 编辑 */ });
        var delBtn = CreateGlyphButton("", "删除", async () => await DeleteTurnAsync(turnId), hoverColor: "#DC2626");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { copyBtn, editBtn, delBtn },
        };
        var tokens = new TextBlock
        {
            Text = $"Tokens: {total}  ↑{input}  ↑*{cache}  ↓{output}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        grid.Children.Add(left);
        grid.Children.Add(tokens);
        Grid.SetColumn(tokens, 1);
        return grid;
    }

    private Control CreateUserOps(string messageId, string text)
    {
        var copyBtn = CreateGlyphButton("", "复制", async () => await CopyText(text));
        var editBtn = CreateGlyphButton("", "编辑", () => { /* TODO: 编辑 */ });
        var delBtn = CreateGlyphButton("", "删除", async () => await DeleteMessageAsync(messageId), hoverColor: "#DC2626");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 20, 8),
            Children = { copyBtn, editBtn, delBtn },
        };
    }

    private Control CreateUserMessageBlock(ChatMessage msg)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 0,
        };
        var (bubble, block) = CreateUserElement();
        block.Text = msg.Content ?? "";
        stack.Children.Add(bubble);
        stack.Children.Add(CreateUserOps(msg.Id, msg.Content ?? ""));
        return stack;
    }

    private async Task CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null) return;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            await top.Clipboard.SetDataAsync(transfer);
        }
        catch
        {
            // 剪贴板不可用时忽略
        }
    }

    private async Task DeleteTurnAsync(string turnId)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这段回复吗？")) return;
        try { await ChatRepository.SoftDeleteTurnAsync(_session.Id, turnId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    private async Task DeleteMessageAsync(string messageId)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这条消息吗？")) return;
        try { await ChatRepository.SoftDeleteMessageAsync(_session.Id, messageId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    private async Task ReloadMessagesAsync()
    {
        _messages.Clear();
        _messages.AddRange(await ChatRepository.GetBySessionAsync(_session.Id));
        _activeTurnId = null;
        _activeTurnRoot = null;
        RenderHistory();
        ScrollToEnd();
    }

    /// <summary>实时：新一轮 assistant 开始流式时创建 UI 元素并返回流式回调。</summary>
    private ChatRoundSink BeginAssistantRound(ChatMessage assistantMsg)
    {
        var (displayName, providerName) = ModelInfo(assistantMsg.ModelName ?? ChatConfig.Model);

        StackPanel root;
        if (assistantMsg.TurnId is not null && assistantMsg.TurnId == _activeTurnId && _activeTurnRoot is not null)
            root = _activeTurnRoot;
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
            _turnSteps.Clear();
            _turnToolCount = 0;
            _turnThinkingCount = 0;
            _turnLastBody = null;
        }

        var card = new ThinkingCard();
        card.Root.IsVisible = false;
        root.Children.Add(card.Root);
        _turnSteps.Add(card.Root);

        // 正文用 Markdown 解析器直接生成 StackPanel（外层由消息列表的 SelectableTextContainer 统一选中）
        var body = new StackPanel();
        var bodyParser = new MarkdownStreamParser(body);
        root.Children.Add(body);
        _turnSteps.Add(body);

        return new ChatRoundSink
        {
            OnContent = delta =>
            {
                _turnLastBody = body;
                bodyParser.AppendText(delta);
                ScrollToEnd();
            },
            OnThinking = delta =>
            {
                if (!card.Root.IsVisible) _turnThinkingCount++;
                card.Show();
                card.Append(delta);
                ScrollToEnd();
            },
            OnFailed = message =>
            {
                _turnLastBody = body;
                bodyParser.AppendText("请求失败：" + message);
            },
        };
    }

    /// <summary>按消息渲染：实时 user 气泡；历史加载走 RenderHistory 按 turn 分组，工具结果走 ToolCallCard。</summary>
    private void AddMessage(ChatMessage m)
    {
        switch (m.Role)
        {
            case "user":
                MessagesPanel.Children.Add(CreateUserMessageBlock(m));
                break;

            default:
                var (plainRoot, plainBlock) = CreatePlainBlock("#1A1A2E", 14);
                plainBlock.Text = m.Content ?? "";
                MessagesPanel.Children.Add(plainRoot);
                break;
        }
    }

    /// <summary>用户消息：浅蓝气泡（黑字，便于选中高亮可辨），靠右。</summary>
    private (Control Root, TextBlock Block) CreateUserElement()
    {
        var block = new CustomSelectableTextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
        };
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#CBE1F3")),
            Padding = new Thickness(12, 10),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(80, 0, 20, 0),
            Child = block,
        };
        return (bubble, block);
    }

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
        var block = new CustomSelectableTextBlock
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

    // ---- 草稿（防抖落库） ----

    private void RestoreDraft()
    {
        InputBox.Text = _session.Draft ?? "";
    }

    private async Task DebouncedSaveDraftAsync()
    {
        using var cts = new CancellationTokenSource(500);
        _draftCts?.Cancel();
        _draftCts = cts;
        try
        {
            await Task.Delay(500, cts.Token);
            _session.Draft = InputBox.Text;
            await WorkspaceSessionRepository.SaveDraftAsync(_session.Id, _session.Draft ?? "");
        }
        catch (TaskCanceledException)
        {
            // 输入未停顿，忽略
        }
    }

    // ============================================================
    // 可折叠「已深度思考」卡片（与 ChatView 一致的实现）
    // ============================================================

    private sealed class ThinkingCard
    {
        private const double ExpandedMaxHeight = 300;
        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

        private readonly ScrollViewer _contentHost;
        private readonly RotateTransform _chevron;
        private readonly Border _header;
        private bool _expanded;

        public Border Root { get; }
        public TextBlock ContentBlock { get; }

        public ThinkingCard()
        {
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

            ContentBlock = new CustomSelectableTextBlock
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

        private static Control CreateLightbulbIcon(Brush brush)
            => new TextBlock
            {
                Text = "", // Segoe Fluent Icons：灯泡（ea80）
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
    }

    // ============================================================
    // 可折叠「{工具名} 工具」卡片（与 ChatView 一致的实现）
    // ============================================================

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

        public Border Root { get; }

        public ToolCallCard()
        {
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

            _inputBlock = new CustomSelectableTextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#333333")),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                FontFamily = new FontFamily("Consolas"),
            };
            _outputBlock = new CustomSelectableTextBlock
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
