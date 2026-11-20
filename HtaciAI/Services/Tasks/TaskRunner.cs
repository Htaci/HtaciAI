using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Tasks;

/// <summary>
/// 把一次任务触发跑成一个真实会话：新建一行 <see cref="TaskRun"/>，借出它自己的
/// <see cref="ConversationRuntime"/>，把任务的提示词当作用户消息发出去，等它跑完，把结果写回那一行。
///
/// <b>为什么复用 runtime 而不是自己写一套循环</b>：与 <see cref="Subagents.SubagentRunner"/> 同理 ——
/// 工具审批、失败重试、上下文压缩、残缺工具消息修复、落库顺序全都已经在
/// <see cref="ConversationRuntime"/> + <see cref="ChatGateway"/> 里了。
///
/// <b>无人值守的两条硬约束</b>：
/// <list type="number">
///   <item>不挂 <see cref="ConversationRuntime.Interaction"/> —— 没有宿主时 runtime 本来就把审批当拒绝、
///     提问按 null 返回。定时任务要的正是这个：不能停在弹窗上等一个不在的人。</item>
///   <item>提示词里明确写清「不问、按最合理假设推进、卡住就说明卡在哪」（见 <see cref="TaskProfile"/>）。</item>
/// </list>
///
/// <b>线程</b>：runtime 与 <see cref="ConversationRuntimeRegistry"/> 只在 UI 线程上访问，
/// 而调度器可能从任意上下文调进来，所以入口统一用 <see cref="OnUiThreadAsync"/> 收口。
/// </summary>
public static class TaskRunner
{
    /// <summary>默认工具循环上限。</summary>
    public const int DefaultMaxTurns = 20;

    /// <summary>上限：定时任务是「到点做一件事」，不该变成没有边界的自主长跑。</summary>
    public const int HardMaxTurns = 50;

    /// <summary>结果摘要长度：列表里看一眼就够，全文要点开。</summary>
    private const int SummaryChars = 160;

    /// <summary>跑一次任务。返回的 <see cref="TaskRun"/> 已带上最终状态。</summary>
    public static Task<TaskRun> RunAsync(TaskDefinition def, CancellationToken ct = default)
        => OnUiThreadAsync(() => RunCoreAsync(def, ct));

    private static async Task<TaskRun> RunCoreAsync(TaskDefinition def, CancellationToken ct)
    {
        var run = new TaskRun
        {
            TaskId = def.Id,
            Title = BuildTitle(def),
            Model = def.Model,
            Thinking = def.Thinking,
            // 提示词在触发这一刻快照进运行行：任务定义之后被改了，回看历史时仍是当时那一份
            SystemPrompt = def.Prompt,
            BaseDirectory = def.BaseDirectory,
            EnabledToolIds = def.EnabledToolIds.ToList(),
            EnabledSkills = def.EnabledSkills.ToList(),
            McpServers = def.McpServers.ToList(),
            ToolPermissionMode = def.ToolPermissionMode,
            MaxTurns = Math.Clamp(def.MaxTurns <= 0 ? DefaultMaxTurns : def.MaxTurns, 1, HardMaxTurns),
            Status = TaskRunStatus.Running,
        };

        await TaskRepository.CreateRunAsync(run);

        try
        {
            using var lease = ConversationRuntimeRegistry.Shared.Acquire(run.Id, () => new TaskProfile(run));
            var runtime = lease.Runtime;

            // 刻意留空：审批一律拒绝、提问一律无回应（见类注释）
            runtime.Interaction = null;

            // 定时任务可能在会话还没打开过时就开始跑：必须等历史读完，否则模型收到的是空上下文
            await runtime.EnsureReadyAsync();
            ct.ThrowIfCancellationRequested();

            if (runtime.IsBusy)
                return await FinishAsync(run, TaskRunStatus.Failed, "上一次运行还没结束，这次没有开始。", null);

            var details = await ResolveModelAsync(runtime, run.Model);
            if (details is null)
                return await FinishAsync(run, TaskRunStatus.Failed,
                    "没有可用的模型：请先在「模型服务」里启用一个，或在任务里指定。", null);

            var client = runtime.Gateway.BuildClient(details);
            var previousMaxRounds = runtime.Gateway.MaxToolRounds;
            runtime.Gateway.MaxToolRounds = run.MaxTurns;

            ChatTurnResult? turn;
            try
            {
                turn = await runtime.SendAsync(new ConversationTurnRequest
                {
                    Settings = new TurnSettings(
                        Model: details,
                        Client: client,
                        Thinking: ResolveThinking(run.Thinking),
                        ToolIds: run.EnabledToolIds),
                    Text = string.IsNullOrWhiteSpace(def.Prompt) ? def.Title : def.Prompt,
                    RequestPreview = def.Title,
                });
            }
            finally
            {
                runtime.Gateway.MaxToolRounds = previousMaxRounds;
            }

            if (turn is null)
                return await FinishAsync(run, TaskRunStatus.Failed,
                    "这一轮没能开始（会话正被别处使用），下次触发会再试。", null);

            var status = turn.Success ? TaskRunStatus.Ok : TaskRunStatus.Failed;
            var error = turn.Success ? null : OneLine(turn.Error ?? "这一轮被中断了。");
            var summary = BuildSummary(turn.FinalAssistant?.Content);

            return await FinishAsync(run, status, error, summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 程序正在关：如实记下「没跑完」，不要把 interrupted 的半截内容当成结果
            return await FinishAsync(run, TaskRunStatus.Failed, "程序关闭，这次运行被中断。", null);
        }
        catch (Exception ex)
        {
            return await FinishAsync(run, TaskRunStatus.Failed, OneLine(ex.Message), null);
        }
    }

    /// <summary>写状态、记连续失败（成功则清零），返回带最终状态的那一行。</summary>
    private static async Task<TaskRun> FinishAsync(TaskRun run, string status, string? error, string? summary)
    {
        run.Status = status;
        run.Error = error;
        run.Summary = summary;
        run.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            await TaskRepository.FinishRunAsync(run.Id, status, error, summary);
        }
        catch
        {
            // 收尾写库失败不该让「已经跑完」变成异常抛给调度器：界面下次读库看到的是 running，
            // 下次启动会被 CloseStaleRunsAsync 收拾掉
        }

        return run;
    }

    private static async Task<ModelDetails?> ResolveModelAsync(ConversationRuntime runtime, string model)
    {
        if (!string.IsNullOrWhiteSpace(model) && await runtime.Gateway.ResolveModelAsync(model) is { } saved)
            return saved;

        // 任务没指定模型（或指定的已被停用/删除）：跟着宿主默认模型走
        return await AppDefaults.ResolveDefaultModelAsync();
    }

    private static ThinkingMode ResolveThinking(int value)
        => Enum.IsDefined(typeof(ThinkingMode), value) ? (ThinkingMode)value : ThinkingMode.Default;

    private static string BuildTitle(TaskDefinition def)
    {
        var name = string.IsNullOrWhiteSpace(def.Title) ? "定时任务" : def.Title.Trim();
        return $"{name} · {DateTime.Now:MM-dd HH:mm}";
    }

    /// <summary>摘要取正文第一段有内容的行：列表里显示结论，不显示格式标记。</summary>
    private static string? BuildSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var line = content.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim().TrimStart('#', '-', '*', '>', ' '))
            .FirstOrDefault(l => l.Length > 0);

        if (string.IsNullOrEmpty(line)) return null;
        return line.Length > SummaryChars ? line[..SummaryChars] + "…" : line;
    }

    private static string OneLine(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    /// <summary>
    /// 在 UI 线程上跑。已经在 UI 线程上就直接跑 —— 界面里点「立即运行」时不必绕一圈 Dispatcher。
    /// </summary>
    private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) return work();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { tcs.TrySetResult(await work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }
}
