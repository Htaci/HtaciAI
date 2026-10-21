using System;
using System.Collections.Generic;
using System.Threading;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 一次对话的<b>状态与请求执行上下文</b>：全量历史、会话档案、请求网关、运行标记、取消源。
///
/// <b>为什么要有它</b>：这些状态原本长在 <c>ConversationView</c> 上，于是「这一轮跑到哪了」
/// 只能通过一个已经打开的视图观察 —— 插件（比如桌宠）在会话没打开时既发起不了对话，
/// 也看不到它跑完了没有。搬出来之后，视图退化成「订阅状态并渲染」的一侧。
///
/// <b>当前进度</b>：只搬了状态字段；发送、跑轮次、落库仍在视图里，
/// 下一步把它们也移进来并改成事件驱动（见 <c>docs/plugin-plan.md</c> 的 M1-B）。
/// 因此现在它还<b>没有</b>按会话复用（一个视图一个 runtime），否则「视图重建后要不要重新读库」
/// 会立刻变成一个必须在事件驱动里一起解决的问题。
///
/// <b>线程约定</b>：只在 UI 线程上访问，内部不加锁（与 <see cref="SessionViewCache"/> 相同）。
/// </summary>
public sealed class ConversationRuntime
{
    public ConversationRuntime(IConversationProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>会话档案：会话状态、system 提示词、落库路径。</summary>
    public IConversationProfile Profile { get; }

    /// <summary>会话 id。日志与提醒文案里标识「这是哪个会话」用。</summary>
    public string SessionId => Profile.State.SessionId;

    /// <summary>
    /// 全量历史。<b>发给模型的就是它全量</b>；渲染可以只取其中一段（视图的分段渲染），
    /// 两者语义不同，别把「渲染窗口」当成「上下文」。
    /// </summary>
    public List<ChatMessage> Messages { get; } = new();

    /// <summary>请求网关：客户端解析、自动重试、工具循环、落库。</summary>
    public ChatGateway Gateway { get; } = new();

    /// <summary>
    /// 当前是否有任务在跑。
    /// <b>放在这里而不是视图上</b>，是为了让「同一会话不能并发跑两轮」由状态本身保证，
    /// 不必指望每个入口都记得先看视图。
    /// </summary>
    public bool IsBusy { get; set; }

    /// <summary>本轮请求的取消源：运行中点「停止」就是取消它。每轮新建、收尾时置空并释放。</summary>
    public CancellationTokenSource? SendCts { get; set; }
}
