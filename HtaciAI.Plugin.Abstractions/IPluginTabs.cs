using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 在宿主主窗口里开自己的标签页（需要声明 <see cref="PluginCapabilities.UiTab"/>）。
///
/// <b>与 <see cref="IPluginWindowHost"/> 的分工</b>：
/// 桌宠、悬浮球那类「属于桌面而不属于工作区」的东西用独立窗口；
/// 浏览器、编辑器、看板那类「用户会来回切的工作区」用标签页 —— 它们要和会话标签并排，
/// 共享同一个窗口尺寸与切换习惯，开成独立窗口反而多一层 Alt+Tab。
///
/// <b>必须在 UI 线程调用。</b>
/// </summary>
public interface IPluginTabHost
{
    /// <summary>
    /// 开一个标签页，返回的句柄可以改标题、切到它、关掉它。
    /// <b>不要求标签页彼此独立</b>：同一个插件可以开多个，宿主按「插件 id + 句柄」记归属。
    /// </summary>
    /// <param name="title">标签页标题，之后可以用 <see cref="IPluginTab.SetTitle"/> 改。</param>
    /// <param name="content">标签页内容。<b>同一个 Control 实例不能同时挂在两个标签页上</b>。</param>
    IPluginTab OpenTab(string title, Control content);

    /// <summary>当前还开着、属于本插件的标签页。只读快照。</summary>
    IReadOnlyList<IPluginTab> OpenTabs { get; }
}

/// <summary>
/// 一个已经打开的标签页。插件被禁用时宿主会统一关掉它们，不需要自己记。
/// </summary>
public interface IPluginTab
{
    /// <summary>打开时给的标题。</summary>
    string Title { get; }

    /// <summary>改标题（标签栏上的文字会立刻更新）。</summary>
    void SetTitle(string title);

    /// <summary>切到这个标签页（等价于用户点了一下它）。</summary>
    void Activate();

    /// <summary>关掉它。重复调用无副作用。</summary>
    void Close();

    /// <summary>标签页被关掉时触发（用户点 × 也算）。<b>在 UI 线程上触发。</b></summary>
    event Action? Closed;
}
