using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 全进程的界面扩展点登记表：哪些插件往哪个插槽插了什么、新标签页该多出哪几张卡片。
///
/// <b>为什么是「登记 + 视图来取」而不是「宿主直接把控件塞进去」</b>：
/// 视图会被重建（会话窗口被缓存淘汰后重进、标签页关掉重开），塞进去的控件跟着旧视图一起没了。
/// 登记的是 <see cref="Func{TResult}"/>（工厂），视图每次重建都重新要一份 ——
/// 代价是工厂会被调用多次，这一点在契约里写明了。
///
/// <b>线程</b>：只在 UI 线程上访问（插控件、刷界面都在 UI 线程）。
/// </summary>
public sealed class PluginUiRegistry
{
    public static PluginUiRegistry Instance { get; } = new();

    private readonly Dictionary<string, List<SlotEntry>> _slots = new(StringComparer.Ordinal);
    private readonly List<CardEntry> _cards = new();

    /// <summary>注册顺序，用来在 <see cref="SlotEntry.Order"/> 相同时保持稳定顺序。</summary>
    private long _sequence;

    private PluginUiRegistry() { }

    /// <summary>登记表变了（插件增删控件 / 卡片、插件被禁用）。视图据此重建插槽内容。</summary>
    public event Action? Changed;

    /// <summary>某个插槽里当前有哪些控件的工厂，已按 Order 排好。</summary>
    public IReadOnlyList<SlotEntry> GetSlot(string slotId)
    {
        if (!_slots.TryGetValue(slotId, out var entries)) return Array.Empty<SlotEntry>();

        return entries
            .OrderBy(e => e.Order)
            .ThenBy(e => e.Sequence)
            .ToList();
    }

    /// <summary>新标签页要显示的插件卡片，已按注册顺序排好。</summary>
    public IReadOnlyList<CardEntry> GetCards() => _cards.ToList();

    // ============================================================
    // 登记 / 撤销
    // ============================================================

    internal IDisposable RegisterControl(string pluginId, string slotId, Func<Control> factory, int order)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // 插槽 id 拼错时只记警告、不抛：抛出去会让整个插件加载失败，
        // 而「一个插槽名写错」远不该有这么大的代价。代价是它不会显示 —— 日志里有名字可查。
        if (!PluginSlots.All.Contains(slotId, StringComparer.Ordinal))
        {
            PluginLog.Warn(pluginId, $"未知的插槽 id：{slotId}（可用：{string.Join(", ", PluginSlots.All)}）");
            return NoOpDisposable.Instance;
        }

        var entry = new SlotEntry(pluginId, slotId, factory, order, ++_sequence);

        if (!_slots.TryGetValue(slotId, out var list))
        {
            list = new List<SlotEntry>();
            _slots[slotId] = list;
        }

        list.Add(entry);
        RaiseChanged();

        return new Registration(() => RemoveControl(entry));
    }

    internal IDisposable RegisterCard(string pluginId, LauncherCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var entry = new CardEntry(pluginId, card);

        // 新标签页把插件卡片排在前面（内置卡片先建、插件卡片追加在后面，见 NewTabPage）
        _cards.Add(entry);
        RaiseChanged();

        return new Registration(() => RemoveCard(entry));
    }

    /// <summary>插件的全部门面都被撤销时调用。禁用插件走这里，一次清干净。</summary>
    internal void RemovePlugin(string pluginId)
    {
        var removed = false;

        foreach (var (_, list) in _slots.ToList())
        {
            if (list.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.Ordinal)) > 0)
                removed = true;
        }

        if (_cards.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.Ordinal)) > 0)
            removed = true;

        if (removed) RaiseChanged();
    }

    private void RemoveControl(SlotEntry entry)
    {
        if (!_slots.TryGetValue(entry.SlotId, out var list)) return;
        if (!list.Remove(entry)) return;

        RaiseChanged();
    }

    private void RemoveCard(CardEntry entry)
    {
        if (!_cards.Remove(entry)) return;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // ============================================================
    // 内部类型
    // ============================================================

    /// <summary>插槽里的一项：谁插的、插在哪、怎么建。</summary>
    public sealed record SlotEntry(string PluginId, string SlotId, Func<Control> Factory, int Order, long Sequence);

    /// <summary>新标签页上的一张插件卡片。</summary>
    public sealed record CardEntry(string PluginId, LauncherCard Card);

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            var action = _remove;
            if (action is null) return;

            _remove = null;
            action();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}

/// <summary>
/// <see cref="IUiExtensionRegistry"/> 的宿主实现：一个插件一个实例。
///
/// 它只是把调用转给 <see cref="PluginUiRegistry"/>，并<b>把所有登记收进自己手里</b> ——
/// <see cref="Dispose"/> 时按插件 id 一次性清掉，插件忘了撤销也不留残骸。
/// </summary>
public sealed class PluginUiFacade(string pluginId) : IUiExtensionRegistry, IDisposable
{
    private bool _disposed;

    public IDisposable AddControl(string slotId, Func<Control> factory, int order = 0)
    {
        if (_disposed) return new Ignored();
        return PluginUiRegistry.Instance.RegisterControl(pluginId, slotId, factory, order);
    }

    public IDisposable AddLauncherCard(LauncherCard card)
    {
        if (_disposed) return new Ignored();
        return PluginUiRegistry.Instance.RegisterCard(pluginId, card);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PluginUiRegistry.Instance.RemovePlugin(pluginId);
    }

    private sealed class Ignored : IDisposable
    {
        public void Dispose() { }
    }
}
