using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Subagents;

/// <summary>
/// 内置的子 Agent 预设：让「子Agent」页开箱就有几个能派的对象，而不是空白页逼用户先配一个。
///
/// <b>为什么用固定 id 而不是每次新建</b>：「通用」这类名字会被模型照着猜，
/// 同一个 id 才能在库里持续对应同一个角色；也才能做到「已存在就跳过」。
///
/// <b>删掉会回来</b>：与本项目的内置技能（<c>SkillRegistry.EnsureSeeded</c>）、
/// 内置示例工具同一套语义 —— id 不在就补一个。要让它彻底消失得改代码，
/// 因为「内置」就是我们替用户准备的那份默认值。
///
/// <b>工具按名字解析成 id</b>：内置工具的 id 是私有常量，直接写字符串会在改 id 时静默失联；
/// 按 <c>Name</c> 查一次，工具不存在就跳过那一条，预设本身仍然可用（只是少了那个工具）。
/// 预设不写技能与 MCP：留空表示全继承父会话，比替用户猜更安全。
/// </summary>
public static class SubagentPresets
{
    private sealed record Preset(
        string Id,
        string Name,
        string Description,
        string SystemPrompt,
        string[] ToolNames);

    /// <summary>所有预设共用的一段：子 Agent 与主 Agent 的差别只有「任务范围」，输出形状要固定。</summary>
    private const string Shared = """
        你是派发出来的子 Agent：主 Agent 把一件完整的活交给你，你只回一份报告。

        规矩：
        - 你看到的只有任务描述，看不到主会话的历史。任务里没写的信息要自己去查（读文件、搜代码），查不到就在报告里写「未能确认」，不要反问。
        - 不要派生子 Agent，也不要要求用户批准计划：直接把活干完。
        - 报告就是产物，不要复述你调用了哪些工具、读了哪些文件的过程。
        - 报告按四段写：结论 / 依据（文件:行 或命令输出）/ 改动的文件 / 未完成或不确定的部分。
        - 没做任何改动时第三段写「无」。
        """;

    private static readonly Preset[] All =
    {
        new(
            "builtin-general",
            "通用",
            "什么活都能接的默认子 Agent：有完整工具，适合独立完成一段需要自己查证的任务。",
            Shared + """

                你没有被限定的专长方向。先判断任务属于「查清一件事」「改一处代码」「跑一遍验证」中的哪一类，
                再选最直接的做法；任务跨了好几类就按依赖顺序一步步做，不要跳步。
                """,
            Array.Empty<string>()),

        new(
            "builtin-project-analyst",
            "项目分析专家",
            "摸清一个项目的结构、技术栈与关键模块，输出结构化的分析报告。只读，不改代码。",
            Shared + """

                你的身份是项目分析专家。目标是让没看过这个项目的人读完报告就能上手。

                做法：
                1. 先看根目录的清单文件（sln / csproj / package.json / requirements.txt 之类）确定技术栈与入口；
                2. 再用目录结构和依赖关系画出模块划分，找出「核心」和最容易被改坏的地方；
                3. 只在需要确认职责时才读具体文件，不要逐文件通读；
                4. 必要时用 git log / git diff --stat 看最近在动哪里。

                报告的「结论」段要写成：这个项目是什么、分几块、从哪个文件开始读。
                """,
            new[] { "read", "glob", "grep", "bash" }),

        new(
            "builtin-code-reviewer",
            "代码审查员",
            "只找真问题：逻辑错误、边界遗漏、资源泄漏、并发与安全问题，每条给位置和改法。",
            Shared + """

                你的身份是代码审查员。只报你能在代码里指出位置的问题，不报「建议加注释」这类没有对错的意见。

                优先级从高到低：会导致错误结果或崩溃的 > 资源/并发/安全 > 边界条件遗漏 > 命名与结构。
                每条都写清：位置（文件:行）、为什么是问题（要能落到一个具体输入或时序上）、怎么改。

                找不到实质问题时，「结论」段直接写「未发现实质问题」，不要为了凑数编几条。
                """,
            new[] { "read", "glob", "grep" }),

        new(
            "builtin-doc-writer",
            "文档写手",
            "读代码写文档：README、模块说明、改动记录。写完列出动了哪些文件。",
            Shared + """

                你的身份是文档写手。文档里出现的每个接口名、参数、行为都必须来自你读过的代码，
                不确定的地方留 TODO 并说明要去哪确认，绝不编造。

                先看有没有现成的文档，能改就不要另起一份；动笔前先把要写的内容在结论里列成提纲，
                再展开成正文。
                """,
            new[] { "read", "write", "edit", "glob", "grep" }),
    };

    /// <summary>
    /// 补齐缺失的内置预设。<b>幂等</b>：id 已存在就跳过，不覆盖用户改过的提示词与工具勾选。
    /// 整体失败只是页面上少几个默认项，不该拦住启动。
    /// </summary>
    public static async Task EnsureSeededAsync()
    {
        try
        {
            var existing = (await AgentRepository.GetAllAsync())
                .Select(a => a.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var preset in All)
            {
                if (existing.Contains(preset.Id)) continue;

                await AgentRepository.CreateAsync(new Agent
                {
                    Id = preset.Id,
                    Name = preset.Name,
                    Description = preset.Description,
                    SystemPrompt = preset.SystemPrompt,
                    EnabledToolIds = ResolveToolIds(preset.ToolNames),
                    EnabledSkills = new List<SessionSkill>(),
                    McpServers = new List<string>(),
                    IsEnabled = true,
                });
            }
        }
        catch
        {
            // 种子失败不影响主流程
        }
    }

    /// <summary>工具名 → 当前注册表里的 id。名字失效就跳过，不让整条预设失败。</summary>
    private static List<string> ResolveToolIds(IEnumerable<string> names)
    {
        var list = new List<string>();
        foreach (var name in names)
            if (ToolRegistry.Instance.ResolveByName(name) is { } tool)
                list.Add(tool.Id);
        return list;
    }
}
