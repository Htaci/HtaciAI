using System;
using System.Collections.Generic;
using System.Linq;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 浏览器窗口的「授权给哪个会话」登记表。
///
/// <b>为什么需要授权这层</b>：浏览器里跑着用户自己的登录态。模型能操作它，
/// 等于能以那个身份点任何按钮。所以「能操作」不能默认成立 ——
/// 必须由用户明确把某个窗口交给某个会话，而且只给那一个会话。
///
/// <b>三条规则</b>：
/// <list type="number">
///   <item>Agent 自己开的窗口默认授权给它自己（它已经在用了，再要一次授权只是噪音）；</item>
///   <item>一个窗口同一时刻只能授权给一个会话 —— 否则两个 Agent 会互相把页面导走，
///         而且日志里看不出是谁干的；</item>
///   <item>用户关掉那个宿主标签页，窗口和授权一起消失（见 BrowserManager.Forget）。
///         这条是「授权状态会被删除」的唯一可靠触发点：窗口没了，授权留着只会让人困惑。</item>
/// </list>
///
/// <b>线程</b>：只在 UI 线程上访问，不加锁。
/// </summary>
internal sealed class BrowserGrants
{
    private readonly Dictionary<string, string> _byBrowser = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>谁占着这个窗口；没有授权时返回 null。</summary>
    public string? OwnerOf(string browserId)
        => _byBrowser.TryGetValue(browserId, out var sessionId) ? sessionId : null;

    /// <summary>这个窗口是不是已经授权给这个会话。</summary>
    public bool IsGranted(string browserId, string? sessionId)
        => !string.IsNullOrEmpty(sessionId) &&
           _byBrowser.TryGetValue(browserId, out var owner) &&
           string.Equals(owner, sessionId, StringComparison.Ordinal);

    /// <summary>
    /// 把窗口授权给某个会话。返回 false 表示它已经属于<b>别的</b>会话
    /// （此时 <paramref name="conflictOwner"/> 是那个会话 id）。
    /// 同一个会话重复授权是幂等的，返回 true。
    /// </summary>
    public bool Grant(string browserId, string sessionId, out string? conflictOwner)
    {
        conflictOwner = null;

        if (_byBrowser.TryGetValue(browserId, out var owner))
        {
            if (string.Equals(owner, sessionId, StringComparison.Ordinal)) return true;

            conflictOwner = owner;
            return false;
        }

        _byBrowser[browserId] = sessionId;
        return true;
    }

    /// <summary>取消授权。返回 false 表示本来就没授权。</summary>
    public bool Revoke(string browserId) => _byBrowser.Remove(browserId);

    /// <summary>窗口没了（用户关掉宿主标签页）时连授权一起清掉。</summary>
    public void Forget(string browserId) => _byBrowser.Remove(browserId);

    /// <summary>某个会话名下授权着的全部窗口。取消共享菜单、清理时用。</summary>
    public IReadOnlyList<string> BrowsersOf(string sessionId)
        => _byBrowser
            .Where(kv => string.Equals(kv.Value, sessionId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

    /// <summary>
    /// 校验一次工具调用是否有权操作这个窗口。
    /// 返回 null = 有权；返回一段话 = 为什么不行（直接回给模型）。
    /// </summary>
    public string? CheckAccess(string browserId, string? sessionId)
    {
        var owner = OwnerOf(browserId);

        // 没人占着：允许。这条覆盖「Agent 自己开的窗口」——
        // 开的时候就已经 Grant 过了，走到这里说明是历史遗留，按放行处理更省事。
        if (owner is null) return null;

        if (string.IsNullOrEmpty(sessionId))
            return $"浏览器 {browserId} 已被授权给会话「{Short(owner)}」，当前调用没有会话上下文，无法确认权限。";

        if (string.Equals(owner, sessionId, StringComparison.Ordinal)) return null;

        return $"浏览器 {browserId} 已授权给会话「{Short(owner)}」，当前会话无权操作它。" +
               "需要请用户在那个会话里取消共享，或者在这里重新打开一个新的浏览器窗口。";
    }

    /// <summary>会话 id 很长且不好看，截一段用于提示文案。</summary>
    private static string Short(string sessionId)
        => sessionId.Length <= 8 ? sessionId : sessionId[..8] + "…";
}
