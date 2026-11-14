using System;
using System.Collections.Generic;
using System.IO;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 宿主侧的插件持久状态（<c>&lt;数据根&gt;/plugins/state.json</c>）。
///
/// <b>必须由宿主管、不能放插件目录或插件私有存储里</b>：崩溃后要读它来决定「这次不加载谁」，
/// 而那一刻插件代码一行都还没跑起来。
/// </summary>
public sealed class PluginStateFile
{
    public List<PluginStateRecord> Plugins { get; set; } = new();
}

/// <summary>单个插件的持久状态。字段全部是「宿主对用户的承诺」，不是插件能自己改的东西。</summary>
public sealed class PluginStateRecord
{
    public string Id { get; set; } = "";

    /// <summary>用户是否启用。首次发现的插件默认启用（是否真的加载还受总开关与崩溃策略约束）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 用户是否允许「崩溃后仍自动加载」。默认 <c>false</c>：
    /// 崩溃一次就停用，避免用户陷入「一启动就崩、连设置页都进不去」的循环。
    /// </summary>
    public bool AutoLoadAfterCrash { get; set; }

    /// <summary>疑似导致崩溃的次数。</summary>
    public int CrashCount { get; set; }

    /// <summary>最近一次疑似崩溃的时间（Unix 毫秒）。</summary>
    public long? LastCrashAt { get; set; }

    /// <summary>最近一次崩溃日志的路径，供列表里的「查看崩溃日志」直接打开。</summary>
    public string? LastCrashLog { get; set; }

    /// <summary>当前是否处于「疑似崩溃源」状态：为 true 且未开 <see cref="AutoLoadAfterCrash"/> 时跳过加载。</summary>
    public bool CrashSuspected { get; set; }

    /// <summary>
    /// 用户设定的<b>加载顺序</b>（小的先加载、列表里也排在前面）。<c>null</c> = 还没排过，
    /// 首次扫描时按目录名补一个。
    ///
    /// <b>为什么它归宿主而不是插件自己写清单里</b>：这是「用户想怎么排」，不是「插件是什么」——
    /// 换台机器、换个人用，顺序本来就该各自不同；写进 plugin.json 等于把用户的偏好钉死在插件里。
    /// </summary>
    public int? Order { get; set; }

    /// <summary>
    /// 最近一次 <b>成功加载</b> 时算出的插件文件指纹（见 <see cref="PluginFingerprint"/>）。
    ///
    /// <b>必须落盘</b>：它回答的是「内存里跑的那一份是什么」，跨进程才有意义 ——
    /// 放在内存里的话，重启后每个插件都会被误报成「文件已更新」。
    /// </summary>
    public string? LoadedHash { get; set; }
}

/// <summary>
/// 插件状态文件的读写。读失败一律当成「空状态」，绝不因为配置文件坏了让程序起不来。
/// </summary>
public static class PluginStateStore
{
    private static readonly object Gate = new();

    private static PluginStateFile? _current;

    public static string FilePath => Path.Combine(AppPaths.Plugins, "state.json");

    /// <summary>当前状态（首次访问时从磁盘加载并缓存）。</summary>
    public static PluginStateFile Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= JsonFileStore.Load<PluginStateFile>(FilePath) ?? new PluginStateFile();
            }
        }
    }

    /// <summary>取某插件的记录，没有就建一条（默认启用）。</summary>
    public static PluginStateRecord GetOrCreate(string id)
    {
        lock (Gate)
        {
            var existing = Current.Plugins.Find(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (existing is not null) return existing;

            var record = new PluginStateRecord { Id = id };
            Current.Plugins.Add(record);
            Save();
            return record;
        }
    }

    /// <summary>记一次疑似崩溃。</summary>
    public static void MarkCrash(string id, string? crashLogPath)
    {
        lock (Gate)
        {
            var record = GetOrCreate(id);
            record.CrashCount++;
            record.CrashSuspected = true;
            record.LastCrashAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrEmpty(crashLogPath)) record.LastCrashLog = crashLogPath;
            Save();
        }
    }

    /// <summary>把一串插件 id 的当前顺序整体写回（列表里调整顺序后调）。</summary>
    public static void SaveOrder(IReadOnlyList<string> orderedIds)
    {
        lock (Gate)
        {
            for (var i = 0; i < orderedIds.Count; i++)
                GetOrCreate(orderedIds[i]).Order = i;

            Save();
        }
    }

    /// <summary>用户确认「继续启用」后清掉疑似标记（崩溃次数保留，便于日后统计）。</summary>
    public static void ClearSuspect(string id)
    {
        lock (Gate)
        {
            var record = GetOrCreate(id);
            record.CrashSuspected = false;
            Save();
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Plugins);
            }
            catch
            {
                // 目录建不出来时 JsonFileStore 自己也会失败，交给它
            }

            JsonFileStore.Save(FilePath, Current);
        }
    }

    /// <summary>丢弃缓存，下次访问重新读盘。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _current = null;
        }
    }
}
