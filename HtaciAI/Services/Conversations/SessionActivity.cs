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
}
