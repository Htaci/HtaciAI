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

    private readonly string _pluginId;

    /// <summary>本插件发起、还没跑完的会话。禁用插件时按它取消。</summary>
    private readonly HashSet<string> _activeSessions = new(StringComparer.Ordinal);

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

    public Task<bool> CompressAsync(string sessionId)
    {
        if (_disposed) return Task.FromResult(false);

        return OnUiThreadAsync(() => CompressCoreAsync(sessionId));
    }

    public Task<IReadOnlyList<SessionMessageSnapshot>> GetRecentMessagesAsync(
        string sessionId, int take, CancellationToken ct = default)
    {
        if (_disposed || take <= 0 || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<IReadOnlyList<SessionMessageSnapshot>>(Array.Empty<SessionMessageSnapshot>());

        return OnUiThreadAsync(() => GetRecentCoreAsync(sessionId, Math.Min(take, MaxSnapshotMessages), ct));
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
    }

    // ============================================================
    // 实现
    // ============================================================

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

    private async Task<bool> CompressCoreAsync(string sessionId)
    {
        try
        {
            var profile = await ResolveProfileAsync(sessionId);
            if (profile is null) return false;

            using var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
            await lease.Runtime.EnsureReadyAsync();

            var result = await lease.Runtime.CompressAsync(manual: true);
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

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>
    /// 按会话 id 建档案：先当普通对话找，找不到再当工作空间会话找。
    /// 两种会话共用同一套 id，但存在两张表里，只能这样判。
    /// </summary>
    private static async Task<IConversationProfile?> ResolveProfileAsync(string sessionId)
    {
        if (await ChatRepository.GetAsync(sessionId) is not null)
            return new StandaloneProfile(sessionId);

        if (await WorkspaceSessionRepository.GetAsync(sessionId) is not { } session)
            return null;

        if (await WorkspaceRepository.GetAsync(session.WorkspaceId) is not { } workspace)
            return null;

        return new WorkspaceProfile(workspace, session);
    }

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
