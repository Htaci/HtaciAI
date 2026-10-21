using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 一轮对话里「必须问用户」的三件事由宿主实现：工具审批、ask_user_question、技能启停后回显。
///
/// <b>为什么是个接口而不是事件</b>：这三件都要<b>等一个结果</b>，事件没有返回值。
///
/// <b>没有宿主时</b>（插件在后台跑着、没有窗口）：审批一律拒绝、提问返回 null，
/// 工具链据此把「用户不在」如实回给模型，而不是让这一轮永远悬在那里。
/// </summary>
public interface IInteractionHost
{
    /// <summary>工具执行前征求同意。返回 false = 拒绝执行。</summary>
    Task<bool> ApproveAsync(ChatToolCall call, ToolDefinition tool, CancellationToken ct);

    /// <summary>向用户提问并等待作答。返回 null = 用户没答（关掉了或取消了）。</summary>
    Task<List<AskAnswer>?> AskAsync(List<AskQuestion> questions, CancellationToken ct);

    /// <summary>工具改动了本会话启用的技能（load_skill / unload_skill），宿主据此刷新显示。</summary>
    void OnSessionSkillsChanged(IReadOnlyList<SessionSkill>? skills);
}
