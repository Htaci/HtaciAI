using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;
using HtaciAI.Views;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 一个会话当前被谁借走显示着。<b>宿主内部用</b>（会话页据此画占位），不暴露给插件。
/// </summary>
public sealed record ConversationDockInfo(string SessionId, string PluginId, string HostLabel);

/// <summary>
/// <see cref="IConversationDock"/> 的宿主实现核心：把会话视图在原宿主与插件之间来回搬。
///
/// <b>为什么必须搬「真那一份」而不是让插件自己再建一个视图</b>：
/// 会话视图带着滚动位置、正在跑的那一轮、输入框草稿、审批卡片。复制一份出来，
/// 一份在跑、另一份是从库里读的快照，用户点哪份都像丢消息 ——
/// 这与 <see cref="SessionViewCache"/> 里「一个会话只有一个窗口」是同一条规则。
///
/// <b>占位归宿主</b>：插件只说「我把 sessionId 借走了，我叫 X」，
/// 会话页自己显示「正在 X 中显示」和召回按钮。插件因此不必知道会话页长什么样。
///
/// <b>线程</b>：只在 UI 线程上访问（视图、缓存、控件树都只能在 UI 线程碰）。
/// </summary>
public sealed class ConversationDockHost
{
    public static ConversationDockHost Instance { get; } = new();

    private sealed class Entry
    {
        public Entry(string sessionId, string pluginId, string hostLabel, string cacheKey, ConversationView view)
        {
            SessionId = sessionId;
            PluginId = pluginId;
            HostLabel = hostLabel;
            CacheKey = cacheKey;
            View = view;
        }

        public string SessionId { get; }
        public string PluginId { get; }
        public string HostLabel { get; }
        public string CacheKey { get; }
        public ConversationView View { get; }

        /// <summary>缓存里的「持有者」标记：借走期间不许被淘汰器清掉。</summary>
        public object CacheHolder { get; } = new();

        /// <summary>本插件这次借用的句柄（插件侧的 <c>Released</c> 事件挂在它上面）。</summary>
        public PluginConversationDockFacade? Owner { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SessionViewCache _viewCache = SessionViewCache.Shared;

    private ConversationDockHost() { }

    /// <summary>
    /// 某个会话的归属变了（被借走 / 被还回来）。参数是会话 id。
    /// <b>在 UI 线程上同步触发</b>：会话页收到后立刻改自己那块界面。
    /// </summary>
    public event Action<string>? DockChanged;

    /// <summary>这个会话现在被谁借走了；没人借返回 null。供会话页画占位用。</summary>
    public ConversationDockInfo? Find(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return _entries.TryGetValue(sessionId, out var entry)
            ? new ConversationDockInfo(entry.SessionId, entry.PluginId, entry.HostLabel)
            : null;
    }

    /// <summary>
    /// 借出会话视图。
    /// </summary>
    public async Task<ConversationDockResult> AttachAsync(
        PluginConversationDockFacade owner, string sessionId, string hostLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return ConversationDockResult.Fail("缺少会话 id。");

        if (_entries.TryGetValue(sessionId, out var existing))
        {
            // 同一个插件再要一次：直接把已经借出去的那个给它，不报错 ——
            // 插件重建自己的面板（关掉再打开）时属于正常操作。
            if (ReferenceEquals(existing.Owner, owner)) return ConversationDockResult.Ok(existing.View);

            return ConversationDockResult.Fail($"这个会话正在「{existing.HostLabel}」中显示。");
        }

        var resolved = await SessionProfileResolver.ResolveWithKeyAsync(sessionId);
        if (resolved is null) return ConversationDockResult.Fail("找不到这个会话。");

        ct.ThrowIfCancellationRequested();

        var (cacheKey, profile) = resolved.Value;

        // 缓存里已经有了就复用（绝大多数情况：用户在会话页打开着它）；
        // 没有就按会话档案借一份运行时、建一个窗口 —— 建出来的窗口与页面建的完全一样。
        var view = await GetOrCreateViewAsync(cacheKey, profile, ct);

        var entry = new Entry(sessionId, owner.PluginId, NormalizeLabel(hostLabel, owner.PluginId), cacheKey, view)
        {
            Owner = owner,
        };

        _entries[sessionId] = entry;

        // 先让会话页把视图交出来（它收到通知后会清空自己那块并换成占位），
        // 再兜底把视图从任何残余父级上摘掉 —— 否则赋给插件容器时会撞「已有父级」。
        DockChanged?.Invoke(sessionId);
        DetachFromParent(view);

        // 借着期间不许被淘汰：淘汰器只看「有没有人登记持有」，不看窗口在不在可视树里
        _viewCache.Claim(cacheKey, entry.CacheHolder);

        PluginLog.Info(owner.PluginId, $"借走会话 {sessionId} 的对话视图（显示为「{entry.HostLabel}」）");

        return ConversationDockResult.Ok(view);
    }

    /// <summary>
    /// 插件主动还回来。<paramref name="pluginId"/> 只用来校验归属：
    /// 已经被宿主召回、又被别的插件借走的会话不会被它误收。
    /// </summary>
    public void Release(string pluginId, string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var entry)) return;
        if (!string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal)) return;

        ReleaseEntry(entry);
    }

    /// <summary>会话页点「召回」：不管现在归谁，一律收回（用户的意思优先）。</summary>
    public void Recall(string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var entry)) return;

        ReleaseEntry(entry);
        entry.Owner?.NotifyReleased(sessionId);
    }

    /// <summary>插件被禁用时调用：把它借走的所有会话一次性收回。</summary>
    public void ReleaseAllForPlugin(string pluginId)
    {
        foreach (var entry in _entries.Values
                     .Where(e => string.Equals(e.PluginId, pluginId, StringComparison.Ordinal))
                     .ToList())
        {
            ReleaseEntry(entry);
        }
    }

    // ============================================================
    // 实现
    // ============================================================

    private void ReleaseEntry(Entry entry)
    {
        _entries.Remove(entry.SessionId);

        // 视图先离开插件的容器，再让会话页重新挂上（否则「已有父级」）
        DetachFromParent(entry.View);

        _viewCache.ReleaseHolder(entry.CacheHolder);

        // 通知在摘干净之后发：会话页收到后会立刻把视图挂回自己那块
        DockChanged?.Invoke(entry.SessionId);

        PluginLog.Info(entry.PluginId, $"归还会话 {entry.SessionId} 的对话视图");
    }

    private async Task<ConversationView> GetOrCreateViewAsync(
        string cacheKey, IConversationProfile profile, CancellationToken ct)
    {
        if (_viewCache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            _viewCache.Touch(cacheKey, DateTime.UtcNow);
            return cached;
        }

        var sessionId = SessionViewCache.SessionIdOf(cacheKey);

        // 运行时按会话复用：页面可能已经借着一份、甚至正在跑一轮，这里必须用同一份
        var lease = ConversationRuntimeRegistry.Shared.Acquire(sessionId, () => profile);
        var view = _viewCache.GetOrAdd(cacheKey, () => new ConversationView(lease.Runtime, lease), DateTime.UtcNow);

        // 活动状态照常落到左侧列表：这个窗口是本方法建的，会话页那张表还没挂上它。
        // 只在新窗口上挂一次 —— 挂在借用流程里的话，每换个插件就多一份重复通知。
        view.ActivityChanged += activity => SessionActivityStore.Set(sessionId, activity);

        await view.InitializeAsync();
        ct.ThrowIfCancellationRequested();
        return view;
    }

    /// <summary>把视图从当前父级上摘下来（页面的 ContentControl、插件的容器、插件的面板）。</summary>
    private static void DetachFromParent(Control view)
    {
        switch (view.Parent)
        {
            case ContentControl owner:
                owner.Content = null;
                break;

            case Panel panel:
                panel.Children.Remove(view);
                break;

            case Decorator decorator:
                decorator.Child = null;
                break;

            case ContentPresenter presenter:
                presenter.Content = null;
                break;
        }
    }

    private static string NormalizeLabel(string? hostLabel, string pluginId)
    {
        var label = (hostLabel ?? "").Trim();
        if (label.Length == 0) return pluginId;
        return label.Length <= 40 ? label : label[..40];
    }
}

/// <summary>
/// <see cref="IConversationDock"/> 的宿主门面：一个插件一个实例。
///
/// <b>为什么门面要自己记着借了哪些会话</b>：插件被禁用时宿主必须收回全部借用
/// （视图里还留着它的控件、它的按钮回调），这件事不能指望插件的
/// <c>ShutdownAsync</c> 记得做 —— 与窗口、标签页、插槽同一条约定。
/// </summary>
public sealed class PluginConversationDockFacade : IConversationDock, IDisposable
{
    private bool _disposed;

    public PluginConversationDockFacade(string pluginId) => PluginId = pluginId;

    public string PluginId { get; }

    /// <summary>借出的会话被宿主收回时触发（会话页点了「召回」/ 会话被删除）。<b>在 UI 线程上触发。</b></summary>
    public event Action<string>? Released;

    public Task<ConversationDockResult> AttachAsync(
        string sessionId, string hostLabel, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(ConversationDockResult.Fail("插件已停用。"));

        return OnUiThreadAsync(() => ConversationDockHost.Instance.AttachAsync(this, sessionId, hostLabel, ct));
    }

    public void Release(string sessionId)
    {
        if (_disposed || string.IsNullOrEmpty(sessionId)) return;

        if (Dispatcher.UIThread.CheckAccess())
            ConversationDockHost.Instance.Release(PluginId, sessionId);
        else
            Dispatcher.UIThread.Post(() => ConversationDockHost.Instance.Release(PluginId, sessionId));
    }

    // 以下是宿主自己的记账，不暴露给插件。

    internal void NotifyReleased(string sessionId) => Released?.Invoke(sessionId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { ConversationDockHost.Instance.ReleaseAllForPlugin(PluginId); }
        catch (Exception ex) { PluginLog.Warn(PluginId, "收回借出的会话视图失败：" + ex.Message); }

        Released = null;
    }

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
