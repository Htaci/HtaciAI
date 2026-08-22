using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HtaciAI.Services.Skills;

/// <summary>
/// 技能注册表：从磁盘目录加载技能定义并提供 id 解析。全局单例（跨页面、跨对话共享）。
/// 技能存放于 <c>%USERPROFILE%/.htaci/skills/&lt;skill-id&gt;/SKILL.md</c>，
/// SKILL.md 以 frontmatter（`---` 分隔的 name / description）+ 正文构成。
/// <see cref="Body"/> 即正文（不含 frontmatter），注入 system 时全文下发。
/// </summary>
public sealed class SkillRegistry
{
    /// <summary>全局单例。</summary>
    public static SkillRegistry Instance { get; } = new();

    /// <summary>技能根目录：%USERPROFILE%/.htaci/skills。</summary>
    public static string SkillsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".htaci", "skills");

    private readonly List<SkillDefinition> _skills = new();
    private readonly Dictionary<string, SkillDefinition> _byId = new();

    private SkillRegistry() { }

    /// <summary>首次运行写入内置示例技能（幂等：SKILL.md 已存在则不覆盖）。</summary>
    public void EnsureSeeded()
    {
        try
        {
            var dir = Path.Combine(SkillsRoot, "code-review");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                File.WriteAllText(file, CodeReviewSkillMd);
        }
        catch
        {
            // 种子写入失败不影响主流程
        }
    }

    /// <summary>从磁盘技能目录重新加载全部技能（替换当前内容）。</summary>
    public void LoadFromDisk()
    {
        _skills.Clear();
        _byId.Clear();
        try
        {
            if (!Directory.Exists(SkillsRoot)) return;
            foreach (var dir in Directory.GetDirectories(SkillsRoot))
            {
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var skill = ParseSkill(Path.GetFileName(dir), file);
                if (skill is null) continue;
                _byId[skill.Id] = skill;
                _skills.Add(skill);
            }
        }
        catch
        {
            // 技能目录读取失败忽略
        }
    }

    public void Register(SkillDefinition skill)
    {
        if (string.IsNullOrEmpty(skill.Id)) return;
        _byId[skill.Id] = skill;
        if (_skills.All(s => s.Id != skill.Id))
            _skills.Add(skill);
    }

    /// <summary>全部技能。</summary>
    public IReadOnlyList<SkillDefinition> GetAll() => _skills.ToList();

    /// <summary>允许使用的技能（Enabled 为 true）。</summary>
    public IReadOnlyList<SkillDefinition> GetEnabled() => _skills.Where(s => s.Enabled).ToList();

    public SkillDefinition? ResolveById(string id)
        => _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>按 id 集合解析（保留注册顺序），用于把会话启用的 id 还原为技能定义。</summary>
    public IReadOnlyList<SkillDefinition> GetByIds(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        return _skills.Where(s => set.Contains(s.Id)).ToList();
    }

    /// <summary>解析单个 SKILL.md：frontmatter 的 name / description + 正文（Body）。</summary>
    private static SkillDefinition? ParseSkill(string id, string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            var (meta, body) = SplitFrontMatter(text);
            var name = meta.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : id;
            var desc = meta.TryGetValue("description", out var d) ? d.Trim() : "";
            return new SkillDefinition { Id = id, Name = name, Description = desc, Body = body };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拆分 frontmatter 与正文。无 frontmatter 时元数据为空、Body 为全文。</summary>
    private static (Dictionary<string, string> Meta, string Body) SplitFrontMatter(string text)
    {
        var meta = new Dictionary<string, string>();
        var t = text.TrimStart('﻿', '\r', '\n', ' ', '\t');
        if (t.StartsWith("---", StringComparison.Ordinal))
        {
            var end = t.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 3)
            {
                var block = t.Substring(3, end - 3);
                foreach (var line in block.Split('\n'))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    meta[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
                return (meta, t.Substring(end + 3).TrimStart('\r', '\n'));
            }
        }
        return (meta, t);
    }

    /// <summary>内置示例技能：代码审查（端到端验证技能注入链路）。</summary>
    private const string CodeReviewSkillMd = """
        ---
        name: code-review
        description: 执行代码审查：定位潜在 bug、安全问题与可读性/性能改进点，并给出修复建议
        ---

        # code-review（代码审查）

        当用户请求“审查代码”“检查这段代码”“评审改动”或给出一个 diff/mr 让你评估时，启用本技能。

        ## 审查维度
        1. 正确性：逻辑错误、边界条件遗漏、空引用 / 越界 / 资源泄漏风险。
        2. 安全：注入、权限绕过、敏感信息泄露、不安全的反序列化。
        3. 性能：明显复杂度问题、无效循环、可避免的分配。
        4. 可读性：命名、抽象、重复代码、注释是否与实现一致。

        ## 输出格式
        无问题的维度可跳过；有问题的项按下面结构给出：
        - **问题**（级别：严重 / 中等 / 建议）
        - **证据**：文件:行号 + 问题代码摘录
        - **修复建议**：给出改法或示例代码

        只针对用户指定的文件 / 改动给出结论，不要泛泛而谈。
        """;
}
