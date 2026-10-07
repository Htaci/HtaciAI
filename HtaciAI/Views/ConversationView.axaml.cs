using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Diagnostics;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 会话视图（普通对话与工作空间会话**共用**）：模型头 + 可折叠思考卡 + 工具卡 +
/// 「处理中/已处理」聚合卡 + 正文原文选择模式（蓝框）+ 复制/编辑/删除与 Tokens 汇总。
///
/// 与具体会话模型解耦——所有差异（工具基准目录、system 提示词、落库路径、草稿能力）
/// 都来自构造时传入的 <see cref="IConversationProfile"/>。消息仍复用 chat_message 表。
/// </summary>
public partial class ConversationView : UserControl
{
    private readonly IConversationProfile _profile;
    private readonly List<ChatMessage> _messages = new();
    private readonly ChatGateway _gateway = new();
    private readonly ToolExecutorDispatcher _toolExecutor = new();
    private bool _busy;
    private bool _skillEventsHooked;
    private bool _toolEventsHooked;
    /// <summary>草稿写入是否在进行中；<see cref="_draftPending"/> 表示期间又有新输入，写完要补一轮。</summary>
    private bool _draftSaving;
    private bool _draftPending;

    /// <summary>
    /// 上一次已知的草稿原文（打开会话时读到的值，之后是每次成功落盘的内容）。
    /// 内容没变就不再写库 —— 见 <see cref="SaveDraftAsync"/>。
    /// </summary>
    private string? _lastSavedDraft;

    /// <summary>当前发送的取消源：运行中点发送按钮即取消（按钮此时是「停止」）。</summary>
    private CancellationTokenSource? _sendCts;

    // ============================================================
    // 历史分段渲染（长会话首屏加速）
    // ============================================================

    /// <summary>
    /// 全量历史按「轮」分组后的结果，由 <see cref="RenderHistory"/> 算一次。
    /// <b>注意：这只用于渲染</b>，发给模型的历史始终取 <see cref="_messages"/> 全量。
    /// </summary>
    private List<(string? TurnId, List<ChatMessage> Msgs)> _turnGroups = new();

    /// <summary>压缩记录覆盖的条数，仅在渲染压缩分割线时用。</summary>
    private readonly Dictionary<string, int> _compressionCovered = new();

    /// <summary>
    /// 已渲染窗口的起始分组下标。0 = 最早的分组也已渲染（没有更早的可加载了）。
    /// </summary>
    private int _firstRenderedGroup;

    /// <summary>正在向上补齐历史：挡住滚动事件重入。</summary>
    private bool _loadingEarlier;

    /// <summary>补历史时正在回填滚动位置：此间忽略「滚到顶部」的判定，否则会连锁触发下一批。</summary>
    private bool _restoringScroll;

    /// <summary>已排队一次「等布局完成再滚到底」，避免重复排队。</summary>
    private bool _scrollToEndPending;

    /// <summary>
    /// 首屏装配期间置位。<see cref="RenderHistory"/> 之后 <see cref="ScrollToEnd"/> 还没落到布局上，
    /// 此刻 Offset 仍是 0，会被 <see cref="IsNearTop"/> 判成「滚到顶部」从而把全部历史一次补齐 ——
    /// 正好把分段渲染的意义抵消掉。所以等首个布局帧跑完再开放判定。
    /// </summary>
    private bool _suppressEarlierLoad;

    /// <summary>本轮渲染的批大小（个「轮」）。</summary>
    private int _historyBatchTurns = 10;

    /// <summary>
    /// 全量历史是否已从库里读完。<b>发消息必须等它为 true</b> ——
    /// 历史没读完就发送，模型会收到一段残缺的上下文，而且这种丢失是静默的。
    /// </summary>
    private bool _historyLoaded;

    /// <summary>历史读取完成信号；<see cref="SendMessageAsync"/> 在没读完时 await 它。</summary>
    private TaskCompletionSource? _historyLoadTcs;

    /// <summary>
    /// tool_call_id → 该调用的 arguments 文本。
    /// 渲染每个工具卡都要按 id 反查入参，逐条遍历全部消息会退化成 O(n²)，
    /// 这在工具调用多的长会话里是主要卡顿来源。一次建索引，查询变 O(1)。
    /// </summary>
    private Dictionary<string, string> _toolCallInputs = new();

    /// <summary>当前轮请求的开始时刻（Unix 毫秒），用于聚合卡显示用时。</summary>
    private long _turnStartedAt;

    private string? _activeTurnId;
    private StackPanel? _activeTurnRoot;

    /// <summary>当前 turn 的聚合卡（收纳思考卡 / 工具卡）；首次出现思考或工具调用时才创建。</summary>
    private TurnActivityCard? _activeTurnCard;

    /// <summary>当前 turn 的选择模式宿主：内容蓝框 + 「显示正文/全部原文」切换。</summary>
    private TurnSelectionHost? _activeTurnHost;

    private int _turnToolCount;
    private int _turnThinkingCount;

    /// <summary>无参构造，仅供 XAML 预览器 / 设计器使用（无真实会话，不会读写数据库）。</summary>
    public ConversationView() : this(new StandaloneProfile("")) { }

    public ConversationView(IConversationProfile profile)
    {
        InitializeComponent();
        _profile = profile;

        // 只做不依赖会话内容的一次性 UI 装配；依赖会话状态的初始化统一放到 InitializeAsync
        // （普通对话要等 LoadAsync 查库回来才有内容，工作空间会话则由页面先行加载）
        LoadTools();
        // 回车发送 / Shift+Enter 换行（TextBox 内部处理 Enter 时事件已 handled）
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDown, handledEventsToo: true);
        InputBox.TextChanged += (_, _) =>
        {
            PruneAttachments();
            _ = SaveDraftAsync();
        };

        // Ctrl+V 走隧道阶段：必须早于 TextBox 自己的粘贴，否则文件/位图已经被当成文本处理掉了
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDownTunnel, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(InputBox, true);
        InputBox.AddHandler(DragDrop.DropEvent, (EventHandler<DragEventArgs>)OnInputDrop);

        // 换模型 = 换上下文上限，占用读数要按新上限重算（用量本身取自历史，不变）
        ModelSelector.SelectedModelChanged += (_, _) => RefreshContextUsage();

        // 每次打开模型菜单都重拉一次列表：服务商/模型改了名、新增了模型都能立刻看到，
        // 不再只依赖会话创建时那一次注入。
        ModelSelector.RefreshRequested += async (_, _) => await RefreshModelsAsync();

        // 滚到接近顶部时补齐更早的历史（长会话首屏只渲染最后一批，见 RenderHistory）
        MessagesScroll.ScrollChanged += OnMessagesScrollChanged;

        SetupMoreMenu();
    }

    // ---- 更多功能菜单 ----

    private MenuItem? _compressMenuItem;

    /// <summary>压缩是否在进行中。防重入：自动压缩与手动点击可能撞上。</summary>
    private bool _compressing;

    /// <summary>
    /// 「更多功能」菜单。刻意在这里装配而不是写进 XAML：Flyout 内部的元素不在本控件的命名域里，
    /// 给它 x:Name 拿不到字段（工具/技能选择器控件里也是这么绕开的）。
    /// </summary>
    private void SetupMoreMenu()
    {
        _compressMenuItem = new MenuItem { Header = "压缩上下文" };
        _compressMenuItem.Click += (_, _) => _ = CompressContextAsync(manual: true);

        var menu = new MenuFlyout();
        menu.Items.Add(_compressMenuItem);

        MoreBtn.Flyout = menu;
        ToolTip.SetTip(MoreBtn, "更多功能");
    }

    private bool _initialized;

    /// <summary>
    /// 载入会话与历史。由调用方显式 await —— 统一了原来「普通对话 await / 工作空间构造期
    /// fire-and-forget」的两种时序，顺带消除后者两路并发的竞态。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _profile.LoadAsync();

        ConfigureToolExecutor();
        InitPermissionMode();
        LoadSkills();
        LoadToolsSelection();
        await LoadMcpSelectionAsync();
        RestoreDraft();

        // 先立好完成信号：LoadMessagesAsync 读完库会 TrySetResult 放行等待中的发送
        _historyLoadTcs = new TaskCompletionSource();

        await LoadModelsAsync();
        await LoadMessagesAsync();
    }

    /// <summary>当前思考模式：透传模型选择器。</summary>
    public ThinkingMode ThinkingMode
    {
        get => ModelSelector.ThinkingMode;
        set => ModelSelector.ThinkingMode = value;
    }

    /// <summary>
    /// 当前激活的工具 id：透传工具选择器。
    /// 新建会话流程用它把「新建页选好的工具」沿用到首条消息（原 ChatView 的公开入口）。
    /// </summary>
    public IReadOnlyList<string> SelectedToolIds
    {
        get => ToolSelector.SelectedToolIds;
        set => ToolSelector.SelectedToolIds = value;
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
        SkillSelector.SelectedSkills = _profile.State.EnabledSkills;
        if (_skillEventsHooked) return;
        _skillEventsHooked = true;
        SkillSelector.SelectionChanged += async (_, _) => await SyncSkillsToSessionAsync();
    }

    private async Task SyncSkillsToSessionAsync()
    {
        _profile.State.EnabledSkills = SkillSelector.SelectedSkills.ToList();
        await PersistSessionAsync();
    }

    private void LoadToolsSelection()
    {
        // 无条件回填：空集合也是有意义的状态（「默认工具」里可以选择一个都不开）
        ToolSelector.SelectedToolIds = _profile.State.EnabledToolIds;
        if (_toolEventsHooked) return;
        _toolEventsHooked = true;
        ToolSelector.SelectionChanged += async (_, _) => await SyncToolsToSessionAsync();
    }

    private async Task SyncToolsToSessionAsync()
    {
        _profile.State.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        await PersistSessionAsync();
    }

    // ---- MCP 选择器 ----

    private bool _mcpEventsHooked;

    /// <summary>
    /// 注入全部 MCP 服务（含未启动的）并回填本会话启用的项。
    /// 未启动的服务也能勾——发送前 <see cref="McpConnectionManager.EnsureToolsAsync"/> 会把它启动起来。
    /// </summary>
    private async Task LoadMcpSelectionAsync()
    {
        try
        {
            McpSelector.Servers = await McpServerRepository.GetAllAsync();
        }
        catch
        {
            // 忽略：保持空列表
        }

        McpSelector.SelectedServerIds = _profile.State.McpServers;

        if (_mcpEventsHooked) return;
        _mcpEventsHooked = true;
        McpSelector.SelectionChanged += async (_, _) => await SyncMcpToSessionAsync();
    }

    private async Task SyncMcpToSessionAsync()
    {
        _profile.State.McpServers = McpSelector.SelectedServerIds.ToList();
        await PersistSessionAsync();
    }

    /// <summary>
    /// 本次请求下发的工具 = 工具选择器勾选的 + 会话启用的 MCP 服务所发现到的。
    /// MCP 的单个工具刻意不进入工具选择器：勾一个服务就等于启用它下面全部工具，
    /// 它们跟随「哪些 MCP 服务被启用」整体生效。
    /// </summary>
    private IReadOnlyList<ToolDefinition>? ResolveActiveTools()
    {
        var tools = new List<ToolDefinition>();

        if (ToolSelector.SelectedToolIds.Count > 0)
            tools.AddRange(ToolRegistry.Instance.ResolveByIds(ToolSelector.SelectedToolIds));

        foreach (var serverId in _profile.State.McpServers)
        {
            if (McpConnectionManager.Instance.GetTools(serverId) is not { } discovered) continue;

            foreach (var info in discovered)
            {
                // 按 id 取（而不是按远端名），因为注册时可能因重名给 Name 加过服务名前缀
                var tool = ToolRegistry.Instance.ResolveById(McpConnectionManager.BuildToolId(serverId, info.Name));
                if (tool is not null) tools.Add(tool);
            }
        }

        return tools.Count > 0 ? tools : null;
    }

    // ---- 工具执行器 ----

    private void ConfigureToolExecutor()
    {
        _toolExecutor.Context = new BuiltinToolContext
        {
            BaseDirectory = _profile.BaseDirectory,
            GetSessionSkills = () => Task.FromResult<List<SessionSkill>?>(_profile.State.EnabledSkills),
            SaveSessionSkills = async skills =>
            {
                _profile.State.EnabledSkills = skills;
                await PersistSessionAsync();
                SkillSelector.SelectedSkills = skills;
            },
            AskUser = async questions =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                return await AskUserDialog.ShowAsync(owner, questions);
            },
        };
        _toolExecutor.ApprovalGate = async (call, tool) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            return await ConfirmDialog.ShowToolApprovalAsync(owner, tool, call);
        };
        _toolExecutor.Mode = _profile.State.PermissionMode;
    }

    private async Task PersistSessionAsync()
    {
        try { await _profile.SaveAsync(); } catch { /* 忽略 */ }
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
        _profile.State.PermissionMode = mode;
        _toolExecutor.Mode = mode;
        ApplyPermissionMode();
        await PersistSessionAsync();
    }

    private void ApplyPermissionMode()
    {
        var mode = _profile.State.PermissionMode;
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
            if (!string.IsNullOrEmpty(_profile.State.Model))
            {
                var saved = ModelSelector.Models.FirstOrDefault(m => m.ModelName == _profile.State.Model);
                if (saved is not null)
                    ModelSelector.SelectedModel = saved;
            }
        }
        catch
        {
            // 数据库未就绪时选择器保持空（显示「未配置模型」），发送侧会拦下并提示
        }
        // 恢复会话保存的思考模式
        ModelSelector.ThinkingMode = (ThinkingMode)_profile.State.Thinking;
    }

    /// <summary>
    /// 重新拉取模型列表（每次打开模型菜单时调用）。
    /// 只刷新列表，<b>不</b>碰当前选中项与思考模式 —— 选择器内部按模型 id 保住选中项；
    /// 沿用 <see cref="LoadModelsAsync"/> 会把用户刚改的思考模式重置回会话保存值。
    /// </summary>
    private async Task RefreshModelsAsync()
    {
        try
        {
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
        }
        catch
        {
            // 读取失败就保持现有列表，不要清空选择器
        }
    }

    private async Task LoadMessagesAsync()
    {
        _messages.Clear();
        PerfTrace.Begin($"会话 {_profile.State.SessionId}");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _messages.AddRange(await ChatRepository.GetBySessionAsync(_profile.State.SessionId));
            PerfTrace.Note("① 读库（全部消息）", sw.Elapsed.TotalMilliseconds);
            PerfTrace.Info($"消息 {_messages.Count} 条");
        }
        finally
        {
            // 无论读成功还是失败都必须放行：否则读库一旦抛异常，
            // 等在 _historyLoadTcs 上的发送会永远卡住（连错误提示都看不到）。
            _historyLoaded = true;
            _historyLoadTcs?.TrySetResult();
        }

        using (PerfTrace.Measure("② 首屏渲染（含分组）"))
        {
            RenderHistory();
            ScrollToEnd();
        }

        // 没有更早的历史就到此结束；还有的话报告由首屏布局那次 Flush 输出
        if (_firstRenderedGroup <= 0) PerfTrace.Flush();
        RefreshContextUsage();
    }

    /// <summary>
    /// 刷新上下文用量环：占用取自最后一条带 usage 的 assistant 消息（口径见 <see cref="TryGetContextUsage"/>）。
    /// 模型未声明上下文长度或历史里没有任何 usage 时自动隐藏。
    /// </summary>
    private void RefreshContextUsage()
    {
        var model = ModelSelector.SelectedModel;
        if (model is null || !TryGetContextUsage(out var used, out var total))
        {
            UsageRing.Clear();
            return;
        }

        UsageRing.SetUsage(used, total, model.DisplayName ?? "");
    }

    /// <summary>外部触发：会话标题/时间变化后由宿主页面刷新导航列表（仅供通知）。</summary>
    public event Action? Updated;

    // ============================================================
    // 上下文压缩
    // ============================================================

    private DispatcherTimer? _compressHintTimer;

    /// <summary>
    /// 压缩上下文：让模型把「当前分界线之后」的全部内容压成一段摘要，作为一条
    /// <see cref="ContextCompression.CompressRole"/> 消息落库。此后构建请求时以它为界，
    /// 它之前的消息不再发给模型 —— 原文仍然留在库里，也仍然显示在界面上。
    /// </summary>
    /// <param name="manual">
    /// 手动触发时失败与「没什么可压」都要给出可见反馈；自动触发（发请求前的阈值检查）则一律安静，
    /// 因为用户并没有在等这个结果，弹一堆提示只会让人莫名其妙。
    /// </param>
    /// <param name="ct">
    /// 自动压缩时传当前这一轮的取消源：压缩也是模型请求，用户在压缩阶段点「停止」就该停下来。
    /// 手动压缩没有可取消的东西，传默认值。
    /// </param>
    /// <returns>是否真的压了。</returns>
    private async Task<bool> CompressContextAsync(bool manual, CancellationToken ct = default)
    {
        if (_compressing) return false;

        if (!ContextCompression.HasCompressibleContent(_messages))
        {
            if (manual) ShowCompressHint("还没有可压缩的内容", HintInfo, autoHide: true);
            return false;
        }

        var slice = ContextCompression.SliceForSummary(_messages);
        if (slice.Count == 0) return false;

        _compressing = true;
        if (_compressMenuItem is not null) _compressMenuItem.IsEnabled = false;
        ShowCompressHint("正在压缩上下文…", HintInfo, autoHide: false);

        try
        {
            // 摘要用当前会话选中的模型：不额外引入设置项，质量也是这里最好的。
            // 没有可用模型就没法压缩：手动触发要说明原因，自动触发（发请求前的那次）安静跳过。
            var client = await _gateway.ResolveClientAsync(ModelSelector.SelectedModel?.ModelId);
            if (client is null)
                return FailCompress(manual, "尚未配置可用的模型，请到「设置 → 模型服务」添加并启用服务商与模型");

            var history = new List<ChatMessage>
            {
                new() { Role = "user", Content = ContextCompression.BuildSummaryRequest(slice) },
            };
            var options = new ChatRequestOptions
            {
                Thinking = ThinkingMode.NoThink,   // 压缩是搬运信息不是解题，不需要推理
                Tools = null,                      // 更不该让它去调工具
                SupportsVision = false,
            };

            var builder = new StringBuilder();
            await client.StreamAsync(
                history,
                ContextCompression.SummarySystemPrompt,
                options,
                chunk => builder.Append(chunk),
                null,
                ct);

            var summary = builder.ToString().Trim();
            if (summary.Length == 0)
                return FailCompress(manual, "模型没有返回摘要内容");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await ChatRepository.InsertAsync(new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = _profile.State.SessionId,
                TurnId = await ChatRepository.GetNextTurnIdAsync(_profile.State.SessionId),
                SequenceNumber = await ChatRepository.GetNextSequenceAsync(_profile.State.SessionId),
                Role = ContextCompression.CompressRole,
                Content = summary,
                Metadata = ContextCompression.SerializeMetadata(),
                Status = "completed",
                ModelName = client.ModelName,
                CreatedAt = now,
                UpdatedAt = now,
            });

            await ReloadMessagesAsync();
            ShowCompressHint($"已压缩 {slice.Count} 条消息，此前的历史不再进入上下文", HintOk, autoHide: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 用户点了「停止」。这里必须放出去：调用方（SendMessageAsync）要据此放弃这一轮的发送，
            // 而不是吞掉异常后继续把消息发出去。
            CompressHint.IsVisible = false;
            throw;
        }
        catch (Exception ex)
        {
            return FailCompress(manual, ex.Message);
        }
        finally
        {
            _compressing = false;
            if (_compressMenuItem is not null) _compressMenuItem.IsEnabled = !_busy;
        }
    }

    private bool FailCompress(bool manual, string reason)
    {
        if (manual) ShowCompressHint($"压缩失败：{reason}", HintFail, autoHide: true);
        else CompressHint.IsVisible = false;

        return false;
    }

    /// <summary>
    /// 发送前按设置自动压缩：上下文占用超过阈值就先压一次。任何一步不满足就安静跳过 ——
    /// 这一步只是顺手的优化，绝不能因为它失败或超时就拖住正常发送。
    /// </summary>
    private async Task MaybeAutoCompressAsync(CancellationToken ct)
    {
        var settings = AppSettingsStore.Current;
        if (!settings.AutoCompressContext) return;
        if (!ContextCompression.HasCompressibleContent(_messages)) return;
        if (!TryGetContextUsage(out var used, out var total)) return;

        var threshold = Math.Clamp(settings.AutoCompressThresholdPercent, 1, 100);

        // 用整数乘法比百分比，避免浮点误差把「刚好等于阈值」判到另一边
        if (used * 100 < total * threshold) return;

        await CompressContextAsync(manual: false, ct);
    }

    private static readonly IBrush HintInfo = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush HintOk = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush HintFail = new SolidColorBrush(Color.Parse("#DC2626"));

    /// <summary>工具栏上的压缩提示。自动消失定时器会自己停（运行中的 DispatcherTimer 会吊住整个页面）。</summary>
    private void ShowCompressHint(string text, IBrush color, bool autoHide)
    {
        CompressHint.Text = text;
        CompressHint.Foreground = color;
        CompressHint.IsVisible = true;

        _compressHintTimer?.Stop();
        if (!autoHide) return;

        _compressHintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _compressHintTimer.Tick -= OnCompressHintTick;
        _compressHintTimer.Tick += OnCompressHintTick;
        _compressHintTimer.Start();
    }

    private void OnCompressHintTick(object? sender, EventArgs e)
    {
        _compressHintTimer?.Stop();
        CompressHint.IsVisible = false;
    }

    /// <summary>手动收起提示（如「正在加载历史」结束后立刻清掉，不必等定时器）。</summary>
    private void HideCompressHint()
    {
        _compressHintTimer?.Stop();
        CompressHint.IsVisible = false;
    }

    /// <summary>
    /// 当前上下文占用的 token 数与窗口上限。无上限（模型未声明）或历史里没有任何 usage 时返回 false。
    /// 无状态 API 的最后一次输入已包含之前所有轮的输入与输出，所以最后一个 usage 就是当前占用。
    /// </summary>
    private bool TryGetContextUsage(out long used, out long total)
    {
        used = 0;
        total = ModelSelector.SelectedModel?.ContextWindow ?? -1;
        if (total <= 0) return false;

        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var m = _messages[i];
            if (m.Role != "assistant" || string.IsNullOrWhiteSpace(m.UsageJson)) continue;
            if (TokenUsage.FromStoredJson(m.UsageJson) is not { } usage) continue;

            used = usage.TotalTokens;
            return true;
        }

        return false;
    }

    /// <summary>压缩记录在消息流里的呈现：一条分割线 + 可展开的摘要卡。</summary>
    private Control CreateCompressionDivider(ChatMessage message, int covered)
    {
        var model = string.IsNullOrWhiteSpace(message.ModelName) ? "" : $" · {message.ModelName}";

        var card = new CompressedContextCard();
        card.SetContent(
            message.Content ?? "",
            $"压缩了 {covered} 条历史消息{model} · {FormatTime(message.CreatedAt)}");
        card.DeleteRequested += () => _ = DeleteCompressionAsync(message);
        return card;
    }

    private async Task DeleteCompressionAsync(ChatMessage message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "删除这条压缩记录？\n\n" +
            "删除后，这段历史会重新以原文进入上下文，占用会变大。\n" +
            "如果后面还有更新的压缩记录，删除这一条不影响上下文内容（更新的摘要已经把它包含进去了）。",
            "删除压缩记录");
        if (!confirmed) return;

        try { await ChatRepository.SoftDeleteMessageAsync(_profile.State.SessionId, message.Id); }
        catch { /* 忽略 */ }

        await ReloadMessagesAsync();
        ShowCompressHint("已删除压缩记录，这段历史重新进入上下文", HintOk, autoHide: true);
    }

    // ---- 发送 ----

    /// <summary>
    /// 发送前解析模型客户端。返回 null 表示这一轮发不出去，<b>并且提示已经在这里给过了</b>，
    /// 调用方只要放开 _busy 并放弃即可。
    ///
    /// 解析失败（数据库异常）也必须收成 null 并给出提示：让它抛出去会让 _busy 永远停在 true，
    /// 页面卡在「正在运行」状态上，连输入框都用不了。
    /// </summary>
    private async Task<IChatClient?> ResolveSendClientAsync(ModelDetails selected)
    {
        try
        {
            var client = await _gateway.ResolveClientAsync(selected.ModelId);
            if (client is not null) return client;

            ShowCompressHint("所选模型已停用或被删除，请到「设置 → 模型服务」重新启用", HintFail, autoHide: false);
            return null;
        }
        catch (Exception ex)
        {
            ShowCompressHint($"读取模型配置失败：{ex.Message}", HintFail, autoHide: false);
            return null;
        }
    }

    public async Task SendMessageAsync(string text)
    {
        if (_busy || string.IsNullOrWhiteSpace(text)) return;

        // 历史还没读完就先等它。界面为了快速进窗只渲染了最后一批，但「读库」是另一回事：
        // 这时发送会把残缺历史交给模型，上下文静默丢一段 —— 所以宁可让用户等这一下。
        // 正常情况这里早就完成了，await 直接过；只有「刚打开窗口就抢着发」才会真的等。
        if (!_historyLoaded && _historyLoadTcs is { } loadTcs)
        {
            ShowCompressHint("正在加载历史记录，完成后自动发送…", HintInfo, autoHide: false);
            await loadTcs.Task;
            HideCompressHint();
        }

        // 没有可用模型就挡在这里，输入内容原样保留 —— 用户去配好模型回来还能接着发。
        // 这一步必须在清空输入框之前：失败一次就要人重打一遍，那是很糟的体验。
        var selected = ModelSelector.SelectedModel;
        if (selected is null)
        {
            ShowCompressHint("尚未配置模型：请到「设置 → 模型服务」添加并启用服务商与模型", HintFail, autoHide: false);
            return;
        }

        // 先占住 _busy：下面要 await 一次数据库，不占住的话连点两下会同时发出去两条。
        // 失败路径上必须自己放开，否则页面会永远卡在「正在运行」。
        _busy = true;
        var client = await ResolveSendClientAsync(selected);
        if (client is null)
        {
            _busy = false;   // 提示已经由 ResolveSendClientAsync 给过
            return;
        }

        SetRunningVisual(true);
        InputBox.Text = "";

        var isFirstTurn = _messages.Count == 0;

        // 标题还是占位说明此前没定过名：按设置补一个（配了命名模型时先留占位，等首轮回复再生成）
        if (ConversationTitle.IsPlaceholder(_profile.State.Title))
            _profile.State.Title = ConversationTitle.InitialTitle(text);
        _profile.State.Model = selected.ModelName;
        _profile.State.Thinking = (int)ModelSelector.ThinkingMode;
        _profile.State.EnabledToolIds = ToolSelector.SelectedToolIds.ToList();
        _profile.State.LastMessageAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await PersistSessionAsync();

        var system = _profile.BuildSystemPrompt(_profile.State.Model);

        var vision = SupportsVision(selected);

        // MCP 是惰性连接的：发消息前确保会话启用的服务都连上并发现了工具。
        // 连不上就跳过（内部已记日志），不打断这次对话。
        await McpConnectionManager.Instance.EnsureToolsAsync(_profile.State.McpServers, CancellationToken.None);

        var options = new ChatRequestOptions
        {
            Thinking = ModelSelector.ThinkingMode,
            Tools = ResolveActiveTools(),
            SupportsVision = vision,
        };
        // 工具执行与请求构造共用同一个开关：不支持视觉时 view_image 直接报错，
        // 历史里遗留的图片 tool 结果也降级成文本，避免整轮请求被 API 拒掉
        _toolExecutor.Context.SupportsVision = vision;

        // 每次发送都现读设置：改完「最大工具循环次数」不用重开会话就生效
        _gateway.MaxToolRounds = AppDefaults.ResolveMaxToolRounds();

        // 取消源提前到压缩之前建：压缩本身也是一次模型请求，期间点「停止」得能停下来。
        // 放在后面建的话，压缩阶段那颗按钮是「停止」却没有任何东西可取消。
        _sendCts = new CancellationTokenSource();
        ChatTurnResult? turn = null;
        try
        {
            // 上下文超阈值时先自动压缩一次（受设置控制，失败或不满足条件都安静跳过）
            await MaybeAutoCompressAsync(_sendCts.Token);

            turn = await _gateway.ChatAsync(
                _profile.State.SessionId,
                system,
                // 有压缩记录时只发「最后一条压缩记录 + 它之后的消息」，更早的历史被摘要在语义上替代
                ContextCompression.BuildWireHistory(_messages),
                ExpandAttachments(text),   // 发给模型的是真实路径，不是 @图片1
                _pending.Count > 0 ? _pending.Select(d => d.Info).ToList() : null,
                options,
                client,
                _toolExecutor,
                PersistMessage,
                BeginAssistantRound,
                _sendCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户点了「停止」：内容已由网关按 interrupted 落库，这里只做收尾
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
        }

        // 附件已经随这一轮发出去了
        _pending.Clear();
        RefreshAttachmentBar();

        if (_activeTurnId is not null && _activeTurnRoot is not null && _activeTurnHost is not null)
        {
            // 回复结束：聚合卡翻为用时统计并折叠；操作栏（含原文模式按钮）追加到蓝框外
            FinishActivityCard();

            var turnAssistants = _messages
                .Where(m => m.Role == "assistant" && m.TurnId == _activeTurnId)
                .ToList();
            _activeTurnRoot.Children.Add(CreateAiReplyOps(_activeTurnId, turnAssistants, _activeTurnHost));
            ScrollToEnd();
        }

        RefreshContextUsage();
        _busy = false;
        SetRunningVisual(false);
        Updated?.Invoke();

        // 自动话题命名只跑首轮一次：首轮之后 _messages 非空，isFirstTurn 恒为假。
        if (isFirstTurn) _ = ApplyAutoTitleAsync(turn, text);
    }

    /// <summary>
    /// 首轮回复结束后给会话定标题。三种情形：开关关闭 → 保持占位等用户手动改名；
    /// 未配置命名模型 → 直接截断首条消息；配置了模型 → 让 AI 总结一个短名。
    /// 任何失败都静默回退到截断，不打扰已经完成的对话。
    /// </summary>
    private async Task ApplyAutoTitleAsync(ChatTurnResult? turn, string firstText)
    {
        try
        {
            var settings = AppSettingsStore.Current;

            string title;
            if (!settings.AutoTopicNaming)
                title = ConversationTitle.Placeholder;
            else if (string.IsNullOrWhiteSpace(settings.TopicNamingModelId))
                title = ConversationTitle.Truncate(firstText);
            else
                title = ConversationTitle.Truncate(await GenerateTitleAsync(turn) ?? firstText);

            if (string.IsNullOrEmpty(title)) return;

            _profile.State.Title = title;
            await PersistSessionAsync();
            Updated?.Invoke();
        }
        catch
        {
            // 定名失败不该影响对话本身
        }
    }

    /// <summary>让「话题命名模型」给这一轮起个短名。任何异常都返回 null，由调用方回退。</summary>
    private async Task<string?> GenerateTitleAsync(ChatTurnResult? turn)
    {
        var modelId = AppSettingsStore.Current.TopicNamingModelId;
        if (string.IsNullOrWhiteSpace(modelId) || turn is null) return null;

        var details = await _gateway.ResolveModelAsync(modelId);
        if (details is null) return null;

        var client = _gateway.BuildClient(details);

        const string system =
            "你是会话标题生成器。根据用户的第一条消息和助手的回复，给出一个简短的会话标题。" +
            "要求：不超过 12 个字，只输出标题本身，不要引号、标点、序号或任何解释。";

        var body = $"用户：{turn.User.Content}\n助手：{ConversationTitle.Truncate(turn.FinalAssistant?.Content, 500)}";
        var history = new List<ChatMessage> { new() { Role = "user", Content = body } };

        var options = new ChatRequestOptions
        {
            Thinking = ThinkingMode.NoThink,
            Tools = null,
            SupportsVision = false,
        };

        var builder = new StringBuilder();
        await client.StreamAsync(history, system, options, chunk => builder.Append(chunk), null, CancellationToken.None);

        return builder.ToString().Trim().Trim('"', '\'', '“', '”', '。', '，', '、', '\r', '\n', ' ');
    }

    /// <summary>
    /// 运行态视觉：发送按钮在「发送（蓝底箭头）」与「停止（红底圆角方块）」之间切换，
    /// 并显示请求等待环。按钮始终保持可点 —— 运行中点它就是停止。
    /// </summary>
    private void SetRunningVisual(bool running)
    {
        SendGlyph.IsVisible = !running;
        StopGlyph.IsVisible = running;
        var brush = new SolidColorBrush(Color.Parse(running ? "#DC2626" : "#4A90D9"));
        SendBtn.Background = brush;
        SendBtn.BorderBrush = brush;
        LoadingRing.IsVisible = running;

        // 生成中不许再压缩：压缩要在「这一轮发出去之前」定下上下文，中途插一条只会让本轮上下文
        // 前后不一致（请求已经带着旧历史发出去了，压缩记录却已经落库）。
        if (_compressMenuItem is not null) _compressMenuItem.IsEnabled = !running;
    }

    /// <summary>首个正文 / 思考分片到达：请求已进入输出阶段，收起等待指示。</summary>
    private void MarkFirstOutput() => LoadingRing.IsVisible = false;

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
    {
        // 运行中这颗按钮是「停止」
        if (_busy)
        {
            _sendCts?.Cancel();
            return;
        }
        _ = SendFromInputAsync();
    }

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

    // ---- 历史渲染 ----

    private void RenderHistory()
    {
        MessagesPanel.Children.Clear();

        _turnGroups = new List<(string? TurnId, List<ChatMessage> Msgs)>();
        _compressionCovered.Clear();
        using (PerfTrace.Measure("   分组 / 工具索引"))
        {
            BuildToolCallIndex();
            GroupMessages();
        }

        // 长会话首屏只渲染最后一批：历史再多也只付出这一批的建控件开销，
        // 用户能看到最新内容、能马上开始输入，更早的轮次等滚到顶部再加载。
        _historyBatchTurns = AppDefaults.ResolveHistoryBatchTurns();
        _firstRenderedGroup = Math.Max(0, _turnGroups.Count - _historyBatchTurns);
        PerfTrace.Info($"分组 {_turnGroups.Count} 轮，首屏渲染 {_turnGroups.Count - _firstRenderedGroup} 轮");

        using (PerfTrace.Measure("   首屏建控件"))
            AppendGroups(_firstRenderedGroup, _turnGroups.Count);

        _activeTurnId = null;
        _activeTurnRoot = null;
        _activeTurnHost = null;
        _activeTurnCard = null;

        // 首屏这批渲染完，ScrollToEnd 还没落到布局上；先关掉「滚到顶部」的自动补齐，
        // 等布局帧跑完（此时 Offset 才真正到底部）再开放，否则会把历史一次全补回来。
        _suppressEarlierLoad = true;
        var firstScreenShownTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(
            () =>
            {
                // 到这里说明首屏那一批的布局已经跑完 —— 这段就是「首屏布局耗时」
                PerfTrace.Note("③ 首屏布局（插入→布局完成）",
                    System.Diagnostics.Stopwatch.GetElapsedTime(firstScreenShownTicks).TotalMilliseconds);

                _suppressEarlierLoad = false;

                // 报告到此为止：这里就是「打开会话窗口」的完整成本。
                // 之后再往上滚加载的批次会各自出一份小报告。
                PerfTrace.Flush();
            },
            DispatcherPriority.Background);
    }

    /// <summary>把消息按「轮」分组，并统计每条压缩记录覆盖了多少条消息。</summary>
    private void GroupMessages()
    {
        // 压缩记录要单独摘出来渲染成一条分割线。不能走下面的分组：role 是 agent，
        // 既不是 user 也不是 assistant/tool，会被整个漏掉（上下文里有它、界面上却没有）。
        // 覆盖条数在这里顺手统计：从上一个压缩记录（不含）数到这一条。
        var lastCompression = -1;

        for (var i = 0; i < _messages.Count; i++)
        {
            var m = _messages[i];

            if (ContextCompression.IsCompression(m))
            {
                _compressionCovered[m.Id] = lastCompression < 0 ? i : i - lastCompression;
                lastCompression = i;
                _turnGroups.Add((m.Id, new List<ChatMessage> { m }));
                continue;
            }

            if (m.Role == "user")
            {
                _turnGroups.Add((null, new List<ChatMessage> { m }));
            }
            else if (m.Role is "assistant" or "tool")
            {
                var key = m.TurnId;
                var last = _turnGroups.Count > 0 ? _turnGroups[^1] : default;
                if (last.TurnId is not null && last.TurnId == key)
                    last.Msgs.Add(m);
                else
                    _turnGroups.Add((key, new List<ChatMessage> { m }));
            }
        }
    }

    /// <summary>渲染 <c>[from, to)</c> 区间的分组并追加到消息面板末尾。</summary>
    private void AppendGroups(int from, int to)
    {
        for (var i = from; i < to && i < _turnGroups.Count; i++)
            MessagesPanel.Children.Add(BuildGroupControl(_turnGroups[i]));
    }

    /// <summary>
    /// 滚到接近顶部时把更早的一批补到列表最前面，每批 <see cref="_historyBatchTurns"/> 轮，
    /// 直到最早的一轮也已渲染。
    ///
    /// <b>刻意不做后台预渲染。</b>曾经试过「进窗后趁空闲把更早的轮次一批批补上」，
    /// 实测反而更糟：长会话下它要在 UI 线程上分好几批干活（构造 + 每批一次全量重新布局），
    /// 而这正是用户切标签页时抢不到 UI 线程的原因。既然渲染已经不便宜，
    /// 就让「谁滚谁加载」——用户明确要看才付这个成本，不看不付。
    /// </summary>
    private void LoadEarlierHistory()
    {
        if (_loadingEarlier || _restoringScroll) return;
        if (_firstRenderedGroup <= 0) return;

        PerfTrace.Begin($"会话 {_profile.State.SessionId} — 向上加载一批");
        PerfTrace.Info($"此前已渲染 {_turnGroups.Count - _firstRenderedGroup} 轮");

        using (PerfTrace.Measure("① 向上加载：构造本批"))
            RenderEarlierBatch();
    }

    /// <summary>
    /// 把更早的一批（<see cref="_historyBatchTurns"/> 轮）补到列表最前面，并补偿滚动位置。
    ///
    /// 补偿是必须的：插入点在视口上方时内容整体下移，而 Offset 不变，
    /// 视口就会滑向更早的内容（表现为「越来越往上跑、回不到底部」）。
    /// 用户本来就在底部时，补偿后正好仍等于新的底部。
    /// </summary>
    private void RenderEarlierBatch()
    {
        if (_firstRenderedGroup <= 0) return;

        _loadingEarlier = true;
        try
        {
            var from = Math.Max(0, _firstRenderedGroup - _historyBatchTurns);

            // 先把这一批构造出来（构造期间不碰面板，避免多次布局）
            var batch = new List<Control>();
            for (var i = from; i < _firstRenderedGroup; i++)
                batch.Add(BuildGroupControl(_turnGroups[i]));

            // 记下插入前的滚动状态：控件插在最前面会把已有内容整体下推，
            // 不补偿的话视口会「跳」——用户正在看的那条消息会瞬移出屏幕。
            var oldExtent = MessagesScroll.Extent.Height;
            var oldOffset = MessagesScroll.Offset.Y;
            var wasAtBottom = IsAtBottom();

            for (var i = 0; i < batch.Count; i++)
                MessagesPanel.Children.Insert(i, batch[i]);

            _firstRenderedGroup = from;
            _restoringScroll = true;

            // 等这一帧布局跑完，Extent 才反映插入后的高度，这时才能算补偿量
            var insertedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // 到这里布局已经跑完 —— 记下这段（构造之外的真正开销）
                    PerfTrace.Note("② 向上加载：布局",
                        System.Diagnostics.Stopwatch.GetElapsedTime(insertedTicks).TotalMilliseconds);

                    if (wasAtBottom)
                    {
                        // 本来贴底就直接滚到底：用高度差做加法会有累积误差，
                        // 几十批下来会累积成肉眼可见的偏移。
                        MessagesScroll.ScrollToEnd();
                        return;
                    }

                    var delta = MessagesScroll.Extent.Height - oldExtent;
                    if (delta > 0)
                        MessagesScroll.Offset = new Vector(MessagesScroll.Offset.X, oldOffset + delta);
                }
                finally
                {
                    _restoringScroll = false;
                    PerfTrace.Flush();
                }
            }, DispatcherPriority.Loaded);
        }
        finally
        {
            _loadingEarlier = false;
        }
    }

    /// <summary>是否已滚到接近顶部（该补下一批了）。</summary>
    private bool IsNearTop()
    {
        // 按视口高度的一屏多一点判定：滚轮滑得快时也不容易「冲过」触发点
        var threshold = Math.Max(200, MessagesScroll.Viewport.Height * 1.2);
        return MessagesScroll.Offset.Y <= threshold;
    }

    /// <summary>是否已贴到底部（留一点容差，浮点与滚动动画都可能差几个像素）。</summary>
    private bool IsAtBottom()
    {
        var max = MessagesScroll.ScrollBarMaximum.Y;
        return max - MessagesScroll.Offset.Y <= 2;
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 首屏还没定位到底部，此时的 Offset=0 不代表用户在看顶部
        if (_suppressEarlierLoad) return;
        // 只在还有更早历史时才关心滚动位置
        if (_firstRenderedGroup <= 0) return;
        if (IsNearTop()) LoadEarlierHistory();
    }

    /// <summary>把一个分组渲染成控件。分组只有两种形态：压缩分割线、或一个完整的轮次块。</summary>
    private Control BuildGroupControl((string? TurnId, List<ChatMessage> Msgs) group)
    {
        var (turnId, msgs) = group;

        if (msgs.Count == 1 && ContextCompression.IsCompression(msgs[0]))
            return CreateCompressionDivider(msgs[0], _compressionCovered.GetValueOrDefault(msgs[0].Id));

        return BuildTurnBlock(turnId, msgs);
    }

    /// <summary>
    /// 渲染一个轮次块并<b>返回</b>控件（不直接挂到面板上）。
    /// 之所以要先构造后挂载：向上补齐历史时需要把控件插到列表最前面，
    /// 直接往面板追加的老写法做不到这件事。
    /// </summary>
    private Control BuildTurnBlock(string? turnId, List<ChatMessage> msgs)
    {
        var first = msgs[0];
        if (first.Role == "user")
            return CreateUserMessageBlock(first);

        var (displayName, providerName) = ModelInfo(first.ModelName ?? "");
        var rootPanel = new StackPanel
        {
            Margin = new Thickness(20, 2, 20, 2),
            Spacing = 10,
        };
        rootPanel.Children.Add(CreateModelHeader(displayName, providerName, first.CreatedAt));

        // 内容区（思考/工具聚合卡 + 各正文块）统一放进蓝框宿主，供原文模式切换
        var host = new TurnSelectionHost { BuildFullRawText = () => BuildFullRawText(turnId) };
        rootPanel.Children.Add(host.Border);

        TurnActivityCard? card = null;
        var toolCount = 0;
        var thinkingCount = 0;
        foreach (var m in msgs)
        {
            if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                {
                    using (PerfTrace.Measure("     思考卡 ThinkingCard"))
                    {
                        card ??= AttachActivityCard(host, expand: false);
                        var thinkCard = new ThinkingCard();
                        thinkCard.SetText(m.Thinking);
                        thinkCard.Show();
                        card.Steps.Children.Add(thinkCard.Root);
                    }
                    thinkingCount++;
                }
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    // 正文一律留在聚合卡外，按顺序排列
                    using (PerfTrace.Measure("     正文块 TurnBodyView"))
                    {
                        var body = new TurnBodyView();
                        body.AppendText(m.Content);
                        body.Complete();
                        host.AddBody(body);
                    }
                }
            }
            else if (m.Role == "tool")
            {
                using (PerfTrace.Measure("     工具卡 ToolCallCard"))
                {
                    card ??= AttachActivityCard(host, expand: false);
                    var toolCard = new ToolCallCard();
                    toolCard.SetData(m.ToolName ?? "工具", FindToolCallInput(m.ToolCallId), m.Content ?? "");
                    card.Steps.Children.Add(toolCard.Root);
                }
                toolCount++;
            }
        }

        // 历史记录：聚合卡显示推算出的用时并保持折叠
        card?.SetFinished(toolCount, thinkingCount, TurnDuration(msgs));

        if (turnId is not null)
            rootPanel.Children.Add(CreateAiReplyOps(turnId, msgs.Where(m => m.Role == "assistant").ToList(), host));

        return rootPanel;
    }

    /// <summary>
    /// 创建并挂上一轮的空聚合卡（首次出现思考或工具调用时调用）。
    /// 实时轮出生即展开（<paramref name="expand"/> 默认 true），历史轮保持折叠。
    /// </summary>
    private static TurnActivityCard AttachActivityCard(TurnSelectionHost host, bool expand = true)
    {
        var card = new TurnActivityCard();
        card.SetProcessing(0, 0);
        if (expand) card.Expand();
        host.SetCard(card);
        return card;
    }

    /// <summary>流式过程中刷新聚合卡头部（计数随思考/工具调用递增）。</summary>
    private void RefreshActivityHeader()
        => _activeTurnCard?.SetProcessing(_turnToolCount, _turnThinkingCount);

    /// <summary>回复结束：聚合卡翻为用时统计并折叠。</summary>
    private void FinishActivityCard()
    {
        _activeTurnCard?.SetFinished(_turnToolCount, _turnThinkingCount, TurnElapsed());
        _activeTurnCard?.Collapse();
        _turnStartedAt = 0;
    }

    /// <summary>
    /// 本轮用时。优先用网关落库的值 —— 它从请求真正发出时开始算，
    /// 与重开会话后读到的完全一致；拿不到才退回视图自己的计时。
    /// </summary>
    private TimeSpan? TurnElapsed()
    {
        var stored = _activeTurnId is null
            ? 0
            : _messages.Where(m => m.Role == "assistant" && m.TurnId == _activeTurnId)
                       .Max(m => m.DurationMs ?? 0);
        return stored > 0 ? TimeSpan.FromMilliseconds(stored) : ElapsedSinceTurnStart();
    }

    /// <summary>本轮自请求开始至今的耗时；没有开始时刻（历史轮、异常路径）时返回 null。</summary>
    private TimeSpan? ElapsedSinceTurnStart()
        => _turnStartedAt <= 0
            ? null
            : TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _turnStartedAt);

    /// <summary>
    /// 历史轮的用时。优先取落库的 duration_ms；老数据没有这一列，退回时间戳推算
    /// （首条创建 → 末条最后更新）—— 单轮消息的这两个值相等，推不出来，那种情况返回 null。
    /// </summary>
    private static TimeSpan? TurnDuration(List<ChatMessage> msgs)
    {
        var stored = msgs.Count == 0 ? 0 : msgs.Max(m => m.DurationMs ?? 0);
        if (stored > 0) return TimeSpan.FromMilliseconds(stored);

        if (msgs.Count == 0) return null;
        var start = msgs.Min(m => m.CreatedAt);
        var end = msgs.Max(m => Math.Max(m.UpdatedAt, m.CreatedAt));
        return start > 0 && end > start ? TimeSpan.FromMilliseconds(end - start) : null;
    }

    /// <summary>
    /// 拼装一轮回复的「全部原文」：按消息顺序输出思考 / 工具调用 / 正文的未渲染原文。
    /// </summary>
    private string BuildFullRawText(string? turnId)
    {
        var sb = new StringBuilder();
        foreach (var m in _messages)
        {
            if (m.TurnId != turnId) continue;

            if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Thinking))
                    sb.AppendLine("[思考] " + m.Thinking.TrimEnd()).AppendLine();
                if (!string.IsNullOrWhiteSpace(m.Content))
                    sb.AppendLine("[正文] " + m.Content.TrimEnd()).AppendLine();
            }
            else if (m.Role == "tool")
            {
                sb.AppendLine($"[工具] {m.ToolName ?? "工具"}");
                sb.AppendLine("输入: " + FindToolCallInput(m.ToolCallId));
                sb.AppendLine("输出: " + (string.IsNullOrWhiteSpace(m.Content) ? "（无输出）" : m.Content.TrimEnd()));
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>实时：工具结果以可折叠工具卡追加到当前 turn 根（无根时兜底追加到消息面板）。</summary>
    private void AddToolCallCard(ChatMessage toolMsg)
    {
        var card = new ToolCallCard();
        card.SetData(toolMsg.ToolName ?? "工具", FindToolCallInput(toolMsg.ToolCallId), toolMsg.Content ?? "");

        _turnToolCount++;
        if (_activeTurnHost is null)
        {
            // 兜底：无活动 turn 时直接挂到消息面板
            MessagesPanel.Children.Add(card.Root);
            return;
        }

        _activeTurnCard ??= AttachActivityCard(_activeTurnHost);
        _activeTurnCard.Steps.Children.Add(card.Root);
        RefreshActivityHeader();
        _activeTurnCard.ScrollContentToEnd();
        ScrollToEnd();
    }

    /// <summary>按 tool_call_id 反查输入参数（来自对应 assistant 消息的 metadata 里的 tool_calls）。</summary>
    private string FindToolCallInput(string? toolCallId)
        => string.IsNullOrWhiteSpace(toolCallId)
            ? "{}"
            : _toolCallInputs.GetValueOrDefault(toolCallId, "{}");

    /// <summary>
    /// 一次性建好 tool_call_id → arguments 的索引。
    /// 原先 FindToolCallInput 每渲染一个工具卡就遍历全部消息并逐个反序列化 metadata，
    /// 整体是 O(消息数 × 工具卡数)；工具调用密集的长会话里，这一项比读库还慢。
    /// </summary>
    private void BuildToolCallIndex()
    {
        _toolCallInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in _messages)
        {
            if (m.Role != "assistant") continue;
            foreach (var call in ToolCallJson.Deserialize(m.Metadata))
            {
                if (!string.IsNullOrEmpty(call.Id))
                    _toolCallInputs[call.Id] = call.Arguments ?? "{}";
            }
        }
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

    /// <summary>
    /// AI 回复操作栏（位于内容蓝框之外）：左侧复制/编辑/删除 + 原文模式切换，右侧 Tokens 汇总（同 turn 累加）。
    /// </summary>
    private Control CreateAiReplyOps(string turnId, IReadOnlyList<ChatMessage> assistantMsgs, TurnSelectionHost host)
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
            Children = { copyBtn, editBtn, delBtn, host.CreateModeButtons() },
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
        SetUserBubbleContent(block, msg);
        stack.Children.Add(bubble);
        stack.Children.Add(CreateUserOps(msg.Id, msg.Content ?? ""));
        return stack;
    }

    /// <summary>
    /// 用户气泡正文。消息里记了附件时，把正文中的绝对路径换成内联文件卡片（放在文本流里，
    /// 所以「图片在句子的哪个位置」这个信息是可见的）；没有附件就走原来的纯文本渲染，不做任何改变。
    ///
    /// 注意：落库的正文里是真实路径（模型要读它），卡片只是渲染层的替换。
    /// </summary>
    private static void SetUserBubbleContent(CustomSelectableTextBlock block, ChatMessage msg)
    {
        var text = msg.Content ?? "";
        var attachments = AttachmentJson.Deserialize(msg.Metadata);
        if (attachments.Count == 0)
        {
            block.Text = text;
            return;
        }

        var inlines = new InlineCollection();
        var rest = text;
        while (rest.Length > 0)
        {
            // 每轮取「最靠前的那个附件路径」，把它之前的文字原样输出，再插一张卡片
            var hit = attachments
                .Select(a => (Attachment: a, Index: rest.IndexOf(a.Path, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.Index >= 0)
                .OrderBy(x => x.Index)
                .FirstOrDefault();

            if (hit.Attachment is null)
            {
                inlines.Add(new Run(rest));
                break;
            }

            if (hit.Index > 0)
                inlines.Add(new Run(rest[..hit.Index]));
            inlines.Add(new InlineUIContainer(new AttachmentChip(hit.Attachment, null, compact: true)));
            rest = rest[(hit.Index + hit.Attachment.Path.Length)..];
        }

        block.Inlines = inlines;
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
        try { await ChatRepository.SoftDeleteTurnAsync(_profile.State.SessionId, turnId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    private async Task DeleteMessageAsync(string messageId)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, "确定删除这条消息吗？")) return;
        try { await ChatRepository.SoftDeleteMessageAsync(_profile.State.SessionId, messageId); } catch { /* 忽略 */ }
        await ReloadMessagesAsync();
    }

    private async Task ReloadMessagesAsync()
    {
        _messages.Clear();
        try
        {
            _messages.AddRange(await ChatRepository.GetBySessionAsync(_profile.State.SessionId));
        }
        finally
        {
            // 同 LoadMessagesAsync：失败也要放行，不能让等待中的发送永远悬着
            _historyLoaded = true;
            _historyLoadTcs?.TrySetResult();
        }
        _activeTurnId = null;
        _activeTurnRoot = null;
        _activeTurnHost = null;
        _activeTurnCard = null;
        RenderHistory();
        ScrollToEnd();
    }

    /// <summary>实时：新一轮 assistant 开始流式时创建 UI 元素并返回流式回调。</summary>
    private ChatRoundSink BeginAssistantRound(ChatMessage assistantMsg)
    {
        var (displayName, providerName) = ModelInfo(assistantMsg.ModelName ?? "");

        // 同一 turn（含多轮工具调用）复用根容器；新 turn 时重建
        if (assistantMsg.TurnId is null || assistantMsg.TurnId != _activeTurnId || _activeTurnRoot is null)
        {
            var root = new StackPanel
            {
                Margin = new Thickness(20, 2, 20, 2),
                Spacing = 10,
            };
            root.Children.Add(CreateModelHeader(displayName, providerName, assistantMsg.CreatedAt));

            var turnId = assistantMsg.TurnId;
            var host = new TurnSelectionHost { BuildFullRawText = () => BuildFullRawText(turnId) };
            root.Children.Add(host.Border);

            MessagesPanel.Children.Add(root);
            ScrollToEnd();
            _activeTurnId = assistantMsg.TurnId;
            _activeTurnRoot = root;
            _activeTurnHost = host;
            _activeTurnCard = null;
            _turnToolCount = 0;
            _turnThinkingCount = 0;
            // 计时从请求发出算起（此刻紧接着就是本轮第一次 StreamAsync）
            _turnStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // 每条 assistant 消息一个正文块；正文一律排在聚合卡外。聚合卡直到首个思考/工具调用才创建
        var body = new TurnBodyView();
        _activeTurnHost!.AddBody(body);

        ThinkingCard? thinkCard = null;
        var turnHost = _activeTurnHost!;

        return new ChatRoundSink
        {
            OnContent = delta =>
            {
                MarkFirstOutput();
                body.AppendText(delta);
                ScrollToEnd();
            },
            OnThinking = delta =>
            {
                MarkFirstOutput();
                // 首个思考分片到达时才建卡（此前不显示任何卡片），之后持续累积文本
                if (thinkCard is null)
                {
                    _activeTurnCard ??= AttachActivityCard(turnHost);
                    thinkCard = new ThinkingCard();
                    thinkCard.Show();
                    _activeTurnCard.Steps.Children.Add(thinkCard.Root);
                    _turnThinkingCount++;
                    RefreshActivityHeader();
                }
                thinkCard.Append(delta);
                _activeTurnCard?.ScrollContentToEnd();
                ScrollToEnd();
            },
            OnFailed = message => body.AppendText("请求失败：" + message),
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
    private (Control Root, CustomSelectableTextBlock Block) CreateUserElement()
    {
        // 单块文本，直接开原生选择：拖选/双击选词/Ctrl+C 均可，无跨块问题
        var block = new CustomSelectableTextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
            NativeSelectionEnabled = true,
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

    /// <summary>
    /// 根据模型调用 id 解析展示信息（显示名 + 服务商名）。早于模型配置体系的历史消息
    /// 可能压根没记模型名，或者记的那个模型后来被删了 —— 两种情况都原样显示能拿到的信息，
    /// 不猜、也不回退到任何写死的模型上。
    /// </summary>
    private (string DisplayName, string ProviderName) ModelInfo(string callId)
    {
        if (string.IsNullOrEmpty(callId)) return ("未知模型", "");
        var m = ModelSelector.Models.FirstOrDefault(x => x.ModelName == callId);
        return m is not null
            ? (m.DisplayName, m.ProviderName)
            : (callId, "未知服务商");
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
            NativeSelectionEnabled = true,
        };
        return (block, block);
    }

    private void ScrollToEnd()
    {
        MessagesScroll.ScrollToEnd();

        // 刚建出来的控件还没测量，此时 Extent 是旧的，ScrollToEnd 只能落在「当时以为的底部」。
        // 等这一帧布局完成后 Extent 才是真值，所以再补一次 —— 否则首屏会停在半空，
        // 随后又因更早的历史不断插入而越飘越高。
        // 用标志合并：跑一次就够，不必每次流式都排队。
        if (_scrollToEndPending) return;
        _scrollToEndPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollToEndPending = false;
                MessagesScroll.ScrollToEnd();
            },
            DispatcherPriority.Loaded);
    }

    // ---- 附件（粘贴 / 上传） ----

    /// <summary>输入框里的一条待发送附件：占位符 token ↔ 真实文件。</summary>
    private sealed record DraftAttachment(string Token, ChatAttachment Info);

    /// <summary>待发送附件。输入框里放的是 <see cref="DraftAttachment.Token"/>，发出去时才展开成真实路径。</summary>
    private readonly List<DraftAttachment> _pending = new();

    /// <summary>token 序号，只增不减，保证同一份草稿里不会出现两个同名 token。</summary>
    private int _tokenSeq;

    private static bool SupportsVision(ModelDetails? model)
        => model?.Capabilities.Contains("vision") == true;

    /// <summary>生成 <c>@图片1</c> 这样的占位符，重名时往后顺延。</summary>
    private string NextToken(string label)
    {
        var text = InputBox.Text ?? "";
        while (true)
        {
            _tokenSeq++;
            var token = $"@{label}{_tokenSeq}";
            if (!text.Contains(token, StringComparison.Ordinal) &&
                _pending.All(d => d.Token != token))
                return token;
        }
    }

    /// <summary>把 token 插到光标处。输入框里存的是它，不是那串绝对路径。</summary>
    private void InsertToken(string token)
    {
        var text = InputBox.Text ?? "";
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        var lead = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        var insertion = lead + token + " ";
        InputBox.Text = text.Insert(caret, insertion);
        InputBox.CaretIndex = caret + insertion.Length;
    }

    private void AddAttachments(IEnumerable<ChatAttachment?> items)
    {
        var added = false;
        foreach (var item in items)
        {
            if (item is null) continue;
            if (_pending.Any(d => string.Equals(d.Info.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 先落文本再登记：Text = ... 会同步触发 TextChanged 里的孤儿清理，
            // 顺序反过来的话这条刚登记的会被自己清掉
            var token = NextToken(item.Label);
            InsertToken(token);
            _pending.Add(new DraftAttachment(token, item));
            added = true;
        }
        if (added) RefreshAttachmentBar();
    }

    private void RefreshAttachmentBar()
    {
        AttachmentBar.Children.Clear();
        foreach (var item in _pending)
        {
            var captured = item;
            AttachmentBar.Children.Add(new AttachmentChip(captured.Info, () => RemoveAttachment(captured)));
        }
        AttachmentBar.IsVisible = _pending.Count > 0;
    }

    /// <summary>移除附件：正文里的 token 与 chip 一起消失。</summary>
    private void RemoveAttachment(DraftAttachment item)
    {
        _pending.Remove(item);
        RemoveTokenFromInput(item.Token);
        RefreshAttachmentBar();
    }

    private void RemoveTokenFromInput(string token)
    {
        var text = InputBox.Text ?? "";
        var idx = text.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return;

        var len = token.Length;
        if (idx + len < text.Length && text[idx + len] == ' ') len++;
        InputBox.Text = text.Remove(idx, len);
        InputBox.CaretIndex = Math.Min(idx, InputBox.Text?.Length ?? 0);
    }

    /// <summary>
    /// 用户在输入框里手工改动了文本（选中一段删掉、Ctrl+Z 等）后，
    /// 把正文里已经不存在的 token 从待发送列表里清掉，保证 chip 栏与正文一致。
    /// </summary>
    private void PruneAttachments()
    {
        var text = InputBox.Text ?? "";
        if (_pending.RemoveAll(d => !text.Contains(d.Token, StringComparison.Ordinal)) > 0)
            RefreshAttachmentBar();
    }

    /// <summary>把正文里的 token 全部换成真实路径 —— 这一步之后才是发给模型的内容。</summary>
    private string ExpandAttachments(string text)
    {
        foreach (var d in _pending)
            text = text.Replace(d.Token, d.Info.Path, StringComparison.Ordinal);
        return text;
    }

    /// <summary>光标前紧邻一个 token 时整体删掉它（而不是删掉一个字符）。</summary>
    private bool TryDeleteTokenBeforeCaret()
    {
        if (InputBox.SelectionStart != InputBox.SelectionEnd) return false;   // 有选区时交给默认删除

        var text = InputBox.Text ?? "";
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (caret <= 0) return false;

        foreach (var d in _pending)
        {
            var idx = text.LastIndexOf(d.Token, caret - 1, StringComparison.Ordinal);
            if (idx < 0 || idx + d.Token.Length != caret) continue;

            _pending.Remove(d);
            InputBox.Text = text.Remove(idx, d.Token.Length);
            InputBox.CaretIndex = idx;
            RefreshAttachmentBar();
            return true;
        }
        return false;
    }

    /// <summary>光标后紧邻一个 token 时整体删掉它。</summary>
    private bool TryDeleteTokenAfterCaret()
    {
        if (InputBox.SelectionStart != InputBox.SelectionEnd) return false;

        var text = InputBox.Text ?? "";
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (caret >= text.Length) return false;

        foreach (var d in _pending)
        {
            if (text.IndexOf(d.Token, caret, StringComparison.Ordinal) != caret) continue;

            var len = d.Token.Length;
            if (caret + len < text.Length && text[caret + len] == ' ') len++;
            _pending.Remove(d);
            InputBox.Text = text.Remove(caret, len);
            InputBox.CaretIndex = caret;
            RefreshAttachmentBar();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 粘贴：剪贴板里是文件（含截图工具直接放进去的位图）就转成附件，
    /// 否则交回 TextBox 走它自己的文本粘贴。
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            InputBox.Paste();
            return;
        }

        try
        {
            // 资源管理器里复制的文件：本来就有路径，直接用
            var files = (await clipboard.TryGetFilesAsync())?.ToList();
            if (files is { Count: > 0 })
            {
                AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
                return;
            }

            // 截图工具直接塞进剪贴板的位图：没有路径，落成缓存文件
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using var buffer = new MemoryStream();
                bitmap.Save(buffer);   // Avalonia 默认存 PNG
                var attachment = await AttachmentStore.SaveClipboardImageAsync(buffer.ToArray());
                if (attachment is not null)
                {
                    AddAttachments(new[] { attachment });
                    return;
                }
            }
        }
        catch
        {
            // 剪贴板读取失败就退回普通文本粘贴
        }

        InputBox.Paste();
    }

    /// <summary>
    /// 输入框的隧道阶段按键：Ctrl+V 要在 TextBox 自己粘贴之前截下来，
    /// Backspace / Delete 要在光标紧邻 token 时整体删掉占位符。
    /// </summary>
    private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;

        if (e.Key == Key.Back && TryDeleteTokenBeforeCaret())
            e.Handled = true;
        else if (e.Key == Key.Delete && TryDeleteTokenAfterCaret())
            e.Handled = true;
    }

    private async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要附加的文件",
                AllowMultiple = true,
            });
            AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
        }
        catch
        {
            // 选择器不可用时不做处理
        }
    }

    private void OnInputDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files is null || files.Count == 0) return;
        AddAttachments(files.Select(f => AttachmentStore.FromPath(f.Path.LocalPath)));
        e.Handled = true;
    }

    // ---- 草稿（随输入实时落库） ----

    /// <summary>
    /// 草稿载荷。有附件时必须把 token → 路径的映射一起存：
    /// 只存正文的话，重开会话后 <c>@图片1</c> 还在，但「图片1 是哪个文件」已经丢了。
    /// </summary>
    private sealed record DraftPayload(string Text, List<DraftAttachmentDto> Attachments);

    private sealed record DraftAttachmentDto(string Token, string Path);

    private string SerializeDraft()
    {
        if (_pending.Count == 0) return InputBox.Text ?? "";
        return JsonSerializer.Serialize(new DraftPayload(
            InputBox.Text ?? "",
            _pending.Select(d => new DraftAttachmentDto(d.Token, d.Info.Path)).ToList()));
    }

    private void RestoreDraft()
    {
        var raw = _profile.Draft?.Load();

        // 必须在碰 InputBox.Text 之前记下「库里现在存的是什么」：下面所有分支都会给输入框赋值，
        // 而 Avalonia 的 TextBox.Text 默认值是 null，连赋空串都是一次真实变化并触发 TextChanged。
        // 先记下来，SaveDraftAsync 才有依据判断「这次触发只是恢复、没有真的改动」。
        _lastSavedDraft = raw ?? "";

        if (string.IsNullOrEmpty(raw))
        {
            InputBox.Text = "";
            _pending.Clear();
            RefreshAttachmentBar();
            return;
        }

        // 不带附件的草稿是纯文本；带附件的存成 JSON。解析失败一律按纯文本处理。
        if (!raw.StartsWith('{'))
        {
            InputBox.Text = raw;
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<DraftPayload>(raw);
            if (payload is null)
            {
                InputBox.Text = raw;
                return;
            }

            _pending.Clear();
            foreach (var dto in payload.Attachments)
            {
                if (string.IsNullOrEmpty(dto.Token)) continue;
                if (AttachmentStore.FromPath(dto.Path) is not { } info) continue;
                _pending.Add(new DraftAttachment(dto.Token, info));
            }

            _tokenSeq = _pending.Count;   // 够用即可，NextToken 还会查重
            InputBox.Text = payload.Text;
            PruneAttachments();
            RefreshAttachmentBar();
        }
        catch
        {
            InputBox.Text = raw;
        }
    }

    private async Task SaveDraftAsync()
    {
        if (_profile.Draft is not { } draft) return;

        // 内容与库里一致就什么都不做。这不是优化，是修 bug：
        // 打开会话时 RestoreDraft 会给输入框赋一次值（草稿为空就是空串），而 Avalonia 的
        // TextBox.Text 默认值是 null，这次 null→"" 同样算「变化」并触发 TextChanged。
        // 少了这道闸门，光是点开一个会话就会往数据库写一次草稿。
        if (SerializeDraft() == _lastSavedDraft) return;

        // 串行化：上一次写还没回来时只做个标记，等它回来后用最新内容再写一遍。
        // 不去重不行 —— 并发写同一条记录时，先发的未必先落，旧草稿会盖掉新的。
        if (_draftSaving)
        {
            _draftPending = true;
            return;
        }

        _draftSaving = true;
        try
        {
            do
            {
                _draftPending = false;

                // 排队期间内容可能又变回原样（或变回已落盘的值），再判一次
                var text = SerializeDraft();
                if (text == _lastSavedDraft) break;

                await draft.SaveAsync(text);
                _lastSavedDraft = text;
            }
            while (_draftPending);
        }
        finally
        {
            _draftSaving = false;
        }
    }

    // ============================================================
    // 可折叠「已深度思考」卡片
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

            _contentHost = new SmoothScrollViewer()
            {
                Content = ContentBlock,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 0,
                Margin = new Thickness(18, 0, 18, 0),
                Classes = { "thin-scrollbar" },
                Opacity = 0,
                // 折叠时整块移出布局：只设 MaxHeight=0 挡不住测量 —— ScrollViewer 测量时
                // 给内容的约束在滚动方向上是无限大，里面的文本照样每次布局都被测一遍。
                IsVisible = false,
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

            // 用 SelectionModeHost：原文模式下自动展开（折叠时内部文本无从选中），退出时回收
            var root = new SelectionModeHost
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
            root.GetExpanded = () => _expanded;
            root.Expand = () => { if (!_expanded) Toggle(); };
            root.Collapse = () => { if (_expanded) Toggle(); };
            Root = root;
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

            // 先恢复可见再起步：MaxHeight 从 0 动画到展开高度，顺序反了会先量到 0 高度。
            // 收起时直接不可见（连带跳过测量），代价是没有收起动画 —— 换来的是长会话不卡，
            // 而且折叠卡默认就是收起的，动画本来也看不到。
            _contentHost.IsVisible = _expanded;
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
    // 可折叠「{工具名} 工具」卡片
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

        /// <summary>
        /// 工具入参原文，等首次展开时才美化成缩进 JSON。
        /// 历史卡默认折叠，而 write / edit 这类入参是整段文件内容 ——
        /// 为「没人会看」的折叠状态解析并重新排版 JSON 是白费功夫。
        /// </summary>
        private string _rawInputJson = "{}";

        /// <summary>入参是否已美化过。只做一次，展开后收起再展开不重复解析。</summary>
        private bool _inputFormatted;

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

            _contentHost = new SmoothScrollViewer()
            {
                Content = contentStack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 0,
                Margin = new Thickness(18, 0, 18, 0),
                Classes = { "thin-scrollbar" },
                Opacity = 0,
                // 同 ThinkingCard：MaxHeight=0 挡不住测量，必须 IsVisible=false。
                // 工具输出常常很长，这里是长会话布局开销的大头。
                IsVisible = false,
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

            // 同 ThinkingCard：原文模式下自动展开，退出时回收
            var root = new SelectionModeHost
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
            root.GetExpanded = () => _expanded;
            root.Expand = () => { if (!_expanded) Toggle(); };
            root.Collapse = () => { if (_expanded) Toggle(); };
            Root = root;
        }

        public void SetData(string toolName, string inputJson, string output)
        {
            _titleBlock.Text = $"{toolName} 工具";
            _outputBlock.Text = string.IsNullOrWhiteSpace(output) ? "（无输出）" : output;

            _rawInputJson = inputJson;
            _inputFormatted = false;

            // 折叠着就先不碰：历史卡默认折叠，而 write / edit 的入参是整段文件内容，
            // 为「没人会看」的状态解析 + 重新排版 JSON 是纯浪费。
            if (_expanded)
                EnsureInputFormatted();
            else
                _inputBlock.Text = "";
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            if (_expanded) EnsureInputFormatted();

            // 同 ThinkingCard：先恢复可见，再让 MaxHeight 从 0 动画展开
            _contentHost.IsVisible = _expanded;
            _contentHost.MaxHeight = _expanded ? ExpandedMaxHeight : 0;
            _contentHost.Opacity = _expanded ? 1 : 0;
            _chevron.Angle = _expanded ? 90 : 0;
        }

        /// <summary>首次展开时才美化入参 JSON，之后复用结果，不重复解析。</summary>
        private void EnsureInputFormatted()
        {
            if (_inputFormatted) return;
            _inputFormatted = true;
            _inputBlock.Text = FormatJson(_rawInputJson);
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
