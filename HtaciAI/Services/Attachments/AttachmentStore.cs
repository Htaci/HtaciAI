using System;
using System.Collections.Generic;
using System.IO;
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
    /// chip 文字：图片 / 文本 / Word·Excel·PPT / 未识别时直接显示后缀名（A.hello → hello）。
    /// 无后缀统一叫「文件」。
    /// </summary>
    public static string LabelFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return "文件";
        if (ImageExts.Contains(ext)) return "图片";
        if (OfficeLabels.TryGetValue(ext, out var office)) return office;
        if (TextExts.Contains(ext)) return "文本";
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
