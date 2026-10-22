using System;
using System.Collections.Generic;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Services.Plugins;

/// <summary>插件当前的运行状态。<b>这是「这一刻」的状态，不落库</b>（持久状态见 <see cref="PluginStateRecord"/>）。</summary>
public enum PluginRunState
{
    /// <summary>宿主总开关关闭，或尚未初始化。</summary>
    NotLoaded,

    /// <summary>用户禁用了它。</summary>
    Disabled,

    /// <summary>已加载并在运行。</summary>
    Loaded,

    /// <summary>加载或初始化失败（清单能读，但 dll 没跑起来）。</summary>
    Failed,

    /// <summary>清单本身不合法：JSON 坏了、id 与目录名不符、版本要求高于宿主、入口文件不存在。</summary>
    Blocked,

    /// <summary>上次疑似由它导致崩溃，按策略跳过加载。</summary>
    SkippedByCrash,

    /// <summary>
    /// 已禁用，但程序集没能卸载掉（还有残留引用）。
    /// 功能已经停止，只是内存没释放；文件也被占用，所以要更新/重新启用都得重启程序。
    /// </summary>
    NeedsRestart,

    /// <summary>
    /// 因为前置插件或互斥插件的关系没有运行（前置没装 / 被禁用 / 加载失败，或与正在运行的插件冲突）。
    ///
    /// <b>与 <see cref="Blocked"/> 分开</b>：Blocked 是「这个插件自己的清单有问题，作者要改」，
    /// 而这里插件本身没问题，是环境不满足 —— 用户看到的处置办法完全不同。
    /// </summary>
    DependencyBlocked,
}

/// <summary>
/// 给界面看的插件快照。刻意做成不可变记录：
/// 列表刷新时整份替换，界面不会拿着一个仍在变化的对象。
/// </summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Version,
    string? Description,
    string Directory,
    PluginRunState State,
    string? Error,
    IReadOnlyList<string> Capabilities,
    bool Enabled,
    bool AutoLoadAfterCrash,
    int CrashCount,
    bool CrashSuspected,
    string? CrashLogPath,
    /// <summary>最近一次往 system 提示词里贡献了多少字（0 = 最近一次没有贡献）。</summary>
    int ContributionChars)
{
    /// <summary>把 <see cref="State"/> 翻译成界面文案。</summary>
    public string StateText => State switch
    {
        PluginRunState.NotLoaded => "未加载",
        PluginRunState.Disabled => "已禁用",
        PluginRunState.Loaded => "运行中",
        PluginRunState.Failed => "加载失败",
        PluginRunState.Blocked => "无法加载",
        PluginRunState.SkippedByCrash => "因崩溃暂停加载",
        PluginRunState.NeedsRestart => "已禁用（需重启才能完全更新启用状态）",
        PluginRunState.DependencyBlocked => "因依赖未运行",
        _ => State.ToString(),
    };

    /// <summary>是否需要在界面上显示「需重启」提示。</summary>
    public bool RequiresRestart => State == PluginRunState.NeedsRestart;
}
