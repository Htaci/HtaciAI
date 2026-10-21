using System;
using System.Collections.Generic;
using System.Linq;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 一份会话运行时的「借用凭据」。拿到它就能用 <see cref="Runtime"/>，用完必须 <see cref="Dispose"/>。
///
/// 用租约而不是直接返回 runtime，是为了让「谁还在用它」这件事可数：
/// 视图借一次、插件订阅时借一次，都还回来之后运行时才允许被回收。
/// </summary>
public sealed class ConversationRuntimeLease : IDisposable
{
    private ConversationRuntimeRegistry? _owner;

    internal ConversationRuntimeLease(
        ConversationRuntimeRegistry owner, string sessionId, ConversationRuntime runtime)
    {
        _owner = owner;
        SessionId = sessionId;
        Runtime = runtime;
    }

    /// <summary>这个会话的运行时。</summary>
    public ConversationRuntime Runtime { get; }

    public string SessionId { get; }

    /// <summary>还回去。<b>重复调用安全</b>（第二次什么也不做，不会把别人的引用减掉）。</summary>
    public void Dispose()
    {
        var owner = _owner;
        if (owner is null) return;

        _owner = null;
        owner.Release(SessionId, Runtime);
    }
}

/// <summary>
/// 会话运行时注册表：<b>一个会话只有一份运行时</b>，视图关掉也不销毁（有人还借着手里的租约时）。
///
/// <b>为什么要有它</b>：在 M1 之前，「跑一轮对话」这件事活在一个打开的视图里，
/// 所以插件（比如桌宠）既不能在会话没打开时发起对话，也看不到它跑完了没有。
/// 现在执行逻辑在 <see cref="ConversationRuntime"/> 里，这个注册表负责让它活得比视图长。
///
/// <b>生命周期</b>：
/// <list type="bullet">
///   <item>每次 <see cref="Acquire"/> 加一，租约 <c>Dispose</c> 减一。</item>
///   <item>引用归零<b>且没有正在跑的轮次</b>时立即丢掉 —— 不设空闲计时器：
///         数据比控件树便宜得多，但「什么时候会消失」必须可预测，
///         留一个看不见的倒计时只会让人怀疑内存有没有泄漏。</item>
///   <item>引用归零但正在跑：先留着，等它自己跑完（<see cref="ConversationRuntime.BusyChanged"/>）
///         再收。半路丢掉等于让调用方手里攥着一个之后再也找不到的会话。</item>
/// </list>
///
/// <b>线程约定</b>：与 runtime 一致，只在 UI 线程上访问，内部不加锁。
/// </summary>
public sealed class ConversationRuntimeRegistry
{
    /// <summary>全进程共用的一份（会话 id 在普通会话与工作空间会话之间是同一套空间）。</summary>
    public static ConversationRuntimeRegistry Shared { get; } = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private ConversationRuntimeRegistry() { }

    /// <summary>当前还活着的运行时个数（诊断 / 测试用）。</summary>
    public int Count => _entries.Count;

    /// <summary>当前还活着的会话 id。</summary>
    public IReadOnlyList<string> SessionIds => _entries.Keys.ToList();

    /// <summary>
    /// 新生成了一个运行时。插件的事件总线靠它「挂到每个会话上」——
    /// 不挂的话，插件只能看见自己借过的那几个会话。
    ///
    /// <b>在 UI 线程上、于 <see cref="Acquire"/> 返回租约之前发出</b>，
    /// 订阅方只应该做「转订阅」这类便宜的事。
    /// </summary>
    public event Action<ConversationRuntime>? RuntimeCreated;

    /// <summary>
    /// 一个运行时被回收了（引用归零且没有正在跑的轮次）。
    /// 订阅方必须在这里退订，否则它手里的委托会把那个运行时（连同它的一份历史）一直吊着。
    /// </summary>
    public event Action<ConversationRuntime>? RuntimeRemoved;

    /// <summary>
    /// 借出某个会话的运行时：有就复用，没有就用 <paramref name="profileFactory"/> 新建一份。
    ///
    /// <b>档案只在新建时创建</b>：复用来的运行时里面已经带着档案与全量历史，
    /// 再调一次工厂只会白建一个对象（还可能把工作空间信息换成新的）。
    /// </summary>
    /// <param name="profileFactory">仅在该会话还没有运行时调用一次。后续会话配置以档案里的 <c>LoadAsync</c> 为准。</param>
    public ConversationRuntimeLease Acquire(string sessionId, Func<IConversationProfile> profileFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(profileFactory);

        if (!_entries.TryGetValue(sessionId, out var entry))
        {
            entry = new Entry(sessionId, profileFactory, this);
            _entries[sessionId] = entry;

            // 先让订阅方挂上，再交出租约：否则「新建那一刻就开始跑」的第一轮会有事件漏掉
            RuntimeCreated?.Invoke(entry.Runtime);
        }

        entry.RefCount++;
        return new ConversationRuntimeLease(this, sessionId, entry.Runtime);
    }

    /// <summary>查一个会话的运行时；没有就返回 null（<b>不会</b>顺手建一个）。</summary>
    public ConversationRuntime? Find(string sessionId)
        => _entries.TryGetValue(sessionId, out var entry) ? entry.Runtime : null;

    internal void Release(string sessionId, ConversationRuntime runtime)
    {
        if (!_entries.TryGetValue(sessionId, out var entry)) return;

        // 换代过的（旧的已经丢掉、又建了新的）不要误减新那一个
        if (!ReferenceEquals(entry.Runtime, runtime)) return;

        if (entry.RefCount > 0) entry.RefCount--;
        entry.DropIfIdle();
    }

    private void Remove(string sessionId, ConversationRuntime runtime)
    {
        if (_entries.TryGetValue(sessionId, out var entry) && ReferenceEquals(entry.Runtime, runtime))
            _entries.Remove(sessionId);

        // 通知在字典删掉之后发：订阅方这时候应该已经拿不到它了，只剩退订这一件事可做
        RuntimeRemoved?.Invoke(runtime);
    }

    private sealed class Entry
    {
        private readonly ConversationRuntimeRegistry _owner;
        private readonly string _sessionId;

        public Entry(string sessionId, Func<IConversationProfile> profileFactory, ConversationRuntimeRegistry owner)
        {
            _sessionId = sessionId;
            _owner = owner;

            Runtime = new ConversationRuntime(profileFactory());

            // 订阅它自己的运行标记：引用归零时如果还在跑，就在跑完那一刻收掉自己
            Runtime.BusyChanged += OnBusyChanged;
        }

        public ConversationRuntime Runtime { get; }

        public int RefCount { get; set; }

        private void OnBusyChanged(bool busy)
        {
            if (!busy) DropIfIdle();
        }

        public void DropIfIdle()
        {
            if (RefCount > 0 || Runtime.IsBusy) return;

            // 退订后再丢：否则 runtime 上的事件委托会把这条 Entry（以及它引用的 runtime）一直吊着
            Runtime.BusyChanged -= OnBusyChanged;
            _owner.Remove(_sessionId, Runtime);
        }
    }
}
