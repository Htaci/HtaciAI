using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Services.Tasks;

/// <summary>
/// 定时任务的调度器：一个 <see cref="DispatcherTimer"/> 加一个「按最近一次触发时刻安排下一次唤醒」的循环。
///
/// <b>不每秒扫库</b>：每次醒来先问「最近的一次触发还有多久」（一次极小的 SQL），
/// 然后按剩余时间设 <see cref="DispatcherTimer.Interval"/>（下限 200ms、上限 30 秒）。
/// 空闲时最多 30 秒看一次库，而临近触发时能准到秒。
///
/// <b>只在前台进程里跑</b>：程序关掉任务就不跑（用户已确认这个取舍）。
/// 因此不需要 Windows 计划任务、不需要常驻服务，也就没有「装了一半的守护进程」这种东西。
///
/// <b>串行</b>：同一时刻只跑一个任务。与子 Agent 派发同样的理由 —— 两个任务同时写同一批文件时，
/// 「谁改了什么」在报告里看不出差别，而这种错误用户很难查。
///
/// <b>错过的触发只补一次</b>：触发时间以「跑完的现在」为基准重算，
/// 于是程序关掉一天再打开，不会醒来连跑十几遍。
///
/// <b>线程</b>：全部在 UI 线程上（DispatcherTimer 的 Tick 就在 UI 线程，runtime 也要求 UI 线程）。
/// </summary>
public sealed class TaskClock
{
    /// <summary>全进程一份。</summary>
    public static TaskClock Shared { get; } = new();

    /// <summary>连续失败到这个次数就自动停用（见 <see cref="TaskRepository.RecordFailureAsync"/>）。</summary>
    public const int DisableAfterFailures = 3;

    /// <summary>空闲时最长多久看一次库。再长会让「刚改过时间」的任务延迟明显。</summary>
    private static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(30);

    /// <summary>最短等待：到点后立刻重排，避免 0 间隔把 UI 线程占满。</summary>
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _timer = new();

    private bool _started;
    private bool _pumping;

    private TaskClock()
    {
        _timer.Tick += (_, _) => _ = TickAsync();
    }

    /// <summary>任务定义或运行状态变了（界面保存 / 手动运行完）：重算下一次唤醒。</summary>
    public event Action? Changed;

    /// <summary>内存里最近一次触发时刻（epoch ms）。界面显示「还有多久」用，避免每次都查库。</summary>
    public long? NextRunAt { get; private set; }

    public bool IsRunning => _pumping;

    /// <summary>启动。必须在 UI 线程上调用（App 启动时 Post 过来）。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        // 上次没跑完的运行先标成失败，否则界面上那一条会永远停在「运行中」
        _ = TaskRepository.CloseStaleRunsAsync();

        Recompute();
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    /// <summary>外部改动后重新安排（界面保存了任务、手动跑了一次）。</summary>
    public void Wake()
    {
        if (!_started) return;
        Recompute();
    }

    /// <summary>立刻跑一次某个任务（界面上的「立即运行」）。不影响它的定时规则。</summary>
    public async Task<TaskRun?> RunNowAsync(TaskDefinition def)
    {
        if (_pumping) return null;

        _pumping = true;
        try
        {
            var run = await TaskRunner.RunAsync(def);
            await AfterRunAsync(def, run);
            return run;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _pumping = false;
            Wake();
        }
    }

    private void Recompute() => _ = RecomputeAsync();

    private async Task RecomputeAsync()
    {
        if (!_started) return;

        try
        {
            NextRunAt = await TaskRepository.GetNextRunAtAsync();
        }
        catch
        {
            // 读不到就按空闲处理，下一轮再试
            NextRunAt = null;
        }

        if (!_started) return;

        var wait = NextRunAt is { } at
            ? TimeSpan.FromMilliseconds(at - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : MaxIdle;

        // 已经过点的任务别等：下限压到 MinWait，让 Tick 立刻去跑它
        _timer.Interval = wait < MinWait ? MinWait : wait > MaxIdle ? MaxIdle : wait;

        // <b>必须先停再起</b>：改 Interval 不会重置已经走过的计时，
        // 原来排着 30 秒的定时器改成 5 秒后仍会在 30 秒那次才醒 —— 任务会晚触发最多半分钟。
        _timer.Stop();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        _timer.Stop();

        if (!_started || _pumping) return;

        _pumping = true;
        try
        {
            // 循环到没有到点的任务为止：上一轮跑完可能又有一个到点了（错过的补跑）
            while (_started)
            {
                var due = await TaskRepository.GetDueDefinitionsAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                if (due.Count == 0) break;

                foreach (var def in due)
                {
                    if (!_started) return;
                    await RunDueAsync(def);
                }
            }
        }
        catch
        {
            // 调度循环本身不该因为一次读写失败就停掉：下一轮重新排
        }
        finally
        {
            _pumping = false;
        }

        Recompute();
    }

    private async Task RunDueAsync(TaskDefinition def)
    {
        var now = DateTimeOffset.UtcNow;

        // 先推进下一次触发，再执行：执行过程中程序被杀也不会导致同一个时间点被反复触发
        long? next;
        try
        {
            next = TaskSchedule.NextOccurrence(def, now);
        }
        catch
        {
            next = null;
        }

        try
        {
            await TaskRepository.AdvanceAsync(def.Id, next, now.ToUnixTimeMilliseconds());
        }
        catch
        {
            // 推进失败就让这次跑掉，下一轮会再看到同一个 next_run_at（最多重复一次）
        }

        TaskRun? run;
        try
        {
            run = await TaskRunner.RunAsync(def);
        }
        catch
        {
            run = null;
        }

        if (run is not null) await AfterRunAsync(def, run);
    }

    /// <summary>跑完之后的记账：成功清零连续失败，失败累计到阈值就自动停用。</summary>
    private async Task AfterRunAsync(TaskDefinition def, TaskRun run)
    {
        try
        {
            if (run.Status == TaskRunStatus.Ok)
                await TaskRepository.ResetFailuresAsync(def.Id);
            else if (run.Status == TaskRunStatus.Failed)
                await TaskRepository.RecordFailureAsync(def.Id, DisableAfterFailures);
        }
        catch
        {
            // 记账失败不影响已经跑完的这一轮
        }

        Changed?.Invoke();
    }
}
