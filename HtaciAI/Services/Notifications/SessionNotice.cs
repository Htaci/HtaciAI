using System;
using System.Threading.Tasks;

namespace HtaciAI.Services.Notifications;

/// <summary>系统消息提醒的类型（决定图标与配色，不影响文案）。</summary>
public enum SessionNoticeKind
{
    /// <summary>有工具调用在等用户审批。</summary>
    Approval,

    /// <summary>AI 提问，等用户作答。</summary>
    Question,

    /// <summary>本轮任务已跑完。</summary>
    Done,

    /// <summary>本轮任务出错（请求失败 / 被中断且没产出内容）。</summary>
    Failed,
}

/// <summary>
/// 用户在提醒里可以直接做的动作（只有 <see cref="SessionNoticeKind.Approval"/> 才有）。
/// </summary>
public enum NoticeAction
{
    /// <summary>拒绝这次工具调用。</summary>
    Deny,

    /// <summary>只允许这一次。</summary>
    Allow,

    /// <summary>允许，并把该会话的权限档位改为「自由」——之后所有工具都不再询问。</summary>
    AllowAll,
}

/// <summary>
/// 一条要弹给用户的系统消息。
/// <see cref="Title"/> 是会话名，<see cref="Body"/> 是一句话说明。
/// </summary>
public sealed record SessionNotice(
    SessionNoticeKind Kind,
    string SessionId,
    string Title,
    string Body)
{
    /// <summary>会话所属工作空间名；普通对话为 null。显示时拼在会话名前面。</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// 提醒上的动作按钮点下去之后干什么。为 null 时不显示按钮。
    /// 由发起方（会话视图）提供：只有它知道该放行哪一次调用、怎么改权限档位。
    /// </summary>
    public Func<NoticeAction, Task>? OnAction { get; init; }
}
