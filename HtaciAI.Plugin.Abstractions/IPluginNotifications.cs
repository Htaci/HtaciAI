using System;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件派出去的任务跑完之后，宿主会在屏幕右下角弹一条提醒（「任务××已完成 / 出错」）。
/// 这个出口决定那条提醒<b>弹不弹、留多久</b>（需要声明 <see cref="PluginCapabilities.NoticeControl"/>）。
///
/// <b>它为什么存在</b>：默认行为是「弹出后过几秒自动收起」。
/// 对桌宠、看板这类「人就在电脑前盯着」的插件，自动收起正合适；
/// 但机器人（QQ / 微信这类）反过来 —— 用户在手机上看结果，桌面那条提醒
/// 要么弹一下就没（他在别处，等于没看见），要么一直留着（堆满屏幕）。
/// 于是这里给两个极端各留一个档位。
///
/// <b>只管插件自己派的那几轮</b>：用户自己在窗口里聊的一轮、别的插件派的任务都不受影响。
///
/// <b>线程</b>：任意线程可调用，宿主自己调度到 UI 线程。
/// </summary>
public interface IPluginNotifications
{
    /// <summary>当前策略。默认 <see cref="PluginNoticeMode.Auto"/>。</summary>
    PluginNoticeMode Mode { get; }

    /// <summary>
    /// 设定策略。<b>立即生效</b>，只影响之后弹的提醒（已经弹出来的那条不会中途改主意）。
    ///
    /// <b>不写盘</b>：这是「这一次运行想怎么提醒」，宿主不替插件记住它。
    /// 插件自己的用户设置该由插件存（<see cref="IPluginStorage"/>），
    /// 下次 <see cref="IPlugin.InitializeAsync"/> 时照旧声明一次。
    /// </summary>
    void SetMode(PluginNoticeMode mode);
}

/// <summary>任务提醒的展示策略。</summary>
public enum PluginNoticeMode
{
    /// <summary>
    /// 默认：弹出后<b>过一会儿自动收起</b>（完成 8 秒、出错 10 秒；
    /// 等你动手的那两类给 30 秒）。鼠标停在上面时倒计时暂停，人一走就继续。
    /// </summary>
    Auto = 0,

    /// <summary>
    /// 常驻：一直留在屏幕上，直到用户自己点关闭（或点了正文跳过去）。
    /// 适合「这条结果用户必须看见」的插件。
    /// </summary>
    Persistent = 1,

    /// <summary>
    /// 不弹：插件自己负责把结果送到用户眼前（发消息、自己的窗口、自己的面板）。
    /// 会话里照常有未读提示，只是不再往桌面上弹浮窗。
    /// </summary>
    Off = 2,
}
