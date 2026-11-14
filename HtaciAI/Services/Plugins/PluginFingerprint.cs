using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件目录的「指纹」：入口程序集与清单的内容哈希。
///
/// <b>为什么需要它</b>：插件文件只是被读进内存加载的（见 <see cref="PluginLoadContext"/>），
/// 覆盖 dll 不会被系统拦住 —— 于是会出现「文件已经是新的、跑在内存里的还是旧的」这种状态，
/// 而用户看到的现象是「我更新了插件，但它没变」。宿主没有任何信号能察觉这件事，
/// 所以在加载成功时记下指纹，重新扫描时再算一遍，两边不一致就说明该重新加载了。
///
/// <b>只算顶层 <c>*.dll</c> 与 <c>plugin.json</c></b>：这三个东西才是「插件是什么」；
/// 日志、数据、README 改了不该让人以为要重载。总量超过上限就放弃（返回 null）——
/// 重新扫描是进入插件页就会做的事，不能变成一次大文件全读。
/// </summary>
public static class PluginFingerprint
{
    /// <summary>参与哈希的总字节上限。</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 算目录指纹。<b>算不出来时返回 null</b>（目录没了、文件被独占、超过大小上限），
    /// 调用方据此跳过比对 —— 宁可不提示更新，也不要因为一次读失败就误报「有更新」。
    /// </summary>
    public static string? Compute(string directory, string pluginId)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            var files = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsPluginContent)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0) return null;

            var total = files.Sum(f => new FileInfo(f).Length);
            if (total > MaxBytes) return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];

            foreach (var file in files)
            {
                // 文件名也进哈希：不然「加一个 dll」和「换一个同名 dll」会被算成同一份内容
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file).ToLowerInvariant() + "\n"));

                // 允许别人同时写：插件更新时文件可能正在被覆盖，此刻算出的是一个中间态，
                // 结果只是「和记录不一致」→ 提示重新加载，不会造成别的后果。
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex)
        {
            PluginLog.Warn(pluginId, "计算插件文件指纹失败（本次不比对更新）：" + ex.Message);
            return null;
        }
    }

    private static bool IsPluginContent(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 目录里有没有文件正被别的进程（或本次进程里的原生库）占着，返回第一个被占的文件名。
    ///
    /// <b>为什么需要它</b>：托管的 dll 都是读成字节流加载的（见 <see cref="PluginLoadContext"/>），
    /// 运行中随时可覆盖；<b>原生 dll 不行</b> —— 插件调 <c>NativeLibrary.Load</c> 之后，
    /// 那个文件会被映射成映像节并一直持有句柄，用户复制新版时会看到「文件正在使用」，
    /// 却不知道是哪一个。这里替他找出来，界面才有话可说。
    ///
    /// 只探测顶层 <c>*.dll</c>；用独占方式打开，失败即视为被占用（只读文件不算：
    /// 只请求 Read 权限，不要求可写）。
    /// </summary>
    public static string? FindLockedFile(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException)
                {
                    return Path.GetFileName(file);
                }
                catch (UnauthorizedAccessException)
                {
                    // 权限不足不是「被占用」，不报给用户
                }
            }
        }
        catch
        {
            // 目录读不了就当作没有占用信息，不影响别的流程
        }

        return null;
    }
}
