using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 把会话的对话视图「借」到自己的界面里显示（需要声明
/// <see cref="PluginCapabilities.ConversationDock"/>）。
///
/// <b>为什么需要这个出口</b>：内置浏览器这类工作区想一边看网页一边聊，
/// 但会话视图只有一个 —— 它带着滚动位置、正在跑的那一轮、输入框草稿、审批卡片，
/// 自己照着 <see cref="ISessionFacade"/> 再搭一份就是「同一个会话两份界面」：
/// 一份在跑、另一份是快照，用户点哪份都像丢消息。
/// 所以这里给的是<b>真正那一份</b>：从原宿主（会话页）摘下来交给你。
///
/// <b>占位由宿主渲染，插件不参与</b>：你只需要说「我把这个会话抢走了，我叫 X」，
/// 会话页会自己显示「正在 X 中显示」和召回按钮。这样每个插件免费得到一致的占位提示，
/// 也不必知道会话页长什么样。
///
/// <b>一个会话同一时刻只能被一个插件占着</b>：已经被别人占了时
/// <see cref="AttachAsync"/> 返回失败并说明是谁。
///
/// <b>线程</b>：必须在 UI 线程上调用（拿到的 <see cref="Control"/> 只能在 UI 线程碰）。
/// </summary>
public interface IConversationDock
{
    /// <summary>
    /// 把会话视图从原宿主摘下来交给我。
    /// </summary>
    /// <param name="sessionId">要显示的会话 id（普通对话与工作空间会话都支持）。</param>
    /// <param name="hostLabel">
    /// 「谁占着它」，用于在原页面显示「正在 XXX 中显示」。
    /// 写用户看得懂的话（如「内置浏览器」），不要写插件 id。
    /// </param>
    Task<ConversationDockResult> AttachAsync(string sessionId, string hostLabel, CancellationToken ct = default);

    /// <summary>
    /// 还回去：把视图从你的容器里摘下来，交回会话页 / 缓存。重复调用无副作用。
    /// <b>插件被禁用时宿主会自动还</b>，所以这只是「用户在我这儿关掉了这个面板」时的正常收尾。
    /// </summary>
    void Release(string sessionId);
}

/// <summary>借会话视图的结果。<paramref name="View"/> 只在成功时非 null。</summary>
/// <param name="Success">是否拿到了视图。</param>
/// <param name="View">那个会话的视图。摘下来之后它已经没有父级，可以直接放进你的容器。</param>
/// <param name="Error">失败原因（会话不存在 / 已被别的插件占着 / 插件已停用），可直接展示给用户。</param>
public sealed record ConversationDockResult(bool Success, Control? View, string? Error)
{
    /// <summary>成功：<paramref name="view"/> 已经可以挂进你的容器。</summary>
    public static ConversationDockResult Ok(Control view) => new(true, view, null);

    /// <summary>失败：<paramref name="error"/> 是给人看的一句话。</summary>
    public static ConversationDockResult Fail(string error) => new(false, null, error);
}
