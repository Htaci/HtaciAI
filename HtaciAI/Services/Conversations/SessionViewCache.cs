using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HtaciAI.Views;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 会话窗口缓存：切换会话时把 <see cref="ConversationView"/> 留下来而不是丢掉，
/// 保留期内切回来直接复用，省掉「重查库 + 重拉全部历史 + 重建整棵消息 UI」这一整套开销。
///
/// Avalonia 里没有现成的「缓存控件」（最接近的是 TabControl，但本项目刻意不用它）；
/// 控件被换下 <c>ContentControl.Content</c> 时只是脱离可视树、实例仍活着，
/// 再赋回去就重新挂上，内部状态（滚动位置、已渲染的消息、输入框草稿）全都还在。
///
/// <b>时间一律由调用方传入</b>（不在这里读 <c>DateTime.UtcNow</c>），
/// 这样淘汰策略可以脱离 UI 与真实时间来验证。
///
/// 只从 UI 线程调用，因此内部不加锁。
/// </summary>
public sealed class SessionViewCache
{
    private sealed class Entry(ConversationView view, DateTime lastShownUtc)
    {
        public ConversationView View { get; } = view;
        public DateTime LastShownUtc { get; set; } = lastShownUtc;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>缓存里的条目数。</summary>
    public int Count => _entries.Count;

    public IReadOnlyList<string> Keys => _entries.Keys.ToList();

    // ---- 键规则：普通会话与会话分属不同的 id 空间，加上前缀避免撞车 ----

    public static string KeyForSession(string sessionId) => "s:" + sessionId;

    public static string KeyForWorkspaceSession(string workspaceId, string sessionId)
        => $"w:{workspaceId}:{sessionId}";

    /// <summary>命中就直接返回；未命中则用 <paramref name="factory"/> 建一个并记下来。</summary>
    public ConversationView GetOrAdd(string key, Func<ConversationView> factory, DateTime utcNow)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastShownUtc = utcNow;
            return existing.View;
        }

        var view = factory();
        _entries[key] = new Entry(view, utcNow);
        return view;
    }

    public bool TryGet(string key, out ConversationView? view)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            view = entry.View;
            return true;
        }

        view = null;
        return false;
    }

    /// <summary>刷新某条的最后显示时间（切回来时调用，让保留期从「这次切回」重新计时）。</summary>
    public void Touch(string key, DateTime utcNow)
    {
        if (_entries.TryGetValue(key, out var entry))
            entry.LastShownUtc = utcNow;
    }

    /// <summary>移除一条（会话被删除、工作空间被删除时调用，否则删了还能从缓存切回来）。</summary>
    public bool Evict(string key) => _entries.Remove(key);

    /// <summary>按前缀批量移除，例如删掉一个工作空间时要带走它下面所有会话的窗口。</summary>
    public void EvictWhere(Func<string, bool> predicate)
    {
        foreach (var key in _entries.Keys.Where(predicate).ToList())
            _entries.Remove(key);
    }

    /// <summary>
    /// 按会话 id 移除，不管它是普通会话还是某个工作空间下的会话——
    /// 删除会话时调用方未必知道（或懒得传）工作空间 id。
    /// </summary>
    public void EvictBySessionId(string sessionId)
    {
        Evict(KeyForSession(sessionId));
        EvictWhere(key => key.StartsWith("w:", StringComparison.Ordinal) &&
                          key.EndsWith(":" + sessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 挑出超过保留期的键。<paramref name="keepKey"/> 是当前正在显示的那个——
    /// 它的「最后显示时间」就是现在，本来也不会过期，但显式排除掉可以防住时钟回拨之类的意外。
    /// </summary>
    /// <param name="retention">保留时长；传 <see cref="Timeout.InfiniteTimeSpan"/> 表示一直保留（永远返回空）。</param>
    public IReadOnlyList<string> CollectExpired(DateTime utcNow, TimeSpan retention, string? keepKey = null)
    {
        if (retention == Timeout.InfiniteTimeSpan) return Array.Empty<string>();

        var expired = new List<string>();
        foreach (var (key, entry) in _entries)
        {
            if (key == keepKey) continue;
            if (utcNow - entry.LastShownUtc > retention) expired.Add(key);
        }

        return expired;
    }
}
