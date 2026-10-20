using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 崩溃归因：记录「上次是不是非正常退出」，并据此判断是不是某个插件导致的。
///
/// <b>立场</b>：装插件是用户自己的判断，宿主不假装能隔离。这里只解决两件事 ——
/// 事后查明是谁干的（日志），以及保证下次不会再被同一个插件崩一次（跳过加载）。
///
/// <b>三条判定线索</b>：
/// <list type="number">
///   <item><c>logs/crash-pending.log</c>：崩溃处理器真的跑了（托管异常、未观察的 Task 异常）。
///         它写下现场后<b>不碰 state.json</b> —— 崩溃中途写状态文件可能写坏，
///         而且会和下面那条路径重复计数。</item>
///   <item><c>plugins/running.flag</c>：启动时写、正常退出时删。下次启动还在 = 上次非正常退出
///         （进度管理器结束进程、断电、栈溢出都走这条）。</item>
///   <item><c>logs/plugin-activity.log</c> 面包屑：崩的那一刻正在跑哪个插件。</item>
/// </list>
/// 归因统一放在<b>下次启动</b>做，只有一条代码路径，不会重复计数。
///
/// <b>措辞用「疑似」</b>：宿主无法证明因果。面包屑为空时不归咎任何插件，
/// 只留一条崩溃记录 —— 用户手动结束进程时面包屑通常是空的，这就是防误判的闸。
/// </summary>
public static class PluginCrashReporter
{
    private static bool _installed;
    private static bool _reportedThisRun;

    /// <summary>
    /// 每个进程只判定一次。<b>不能省</b>：插件「重载」会再走一遍初始化，
    /// 那时运行标记是本次启动刚写的，再判一次会把正常重启当成崩溃。
    /// </summary>
    private static bool _recordedThisRun;

    private static string RunningFlagPath => Path.Combine(AppPaths.Plugins, "running.flag");
    private static string PendingPath => Path.Combine(AppPaths.Logs, "crash-pending.log");

    /// <summary>挂上兜底处理器。<b>只能挂一次</b>，重复挂会让同一次崩溃写多份日志。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WritePending("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 未观察的 Task 异常默认不会杀进程，但它往往意味着插件某个后台任务已经炸了，
            // 值得留痕。不 SetObserved：那是升级全局策略，超出这里该管的范围。
            WritePending("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
        };
    }

    /// <summary>
    /// 启动时调用一次：判定上次是否非正常退出、写崩溃日志、给插件打「疑似」标记，
    /// 然后重新写下本次运行的标记文件。
    /// </summary>
    public static void RecordPreviousRun()
    {
        if (_recordedThisRun) return;
        _recordedThisRun = true;

        try
        {
            Directory.CreateDirectory(AppPaths.Plugins);
            Directory.CreateDirectory(AppPaths.Logs);

            var hadFlag = File.Exists(RunningFlagPath);
            var hadPending = File.Exists(PendingPath);

            if ((hadFlag || hadPending) && !_reportedThisRun)
            {
                _reportedThisRun = true;
                HandlePreviousRun(hadPending);
            }

            // 本次运行从这里开始
            File.WriteAllText(RunningFlagPath,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        catch (Exception ex)
        {
            PluginLog.Host("记录上次运行状态失败：" + ex.Message, ex);
        }
    }

    private static void HandlePreviousRun(bool hadPending)
    {
        var breadcrumb = PluginBreadcrumb.Read();
        var culprit = PluginBreadcrumb.ParsePluginId(breadcrumb);

        var logPath = WriteCrashLog(hadPending, breadcrumb, culprit);

        if (culprit is not null)
        {
            PluginStateStore.MarkCrash(culprit, logPath);
            PluginLog.Warn(culprit, "上次退出时疑似由该插件导致崩溃，已标记；详情见 " + logPath);
        }
        else
        {
            PluginLog.Host("检测到上次非正常退出，但没有正在进行中的插件调用，未归因到具体插件。日志：" + logPath);
        }

        // 现场已消费，删掉以免下次重复判定
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); } catch { /* 忽略 */ }
        PluginBreadcrumb.Clear();
    }

    private static string WriteCrashLog(bool hadPending, string? breadcrumb, string? culprit)
    {
        var stamp = DateTimeOffset.Now;
        var path = Path.Combine(AppPaths.Logs, $"crash-{stamp:yyyyMMdd-HHmmss}.log");

        var sb = new StringBuilder();
        sb.AppendLine("HtaciAI 崩溃记录（由上一次运行留下）");
        sb.AppendLine($"检测时间：{stamp:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"宿主版本：{AppVersion.Current}");
        sb.AppendLine($"疑似来源：{culprit ?? "（无法归因）"}");
        sb.AppendLine();

        if (hadPending)
        {
            sb.AppendLine("—— 崩溃时的异常现场 ——");
            try
            {
                sb.AppendLine(File.ReadAllText(PendingPath).TrimEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine("（现场文件读取失败：" + ex.Message + "）");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("—— 没有捕获到异常现场 ——");
            sb.AppendLine("进程是被外部终止的（任务管理器结束进程、断电、原生层崩溃等），或者异常发生在");
            sb.AppendLine("托管崩溃处理器能起作用之前（栈溢出、内存耗尽、访问冲突）。");
            sb.AppendLine();
        }

        sb.AppendLine("—— 崩溃时正在执行的插件调用（面包屑）——");
        sb.AppendLine(string.IsNullOrWhiteSpace(breadcrumb)
            ? "（无：崩溃时没有正在执行的插件调用）"
            : breadcrumb.Trim());

        try
        {
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            PluginLog.Host("写崩溃日志失败：" + ex.Message, ex);
        }

        return path;
    }

    /// <summary>崩溃处理器里写现场。可能只写得出一次，所以尽量简单、尽量少依赖。</summary>
    private static void WritePending(string source, Exception? ex, bool isTerminating)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);

            var sb = new StringBuilder();
            sb.AppendLine($"来源：{source}");
            sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"进程将退出：{(isTerminating ? "是" : "否")}");
            sb.AppendLine($"宿主版本：{AppVersion.Current}");
            sb.AppendLine($"面包屑：{PluginBreadcrumb.Read() ?? "（无）"}");
            sb.AppendLine();

            if (ex is not null)
            {
                sb.AppendLine("异常类型：" + ex.GetType().FullName);
                sb.AppendLine("异常消息：" + ex.Message);

                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                {
                    sb.AppendLine();
                    sb.AppendLine("内部异常：" + inner.GetType().FullName);
                    sb.AppendLine(inner.Message);
                }

                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    sb.AppendLine();
                    sb.AppendLine("调用栈：");
                    sb.AppendLine(ex.StackTrace);
                }
            }

            File.WriteAllText(PendingPath, sb.ToString());
        }
        catch
        {
            // 崩溃处理器里不能再有任何东西把进程二次搞崩
        }
    }

    /// <summary>正常退出时调用：删掉运行标记，下次启动就不会误判成崩溃。</summary>
    public static void MarkCleanExit()
    {
        try
        {
            if (File.Exists(RunningFlagPath)) File.Delete(RunningFlagPath);
        }
        catch
        {
            // 删不掉最多是下次启动多写一份「无法归因」的崩溃日志，不做补救
        }
    }

    /// <summary>崩溃日志目录，供设置页「查看崩溃日志」使用。</summary>
    public static string LogDirectory => AppPaths.Logs;
}
