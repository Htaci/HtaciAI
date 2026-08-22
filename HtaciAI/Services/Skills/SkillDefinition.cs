namespace HtaciAI.Services.Skills;

/// <summary>
/// 统一技能定义：技能是「怎么做一类任务」的知识包，可能内部调用若干工具。
/// <see cref="Body"/> 为 SKILL.md 正文（注入 system 时全文下发给 LLM）。
/// 与工具不同，技能不通过 function calling 暴露 API，而是作为提示词知识随上下文注入。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>内部唯一 id（同库表主键 / 文件目录名）。</summary>
    public string Id { get; init; } = "";

    /// <summary>技能名（snake_case），用于元数据列表展示与按需触发。</summary>
    public string Name { get; init; } = "";

    /// <summary>一句话描述：做什么、何时用。模型凭它在“可用技能列表”中判断是否触发。</summary>
    public string Description { get; init; } = "";

    /// <summary>SKILL.md 全文正文（注入 system 的内容）。</summary>
    public string Body { get; init; } = "";

    /// <summary>是否允许使用：false 时不进入可用技能列表，也不可被启用。</summary>
    public bool Enabled { get; init; } = true;
}
