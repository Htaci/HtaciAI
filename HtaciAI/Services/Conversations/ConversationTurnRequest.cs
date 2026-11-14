using System.Collections.Generic;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 「这一轮用哪个模型、哪个客户端、思考档位、哪些工具」。
///
/// 刻意由调用方解析好再交进来：解析模型是调用方的判断（视图读它的选择器，插件读自己的配置），
/// 放进 runtime 反而要它反过来去问 UI。
/// </summary>
public sealed record TurnSettings(
    ModelDetails Model,
    IChatClient Client,
    ThinkingMode Thinking = ThinkingMode.Default,
    IReadOnlyList<string>? ToolIds = null);

/// <summary>发起一轮对话所需的全部输入。</summary>
public sealed record ConversationTurnRequest
{
    /// <summary>模型、客户端、思考档位、工具。</summary>
    public required TurnSettings Settings { get; init; }

    /// <summary>要发送的正文。<b>不能为空</b>；重新生成时是 null（那种情况走 <see cref="InsertAfterSequence"/>）。</summary>
    public string? Text { get; init; }

    /// <summary>随这一轮发出的附件；只在新建用户消息时写进 metadata。</summary>
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }

    /// <summary>
    /// 重新生成时用：新回复插回这条序号之后。
    /// 普通发送传 null（追加到末尾）。同时它也决定这次<b>不新建用户消息</b>。
    /// </summary>
    public int? InsertAfterSequence { get; init; }

    /// <summary>本轮「用户请求」的原文，只用于任务完成时那条提醒的文案。</summary>
    public string? RequestPreview { get; init; }

    /// <summary>
    /// 发给模型的历史。null = 用会话全量历史（普通发送与重新生成都是这样）。
    /// 「重试失败的那一轮」与「继续被中断的那一轮」要显式给。
    /// </summary>
    public IReadOnlyList<ChatMessage>? History { get; init; }

    /// <summary>重新生成：不新建用户消息，历史末尾那条用户消息就是请求。</summary>
    public bool IsRegenerate => InsertAfterSequence is not null;

    /// <summary>
    /// 这一轮是「继续」接出来的：值是它承接的那一轮的 turn_id。
    ///
    /// 只有「继续」会传它。<b>它不是「重新生成」</b>：继续是接着跑同一件事，
    /// 界面必须把两段画成一整轮；重新生成是换一条新的回复。
    /// </summary>
    public string? ContinuedFrom { get; init; }

    /// <summary>
    /// 这一轮是插件经 <c>ISessionFacade</c> 派的（用户自己发的为 false）。
    /// 只影响收尾事件上的标记（<c>ChatEvent.IsPluginInitiated</c>）：
    /// 插件派完任务等结果时，必须能把自己派的那轮和用户同时在聊的那轮分开。
    /// </summary>
    public bool PluginInitiated { get; init; }

    /// <summary>
    /// 派这一轮的是哪个插件（用户自己发的为 null）。
    /// 收尾提醒要按它查插件声明的策略：常驻、不弹，还是弹完自动收。
    /// 与 <see cref="PluginInitiated"/> 分开是因为那个是 bool，标不出「谁派的」。
    /// </summary>
    public string? PluginId { get; init; }
}
