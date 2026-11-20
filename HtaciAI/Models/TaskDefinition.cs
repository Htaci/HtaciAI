using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 定时任务定义（task_defs 表）：一条「什么时候跑、跑什么、用什么配置跑」。
///
/// <b>触发规则是闹钟模型，不是 cron</b>：用户要的是「每周一三五 08:30」「每月 1 号 09:00」
/// 「每天 22:15:00」这种能照着手机闹钟填的东西。cron 那五个星号段能表达更多，
/// 但对着它调「周几 + 几点」要查语法，而这里每个功能都必须在界面上说得清。
///
/// <b>下一次触发时间落库（<see cref="NextRunAt"/>）</b>：不每次现算。程序关掉期间错过的触发
/// 在库里看得见，重新打开时该补一次还是跳过，由调度器按这一列判断，不用回头翻历史。
///
/// 每次运行会派生一行 <see cref="TaskRun"/>（= 一个会话），消息复用 chat_message，
/// 与子 Agent 会话同一套约定。
/// </summary>
public sealed class TaskDefinition
{
    public string Id { get; set; } = NewId();

    public string Title { get; set; } = "";

    /// <summary>每次触发发给模型的那条用户消息。定时任务没有交互，它必须把要求写全。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>工具基准目录（相对路径落到这里）。空 = 进程当前目录。</summary>
    public string? BaseDirectory { get; set; }

    /// <summary>模型调用 id。空 = 用宿主默认模型（跑的时候才解析，换默认模型的任务会跟着走）。</summary>
    public string Model { get; set; } = "";

    /// <summary>思考模式（<see cref="HtaciAI.Services.ThinkingMode"/> 的整型值）。</summary>
    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;

    public List<string> EnabledToolIds { get; set; } = new();

    public List<SessionSkill> EnabledSkills { get; set; } = new();

    public List<string> McpServers { get; set; } = new();

    /// <summary>
    /// 权限档位。<b>定时任务必须显式选</b>：没人守在旁边的审批等于拒绝，
    /// 所以这一列在界面上不提供「跟随上次」这种模糊选项。
    /// </summary>
    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    /// <summary>一轮内允许的工具循环上限。</summary>
    public int MaxTurns { get; set; } = 20;

    /// <summary>见 <see cref="TaskTriggerKind"/>。</summary>
    public string TriggerKind { get; set; } = TaskTriggerKind.Daily;

    /// <summary>
    /// 哪几天。<b>weekly 存 1-7（周一为 1），monthly 存 1-31</b>；daily / once 不用。
    /// 存逗号文本而不是位图：打开库时人能直接看懂，也不用约定「第 0 位是哪天」。
    /// </summary>
    public List<int> TriggerDays { get; set; } = new();

    /// <summary>每天的触发时刻，<c>HH:mm:ss</c>。once 不用。</summary>
    public string TriggerTime { get; set; } = "09:00:00";

    /// <summary>一次性任务的绝对触发时刻（epoch ms，UTC）。once 才用。</summary>
    public long? OnceAt { get; set; }

    /// <summary>
    /// 下一次该触发的时刻（epoch ms，UTC）。null = 不会再触发（一次性任务已跑完）。
    /// <b>由调度器维护</b>，界面只读。
    /// </summary>
    public long? NextRunAt { get; set; }

    /// <summary>上一次触发的时刻（epoch ms，UTC）。</summary>
    public long? LastRunAt { get; set; }

    /// <summary>停用后不参与调度，但定义与历史都留着。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 连续失败次数。跑成功一次就清零，够多就自动停用 ——
    /// 没人看着的任务一直失败，唯一后果是每天多一条出错的系统提醒。
    /// </summary>
    public int FailureCount { get; set; }

    public bool IsDeleted { get; set; }

    public long? DeletedAt { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    /// <summary>id 前缀。任务定义不是会话，但和会话 id 同处一套字符串空间，加前缀避免看混。</summary>
    public static string NewId() => "task-" + Guid.NewGuid().ToString("N");
}

/// <summary>触发规则种类。<b>字符串常量</b>：这一列要能在库里直接读出来。</summary>
public static class TaskTriggerKind
{
    /// <summary>指定某一刻跑一次，跑完自动停用。</summary>
    public const string Once = "once";

    /// <summary>每天 <see cref="TaskDefinition.TriggerTime"/> 跑。</summary>
    public const string Daily = "daily";

    /// <summary>每周的 <see cref="TaskDefinition.TriggerDays"/>（1=周一 … 7=周日）那一天跑。</summary>
    public const string Weekly = "weekly";

    /// <summary>每月的 <see cref="TaskDefinition.TriggerDays"/>（1-31）那一天跑；当月没这一天就跳过。</summary>
    public const string Monthly = "monthly";

    public static bool IsDefined(string kind) => kind is Once or Daily or Weekly or Monthly;
}

/// <summary>
/// 一次运行（task_runs 表）。<b>每一行就是一个会话</b>：消息写在 chat_message 里，
/// 上下文压缩、工具调用修复、持久化全部复用现有那套，这里只多记「哪来的、结果如何」。
/// </summary>
public sealed class TaskRun
{
    /// <summary>id 前缀。<b>必须与普通会话、插件会话、子会话都不同</b>，解析档案时按前缀分流。</summary>
    public const string IdPrefix = "task-run-";

    public string Id { get; set; } = IdPrefix + Guid.NewGuid().ToString("N");

    public string TaskId { get; set; } = "";

    /// <summary>会话标题（通知与列表用），形如「每日备份 · 08-18 09:00」。</summary>
    public string Title { get; set; } = "";

    public string Model { get; set; } = "";

    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;

    public string SystemPrompt { get; set; } = "";

    public string? BaseDirectory { get; set; }

    public List<string> EnabledToolIds { get; set; } = new();

    public List<SessionSkill> EnabledSkills { get; set; } = new();

    public List<string> McpServers { get; set; } = new();

    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    public int MaxTurns { get; set; } = 20;

    public int TurnCount { get; set; }

    /// <summary>见 <see cref="TaskRunStatus"/>。</summary>
    public string Status { get; set; } = TaskRunStatus.Running;

    /// <summary>失败原因（一行）。成功时为空。</summary>
    public string? Error { get; set; }

    /// <summary>结果摘要：最后一条回复的首段截断。列表里一眼看结论，不必点进去。</summary>
    public string? Summary { get; set; }

    public long? LastMessageAt { get; set; }

    /// <summary>跑完的时刻（epoch ms，UTC）。运行中为 null。</summary>
    public long? FinishedAt { get; set; }

    public bool IsDeleted { get; set; }

    public long? DeletedAt { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>一次运行的状态。</summary>
public static class TaskRunStatus
{
    /// <summary>还在跑。</summary>
    public const string Running = "running";

    /// <summary>正常结束。</summary>
    public const string Ok = "ok";

    /// <summary>出错（模型调用失败、没有可用模型等）。</summary>
    public const string Failed = "failed";

    /// <summary>还没跑就被跳过（任务被停用 / 定义已删）。</summary>
    public const string Skipped = "skipped";
}
