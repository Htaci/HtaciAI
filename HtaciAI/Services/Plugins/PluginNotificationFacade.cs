using System;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// <see cref="IPluginNotifications"/> 的宿主实现：把插件的声明写进
/// <see cref="PluginNoticePolicyStore"/>，弹提醒时由 <c>SessionNotifier</c> 读。
///
/// 它自己不弹任何东西 —— 提醒仍然由宿主在轮次收尾时统一发出，
/// 插件只决定「那条提醒留多久、要不要弹」。
/// </summary>
internal sealed class PluginNotificationFacade(string pluginId) : IPluginNotifications, IDisposable
{
    /// <summary>缓存一份，读属性不必每次都查表（表是线程安全的，这里只是省一次字典查找）。</summary>
    private PluginNoticeMode _mode = PluginNoticeMode.Auto;

    public PluginNoticeMode Mode => _mode;

    public void SetMode(PluginNoticeMode mode)
    {
        // 不认识的枚举值（插件那边用了更高的版本）按默认处理：宁可弹一条会自己收起的提醒，
        // 也不要让一个非法的 int 把提醒永久留在屏幕上。
        if (!Enum.IsDefined(mode)) mode = PluginNoticeMode.Auto;

        _mode = mode;
        PluginNoticePolicyStore.Set(pluginId, mode);

        PluginLog.Info(pluginId, $"任务提醒策略改为 {mode}");
    }

    /// <summary>禁用 / 卸载插件时调用：把它的策略清掉，免得下次加载前的那段时间还按旧策略弹。</summary>
    public void Dispose()
    {
        PluginNoticePolicyStore.Clear(pluginId);
        _mode = PluginNoticeMode.Auto;
    }
}
