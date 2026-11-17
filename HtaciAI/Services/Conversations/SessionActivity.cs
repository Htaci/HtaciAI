using System;
using System.Collections.Generic;

namespace HtaciAI.Services.Conversations;

/// <summary>会话在左侧列表里的活动状态。</summary>
public enum SessionActivity
{
    /// <summary>空闲：列表照常显示时间。</summary>
    Idle,

    /// <summary>本轮请求进行中：显示转圈。</summary>
    Running,

    /// <summary>在等用户作答 / 授权：显示黄色「等待确认」。</summary>
    WaitingConfirm,

    /// <summary>本轮已结束、用户还没看：显示绿点（打开会话即视为已读）。</summary>
    Done,
}

/// <summary>
/// 会话活动状态表（普通对话与工作空间会话共用一套 id 空间）。
///
/// <b>纯内存</b>：这是「用户此刻看到了什么」的界面状态，不该落库 —— 重启之后
/// 一切都该回到空闲，而不是满列表绿点。
/// </summary>
public static class SessionActivityStore
{
    private static readonly Dictionary<string, SessionActivity> States = new(StringComparer.Ordinal);

    /// <summary>会话 id → 所属工作空间 id。工作空间节点要靠它汇总「我下面有没有会话在跑」。</summary>
    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);

    /// <summary>某个会话的状态发生变化（含被清除）。参数是会话 id。</summary>
    public static event Action<string>? Changed;

    public static SessionActivity Get(string sessionId)
        => sessionId.Length > 0 && States.TryGetValue(sessionId, out var s) ? s : SessionActivity.Idle;

    public static void Set(string sessionId, SessionActivity activity)
    {
        if (string.IsNullOrEmpty(sessionId) || Get(sessionId) == activity) return;

        if (activity == SessionActivity.Idle) States.Remove(sessionId);
        else States[sessionId] = activity;

        Changed?.Invoke(sessionId);
    }

    /// <summary>
    /// 登记「这个会话属于哪个工作空间」。重复登记同一个归属不会有副作用。
    ///
    /// <b>为什么要单独记一份</b>：状态表只有会话 id，而工作空间节点收起之后，
    /// 它下面的会话行整个不在视觉树上、甚至已经随导航重建被丢掉 ——
    /// 可「里面还有一个会话在跑」这件事必须还能看出来。归属记在这里之后，
    /// 汇总就不依赖会话行是否存在；也因为是<b>进程级</b>的，同一个工作空间在别的标签页里
    /// 跑着的会话这里同样能看到。
    /// </summary>
    public static void Bind(string sessionId, string workspaceId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(workspaceId)) return;
        if (Owners.TryGetValue(sessionId, out var old) && old == workspaceId) return;

        Owners[sessionId] = workspaceId;
        Changed?.Invoke(sessionId);   // 归属变了，工作空间节点的汇总要重算
    }

    /// <summary>这个会话属于哪个工作空间；普通对话 / 插件会话返回 null。</summary>
    public static string? WorkspaceOf(string sessionId)
        => sessionId.Length > 0 && Owners.TryGetValue(sessionId, out var w) ? w : null;

    /// <summary>
    /// 把一个工作空间下所有会话的状态汇总成一个，用于工作空间节点上的状态标记。
    ///
    /// 优先级：<b>有会话在等用户 &gt; 有会话在跑 &gt; 有会话已回复未看</b>。
    /// 「在等用户」压过「在跑」是刻意的：等着的那一条本身不动，用户不处理就永远不会结束，
    /// 被它挡住的转圈只会让人以为还在干活。反过来「在跑」又压过「已回复」——
    /// 绿点说的是「有个会话可以看了」，而转圈说的是「整个工作空间还没停下来」。
    /// </summary>
    public static SessionActivity AggregateOf(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId)) return SessionActivity.Idle;

        var best = SessionActivity.Idle;
        foreach (var (sessionId, owner) in Owners)
        {
            if (!string.Equals(owner, workspaceId, StringComparison.Ordinal)) continue;

            var activity = Get(sessionId);
            if (Rank(activity) > Rank(best)) best = activity;
            if (best == SessionActivity.WaitingConfirm) return best;   // 到顶了，不必再翻
        }

        return best;

        static int Rank(SessionActivity a) => a switch
        {
            SessionActivity.WaitingConfirm => 3,
            SessionActivity.Running => 2,
            SessionActivity.Done => 1,
            _ => 0,
        };
    }
}
