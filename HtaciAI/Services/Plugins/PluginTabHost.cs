using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Views;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// <see cref="IPluginTabHost"/> 的宿主实现：一个插件一个实例。
///
/// <b>为什么不让插件直接摸 <see cref="MainWindow"/> 的标签页列表</b>：
/// 那几个列表（内容、头部、标题块）是按<em>下标</em>对齐的平行数组，
/// 关掉一个标签后要重排下标再重选。让插件参与这套下标维护，等于把宿主内部约定
/// 变成公开契约 —— 宿主一换标签栏实现，所有插件一起坏。所以只暴露
/// 「改标题 / 切过去 / 关掉」这三件事。
///
/// <b>归属</b>：句柄都收在 <see cref="_tabs"/> 里，插件被禁用时 <see cref="Dispose"/>
/// 一次性关掉 —— 关标签页必须发生在 <c>AssemblyLoadContext.Unload()</c> 之前，
/// 否则标签还在、画它的代码已经没了。
///
/// <b>线程</b>：只在 UI 线程上使用。
/// </summary>
internal sealed class PluginTabHost(string pluginId) : IPluginTabHost, IDisposable
{
    private readonly List<MainWindow.PluginTabHandle> _tabs = new();
    private bool _disposed;

    public IReadOnlyList<IPluginTab> OpenTabs => _tabs.Cast<IPluginTab>().ToList();

    public IPluginTab OpenTab(string title, Control content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (_disposed)
            throw new ObjectDisposedException(nameof(PluginTabHost), "插件已停用，不能再开标签页。");

        var window = ResolveMainWindow()
            ?? throw new InvalidOperationException("主窗口还没建好，现在开不了标签页。");

        var handle = window.AddPluginTab(
            string.IsNullOrWhiteSpace(title) ? pluginId : title,
            content);

        // 用户点 × 也要从自己的表里摘掉，否则插件会一直以为那个标签还开着
        handle.Closed += () => _tabs.Remove(handle);

        _tabs.Add(handle);
        return handle;
    }

    /// <summary>
    /// 主窗口从应用生命周期上取，而不是让插件传进来：插件只应该拿到
    /// 「自己的标签页」，不该拿到整个主窗口的引用（那等于把整棵可视树交出去）。
    /// </summary>
    private static MainWindow? ResolveMainWindow()
    {
        if (Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.MainWindow as MainWindow;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var tab in _tabs.ToList())
        {
            try { tab.Close(); }
            catch (Exception ex) { PluginLog.Warn(pluginId, "关闭插件标签页失败：" + ex.Message); }
        }

        _tabs.Clear();
    }
}
