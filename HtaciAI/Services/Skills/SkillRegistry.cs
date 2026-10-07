using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Skills;

/// <summary>
/// 技能注册表：从磁盘目录加载技能定义并提供 id 解析。全局单例（跨页面、跨对话共享）。
/// 技能存放于 <c>&lt;数据根&gt;/skills/&lt;skill-id&gt;/SKILL.md</c>（数据根见 <see cref="AppPaths"/>），
/// SKILL.md 以 frontmatter（`---` 分隔的 name / description）+ 正文构成。
/// <see cref="Body"/> 即正文（不含 frontmatter），注入 system 时全文下发。
/// </summary>
public sealed class SkillRegistry
{
    /// <summary>全局单例。</summary>
    public static SkillRegistry Instance { get; } = new();

    /// <summary>技能根目录：<c>&lt;数据根&gt;/skills</c>，见 <see cref="AppPaths"/>。</summary>
    public static string SkillsRoot => AppPaths.Skills;

    private readonly List<SkillDefinition> _skills = new();
    private readonly Dictionary<string, SkillDefinition> _byId = new();

    private SkillRegistry() { }

    /// <summary>
    /// 首次运行写入内置技能（幂等：SKILL.md 已存在则不覆盖，用户改写的内容会被保留）。
    /// 只判「文件不存在」，因此内置技能被删掉后下次启动会重新出现。
    /// 逐个 try：某一个写入失败不影响其余技能。
    /// </summary>
    public void EnsureSeeded()
    {
        foreach (var (id, md) in BuiltinSkills.All)
        {
            try
            {
                var dir = Path.Combine(SkillsRoot, id);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file))
                    File.WriteAllText(file, md);
            }
            catch
            {
                // 单个种子写入失败不影响主流程
            }
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

    /// <summary>
    /// 新建技能：写入 <c>&lt;SkillsRoot&gt;/&lt;id&gt;/SKILL.md</c> 后重载注册表。
    /// 写入与解析都在这里，格式不会两头跑偏。id 已被占用时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public SkillDefinition CreateOnDisk(string id, string name, string alias, string description, string body)
    {
        var dir = Path.Combine(SkillsRoot, id);
        var file = Path.Combine(dir, "SKILL.md");
        if (File.Exists(file))
            throw new InvalidOperationException($"技能「{id}」已存在");

        Directory.CreateDirectory(dir);
        File.WriteAllText(file, BuildSkillMd(name, alias, description, body), Encoding.UTF8);
        LoadFromDisk();

        return ResolveById(id) ?? new SkillDefinition
        {
            Id = id,
            Name = name,
            Alias = alias,
            Description = description,
            Body = body,
        };
    }

    /// <summary>
    /// 按 SKILL.md 的约定拼文件内容：frontmatter 只写非空项，与正文之间留一个空行。
    /// 元数据项必须是单行，否则会破坏 frontmatter 解析，所以这里统一压平。
    /// </summary>
    public static string BuildSkillMd(string name, string alias, string description, string body)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(OneLine(name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(alias)) sb.Append("alias: ").Append(OneLine(alias)).Append('\n');
        if (!string.IsNullOrWhiteSpace(description)) sb.Append("description: ").Append(OneLine(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(body);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>把可能带换行的文本压成单行（多余空白折叠为一个空格）。</summary>
    public static string OneLine(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? ""
            // 分隔符必须显式给成数组：Split('\r', '\n', opts) 会绑到
            // Split(char separator, int count, StringSplitOptions) —— '\n' 被当成 count，结果一个都不切。
            : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

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

    /// <summary>解析单个 SKILL.md：frontmatter 的 name / alias / description + 正文（Body）。</summary>
    private static SkillDefinition? ParseSkill(string id, string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            var (meta, body) = SplitFrontMatter(text);
            var name = meta.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : id;
            var desc = meta.TryGetValue("description", out var d) ? d.Trim() : "";
            var alias = meta.TryGetValue("alias", out var a) ? a.Trim() : "";
            return new SkillDefinition { Id = id, Name = name, Alias = alias, Description = desc, Body = body };
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

}
