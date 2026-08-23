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
using HtaciAI.Services.Skills;
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
    private readonly ToolExecutorDispatcher _toolExecutor = new();
    private bool _busy;
    private bool _skillEventsHooked;
    private bool _toolEventsHooked;

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

    /// <summary>把可选技能与当前会话已启用的技能注入技能选择器，并挂载变更回调（仅一次）。</summary>
    private void LoadSkills()
    {
        SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();
        SkillSelector.SelectedSkills = _session.EnabledSkills;
        if (_skillEventsHooked) return;
        _skillEventsHooked = true;
        SkillSelector.SelectionChanged += async (_, _) => await SyncSkillsToSessionAsync();
    }

    /// <summary>技能选择变化后回写到会话对象并落库，供下次请求构建 system 时动态生效。</summary>
    private async Task SyncSkillsToSessionAsync()
    {
        _session.EnabledSkills = SkillSelector.SelectedSkills.ToList();
        if (_sessionId is null) return;
        try { await ChatRepository.UpdateAsync(_session); } catch { /* 忽略 */ }
    }

    /// <summary>恢复会话激活的工具选择，并挂载变更回调（仅一次）。已有启用记录时才覆盖当前选择。</summary>
    private void LoadToolsSelection()
    {
        if (_session.EnabledToolIds.Count > 0)
            ToolSelector.SelectedToolIds = _session.EnabledToolIds;
        if (_toolEventsHooked) return;
        _toolEventsHooked = true;
        ToolSelector.SelectionChanged += async (_, _) => await SyncToolsToSessionAsync();
    }

    /// <summary>工具选择变化后回写会话对象并落库，供重开会话时恢复。</summary>
    private async Task SyncToolsToSessionAsync()
    {
        _session.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        if (_sessionId is null) return;
        try { await ChatRepository.UpdateAsync(_session); } catch { /* 忽略 */ }
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
        // 回车发送、Shift+Enter 换行：Box 内部处理 Enter（AcceptsReturn）时 C# 事件已被标记 handled，
        // 需注册 handledEventsToo:true 才能可靠拦截回车（否则 Enter 只在输入框内换行而不发送）。
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDown, handledEventsToo: true);
        ConfigureToolExecutor();
        InitPermissionMode();
        LoadTools();
        if (_sessionId is null)
            ShowLoadFailed();
    }

    /// <summary>配置工具执行器：审批弹窗、内置工具宿主上下文、权限档位（写自会话级配置）。</summary>
    private void ConfigureToolExecutor()
    {
        _toolExecutor.Context = new BuiltinToolContext
        {
            BaseDirectory = Environment.CurrentDirectory,
            GetSessionSkills = () => Task.FromResult<List<SessionSkill>?>(_session.EnabledSkills),
            SaveSessionSkills = async skills =>
            {
                _session.EnabledSkills = skills;
                if (_sessionId is not null)
                {
                    try { await ChatRepository.UpdateAsync(_session); } catch { /* 忽略 */ }
                }
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

    // ---- 权限模式（输入框盾牌图标） ----

    /// <summary>取得权限菜单（通过按钮 Flyout 访问，规避 x:Name 在 Flyout 上的编译歧义）。</summary>
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

    /// <summary>切换权限档位：写回会话对象并按需落库（权限是会话级，非全局）。</summary>
    private async Task SetPermissionModeAsync(PermissionMode mode)
    {
        _session.ToolPermissionMode = mode;
        ApplyPermissionMode();
        if (_sessionId is not null)
        {
            try { await ChatRepository.UpdateAsync(_session); } catch { /* 忽略 */ }
        }
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
            LoadSkills();
            LoadToolsSelection();
            ApplyPermissionMode(); // 恢复会话各自保存的权限档位
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
        // 持久化会话激活工具，并标记本次实际对话请求时间（用于会话排序；配置文件变更不动它）
        _session.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        _session.LastMessageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await ChatRepository.UpdateAsync(_session);

        // 统一入口：网关负责 turn 管理、工具循环、消息落库与流式 UI 回调。
        // 按当前选中模型解析客户端（未配置/不可用时回退内置默认模型），确保所选模型被真正调用。
        var client = await _gateway.ResolveClientAsync(selected?.ModelId);

        await _gateway.ChatAsync(
            _sessionId,
            BuildSystemPrompt(),
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

        // 回复结束：在当前 turn 根追加操作栏（复制/编辑/删除 + Tokens 汇总）
        if (_activeTurnId is not null && _activeTurnRoot is not null)
        {
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

    /// <summary>
    /// 构建发送给模型的 system 提示词：在会话自带的 system_prompt 之后，动态拼接会话启用技能。
    /// 允许自动使用（allowed）的技能只列元数据（name: description），模型按需唤醒；
    /// 已加载（loaded）的技能将其 SKILL.md 全文注入，始终可用。手动关闭技能后下次请求不再包含。
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("你是 HtaciAI（Agent），一款由 赫塔奇智能科技有限公司 研发的智能AI助手/智能体，可以完成办公协助、项目开发、答疑解惑、代理操作等很多工作。");

        sb.AppendLine("遵循原则：\r\n\r\n" +
            "严格遵守中国法律法规，拒绝回答涉及色情、暴力、政治敏感、违法犯罪等不安全内容，履行 AI 安全规范。\r\n\r\n" +
            "默认语气风格：简洁、直接、切题。除非用户要求，否则不要使用 emoji。" +
            "如无用户要求，尽量用 4 行以内的文字回答，不要加无关的前言后语，如果用户提示词中有明确其他语气设定，或用户喜欢其他语气风格，则按照用户要求。\r\n\r\n" +
            "主动程度：只在被要求时主动，不要擅自行动吓到用户，如果不确定用户是否有让开始行动时，则询问用户是否要开始，直到用户明确指示开始。\r\n\r\n" +
            "遇到敏感问题时，统一回复：“我无法回答该问题，请换个问题试试吧。”");

        sb.AppendLine($"当前系统环境：{Environment.OSVersion}，模型id为： {_session.Model}");

        sb.AppendLine("\r\n\r\n#========用户提示词开始========#\r\n\r\n");


        if (!string.IsNullOrWhiteSpace(_session.SystemPrompt))
            sb.AppendLine(_session.SystemPrompt);

        sb.AppendLine("\r\n\r\n#========用户提示词结束========#\r\n\r\n");

        var loadedIds = _session.EnabledSkills.Where(s => s.Status == "loaded").Select(s => s.Id).ToHashSet();      // 已加载的技能
        var allowedIds = _session.EnabledSkills.Where(s => s.Status == "allowed").Select(s => s.Id).ToHashSet();    // 允许使用的技能
        
        // 1) 允许自动使用的技能：仅元数据
        if (allowedIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下是可用技能（仅元数据，完整内容按需加载）：");
            foreach (var skill in SkillRegistry.Instance.GetByIds(allowedIds))
                sb.AppendLine($"{skill.Name}: {skill.Description}");
        }

        // 2) 已加载的技能：SKILL.md 全文注入
        foreach (var skill in SkillRegistry.Instance.GetByIds(loadedIds))
        {
            sb.AppendLine();
            sb.AppendLine($"<skill name=\"{skill.Name}\">");
            sb.AppendLine(skill.Body);
            sb.AppendLine("</skill>");
        }

        return sb.ToString();
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

        foreach (var (turnId, msgs) in groups)
            RenderTurnBlock(turnId, msgs);
    }

    /// <summary>渲染一组消息：user 气泡 + 操作栏；assistant 起头则渲染模型头 + 思考卡 + 工具卡 + 正文 + 回复操作栏。</summary>
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

    // ---- 消息操作（复制 / 编辑 / 删除）与 Tokens 汇总 ----

    /// <summary>解析 assistant 消息的 usage_json，返回 (输入, 缓存命中, 输出) token。</summary>
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

    /// <summary>构造一个仅图标的操作按钮（Segoe Fluent Icons），悬停变色。</summary>
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

    /// <summary>AI 回复操作栏：左侧复制/编辑/删除，右侧 Tokens 汇总（同 turn 累加）。</summary>
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

    /// <summary>用户消息操作栏：气泡下方、右对齐的复制/编辑/删除。</summary>
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

    /// <summary>用户消息块：气泡 + 其下方右对齐操作栏。</summary>
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

    /// <summary>软删除一轮 AI 回复（按 turn_id），确认后刷新。</summary>
    private async Task DeleteTurnAsync(string turnId)
    {
        if (_sessionId is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这段回复吗？")) return;
        try { await ChatRepository.SoftDeleteTurnAsync(_sessionId, turnId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    /// <summary>软删除一条用户消息，确认后刷新。</summary>
    private async Task DeleteMessageAsync(string messageId)
    {
        if (_sessionId is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这条消息吗？")) return;
        try { await ChatRepository.SoftDeleteMessageAsync(_sessionId, messageId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    /// <summary>从库重新加载未删除消息并按 turn 重渲染（删除后调用）。</summary>
    private async Task ReloadMessagesAsync()
    {
        _messages.Clear();
        _messages.AddRange(await ChatRepository.GetBySessionAsync(_sessionId!));
        _activeTurnId = null;
        _activeTurnRoot = null;
        RenderHistory();
        ScrollToEnd();
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
                MessagesPanel.Children.Add(CreateUserMessageBlock(m));
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
            return new TextBlock
            {
                Text = "", // Segoe Fluent Icons：灯泡（ea80）
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
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
