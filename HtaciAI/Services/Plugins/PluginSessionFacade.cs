using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// <see cref="ISessionFacade"/> 的宿主实现：把插件的调用翻成
/// <see cref="ConversationRuntimeRegistry"/> 上的租约 + <see cref="ConversationRuntime"/> 的入口。
///
/// <b>为什么需要它而不是让插件直接用运行时</b>：
/// <list type="bullet">
///   <item>插件只拿到会话 id，不知道那个会话是普通对话还是工作空间会话 —— 建档案是宿主的知识；</item>
///   <item>插件可能在自己的定时器线程上调用，而运行时与视图约定在 UI 线程；</item>
///   <item>插件被禁用时，它发起但还没跑完的那一轮必须停掉，否则插件代码已经卸载、回调还在回来。</item>
/// </list>
///
/// <b>不改用户的选择</b>：一轮发送会把模型/思考/工具写回会话存档，所以这里一律用会话当前的配置，
/// 不提供「临时指定模型」——那会把用户在界面上选的模型悄悄换掉。
///
/// <b>线程</b>：公开方法可以在任意线程进入，内部统一切到 UI 线程再碰运行时。
/// </summary>
public sealed class PluginSessionFacade : ISessionFacade, IDisposable
{
    /// <summary>单次最多返回多少条历史。防止插件一口要全部长会话。</summary>
    private const int MaxSnapshotMessages = 500;

    /// <summary>一次最多列多少个会话。插件做下拉列表够用了，也不至于让超长列表拖慢界面。</summary>
    private const int MaxListedSessions = 200;

    private readonly string _pluginId;

    /// <summary>本插件发起、还没跑完的会话。禁用插件时按它取消。</summary>
    private readonly HashSet<string> _activeSessions = new(StringComparer.Ordinal);

    /// <summary>登记了交互桥的会话 → 桥。禁用插件时连同运行时上的引用一起清掉。</summary>
    private readonly Dictionary<string, IPluginInteractionHost> _interactionHosts = new(StringComparer.Ordinal);

    private bool _disposed;

    public PluginSessionFacade(string pluginId) => _pluginId = pluginId;

    // ============================================================
    // ISessionFacade
    // ============================================================

    public Task<SessionSendResult> SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(SessionSendResult.Failed("插件已停用。"));
        if (string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(SessionSendResult.NotFound);
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(SessionSendResult.Failed("正文为空。"));

        return OnUiThreadAsync(() => SendCoreAsync(sessionId, text, ct));
    }

    public Task<bool> CancelAsync(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(false);

        return OnUiThreadAsync(() =>
        {
            // 只找不建：会话根本没打开、也没人在跑时，没必要为「停一轮」把整个运行时建出来再回收
            var runtime = ConversationRuntimeRegistry.Shared.Find(sessionId);
            if (runtime is null || !runtime.IsBusy) return Task.FromResult(false);

            runtime.Cancel();
            return Task.FromResult(true);
        });
    }

    public Task<bool> SetPermissionModeAsync(string sessionId, PluginPermissionMode mode)
    {
        if (_disposed) return Task.FromResult(false);

        return OnUiThreadAsync(() => SetPermissionModeCoreAsync(sessionId, mode));
    }

    public Task<bool> CompressAsync(string sessionId, int keepRecentTurns = 0)
    {
        if (_disposed) return Task.FromResult(false);

        return OnUiThreadAsync(() => CompressCoreAsync(sessionId, keepRecentTurns));
    }

    public Task<IReadOnlyList<SessionMessageSnapshot>> GetRecentMessagesAsync(
        string sessionId, int take, CancellationToken ct = default)
    {
        if (_disposed || take <= 0 || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<IReadOnlyList<SessionMessageSnapshot>>(Array.Empty<SessionMessageSnapshot>());

        return OnUiThreadAsync(() => GetRecentCoreAsync(sessionId, Math.Min(take, MaxSnapshotMessages), ct));
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int take = 50, CancellationToken ct = default)
    {
        if (_disposed || take <= 0)
            return Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        // 只读库，不建运行时：列个会话列表不该把每个会话的运行时都拉起来
        return ListSessionsCoreAsync(Math.Min(take, MaxListedSessions), ct);
    }

    public Task<string?> CreateSessionAsync(string? title = null, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<string?>(null);
        return CreateSessionCoreAsync(title, ct);
    }

    public Task<IReadOnlyList<SessionSummary>> ListMySessionsAsync(
        int take = 50, CancellationToken ct = default)
    {
        if (_disposed || take <= 0)
            return Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        return ListMySessionsCoreAsync(Math.Min(take, MaxListedSessions), ct);
    }

    public Task<bool> DeleteSessionAsync(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(false);

        // 停掉正在跑的那一轮要碰运行时，切到 UI 线程（与其他方法一致）
        return OnUiThreadAsync(() => DeleteSessionCoreAsync(sessionId));
    }

    public Task<SessionUsage?> GetUsageAsync(string sessionId, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult<SessionUsage?>(null);

        // 只读库：查用量不该把会话的历史读进内存（列十个会话就会读十份）
        return GetUsageCoreAsync(sessionId, ct);
    }

    public Task<SessionModelInfo?> GetModelInfoAsync(string sessionId, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<SessionModelInfo?>(null);

        return GetModelInfoCoreAsync(sessionId, ct);
    }

    public Task<IReadOnlyList<PluginToolInfo>> ListAvailableToolsAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return Task.FromResult<IReadOnlyList<PluginToolInfo>>(Array.Empty<PluginToolInfo>());

        // GetEnabled 已经滤掉了内部哨兵工具（invalid 之类），它们不该出现在插件视野里
        var tools = ToolRegistry.Instance.GetEnabled()
            .Select(t => new PluginToolInfo(
                t.Id,
                t.Name,
                string.IsNullOrWhiteSpace(t.Description) ? null : t.Description,
                MapDanger(t.DangerLevel),
                DescribeSource(t.Source)))
            .ToList();

        return Task.FromResult<IReadOnlyList<PluginToolInfo>>(tools);
    }

    public Task<IReadOnlyList<string>?> GetSessionToolsAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<IReadOnlyList<string>?>(null);

        return OnUiThreadAsync(() => GetSessionToolsCoreAsync(sessionId));
    }

    public Task<bool> SetSessionToolsAsync(
        string sessionId, IReadOnlyList<string> toolIds, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(false);

        return OnUiThreadAsync(() => SetSessionToolsCoreAsync(sessionId, toolIds));
    }

    public Task<bool> SetInteractionAsync(string sessionId, IPluginInteractionHost? host)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(false);

        return OnUiThreadAsync(() => SetInteractionCoreAsync(sessionId, host));
    }

    /// <summary>
    /// 插件被禁用 / 卸载时调用：拒绝后续调用，并停掉它发起但还在跑的轮次。
    /// 这里<b>不</b>去取消用户自己发起的那一轮 —— 同一会话只有一轮在跑，
    /// 而 _activeSessions 里只记本插件发起的那些。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sessionId in _activeSessions.ToArray())
        {
            try { ConversationRuntimeRegistry.Shared.Find(sessionId)?.Cancel(); }
            catch { /* 取消本身失败不影响禁用流程 */ }
        }

        _activeSessions.Clear();

        // 交互桥必须摘掉：桥里握着插件的对象，留着它运行时（活得比插件久）就永远吊着那份程序集，
        // 插件禁用后无法卸载 —— 表现是「必须重启」。
        foreach (var sessionId in _interactionHosts.Keys.ToArray())
        {
            try
            {
                if (ConversationRuntimeRegistry.Shared.Find(sessionId) is { } runtime)
                    runtime.PluginInteraction = null;
            }
            catch { /* 摘不掉也不该让禁用流程失败 */ }
        }

        _interactionHosts.Clear();
    }

    // ============================================================
    // 实现
    // ============================================================

    /// <summary>
    /// 建一个属于本插件的会话。只碰 <c>plugin_sessions</c>，不建运行时 ——
    /// 插件建完不一定要马上发消息，没必要为它把整套运行时装好。
    /// </summary>
    private async Task<string?> CreateSessionCoreAsync(string? title, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var session = new PluginSession
            {
                PluginId = _pluginId,
                Title = NormalizeTitle(title),
            };

            await PluginSessionRepository.CreateAsync(session);
            PluginLog.Info(_pluginId, $"新建插件会话 {session.Id}");
            return session.Id;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, "新建插件会话失败", ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<SessionSummary>> ListMySessionsCoreAsync(int take, CancellationToken ct)
    {
        try
        {
            var sessions = await PluginSessionRepository.GetByPluginAsync(_pluginId);
            ct.ThrowIfCancellationRequested();

            var picked = sessions.Take(take).ToList();

            // 用量与窗口一起带回去：插件画「会话列表 + 上下文百分比」时不该逐个再问一遍（N+1）
            var models = picked.ToDictionary(s => s.Id, s => s.Model ?? "", StringComparer.Ordinal);
            var usage = await ReadContextUsageAsync(
                picked.Select(s => s.Id).ToList(), id => models[id], ct);

            return picked
                .Select(s =>
                {
                    usage.TryGetValue(s.Id, out var context);
                    return new SessionSummary(
                        s.Id, s.Title ?? "", s.LastMessageAt ?? s.CreatedAt, context.Used, context.Window);
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SessionSummary>();
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, "列出插件会话失败", ex);
            return Array.Empty<SessionSummary>();
        }
    }

    /// <summary>
    /// 删自己的会话。归属校验放在最前面：id 是谁的写在行里，不看前缀也不看调用方说法。
    /// </summary>
    private async Task<bool> DeleteSessionCoreAsync(string sessionId)
    {
        try
        {
            var session = await PluginSessionRepository.GetAsync(sessionId);
            if (session is null || !string.Equals(session.PluginId, _pluginId, StringComparison.Ordinal))
            {
                PluginLog.Warn(_pluginId, $"试图删除不属于自己的会话 {sessionId}，已忽略");
                return false;
            }

            // 先停这一轮：会话删了、回调却还在回来，插件那边会收到自己已经不再关心的事件
            try { ConversationRuntimeRegistry.Shared.Find(sessionId)?.Cancel(); } catch { /* 停不下来也照删 */ }
            _activeSessions.Remove(sessionId);

            var deleted = await PluginSessionRepository.SoftDeleteAsync(sessionId, _pluginId);
            if (deleted) PluginLog.Info(_pluginId, $"删除插件会话 {sessionId}");
            return deleted;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"删除插件会话失败：{sessionId}", ex);
            return false;
        }
    }

    private static string NormalizeTitle(string? title)
    {
        var text = (title ?? "").Trim();
        if (text.Length == 0) return "插件会话";
        return text.Length <= 100 ? text : text[..100];
    }

    // ============================================================
    // 用量 / 模型 / 工具集 / 交互桥
    // ============================================================

    /// <summary>
    /// 用量汇总。<b>全程只读库</b>：不 Acquire 运行时 —— 那会把整个会话的历史读进内存，
    /// 而插件问用量时往往一次问一批会话。
    /// </summary>
    private async Task<SessionUsage?> GetUsageCoreAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return null;

            ct.ThrowIfCancellationRequested();

            var details = await ResolveModelDetailsAsync(profile.State.Model);
            var window = details?.ContextWindow ?? -1;

            var rows = await ChatRepository.GetUsageMessagesAsync(new[] { sessionId });
            ct.ThrowIfCancellationRequested();

            rows.TryGetValue(sessionId, out var list);
            list ??= new List<ChatMessage>();

            long input = 0, cached = 0, output = 0, updatedAt = 0;
            foreach (var message in list)
            {
                if (TokenUsage.FromStoredJson(message.UsageJson) is not { } usage) continue;

                input += usage.InputTokens;
                cached += usage.CacheHitTokens;
                output += usage.OutputTokens;
                if (message.CreatedAt > updatedAt) updatedAt = message.CreatedAt;
            }

            var used = ContextUsage.TryComputeFromLatest(list, window, out var computed, out _) ? computed : 0;

            return new SessionUsage(
                details?.ModelId ?? profile.State.Model,
                window,
                used,
                input,
                cached,
                output,
                list.Count,
                updatedAt);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"读取会话用量失败：{sessionId}", ex);
            return null;
        }
    }

    /// <summary>
    /// 模型能力。会话没选模型 / 选的模型已被删除时回退宿主默认模型，
    /// 与 <see cref="ResolveModelAsync"/> 同一套回退，插件据此决定图片直传还是转路径。
    /// </summary>
    private async Task<SessionModelInfo?> GetModelInfoCoreAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return null;

            ct.ThrowIfCancellationRequested();

            var saved = profile.State.Model;
            var details = string.IsNullOrWhiteSpace(saved) ? null : await ModelCatalog.ResolveAsync(saved);
            var fallback = details is null;
            details ??= await AppDefaults.ResolveDefaultModelAsync();
            if (details is null) return null;

            return new SessionModelInfo(
                details.ModelId,
                details.DisplayName,
                details.ContextWindow,
                details.MaxOutputTokens,
                ConversationRuntime.SupportsVision(details),
                details.SupportsThinking,
                fallback);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"读取会话模型信息失败：{sessionId}", ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>?> GetSessionToolsCoreAsync(string sessionId)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return null;

            // 运行时的档案优先：它可能刚到过别的入口的修改，而下一轮请求用的就是它
            if (ConversationRuntimeRegistry.Shared.Find(sessionId) is { IsHistoryLoaded: true } runtime)
                return runtime.Profile.State.EnabledToolIds.ToList();

            return new List<string>(profile.State.EnabledToolIds);
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"读取会话工具集失败：{sessionId}", ex);
            return null;
        }
    }

    /// <summary>
    /// 覆盖式写入自己的会话的工具集。
    ///
    /// <b>只认自己的会话</b>：普通会话与工作空间会话的 <c>EnabledToolIds</c> 是用户在界面上选的，
    /// 插件改它等于悄悄替用户做决定。归属看行里的 <c>PluginId</c>，不看 id 前缀（前缀不分插件）。
    /// </summary>
    private async Task<bool> SetSessionToolsCoreAsync(string sessionId, IReadOnlyList<string> toolIds)
    {
        try
        {
            var session = await PluginSessionRepository.GetAsync(sessionId);
            if (session is null || session.IsDeleted ||
                !string.Equals(session.PluginId, _pluginId, StringComparison.Ordinal))
            {
                PluginLog.Warn(_pluginId, $"试图修改不属于自己的会话 {sessionId} 的工具集，已忽略");
                return false;
            }

            // 解析不到的 id 丢掉：留在会话里只会让下一轮请求带着一个不存在的工具名
            var cleaned = toolIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Where(id => ToolRegistry.Instance.ResolveById(id) is not null)
                .ToList();

            // 运行时已经读过库就改它的档案并落库 —— 下一轮请求用的是内存里那一份，
            // 只写库的话要等运行时报废才生效。跑起来之前的不动，免得把还没载入的档案写空。
            if (ConversationRuntimeRegistry.Shared.Find(sessionId) is { IsHistoryLoaded: true } runtime)
            {
                runtime.Profile.State.EnabledToolIds = cleaned;
                await runtime.PersistSessionAsync();
            }
            else
            {
                session.EnabledToolIds = cleaned;
                await PluginSessionRepository.UpdateAsync(session);
            }

            PluginLog.Info(_pluginId, $"会话 {sessionId} 的工具集改为 {cleaned.Count} 个");
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"修改会话工具集失败：{sessionId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 登记 / 撤销交互桥。会话必须真实存在（四种会话都行）：
    /// 插件派任务到工作空间会话时同样需要一个「问用户」的出口。
    ///
    /// <b>不覆盖窗口</b>：桥只挂在 <see cref="ConversationRuntime.PluginInteraction"/> 上，
    /// 视图打开时自己的 <c>Interaction</c> 优先，插件收不到（见该属性的说明）。
    /// </summary>
    private async Task<bool> SetInteractionCoreAsync(string sessionId, IPluginInteractionHost? host)
    {
        try
        {
            if (await ResolveProfileAsync(sessionId) is null) return false;

            if (host is null) _interactionHosts.Remove(sessionId);
            else _interactionHosts[sessionId] = host;

            // 运行时有就立刻挂上；没有也不建 —— 发消息时 SendCoreAsync 会再挂一次
            if (ConversationRuntimeRegistry.Shared.Find(sessionId) is { } runtime)
                runtime.PluginInteraction = BuildBridge(sessionId, host);

            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"登记交互桥失败：{sessionId}", ex);
            return false;
        }
    }

    /// <summary>按会话 id 造一个交互桥；<paramref name="host"/> 为 null 时返回 null。</summary>
    private PluginInteractionBridge? BuildBridge(string sessionId, IPluginInteractionHost? host)
        => host is null ? null : new PluginInteractionBridge(sessionId, host);


    private async Task<SessionSendResult> SendCoreAsync(string sessionId, string text, CancellationToken ct)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return SessionSendResult.NotFound;

            // 档案只在「这个会话还没有运行时」被用到，多建一个对象没有副作用
            using var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
            var runtime = lease.Runtime;

            // 插件可能在会话没打开时就发起第一轮：这里必须等历史读完，
            // 否则模型收到的是空上下文（而且这种丢失是静默的）。
            await runtime.EnsureReadyAsync();
            ct.ThrowIfCancellationRequested();

            if (runtime.IsBusy) return SessionSendResult.Busy;

            // 插件登记过交互桥就挂上：没有窗口时审批与提问走它，否则一律是「用户不在」
            if (_interactionHosts.TryGetValue(sessionId, out var interactionHost))
                runtime.PluginInteraction = BuildBridge(sessionId, interactionHost);

            var details = await ResolveModelAsync(runtime);
            if (details is null) return SessionSendResult.NoModel;

            var client = runtime.Gateway.BuildClient(details);

            _activeSessions.Add(sessionId);
            ChatTurnResult? turn;
            try
            {
                turn = await runtime.SendAsync(new ConversationTurnRequest
                {
                    Settings = new TurnSettings(
                        Model: details,
                        Client: client,
                        Thinking: ResolveThinking(runtime.Profile.State.Thinking),
                        ToolIds: runtime.Profile.State.EnabledToolIds),
                    Text = text,

                    // 收尾事件据此标出「这一轮是插件派的」：插件派完任务等结果时，
                    // 必须能和自己/用户在界面上聊的那几轮区分开
                    PluginInitiated = true,

                    // 收尾提醒按这个插件的声明决定弹不弹、留多久（常驻 / 自动收 / 不弹）
                    PluginId = _pluginId,
                });
            }
            finally
            {
                _activeSessions.Remove(sessionId);
            }

            // 竞态：进来时还没在跑，真正发送时被用户抢先了
            if (turn is null) return SessionSendResult.Busy;

            PluginLog.Info(_pluginId,
                $"插件发起的一轮已结束：会话 {sessionId}，结果 {(turn.Success ? "成功" : "失败")}");

            return SessionSendResult.Sent(turn.Success, turn.Success ? null : Describe(turn));
        }
        catch (OperationCanceledException)
        {
            return SessionSendResult.Failed("发起前已取消。");
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"插件发起会话失败：会话 {sessionId}", ex);
            return SessionSendResult.Failed(OneLine(ex.Message));
        }
    }

    private async Task<bool> SetPermissionModeCoreAsync(string sessionId, PluginPermissionMode mode)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return false;

            using var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
            await lease.Runtime.EnsureReadyAsync();
            await lease.Runtime.SetPermissionModeAsync(Map(mode));

            PluginLog.Info(_pluginId, $"把会话 {sessionId} 的权限档位改成 {mode}");
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"修改会话权限档位失败：会话 {sessionId}", ex);
            return false;
        }
    }

    private async Task<bool> CompressCoreAsync(string sessionId, int keepRecentTurns)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return false;

            using var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
            await lease.Runtime.EnsureReadyAsync();

            var result = await lease.Runtime.CompressAsync(manual: true, keepRecentTurns);
            if (result.Error is not null)
                PluginLog.Warn(_pluginId, $"压缩会话 {sessionId} 失败：{result.Error}");

            return result.Compressed;
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"压缩会话失败：会话 {sessionId}", ex);
            return false;
        }
    }

    private async Task<IReadOnlyList<SessionMessageSnapshot>> GetRecentCoreAsync(
        string sessionId, int take, CancellationToken ct)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return Array.Empty<SessionMessageSnapshot>();

            using var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
            await lease.Runtime.EnsureReadyAsync();
            ct.ThrowIfCancellationRequested();

            var messages = lease.Runtime.Messages;
            return messages
                .Skip(Math.Max(0, messages.Count - take))
                .Select(m => new SessionMessageSnapshot(
                    m.Role, m.Content, m.Thinking, m.Status, m.SequenceNumber, m.CreatedAt))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SessionMessageSnapshot>();
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, $"读取会话历史失败：会话 {sessionId}", ex);
            return Array.Empty<SessionMessageSnapshot>();
        }
    }

    private async Task<IReadOnlyList<SessionSummary>> ListSessionsCoreAsync(
        int take, CancellationToken ct)
    {
        try
        {
            var sessions = await ChatRepository.GetAllAsync();
            ct.ThrowIfCancellationRequested();

            var picked = sessions.OrderByDescending(s => s.UpdatedAt).Take(take).ToList();

            var models = picked.ToDictionary(s => s.Id, s => s.Model ?? "", StringComparer.Ordinal);
            var usage = await ReadContextUsageAsync(picked.Select(s => s.Id).ToList(), id => models[id], ct);

            return picked
                .Select(s =>
                {
                    usage.TryGetValue(s.Id, out var context);
                    return new SessionSummary(s.Id, s.Title ?? "", s.UpdatedAt, context.Used, context.Window);
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SessionSummary>();
        }
        catch (Exception ex)
        {
            PluginLog.Error(_pluginId, "列出会话失败", ex);
            return Array.Empty<SessionSummary>();
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>
    /// 按会话 id 建档案：先当普通对话找，找不到再当工作空间会话找。
    /// 两种会话共用同一套 id，但存在两张表里，只能这样判。
    /// </summary>
    private static Task<IConversationProfile?> ResolveProfileAsync(string sessionId)
        => SessionProfileResolver.ResolveAsync(sessionId);

    /// <summary>
    /// 用会话存档的模型；它被删了 / 会话还没选过模型时回退到宿主的默认模型。
    /// 都没有就是「没模型可用」—— 不回退任何内置模型（那会拿别人的 key 发请求）。
    /// </summary>
    private static async Task<ModelDetails?> ResolveModelAsync(ConversationRuntime runtime)
    {
        var modelId = runtime.Profile.State.Model;
        if (!string.IsNullOrWhiteSpace(modelId) && await runtime.Gateway.ResolveModelAsync(modelId) is { } saved)
            return saved;

        return await AppDefaults.ResolveDefaultModelAsync();
    }

    private static ThinkingMode ResolveThinking(int value)
        => Enum.IsDefined(typeof(ThinkingMode), value) ? (ThinkingMode)value : ThinkingMode.Default;

    /// <summary>
    /// 只读库地解析一个会话的模型：存档里的那个 → 宿主默认模型。
    /// 与 <see cref="ResolveModelAsync"/> 同一套回退，区别是这里不需要运行时。
    /// </summary>
    private static Task<ModelDetails?> ResolveModelDetailsAsync(string? modelId)
        => PluginUsageReader.ResolveModelDetailsAsync(modelId);

    /// <summary>批量算「上下文占用 + 窗口」，实现见 <see cref="PluginUsageReader"/>。</summary>
    private static Task<Dictionary<string, (long Used, long Window)>> ReadContextUsageAsync(
        IReadOnlyList<string> sessionIds, Func<string, string> modelOf, CancellationToken ct)
        => PluginUsageReader.ReadContextUsageAsync(sessionIds, modelOf, ct);

    private static PluginToolDangerLevel MapDanger(ToolDangerLevel level) => level switch
    {
        ToolDangerLevel.Risk => PluginToolDangerLevel.Risk,
        ToolDangerLevel.Danger => PluginToolDangerLevel.Danger,
        _ => PluginToolDangerLevel.Safe,
    };

    /// <summary>
    /// 工具来源翻译成插件看得懂的短词。枚举名小写即可（Builtin → builtin），
    /// 不另造一套字符串常量：多一套就多一处要同步。
    /// </summary>
    private static string DescribeSource(ToolSource source) => source switch
    {
        ToolSource.Builtin => "builtin",
        ToolSource.Script => "script",
        ToolSource.Mcp => "mcp",
        ToolSource.Plugin => "plugin",
        _ => source.ToString().ToLowerInvariant(),
    };

    private static PermissionMode Map(PluginPermissionMode mode) => mode switch
    {
        PluginPermissionMode.Strict => PermissionMode.Strict,
        PluginPermissionMode.Loose => PermissionMode.Loose,
        PluginPermissionMode.Free => PermissionMode.Free,
        _ => PermissionMode.Normal,
    };

    private static string? Describe(ChatTurnResult turn)
    {
        if (turn.Error is { Length: > 0 } error) return OneLine(error);
        // 没有错误信息 = 用户（或插件自己）把它停了
        return "这一轮被停止了。";
    }

    private static string OneLine(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    /// <summary>
    /// 在 UI 线程上跑 <paramref name="work"/>。已经在 UI 线程上就直接跑 —— 插件在按钮回调里
    /// 调用时不必绕一圈 Dispatcher。
    /// </summary>
    private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) return work();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { tcs.TrySetResult(await work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }
}
