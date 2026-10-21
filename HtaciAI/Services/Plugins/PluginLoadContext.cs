using System;
using System.Collections.Generic;
using System.IO;
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
        return File.Exists(candidate) ? ReadIntoMemory(candidate) : null;
    }

    /// <summary>
    /// 把程序集读成字节流再加载，<b>不用 <c>LoadFromAssemblyPath</c></b>。
    ///
    /// 那个 API 在 Windows 上会把 dll 映射成映像节（image section），映射一旦建立，
    /// 文件就一直不能以写方式打开 —— 即使 ALC 已经卸载、引用也断干净了。
    /// 实测：`LoadFromAssemblyPath` 加载过的插件，禁用后 10 轮 GC 之后文件依旧不可写，
    /// 用户没法更新插件、也没法删除它。走流加载则只读一次文件就关闭句柄，
    /// 卸载后文件立刻可以替换。
    ///
    /// 代价是 <see cref="Assembly.Location"/> 为空（依赖解析因此只能靠这里的手工查找，
    /// 不能用 <c>AssemblyDependencyResolver</c>），且原生 dll 需要插件自己处理。
    /// 对插件这种「自带依赖放在同目录」的形态来说，这两条都可以接受。
    /// </summary>
    private Assembly ReadIntoMemory(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // 有 pdb 就一起读进去，异常栈里才有行号；没有就只加载本体。
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdbPath))
            return LoadFromStream(new MemoryStream(bytes), new MemoryStream(File.ReadAllBytes(pdbPath)));

        return LoadFromStream(new MemoryStream(bytes));
    }

    /// <summary>加载入口程序集（<c>plugin.json</c> 里 entry 指定的那个）。</summary>
    public Assembly LoadPluginAssembly(string path) => ReadIntoMemory(path);

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
