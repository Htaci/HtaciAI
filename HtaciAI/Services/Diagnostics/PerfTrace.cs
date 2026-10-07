using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Diagnostics;

/// <summary>
/// 「会话窗口加载 / 渲染」耗时探针。
///
/// <b>只在 Debug 构建里真正做工</b>：Release 下所有方法都是空操作 ——
/// 不分配 Stopwatch、不拼字符串、不碰文件系统。计时与写日志本身就有开销，
/// 而这类诊断只在开发期需要，所以用 <c>#if DEBUG</c> 在编译期整个剔除，
/// 而不是靠运行时开关（那样 Release 版仍要付出分支与调用成本）。
///
/// 用法：
/// <code>
/// PerfTrace.Begin("会话 abc123", "421 条消息");
/// PerfTrace.Note("① 读库", ms);
/// using (PerfTrace.Measure("② 渲染 / 正文块")) { … }
/// PerfTrace.Flush();
/// </code>
///
/// 同名分段会自动累计「次数 + 总耗时」，报告按总耗时降序排列 ——
/// 最耗时的那一段直接排在最上面，不用自己算。
/// </summary>
internal static class PerfTrace
{
#if DEBUG
    private sealed class Entry
    {
        public double TotalMs;
        public int Count;
    }

    /// <summary>分段名 → 累计数据。</summary>
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>报告头部的一次性说明行（会话 id、消息条数等）。</summary>
    private static readonly List<string> Header = new();

    /// <summary>记完就报，报告之间不混。</summary>
    private static readonly StringBuilder Report = new();

    private static readonly Stopwatch Wall = new();
    private static bool _active;

    /// <summary>开启一次采样。上一次未 Flush 的数据会被丢弃。</summary>
    public static void Begin(params string[] headerLines)
    {
        Entries.Clear();
        Header.Clear();
        Report.Clear();
        Wall.Restart();
        _active = true;
        if (headerLines is { Length: > 0 }) Header.AddRange(headerLines);
    }

    /// <summary>记录一个一次性耗时（如「读库」），与分段一起出现在报告里。</summary>
    /// <summary>
    /// 记录一个耗时样本（如「读库」、或每次的「批间间隔」）。
    /// <b>会累计</b>而不是覆盖：同名多次调用会合并成「次数 + 总耗时 + 均值」，
    /// 这样逐批的间隔才能看出总账。
    /// </summary>
    public static void Note(string name, double ms)
    {
        if (!_active) return;
        Add(name, ms);
    }

    /// <summary>记录一条说明（不计时，如「分组 137 轮」）。</summary>
    public static void Info(string line)
    {
        if (_active) Header.Add(line);
    }

    /// <summary>给一个过程计时；用 <c>using</c> 包住要测的代码。</summary>
    public static Scope Measure(string category)
        => _active ? new Scope(category, Stopwatch.GetTimestamp()) : default;

    private static void Add(string category, double ms)
    {
        if (!Entries.TryGetValue(category, out var e))
            Entries[category] = e = new Entry();

        e.TotalMs += ms;
        e.Count++;
    }

    /// <summary>输出报告：Debug 输出窗口写一份，数据目录 logs 下追加一份。</summary>
    public static void Flush()
    {
        if (!_active) return;
        _active = false;
        Wall.Stop();

        Report.AppendLine();
        Report.AppendLine("==================== 会话窗口加载耗时 ====================");
        foreach (var line in Header)
            Report.AppendLine("  " + line);
        Report.AppendLine($"  总耗时 {Wall.Elapsed.TotalMilliseconds:F0} ms");
        Report.AppendLine("  ------------------------------------------------------");

        // 降序：最耗时的排最前，一眼看出瓶颈
        var ordered = Entries
            .OrderByDescending(kv => kv.Value.TotalMs)
            .ToList();

        if (ordered.Count == 0)
        {
            Report.AppendLine("  （无分段数据）");
        }
        else
        {
            var width = ordered.Max(kv => kv.Key.Length);
            foreach (var (name, e) in ordered)
            {
                var perCall = e.Count > 1 ? $"  ({e.Count} 次, 均 {e.TotalMs / e.Count:F1} ms)" : "";
                Report.AppendLine($"  {name.PadRight(width)}  {e.TotalMs,9:F1} ms{perCall}");
            }
        }

        Report.AppendLine("======================================================");
        Report.AppendLine();

        var text = Report.ToString();
        Debug.WriteLine(text);
        TryAppendToFile(text);
    }

    private static void TryAppendToFile(string text)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "perf-debug.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{text}", Encoding.UTF8);
        }
        catch
        {
            // 诊断日志写不进去（只读目录、磁盘满）不该影响正常功能
        }
    }

    /// <summary>计时作用域。<c>default</c> 表示探针未开启，<see cref="Dispose"/> 直接返回。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _category;
        private readonly long _start;

        internal Scope(string category, long start)
        {
            _category = category;
            _start = start;
        }

        public void Dispose()
        {
            if (_category is null) return;
            Add(_category, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
        }
    }
#else
    // ---- Release：全部编译成空操作，不留任何运行时开销 ----

    public static void Begin(params string[] headerLines) { }

    public static void Note(string name, double ms) { }

    public static void Info(string line) { }

    public static void Flush() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Scope Measure(string category) => default;

    public readonly struct Scope : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() { }
    }
#endif
}
