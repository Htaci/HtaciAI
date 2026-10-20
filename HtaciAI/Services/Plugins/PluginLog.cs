using System;
using System.IO;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件日志：追加写 <c>&lt;数据根&gt;/logs/plugin.log</c>，每行带时间、级别与插件 id。
///
/// <b>所有写盘都包在 try 里并且不抛</b>：日志是排查手段，它自己不能成为新的故障源。
/// 写不进去（磁盘满、被占用、权限不足）就静默丢弃 —— 调用方不必为此套 try。
///
/// 单个文件上限 2 MB，超过就轮转成 <c>plugin.1.log</c>（只保留一代）。
/// 不做按天分文件：插件日志量不大，保留「上一份」就够定位上一次启动的问题。
/// </summary>
public static class PluginLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.Logs, "plugin.log");
    private static string RotatedPath => Path.Combine(AppPaths.Logs, "plugin.1.log");

    public static void Info(string pluginId, string message) => Write("INFO ", pluginId, message, null);
    public static void Warn(string pluginId, string message) => Write("WARN ", pluginId, message, null);
    public static void Error(string pluginId, string message, Exception? ex = null) => Write("ERROR", pluginId, message, ex);

    /// <summary>宿主侧日志（不属于任何插件）。</summary>
    public static void Host(string message, Exception? ex = null) => Write("INFO ", "host", message, ex);

    private static void Write(string level, string pluginId, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);

                var path = FilePath;
                RotateIfNeeded(path);

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{pluginId}] {message}";
                if (ex is not null)
                    line += Environment.NewLine + ex;

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不应影响任何业务流程
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            // File.Move 到已存在的目标会抛，先删；这一代旧的本来就要丢
            if (File.Exists(RotatedPath)) File.Delete(RotatedPath);
            File.Move(path, RotatedPath);
        }
        catch
        {
            // 轮转失败就继续往原文件追加，总比丢日志好
        }
    }
}
