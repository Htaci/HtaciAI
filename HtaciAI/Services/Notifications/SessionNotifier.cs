using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Plugins;

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
/// <b>留多久</b>：默认自动收起（完成 8 秒、出错 10 秒，等你动手的审批 / 提问 30 秒）。
/// 插件派的轮次由插件自己声明策略（<see cref="PluginNoticeMode"/>）：
/// 常驻，或者干脆不弹。<b>只有插件能把提醒留成常驻</b> —— 宿主的默认行为是「过一会儿就收」，
/// 免得人不在时浮窗一层层堆满屏幕右下角。
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

    /// <summary>一轮跑完 / 出错的提醒留多久。</summary>
    private static readonly TimeSpan InfoHold = TimeSpan.FromSeconds(8);

    /// <summary>出错的提醒留得久一点：人可能正看着别处，多给两秒不至于错过。</summary>
    private static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(10);

    /// <summary>审批 / 提问：这条要人动手，给足反应时间，但也不用到天荒地老。</summary>
    private static readonly TimeSpan ActionHold = TimeSpan.FromSeconds(30);

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
    /// <param name="pluginId">
    /// 这一轮是哪个插件派的；用户自己聊的一轮传 null。
    /// 只用来看该插件声明的策略（常驻 / 不弹 / 自动收起）。
    /// </param>
    public static void Notify(
        SessionNoticeKind kind,
        string sessionId,
        string? sessionTitle,
        string body,
        string? workspaceName = null,
        Func<NoticeAction, Task>? onAction = null,
        string? pluginId = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => Notify(kind, sessionId, sessionTitle, body, workspaceName, onAction, pluginId));
            return;
        }

        if (IsAppActive?.Invoke() ?? false) return;

        // 插件把提醒关掉了：这一轮仍然照常跑、照常落库，只是不往桌面上弹浮窗。
        var mode = PluginNoticePolicyStore.Get(pluginId);
        if (mode == PluginNoticeMode.Off) return;

        var title = string.IsNullOrWhiteSpace(sessionTitle) ? "会话" : sessionTitle.Trim();
        Raised?.Invoke(new SessionNotice(kind, sessionId, title, body)
        {
            WorkspaceName = string.IsNullOrWhiteSpace(workspaceName) ? null : workspaceName.Trim(),
            OnAction = onAction,
            AutoDismissAfter = HoldFor(kind, mode),
        });
    }

    /// <summary>这条提醒该留在屏上多久（<see cref="TimeSpan.Zero"/> = 常驻）。</summary>
    private static TimeSpan HoldFor(SessionNoticeKind kind, PluginNoticeMode mode)
    {
        // 插件声明「常驻」就一条都不自动收 —— 包括审批 / 提问，
        // 那是插件自己判断过「这条必须让用户看见」的结果。
        if (mode == PluginNoticeMode.Persistent) return TimeSpan.Zero;

        return kind switch
        {
            SessionNoticeKind.Failed => FailureHold,
            SessionNoticeKind.Approval or SessionNoticeKind.Question => ActionHold,
            _ => InfoHold,
        };
    }
}

