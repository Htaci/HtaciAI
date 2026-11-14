using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件接管的「问用户」出口（经 <see cref="ISessionFacade.SetInteractionAsync"/> 登记）。
///
/// <b>它存在的理由</b>：宿主的规则是「没有窗口时审批一律拒绝、提问返回用户不在」。
/// 机器人（QQ / 微信这类）永远没有窗口，于是任何 Risk / Danger 工具都会被自动拒绝，
/// 提问永远得不到回答 —— 那只能跑只读工具。登记了这个桥，这些交互改成「发一条消息等用户回复」。
///
/// <b>窗口优先</b>：会话窗口开着的时候，交互仍然交给界面，本接口的实例<b>不会</b>被调用。
///
/// <b>超时由插件自己管</b>：用户可能永远不回。等不到就返回
/// <c>false</c> / <c>null</c>（等同于「用户拒绝 / 不在」），这一轮照常跑完而不是卡死。
/// <paramref name="ct"/> 是「这一轮被停止了」的信号，实现了它就能在用户按停止时立刻退出。
/// </summary>
public interface IPluginInteractionHost
{
    /// <summary>工具执行前征求同意。返回 false = 拒绝执行（模型会看到「用户拒绝执行工具」）。</summary>
    Task<bool> ApproveAsync(PluginToolApprovalRequest request, CancellationToken ct);

    /// <summary>
    /// 向用户提问并等待作答。返回 <c>null</c> = 没得到回答（超时/取消），
    /// 工具链会把它当成「用户不在」。
    ///
    /// 返回的每一项按 <see cref="PluginAskQuestion.Question"/> 与提问对上；对不上的问题按未作答处理。
    /// </summary>
    Task<IReadOnlyList<PluginAskAnswer>?> AskAsync(
        IReadOnlyList<PluginAskQuestion> questions, CancellationToken ct);

    /// <summary>
    /// 请用户去做一件模型自己做不了的事（扫码、输验证码、在手机上确认），等他做完再继续。
    /// 返回 null = 用户取消了这件事。
    /// </summary>
    Task<bool?> WaitForUserAsync(string title, string instructions, CancellationToken ct);
}

/// <summary>一次工具审批请求。</summary>
/// <param name="SessionId">发起这次调用的会话。</param>
/// <param name="ToolId">工具 id。</param>
/// <param name="ToolName">发给模型的调用名（给用户看的话优先用它）。</param>
/// <param name="ArgumentsJson">模型给的原始参数 JSON（可能很长，展示前请自己截断）。</param>
/// <param name="DangerLevel">危险等级。</param>
public sealed record PluginToolApprovalRequest(
    string SessionId,
    string ToolId,
    string ToolName,
    string ArgumentsJson,
    PluginToolDangerLevel DangerLevel);

/// <summary>要问用户的一个问题。</summary>
/// <param name="Question">问题正文。</param>
/// <param name="Header">短标签（可能为空）。</param>
/// <param name="MultiSelect">true = 多选。</param>
/// <param name="Options">选项（至少一个）。</param>
public sealed record PluginAskQuestion(
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<PluginAskOption> Options);

/// <summary>一个可选项。</summary>
/// <param name="Label">选项文字。</param>
/// <param name="Description">补充说明（可能为空）。</param>
public sealed record PluginAskOption(string Label, string Description);

/// <summary>用户对一条问题的回答。选项与自定义文本可以并存。</summary>
/// <param name="Question">对应的问题正文（用来和提问对上）。</param>
/// <param name="Selected">选中的选项 label，按选项原始顺序。</param>
/// <param name="Custom">用户自己打的回答（没填为 null）。</param>
public sealed record PluginAskAnswer(
    string Question,
    IReadOnlyList<string> Selected,
    string? Custom);
