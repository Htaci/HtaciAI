using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 浏览器数据的<b>纯文件操作</b>：统计体积、删缓存、删登录数据、删整个 profile、删截图。
///
/// <b>为什么单独一个类</b>：这些操作全是磁盘 I/O，可能要几秒（这份 profile 实测 286MB）。
/// 它们不该混进 <see cref="BrowserManager"/> 的 UI 线程逻辑里；放在这里就可以整体丢到后台线程
/// （<c>Task.Run</c>）执行，界面只负责等待与显示结果。
///
/// <b>调用方必须先关掉所有浏览器窗口</b>：文件被浏览器进程占着时删除会失败 ——
/// 这里能做的只有重试与「跳过锁着的文件」，不能替用户关窗口。
/// </summary>
internal static class BrowserData
{
    /// <summary>缓存类子目录（相对 <c>webview-data\EBWebView</c>）。删掉不影响登录态。</summary>
    private static readonly string[] CacheFolders =
    {
        @"Default\Cache",
        @"Default\Code Cache",
        @"Default\GPUCache",
        @"Default\DawnWebGPUCache",
        @"Default\DawnGraphiteCache",
        @"Default\Service Worker\CacheStorage",
        "GrShaderCache",
        "ShaderCache",
        "GPUPersistentCache",
        "BrowserMetrics",
    };

    /// <summary>
    /// 登录态与自动填充（相对 <c>webview-data\EBWebView</c>）。
    /// <b>删这些等于退出所有网站</b>，界面必须说清楚。
    /// </summary>
    private static readonly string[] IdentityItems =
    {
        @"Default\Network\Cookies",
        @"Default\Network\Cookies-journal",
        @"Default\Network\Network Persistent State",
        @"Default\Local Storage",
        @"Default\Session Storage",
        @"Default\Login Data",
        @"Default\Login Data-journal",
        @"Default\Login Data For Account",
        @"Default\Login Data For Account-journal",
        @"Default\Web Data",
        @"Default\Web Data-journal",
        @"Default\Sessions",
        @"Default\Extension State",
    };

    /// <summary>体积概览。</summary>
    internal sealed record Usage(long ProfileBytes, long CacheBytes, long ShotBytes, int ShotCount, long HistoryBytes)
    {
        public long TotalBytes => ProfileBytes + ShotBytes + HistoryBytes;
    }

    /// <summary>一次清理的结果。失败条目不抛给用户，只汇总成一行。</summary>
    internal sealed record Cleanup(int Removed, int Failed, string? FirstError)
    {
        public bool Ok => Failed == 0;

        public string Describe(string folder)
            => Failed == 0
                ? $"已清理 {folder}（删除 {Removed} 项）"
                : $"清理 {folder}：删除 {Removed} 项，{Failed} 项被占用或无法删除" +
                  (FirstError is null ? "" : $"（{FirstError}）");
    }

    // ---- 统计 ----

    public static Usage Measure(string dataFolder, string shotsFolder)
    {
        var profile = Path.Combine(dataFolder, "EBWebView");

        var profileBytes = FolderSize(profile);
        var cacheBytes = CacheFolders.Sum(f => FolderSize(Path.Combine(profile, f)));

        var (shotBytes, shotCount) = FolderFiles(shotsFolder);
        var historyBytes = FileSize(Path.Combine(dataFolder, "history.json"));

        return new Usage(profileBytes, cacheBytes, shotBytes, shotCount, historyBytes);
    }

    // ---- 清理 ----

    /// <summary>删缓存。不动 Cookie 与登录数据。</summary>
    public static Cleanup ClearCache(string dataFolder)
    {
        var profile = Path.Combine(dataFolder, "EBWebView");
        var removed = 0;
        var failed = 0;
        string? firstError = null;

        foreach (var relative in CacheFolders)
        {
            DeletePath(Path.Combine(profile, relative), ref removed, ref failed, ref firstError);
        }

        return new Cleanup(removed, failed, firstError);
    }

    /// <summary>删 Cookie / 本地存储 / 保存的密码与表单。等同于「全部退出登录」。</summary>
    public static Cleanup ClearIdentity(string dataFolder)
    {
        var profile = Path.Combine(dataFolder, "EBWebView");
        var removed = 0;
        var failed = 0;
        string? firstError = null;

        foreach (var relative in IdentityItems)
        {
            DeletePath(Path.Combine(profile, relative), ref removed, ref failed, ref firstError);
        }

        return new Cleanup(removed, failed, firstError);
    }

    /// <summary>删整个 profile。<b>只在所有浏览器窗口都关了之后调。</b></summary>
    public static Cleanup ResetProfile(string dataFolder)
    {
        var profile = Path.Combine(dataFolder, "EBWebView");
        if (!Directory.Exists(profile)) return new Cleanup(0, 0, null);

        var removed = 0;
        var failed = 0;
        string? firstError = null;

        DeletePath(profile, ref removed, ref failed, ref firstError);
        return new Cleanup(removed, failed, firstError);
    }

    /// <summary>删截图（<c>web_screenshot</c> 的产物）。</summary>
    public static Cleanup ClearShots(string shotsFolder)
    {
        if (!Directory.Exists(shotsFolder)) return new Cleanup(0, 0, null);

        var removed = 0;
        var failed = 0;
        string? firstError = null;

        foreach (var file in Directory.EnumerateFiles(shotsFolder, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        return new Cleanup(removed, failed, firstError);
    }

    // ---- 文件工具 ----

    /// <summary>
    /// 删一个文件或目录，<b>带重试</b>。
    ///
    /// Chromium 关掉之后还会有一小段时间持有文件句柄（子进程退出是异步的），
    /// 一次删除失败就报错会让用户以为「清理坏了」。重试几次能吃掉绝大多数这种情况；
    /// 真删不掉的文件跳过并计数 —— 部分成功也比整批放弃好。
    /// </summary>
    private static void DeletePath(string path, ref int removed, ref int failed, ref string? firstError)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
                else
                {
                    return; // 本来就没有，不算失败
                }

                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    failed++;
                    firstError ??= ex.Message;
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }

    private static long FolderSize(string path)
    {
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += FileSize(file);
            }
        }
        catch (Exception)
        {
            // 统计只是给用户看个大概；遍历到一半被占用/权限拒绝不该让整次统计失败
        }

        return total;
    }

    private static (long Bytes, int Count) FolderFiles(string path)
    {
        if (!Directory.Exists(path)) return (0, 0);

        long total = 0;
        var count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                total += FileSize(file);
                count++;
            }
        }
        catch (Exception)
        {
            // 同上：统计失败不影响清理
        }

        return (total, count);
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>把字节数写成「286 MB」这种形式。设置页要给人看。</summary>
    public static string Describe(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1048576.0:0.#} MB";
        return $"{bytes / 1073741824.0:0.##} GB";
    }
}
