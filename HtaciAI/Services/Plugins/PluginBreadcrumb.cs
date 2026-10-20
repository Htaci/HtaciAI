using System;
using System.IO;
using System.Text.Json;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 「面包屑」：在调用插件代码<b>之前</b>把「此刻正在跑哪个插件、哪个阶段」写到一个固定文件，
/// 调用返回后清掉。
///
/// <b>为什么必须写在崩溃之前</b>：进程崩溃时（尤其 <c>Environment.FailFast</c>、栈溢出、
/// 在别的线程上炸掉的 <c>async void</c>）没有任何代码还能执行，事后没法知道是谁干的。
/// 崩溃处理器能跑的时候它自己就有调用栈；跑不了的时候，这个文件就是唯一线索。
///
/// <b>为什么是覆盖写而不是追加</b>：只关心「最后一个还没返回的调用」，追加会让文件随运行时间
/// 无限增长，把真正的现场淹掉。代价是多次调用之间的历史丢失 —— 那本来也不需要。
///
/// <b>节流</b>：按 token 逐条写盘是不可接受的（流式输出每秒几十上百次），
/// 所以非关键阶段最多每秒写一次；<c>Initialize</c> / 窗口创建 / 工具执行 / 控件工厂
/// 这几个崩溃高发点标记为 critical，不节流。
/// </summary>
public static class PluginBreadcrumb
{
    /// <summary>非关键阶段两次写盘的最小间隔。</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    private static readonly object Gate = new();

    /// <summary>上一次真正写盘的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private static long _lastWriteTick;

    /// <summary>当前是否有未清除的面包屑。</summary>
    private static bool _armed;

    private static string FilePath => Path.Combine(AppPaths.Logs, "plugin-activity.log");

    /// <summary>记下「即将进入某插件的某个阶段」。</summary>
    /// <param name="critical">true = 不节流，立即写盘。</param>
    public static void Enter(string pluginId, string phase, bool critical = false)
    {
        try
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;
                if (!critical && _armed && now - _lastWriteTick < Throttle.TotalMilliseconds) return;

                Directory.CreateDirectory(AppPaths.Logs);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new
                {
                    plugin = pluginId,
                    phase,
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }));

                _lastWriteTick = now;
                _armed = true;
            }
        }
        catch
        {
            // 写不下来就退化成「内存里知道」，不影响调用本身
        }
    }

    /// <summary>调用已返回（无论成功失败），清除。</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                _armed = false;
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }
        catch
        {
            // 删不掉也没关系：下次 Enter 会覆盖
        }
    }

    /// <summary>读取当前面包屑的原始内容；没有则 null。</summary>
    public static string? Read()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从面包屑原文里取出插件 id；解析不出来返回 null。</summary>
    public static string? ParsePluginId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("plugin", out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                var id = p.GetString();
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }
        catch
        {
            // 崩溃瞬间写出的文件可能是半截的，按「无法归因」处理
        }

        return null;
    }
}
