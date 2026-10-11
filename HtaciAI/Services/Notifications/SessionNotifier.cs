using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace HtaciAI.Services.Notifications;

/// <summary>
/// 系统消息提醒的出口。业务侧只管调 <see cref="Notify"/>，
/// 「该不该打扰」「怎么显示」「点了跳到哪」都由宿主（<c>MainWindow</c>）注入。
///
/// <b>要不要打扰只看一件事</b>：应用窗口是否聚焦。
/// <list type="bullet">
///   <item>窗口聚焦 → 不弹（人正看着这个软件，弹出来是噪音，哪怕他看的是另一个会话）；</item>
///   <item>没聚焦（在看别的程序 / 最小化）→ 弹。</item>
/// </list>
/// 只看窗口激活状态、不看具体是哪个会话：用户既然坐在这个软件前面，
/// 侧栏的未读绿点已经够提示了，没必要再叠一层系统提醒。
///
/// 判定不落库：这是「此刻屏幕上的情况」，重启后没有意义。
/// </summary>
public static class SessionNotifier
{
    /// <summary>应用窗口当前是否处于聚焦状态。未注入时按「不聚焦」处理（宁可多提醒）。</summary>
    public static Func<bool>? IsAppActive { get; set; }

    /// <summary>点击提醒时跳到对应会话。未注入时点击无效。</summary>
    public static Action<string>? OpenSession { get; set; }

    /// <summary>有提醒要展示。宿主订阅后负责弹窗。</summary>
    public static event Action<SessionNotice>? Raised;

    /// <summary>
    /// 发一条提醒。<paramref name="sessionTitle"/> 为空时退回「会话」。
    ///
    /// 整段（含判定）都在 UI 线程上跑：调用方可能是工具执行链路里的后台线程，
    /// 而判定要读窗口激活状态、展示要建窗口，都不是线程安全的。
    /// </summary>
    /// <param name="workspaceName">
    /// 工作空间会话所属的工作空间名（普通对话传 null）。显示时会拼在会话名前面，
    /// 因为不同工作空间里可能有同名会话，光看会话名分不清是哪一个。
    /// </param>
    /// <param name="onAction">
    /// 提醒上的动作按钮点下去之后干什么；为 null 就不显示按钮。
    /// 由发起方提供：只有它知道该放行哪一次调用、改哪个会话的权限档位。
    /// </param>
    public static void Notify(
        SessionNoticeKind kind,
        string sessionId,
        string? sessionTitle,
        string body,
        string? workspaceName = null,
        Func<NoticeAction, Task>? onAction = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Notify(kind, sessionId, sessionTitle, body, workspaceName, onAction));
            return;
        }

        if (IsAppActive?.Invoke() ?? false) return;

        var title = string.IsNullOrWhiteSpace(sessionTitle) ? "会话" : sessionTitle.Trim();
        Raised?.Invoke(new SessionNotice(kind, sessionId, title, body)
        {
            WorkspaceName = string.IsNullOrWhiteSpace(workspaceName) ? null : workspaceName.Trim(),
            OnAction = onAction,
        });
    }
}
