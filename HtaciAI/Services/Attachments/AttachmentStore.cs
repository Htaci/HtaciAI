using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Attachments;

/// <summary>
/// 附件落盘与分类。
///  - 剪贴板里的位图没有路径，落到缓存目录变成一个真文件（按内容哈希命名，同一张图重复粘贴不会重复写）；
///  - 上传 / 粘贴的文件本来就有路径，直接用原路径。
/// chip 上显示什么文字由 <see cref="LabelFor"/> 按扩展名推导。
/// </summary>
public static class AttachmentStore
{
    /// <summary>缓存目录：<c>&lt;数据根&gt;/attachments</c>，见 <see cref="AppPaths"/>。</summary>
    public static string CacheRoot => AppPaths.Attachments;

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff", ".svg",
    };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".log",
        ".ini", ".toml", ".env", ".conf", ".cfg",
        ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".go", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".csproj", ".sln", ".props", ".targets",
        ".css", ".scss", ".less", ".html", ".htm", ".vue", ".sql", ".sh", ".ps1", ".bat", ".cmd",
    };

    private static readonly Dictionary<string, string> OfficeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "Word", [".docx"] = "Word", [".docm"] = "Word", [".rtf"] = "Word",
        [".xls"] = "Excel", [".xlsx"] = "Excel", [".xlsm"] = "Excel", [".xlsb"] = "Excel",
        [".ppt"] = "PPT", [".pptx"] = "PPT", [".pptm"] = "PPT", [".ppsx"] = "PPT", [".ppsm"] = "PPT",
    };

    /// <summary>
    /// 把剪贴板里的位图字节落成缓存文件并返回附件。
    /// 文件名取内容哈希：同一张图反复粘贴只占一份，且同一份字节每次编码出的 base64 完全一致，
    /// 这对上下文缓存命中是前提条件。
    /// </summary>
    public static async Task<ChatAttachment?> SaveClipboardImageAsync(byte[] bytes, string extension = ".png")
    {
        if (bytes.Length == 0) return null;
        try
        {
            Directory.CreateDirectory(CacheRoot);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
            var path = Path.Combine(CacheRoot, hash + extension);
            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, bytes);
            return FromPath(path);
        }
        catch
        {
            return null;
        }
    }

    // ── 助手回复里的「可点击文件卡片」 ──────────────────────────────────────
    //
    // AI 输出的路径可能是文件、也可能是目录，而且可能根本不存在（模型凭印象编的）。
    // 这两个判断都要碰磁盘，而渲染层在长会话里会问上几百次 —— 所以按路径缓存一次结果。
    //
    // 缓存不设过期：路径的种类（文件 / 目录）在会话期间不会变，真变了也只是卡片文字旧一点，
    // 而「每次渲染都 stat 一遍磁盘」的代价是实打实的。

    /// <summary>缓存值：空串 = 不存在；<c>folder</c> = 目录；其余是 <see cref="KindFor"/> 的结果。</summary>
    private static readonly ConcurrentDictionary<string, string> DisplayKindCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这些后缀点下去会「运行」而不是「打开」，卡片因此只做定位，不直接执行。</summary>
    private static readonly HashSet<string> ExecutableExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".msi", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".lnk", ".url", ".reg", ".hta", ".cpl", ".msc", ".jar",
    };

    /// <summary>
    /// 把一个「正文里出现的路径」描述成可点击卡片；路径不存在时返回 <c>null</c>，
    /// 调用方据此退回普通文本。
    ///
    /// <b>为什么要校验存在性</b>：助手回复里的路径是模型写的，可能压根不存在。
    /// 给一个点下去什么都没有的卡片，比直接显示路径更让人困惑。
    /// （用户气泡里的附件不走这里 —— 那些是用户真选过的文件，删了也该照常显示。）
    /// </summary>
    public static ChatAttachment? DescribeForDisplay(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"', '\'')); }
        catch { return null; }

        var kind = DisplayKindCache.GetOrAdd(full, Classify);

        return kind.Length == 0
            ? null
            : new ChatAttachment
            {
                Path = full,
                Kind = kind,
                Label = kind == "folder" ? "文件夹" : LabelFor(full),
            };

        static string Classify(string candidate)
        {
            try
            {
                if (Directory.Exists(candidate)) return "folder";
                if (File.Exists(candidate)) return KindFor(candidate);
            }
            catch
            {
                // 无权访问 / 路径过长等：当作不存在
            }

            return "";
        }
    }

    /// <summary>点这个路径会运行程序而不是打开文档。</summary>
    public static bool IsExecutable(string path)
        => ExecutableExts.Contains(Path.GetExtension(path));

    /// <summary>
    /// 打开一个路径：目录 → 资源管理器；普通文件 → 系统默认程序；
    /// <b>可执行文件只做定位</b>（见 <see cref="IsExecutable"/>）。
    /// </summary>
    public static void OpenOrReveal(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

                return;
            }

            // 可执行文件不直接跑：这个入口的文字来自模型输出，别人让它写一个路径进来就是一次点击即执行
            if (IsExecutable(path))
            {
                RevealInExplorer(path);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不值得弹错（与 RevealInExplorer 同一个立场）
        }
    }

    /// <summary>由路径构造附件；空路径或非法路径返回 null。</summary>
    public static ChatAttachment? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"', '\'')); }
        catch { return null; }
        return new ChatAttachment { Path = full, Label = LabelFor(full), Kind = KindFor(full) };
    }

    public static bool IsImage(string path)
        => ImageExts.Contains(Path.GetExtension(path));

    public static string KindFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExts.Contains(ext)) return "image";
        if (OfficeLabels.ContainsKey(ext)) return "office";
        if (TextExts.Contains(ext)) return "text";
        return "other";
    }

    /// <summary>
    /// chip 文字：图片 / 文本文件 / Word·Excel·PPT / 未识别时直接显示后缀名（A.hello → hello）。
    /// 无后缀统一叫「文件」。
    /// </summary>
    public static string LabelFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return "文件";
        if (ImageExts.Contains(ext)) return "图片";
        if (OfficeLabels.TryGetValue(ext, out var office)) return office;
        if (TextExts.Contains(ext)) return "文本文件";
        return ext.TrimStart('.');
    }

    /// <summary>在资源管理器里定位该文件（选中而不是打开）。</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(path) ?? path,
                    UseShellExecute = true,
                });
        }
        catch
        {
            // 打不开就算了，不值得弹错
        }
    }
}
