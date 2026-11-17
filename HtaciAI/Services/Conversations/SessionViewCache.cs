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

    /// <summary>
    /// 缓存键 → 正在显示这个窗口的宿主（页面实例）。
    ///
    /// <b>为什么需要它</b>：缓存是<b>全进程</b>共用的，但「正在显示」是<b>每个页面</b>各自知道的。
    /// 开两个「智能对话」标签页时，A 页显示会话 X、B 页显示会话 Y，
    /// 各自的淘汰器只知道自己手上那一个 —— B 的淘汰器看到 X「很久没显示」就会把它清掉，
    /// 而 X 此刻正挂在 A 页上。清掉的后果不是「下次重新加载」，是 A 页那个窗口当场退订、
    /// 还掉租约，界面从此不再更新。
    /// </summary>
    private readonly Dictionary<string, object> _holders = new(StringComparer.Ordinal);

    /// <summary>登记「这个窗口正被某个宿主显示着」，淘汰时跳过。一个窗口同时只可能被一个宿主显示。</summary>
    public void Claim(string key, object holder) => _holders[key] = holder;

    /// <summary>放弃某个宿主持有的全部登记（切到新建页、关标签页、被别的页面抢走时）。</summary>
    public void ReleaseHolder(object holder)
    {
        foreach (var key in _holders.Where(kv => ReferenceEquals(kv.Value, holder)).Select(kv => kv.Key).ToList())
            _holders.Remove(key);
    }

    /// <summary>
    /// 全进程共用的一份缓存。
    ///
    /// <b>必须共用，不能每个页面一份。</b>每个标签页都有自己的 <c>ConversationPage</c>，
    /// 各建一份缓存就会出现「同一个会话两份窗口」：一份还在跑、继续往库里和界面上写，
    /// 另一份是从库里读来的快照，看起来像任务已经结束了（复制按钮都出来了、输入框也退回发送态），
    /// 点它还能再发一条，同一会话两轮并发写。共用之后，「一个会话只有一个窗口」
    /// 由数据结构本身保证，不必指望每个调用点都记得先去查。
    /// </summary>
    public static SessionViewCache Shared { get; } = new();

    /// <summary>缓存里的条目数。</summary>
    public int Count => _entries.Count;

    public IReadOnlyList<string> Keys => _entries.Keys.ToList();

    // ---- 键规则：普通会话 / 工作空间会话 / 子 Agent 会话分属不同的 id 空间，加上前缀避免撞车 ----

    public static string KeyForSession(string sessionId) => "s:" + sessionId;

    public static string KeyForWorkspaceSession(string workspaceId, string sessionId)
        => $"w:{workspaceId}:{sessionId}";

    /// <summary>
    /// 子 Agent 会话的键。<b>不拼父会话 id</b>：子会话 id 自带 <c>sub-</c> 前缀、全局唯一，
    /// 而同一个子会话可能被两个父会话先后续用，把它绑在某个父会话上会让缓存白白多出一份。
    /// </summary>
    public static string KeyForSubagent(string sessionId) => "a:" + sessionId;

    /// <summary>插件会话的键。同样只带会话 id：插件会话 id 自带 <c>plugin-</c> 前缀、全局唯一。</summary>
    public static string KeyForPluginSession(string sessionId) => "p:" + sessionId;

    /// <summary>从缓存键里取回会话 id（键的三种格式见上面）。会话 id 本身不含冒号。</summary>
    public static string SessionIdOf(string key)
    {
        if (key.StartsWith("s:", StringComparison.Ordinal)) return key[2..];
        if (key.StartsWith("a:", StringComparison.Ordinal)) return key[2..];
        if (key.StartsWith("p:", StringComparison.Ordinal)) return key[2..];

        var index = key.IndexOf(':', 2);
        return index < 0 ? key : key[(index + 1)..];
    }

    /// <summary>从缓存键里取回所属工作空间 id；不是工作空间会话的键返回 null。</summary>
    public static string? WorkspaceIdOf(string key)
    {
        if (!key.StartsWith("w:", StringComparison.Ordinal)) return null;

        var index = key.IndexOf(':', 2);
        return index < 0 ? null : key[2..index];
    }

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

    /// <summary>
    /// 刷新某个会话（不论普通会话还是工作空间会话）的最后显示时间。
    /// 用途：任务跑完时重新起算保留期 —— 运行期间窗口是被钉住不淘汰的，
    /// 淘汰倒计时该从「跑完」开始，而不是从「当初切走」开始。
    /// </summary>
    public void TouchSession(string sessionId, DateTime utcNow)
    {
        foreach (var (key, entry) in _entries)
            if (SessionIdOf(key) == sessionId)
                entry.LastShownUtc = utcNow;
    }

    /// <summary>
    /// 移除一条（保留期到了、会话被删除、工作空间被删除时调用，否则删了还能从缓存切回来）。
    ///
    /// <b>顺带把窗口扔掉</b>（<see cref="ConversationView.Dispose"/>）：它退了 runtime 的订阅、
    /// 还掉租约，这样「缓存里没有的东西」就真的没人再引用它了。只从字典里删键而不扔窗口，
    /// 那个窗口会被 runtime 的事件一直吊着 —— 缓存看着是空的，内存其实还在涨。
    /// </summary>
    public bool Evict(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return false;

        // 还有宿主在显示它：键可以带走（下次按需重建），但窗口<b>不能</b>扔 ——
        // 扔了就是把人家正看着的那个界面退订掉
        if (_holders.ContainsKey(key))
        {
            _entries.Remove(key);
            return true;
        }

        _entries.Remove(key);
        entry.View.Dispose();
        return true;
    }

    /// <summary>按前缀批量移除，例如删掉一个工作空间时要带走它下面所有会话的窗口。</summary>
    public void EvictWhere(Func<string, bool> predicate)
    {
        foreach (var key in _entries.Keys.Where(predicate).ToList())
            Evict(key);
    }

    /// <summary>
    /// 按会话 id 移除，不管它是普通会话还是某个工作空间下的会话——
    /// 删除会话时调用方未必知道（或懒得传）工作空间 id。
    /// </summary>
    public void EvictBySessionId(string sessionId)
    {
        Evict(KeyForSession(sessionId));
        Evict(KeyForSubagent(sessionId));
        EvictWhere(key => key.StartsWith("w:", StringComparison.Ordinal) &&
                          key.EndsWith(":" + sessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 挑出超过保留期的键。<b>有宿主正在显示的一律跳过</b>（见 <see cref="Claim"/>）——
    /// 包括本页正在看的那个，所以调用方不必再传「别动我手上这个」。
    /// </summary>
    /// <param name="retention">保留时长；传 <see cref="Timeout.InfiniteTimeSpan"/> 表示一直保留（永远返回空）。</param>
    /// <param name="isPinned">
    /// 额外钉住的键，<b>不看保留期</b>。当前用途是「这一轮还在跑」的会话：
    /// 运行中的窗口一旦被淘汰，切回来就会重新建一个空窗口、从库里读一段半成品历史，
    /// 而旧窗口其实还活着、还在往里写 —— 界面上就是「任务明明还在跑，却显示已完成」。
    /// 所以只要任务没结束，窗口就不许走。
    /// </param>
    public IReadOnlyList<string> CollectExpired(
        DateTime utcNow, TimeSpan retention, Func<string, bool>? isPinned = null)
    {
        if (retention == Timeout.InfiniteTimeSpan) return Array.Empty<string>();

        var expired = new List<string>();
        foreach (var (key, entry) in _entries)
        {
            if (_holders.ContainsKey(key)) continue;
            if (isPinned?.Invoke(key) == true) continue;
            if (utcNow - entry.LastShownUtc > retention) expired.Add(key);
        }

        return expired;
    }
}
