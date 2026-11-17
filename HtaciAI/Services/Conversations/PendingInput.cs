using System;
using System.Collections.Generic;
using HtaciAI.Models;

namespace HtaciAI.Services.Conversations;

/// <summary>排队中的一条用户消息处在什么状态。</summary>
public enum PendingInputState
{
    /// <summary>任务跑完就自动发出去。</summary>
    Queued,

    /// <summary>用户点了「引导插入」：等工具循环的下一个边界插进当前这一轮。</summary>
    Steer,

    /// <summary>用户正在改它。<b>这期间整条队列都不自动发送</b> —— 见 <c>ConversationRuntime.PendingPaused</c>。</summary>
    Editing,
}

/// <summary>
/// 会话正在跑任务时用户又发的那条消息。
///
/// 它必须自己带着 <see cref="TurnSettings"/>：排队期间用户可能换模型、换思考档位、
/// 改工具勾选，而这条消息是「按当时的口径」写下来的。等轮到自己发送时再去读界面，
/// 拿到的是别人的设置 —— 与用户按下回车那一刻看到的不是同一件事。
///
/// 放在 runtime 而不是视图上：视图会被缓存淘汰后重进（切标签页、开别的会话），
/// 队列挂在视图上就等于「切走再切回来，排的消息全没了」。
/// </summary>
public sealed class PendingInput
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public required string Text { get; set; }

    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }

    public required TurnSettings Settings { get; init; }

    public PendingInputState State { get; set; } = PendingInputState.Queued;

    /// <summary>进编辑之前是哪一档。改完要退回它，而不是一律退成「排队」。</summary>
    public PendingInputState BeforeEdit { get; set; } = PendingInputState.Queued;
}
