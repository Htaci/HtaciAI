using System;
using System.Collections.Concurrent;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 各插件声明的提醒策略（<see cref="PluginNoticeMode"/>）。
///
/// <b>为什么存在这张表</b>：弹出提醒的地方（<c>ConversationRuntime</c>）只知道会话 id，
/// 不知道「这一轮是哪个插件派的」；而策略是插件级、声明式的，不该跟着每一轮请求传下去。
/// 于是插件经 <see cref="PluginNotificationFacade"/> 写在这里，
/// <c>SessionNotifier</c> 按插件 id 读。
///
/// <b>只在内存里</b>：这是「这次运行想怎么提醒」，插件重启后会自己重新声明（见接口说明）。
/// 没有登记过的插件（或已卸载的）一律按默认 <see cref="PluginNoticeMode.Auto"/>。
/// </summary>
internal static class PluginNoticePolicyStore
{
    private static readonly ConcurrentDictionary<string, PluginNoticeMode> Modes = new(StringComparer.Ordinal);

    /// <summary>读某个插件的策略。没登记过 → <see cref="PluginNoticeMode.Auto"/>。</summary>
    public static PluginNoticeMode Get(string? pluginId)
        => pluginId is not null && Modes.TryGetValue(pluginId, out var mode)
            ? mode
            : PluginNoticeMode.Auto;

    /// <summary>写入策略。<see cref="PluginNoticeMode.Auto"/> 就是默认值，直接抹掉那一行。</summary>
    public static void Set(string pluginId, PluginNoticeMode mode)
    {
        if (string.IsNullOrEmpty(pluginId)) return;

        if (mode == PluginNoticeMode.Auto) Modes.TryRemove(pluginId, out _);
        else Modes[pluginId] = mode;
    }

    /// <summary>插件卸载时清掉：下次加载按它自己的声明重新来。</summary>
    public static void Clear(string pluginId) => Modes.TryRemove(pluginId, out _);
}
