namespace HtaciAI.Models;

/// <summary>
/// 会话中启用的一条技能记录（chat_sessions.enabled_skills 的 JSON 数组元素）。
/// status 区分两种注入方式：
///  - "allowed"：允许自动使用，仅把技能元数据（name: description）注入 system，模型按需唤醒；
///  - "loaded" ：已加载，把该技能的 SKILL.md 全文注入 system，始终可用。
/// 同一技能可同时以 allowed 与 loaded 两条记录出现（各自独立注入）。
/// </summary>
public sealed class SessionSkill
{
    public string Id { get; set; } = "";

    /// <summary>allowed / loaded（见类注释）。</summary>
    public string Status { get; set; } = "loaded";
}
