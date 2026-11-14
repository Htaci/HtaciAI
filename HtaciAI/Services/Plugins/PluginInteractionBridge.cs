using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 把插件的 <see cref="IPluginInteractionHost"/> 接到宿主的 <see cref="IInteractionHost"/> 上：
/// 「会话没有窗口时，工具审批 / 提问 / 等人做完一件事」于是可以转发到 QQ、微信这类聊天软件。
///
/// <b>它只做类型翻译</b>：审批请求把 <see cref="ToolDefinition"/> 压成插件看得懂的
/// <see cref="PluginToolApprovalRequest"/>，提问在两个方向各映射一次。
/// 语义与超时都由插件那边决定（宿主只负责把取消令牌传下去）。
///
/// <b>挂在哪里</b>：<c>ConversationRuntime.PluginInteraction</c>，而运行时优先用窗口的
/// <c>Interaction</c> —— 界面开着的时候插件不会收到，两处不会同时弹卡。
/// </summary>
internal sealed class PluginInteractionBridge(string sessionId, IPluginInteractionHost host)
    : IInteractionHost
{
    public Task<bool> ApproveAsync(ChatToolCall call, ToolDefinition tool, CancellationToken ct)
        => host.ApproveAsync(new PluginToolApprovalRequest(
            sessionId,
            tool.Id,
            tool.Name,
            call.Arguments ?? "",
            MapDanger(tool.DangerLevel)), ct);

    public async Task<List<AskAnswer>?> AskAsync(List<AskQuestion> questions, CancellationToken ct)
    {
        var asked = questions
            .Select(q => new PluginAskQuestion(
                q.Question,
                q.Header ?? "",
                q.MultiSelect,
                q.Options.Select(o => new PluginAskOption(o.Label, o.Description ?? "")).ToList()))
            .ToList();

        var answered = await host.AskAsync(asked, ct);
        if (answered is null) return null;

        // 按问题正文对上；插件没答的题目留空，工具链会显示「未作答」
        var result = new List<AskAnswer>(questions.Count);
        foreach (var question in questions)
        {
            var match = answered.FirstOrDefault(a =>
                string.Equals(a.Question, question.Question, StringComparison.Ordinal));

            result.Add(new AskAnswer
            {
                Question = question.Question,
                Selected = match?.Selected?.Where(label => label is { Length: > 0 }).ToList() ?? new(),
                Custom = string.IsNullOrWhiteSpace(match?.Custom) ? null : match!.Custom!.Trim(),
            });
        }

        return result;
    }

    public Task<bool?> WaitForUserAsync(string title, string instructions, CancellationToken ct)
        => host.WaitForUserAsync(title, instructions, ct);

    /// <summary>
    /// 技能启停的刷新通知只对界面有意义：插件侧的工具集与技能是另一套，
    /// 它拿到这条通知也无从下手，这里刻意不发。
    /// </summary>
    public void OnSessionSkillsChanged(IReadOnlyList<SessionSkill>? skills) { }

    private static PluginToolDangerLevel MapDanger(ToolDangerLevel level) => level switch
    {
        ToolDangerLevel.Risk => PluginToolDangerLevel.Risk,
        ToolDangerLevel.Danger => PluginToolDangerLevel.Danger,
        _ => PluginToolDangerLevel.Safe,
    };
}
