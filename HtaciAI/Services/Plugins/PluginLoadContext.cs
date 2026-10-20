using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 每个插件一份的可卸载加载上下文。
///
/// <b>为什么必须共享宿主与 Avalonia 的程序集</b>：不是为了省内存，是类型同一性。
/// 插件自己加载一份 <c>Avalonia.Base.dll</c>，那份里的 <c>Control</c> 与宿主可视树认的
/// <c>Control</c> 是两个不同 <c>Type</c>，把前者塞进后者的子集合会直接失败；
/// 而且第二份 Avalonia 的静态状态是独立的 —— 插件里 <c>Application.Current</c> 为 null、
/// 字体与主题资源全都取不到，症状极难定位。所以这类程序集一律解析到默认上下文。
///
/// <b>可回收（<c>isCollectible: true</c>）</b>：禁用插件时调用 <see cref="AssemblyLoadContext.Unload"/>，
/// 让这一整套程序集有机会被回收。能不能真的回收取决于有没有残留引用，
/// 见 <c>PluginManager.UnloadAsync</c> 的说明。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// 必须走默认上下文的程序集前缀 / 全名。判断方式两条：
    /// 一是明确列出的（UI 框架与宿主自身），二是「默认上下文里已经加载过同名程序集」——
    /// 后者能自动覆盖宿主引用的一切（Microsoft.Data.Sqlite 之类），
    /// 否则插件里会出现第二份，静态状态与类型身份都会分裂。
    /// </summary>
    private static readonly string[] AlwaysSharedPrefixes =
    {
        "Avalonia",
        "SkiaSharp",
        "HarfBuzzSharp",
        "HtaciAI.Plugin.Abstractions",
    };

    private static readonly string[] AlwaysSharedNames =
    {
        "HtaciAI",
        "HtaciAI.Shared",
    };

    private readonly string _directory;

    public PluginLoadContext(string pluginId, string directory)
        : base($"plugin:{pluginId}", isCollectible: true)
    {
        _directory = directory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)) return null;

        if (ShouldUseDefaultContext(name)) return null;

        // 插件自带的依赖：只从插件目录取，绝不回落到程序目录 ——
        // 回落到程序目录会让「插件想用某个库的特定版本」这件事失效。
        var candidate = Path.Combine(_directory, name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    private static bool ShouldUseDefaultContext(string name)
    {
        if (AlwaysSharedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        foreach (var prefix in AlwaysSharedPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        // 默认上下文里已经有的，一律共用。这个判断必须放在最后：
        // 前面两条是为了在「宿主还没用到那个程序集」时也保证共享。
        foreach (var loaded in Default.Assemblies)
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
