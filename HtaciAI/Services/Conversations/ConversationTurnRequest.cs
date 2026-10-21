using System.Collections.Generic;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 发起一轮对话所需的全部输入。刻意把「用哪个模型、用哪份客户端」也交进来：
/// 解析模型是调用方的判断（视图读它的选择器，插件读自己的配置），
/// 放进 runtime 反而要它反过来去问 UI。
/// </summary>
public sealed record ConversationTurnRequest
{
    /// <summary>要发送的正文。<b>不能为空</b>；重新生成时是 null（那种情况走 <see cref="InsertAfterSequence"/>）。</summary>
    public string? Text { get; init; }

    /// <summary>已解析的模型详情（落库记名字、判是否支持视觉）。</summary>
    public required ModelDetails Model { get; init; }

    /// <summary>已构建好的客户端。</summary>
    public required IChatClient Client { get; init; }

    /// <summary>随这一轮发出的附件；只在新建用户消息时写进 metadata。</summary>
    public IReadOnlyList<ChatAttachment>? Attachments { get; init; }

    public ThinkingMode Thinking { get; init; } = ThinkingMode.Default;

    /// <summary>本轮启用的工具 id。为 null 表示一个都不启用。</summary>
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>
    /// 重新生成时用：新回复插回这条序号之后。
    /// 普通发送传 null（追加到末尾）。同时它也决定这次<b>不新建用户消息</b>。
    /// </summary>
    public int? InsertAfterSequence { get; init; }

    /// <summary>本轮「用户请求」的原文，只用于任务完成时那条提醒的文案。</summary>
    public string? RequestPreview { get; init; }

    /// <summary>
    /// 发给模型的历史。null = 用会话全量历史（普通发送与重新生成都是这样）。
    /// 「重试失败的那一轮」要显式给：失败消息还在历史里，发全量会把它们一起带上。
    /// </summary>
    public IReadOnlyList<ChatMessage>? History { get; init; }

    /// <summary>重新生成：不新建用户消息，历史末尾那条用户消息就是请求。</summary>
    public bool IsRegenerate => InsertAfterSequence is not null;
}
