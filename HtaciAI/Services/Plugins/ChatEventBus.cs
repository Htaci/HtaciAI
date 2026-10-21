using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 会话事件总线：把 <see cref="ConversationRuntime"/> 的事件翻译成插件侧的
/// <see cref="ChatEvent"/>，**合并后**再投递。
///
/// <b>它不是一套新事件</b>，只是三件宿主自己的事：
/// <list type="number">
///   <item><b>挂到每个运行时上</b>：运行时由注册表按需创建，所以订阅
///         <see cref="ConversationRuntimeRegistry.RuntimeCreated"/>，不指望插件自己借过哪个会话；</item>
///   <item><b>合并与不阻塞</b>：流式增量按 50 ms 合并（<see cref="ChatEvent.Text"/> 是累积正文），
///         投递走有界队列，队列满了丢最旧的增量 —— 累积文本不丢，语义也就不丢；</item>
///   <item><b>异常隔离</b>：插件回调一律 try/catch，既不打断对话也不影响别的插件。</item>
/// </list>
///
/// <b>线程</b>：订阅/退订加锁；投递在后台线程上进行，与对话主流程完全解耦
/// （插件回调慢只会让它自己排后面，不会拖慢模型输出）。
/// </summary>
public sealed class ChatEventBus
{
    public static ChatEventBus Instance { get; } = new();

    /// <summary>流式增量的合并窗口。</summary>
    private const int CoalesceMs = 50;

    /// <summary>队列上限。超了丢最旧的增量事件（累积文本仍然完整）。</summary>
    private const int MaxQueued = 256;

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Dictionary<ConversationRuntime, RuntimeHook> _hooks = new();
    private readonly Queue<ChatEvent> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    private bool _attached;
    private bool _pumpStarted;
    private long _dropped;

    private ChatEventBus() { }

    /// <summary>被丢掉的增量事件数（诊断用）。</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    // ============================================================
    // 订阅
    // ============================================================

    /// <summary>
    /// 订阅会话事件。返回的 <see cref="IDisposable"/> 退订；同一实例重复 Dispose 安全。
    /// </summary>
    internal IDisposable Subscribe(string pluginId, IChatEventSink sink, ChatEventFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var subscription = new Subscription(pluginId, sink, filter ?? ChatEventFilter.All);

        lock (_gate)
        {
            _subscriptions.Add(subscription);

            if (!_attached)
            {
                _attached = true;
                ConversationRuntimeRegistry.Shared.RuntimeCreated += OnRuntimeCreated;
                ConversationRuntimeRegistry.Shared.RuntimeRemoved += OnRuntimeRemoved;

                // 插件可能在会话已经打开之后才被启用：把已经存在的运行时也挂上
                foreach (var sessionId in ConversationRuntimeRegistry.Shared.SessionIds)
                {
                    if (ConversationRuntimeRegistry.Shared.Find(sessionId) is { } runtime)
                        EnsureHook(runtime);
                }
            }

            StartPump();
        }

        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(subscription)) return;
            if (_subscriptions.Count > 0) return;

            // 没人听了就把挂载全部撤掉：不然所有新运行时都会为「一个订阅都没有」的状态转一遍事件
            _attached = false;
            ConversationRuntimeRegistry.Shared.RuntimeCreated -= OnRuntimeCreated;
            ConversationRuntimeRegistry.Shared.RuntimeRemoved -= OnRuntimeRemoved;

            foreach (var (runtime, hook) in _hooks.ToList())
            {
                hook.Detach();
                _hooks.Remove(runtime);
            }
        }
    }

    // ============================================================
    // 挂载到运行时
    // ============================================================

    private void OnRuntimeCreated(ConversationRuntime runtime)
    {
        lock (_gate)
        {
            if (!_attached) return;
            EnsureHook(runtime);
        }
    }

    private void OnRuntimeRemoved(ConversationRuntime runtime)
    {
        lock (_gate)
        {
            if (!_hooks.Remove(runtime, out var hook)) return;

            hook.Detach();

            // 这个会话的流式残留（正常情况下轮次结束时已经冲过）顺手清掉
            hook.Reset();
        }
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private void EnsureHook(ConversationRuntime runtime)
    {
        if (_hooks.ContainsKey(runtime)) return;

        var hook = new RuntimeHook(runtime, this);
        _hooks[runtime] = hook;

        runtime.RoundStarted += hook.OnRoundStarted;
        runtime.ContentDelta += hook.OnContentDelta;
        runtime.ThinkingDelta += hook.OnThinkingDelta;
        runtime.ToolCallDelta += hook.OnToolCallDelta;
        runtime.MessageInserted += hook.OnMessageInserted;
        runtime.MessageUpdated += hook.OnMessageUpdated;
        runtime.ActivityChanged += hook.OnActivityChanged;
        runtime.TurnEnded += hook.OnTurnEnded;
        runtime.BusyChanged += hook.OnBusyChanged;
    }

    // ============================================================
    // 队列与投递
    // ============================================================

    private void StartPump()
    {
        if (_pumpStarted) return;
        _pumpStarted = true;
        _ = Task.Run(PumpAsync);
    }

    /// <summary>入队。调用方可以在任意线程。队列满了优先丢最旧的增量事件。</summary>
    private void Enqueue(ChatEvent e)
    {
        lock (_gate)
        {
            if (_queue.Count >= MaxQueued) DropOldestMotionEvent();
            _queue.Enqueue(e);
        }

        _signal.Release();
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private void DropOldestMotionEvent()
    {
        var count = _queue.Count;
        for (var i = 0; i < count; i++)
        {
            var candidate = _queue.Dequeue();
            if (candidate.Kind is ChatEventKind.ContentDelta or ChatEventKind.ThinkingDelta)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            _queue.Enqueue(candidate);
        }

        // 一条增量都没有（全是关键事件）：只能丢最旧的那条，否则队列会一直涨
        _queue.Dequeue();
        Interlocked.Increment(ref _dropped);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            while (true)
            {
                ChatEvent item;
                Subscription[] targets;

                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                    targets = _subscriptions.ToArray();
                }

                foreach (var subscription in targets)
                {
                    if (!subscription.IsActive || !subscription.Filter.Matches(item)) continue;

                    try
                    {
                        await subscription.Sink.OnEventAsync(item, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error(subscription.PluginId, $"处理会话事件 {item.Kind} 时抛异常（已忽略）", ex);
                    }
                }
            }
        }
    }

    // ============================================================
    // 内部类型
    // ============================================================

    private sealed class Subscription(string pluginId, IChatEventSink sink, ChatEventFilter filter) : IDisposable
    {
        private ChatEventBus? _owner = ChatEventBus.Instance;

        public string PluginId { get; } = pluginId;
        public IChatEventSink Sink { get; } = sink;
        public ChatEventFilter Filter { get; } = filter;

        public bool IsActive => _owner is not null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;

            _owner = null;
            owner.Unsubscribe(this);
        }
    }

    /// <summary>
    /// 一个会话运行时的挂载点：转订阅、攒增量、冲缓冲。
    ///
    /// 增量按 **(运行时)** 攒而不是按订阅方攒：文本与是否有人听无关，
    /// 每个订阅方都应该拿到同一份「这一轮到目前为止的完整正文」。
    /// </summary>
    private sealed class RuntimeHook
    {
        private readonly ConversationRuntime _runtime;
        private readonly ChatEventBus _bus;
        private readonly object _lock = new();

        private readonly StringBuilder _content = new();
        private readonly StringBuilder _pendingContent = new();
        private readonly StringBuilder _thinking = new();
        private readonly StringBuilder _pendingThinking = new();
        private readonly HashSet<int> _announcedToolCalls = new();

        private Timer? _flushTimer;
        private bool _flushScheduled;

        public RuntimeHook(ConversationRuntime runtime, ChatEventBus bus)
        {
            _runtime = runtime;
            _bus = bus;
        }

        private string SessionId => _runtime.SessionId;

        public void OnRoundStarted(ChatMessage message)
        {
            // 新一轮开始前把累积清掉：上一轮的文本不能混进这一轮的「累积正文」
            Reset();

            _bus.Enqueue(new ChatEvent
            {
                Kind = ChatEventKind.RoundStarted,
                SessionId = SessionId,
                Role = message.Role,
                Timestamp = Now(),
            });
        }

        public void OnContentDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            lock (_lock)
            {
                _content.Append(delta);
                _pendingContent.Append(delta);
            }

            ScheduleFlush();
        }

        public void OnThinkingDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            lock (_lock)
            {
                _thinking.Append(delta);
                _pendingThinking.Append(delta);
            }

            ScheduleFlush();
        }

        public void OnToolCallDelta(ToolCallDelta delta)
        {
            // 只在「第一次看到这个 index 且拿到了工具名」时通知一次：
            // 参数分片会来很多次，每次都说一句「开始调工具」没有意义
            if (string.IsNullOrEmpty(delta.Name)) return;

            lock (_lock)
            {
                if (!_announcedToolCalls.Add(delta.Index)) return;
            }

            _bus.Enqueue(new ChatEvent
            {
                Kind = ChatEventKind.ToolCallStarted,
                SessionId = SessionId,
                ToolName = delta.Name,
                Timestamp = Now(),
            });
        }

        public void OnMessageInserted(ChatMessage message) => PublishMessage(message);

        public void OnMessageUpdated(ChatMessage message) => PublishMessage(message);

        private void PublishMessage(ChatMessage message)
        {
            _bus.Enqueue(new ChatEvent
            {
                Kind = ChatEventKind.MessagePersisted,
                SessionId = SessionId,
                Role = message.Role,
                Text = message.Content,
                Status = message.Status,
                SequenceNumber = message.SequenceNumber,
                Timestamp = Now(),
            });
        }

        public void OnActivityChanged(SessionActivity activity)
        {
            _bus.Enqueue(new ChatEvent
            {
                Kind = ChatEventKind.ActivityChanged,
                SessionId = SessionId,
                Status = activity.ToString(),
                Timestamp = Now(),
            });
        }

        public void OnBusyChanged(bool busy)
        {
            _bus.Enqueue(new ChatEvent
            {
                Kind = ChatEventKind.ActivityChanged,
                SessionId = SessionId,
                Status = busy ? "Running" : "Idle",
                Timestamp = Now(),
            });
        }

        /// <summary>
        /// 一轮结束：<b>先把攒着的增量冲干净，再发结束事件</b> ——
        /// 反过来的话插件会先收到「完成了」，然后才收到最后几个字的增量。
        /// 顺序在这里是同步保证的（同一个方法里先冲后发），不依赖定时器。
        /// </summary>
        public Task OnTurnEnded(TurnOutcome outcome)
        {
            // 自动话题命名也会走一次 TurnEnded（只带 TitleChanged），那不是一个轮次的结束：
            // 把它当成完成事件发出去，插件会看到「这一轮完成了」两次。
            if (outcome.TitleChanged) return Task.CompletedTask;

            FlushPending();

            var success = outcome.Result?.Success == true;
            _bus.Enqueue(new ChatEvent
            {
                Kind = success ? ChatEventKind.RoundCompleted : ChatEventKind.RoundFailed,
                SessionId = SessionId,
                Success = success,
                Text = outcome.Result?.Error
                       ?? (outcome.StoppedByUser ? "这一轮被停止了。" : null),
                Status = outcome.StoppedByUser ? "stopped" : success ? "completed" : "failed",
                Timestamp = Now(),
            });

            // 这一轮的内容已经交出，清掉累积，免得下一轮的事件里带上上一轮的尾巴
            Reset();

            return Task.CompletedTask;
        }

        /// <summary>清空累积状态（新一轮开始、运行时被回收时调）。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _content.Clear();
                _thinking.Clear();
                _pendingContent.Clear();
                _pendingThinking.Clear();
                _announcedToolCalls.Clear();
            }
        }

        private void ScheduleFlush()
        {
            lock (_lock)
            {
                if (_flushScheduled) return;
                _flushScheduled = true;
            }

            // 定时器而不是「让泵每 50 ms 醒一次」：空闲的时候一个 tick 都不该有
            _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(CoalesceMs, Timeout.Infinite);
        }

        private void FlushPending()
        {
            string? content = null, delta = null, thinking = null;

            lock (_lock)
            {
                _flushScheduled = false;

                if (_pendingContent.Length > 0)
                {
                    content = _content.ToString();
                    delta = _pendingContent.ToString();
                    _pendingContent.Clear();
                }

                if (_pendingThinking.Length > 0)
                {
                    thinking = _thinking.ToString();
                    _pendingThinking.Clear();
                }
            }

            if (delta is not null)
            {
                _bus.Enqueue(new ChatEvent
                {
                    Kind = ChatEventKind.ContentDelta,
                    SessionId = SessionId,
                    Text = content,
                    Delta = delta,
                    Timestamp = Now(),
                });
            }

            if (thinking is not null)
            {
                _bus.Enqueue(new ChatEvent
                {
                    Kind = ChatEventKind.ThinkingDelta,
                    SessionId = SessionId,
                    Thinking = thinking,
                    Timestamp = Now(),
                });
            }
        }

        public void Detach()
        {
            _runtime.RoundStarted -= OnRoundStarted;
            _runtime.ContentDelta -= OnContentDelta;
            _runtime.ThinkingDelta -= OnThinkingDelta;
            _runtime.ToolCallDelta -= OnToolCallDelta;
            _runtime.MessageInserted -= OnMessageInserted;
            _runtime.MessageUpdated -= OnMessageUpdated;
            _runtime.ActivityChanged -= OnActivityChanged;
            _runtime.TurnEnded -= OnTurnEnded;
            _runtime.BusyChanged -= OnBusyChanged;

            lock (_lock)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                _flushScheduled = false;
            }
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// <see cref="IChatEventBus"/> 的宿主实现：一个插件一个实例。
/// 订阅同时登记进 <see cref="PluginEntry.Subscriptions"/>，禁用插件时由宿主统一退订。
/// </summary>
public sealed class PluginChatFacade(string pluginId, List<IDisposable> subscriptions) : IChatEventBus
{
    public IDisposable Subscribe(IChatEventSink sink, ChatEventFilter? filter = null)
    {
        var subscription = ChatEventBus.Instance.Subscribe(pluginId, sink, filter);
        subscriptions.Add(subscription);
        return subscription;
    }
}
