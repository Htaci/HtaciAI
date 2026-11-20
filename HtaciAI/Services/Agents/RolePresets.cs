using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Agents;

/// <summary>
/// 内置的角色预设：让「助手」页开箱就有「对话」「任务」两个能选的对象。
///
/// <b>与子 Agent 预设同一套语义</b>（固定 id、已存在就跳过、删掉会回来），差别只在
/// <c>Kind = role</c>：它们是被用户选中的，不是被 task 派发的。
///
/// <b>「对话」为什么不带本地工具</b>：这一档的意义是让模型专注说话，而不是在用户的磁盘上
/// 翻文件、跑命令。所以这里显式列出要保留的工具，其余一概不给 ——
/// 用户想要更多，直接在页面上勾回来即可，这只是默认值。
///
/// <b>「任务」为什么留空</b>：空集合表示全部工具都可用，任务档要的正是「该用的都能用」。
/// 想收窄的用户自己勾。
/// </summary>
public static class RolePresets
{
    private sealed record Preset(
        string Id,
        string Name,
        string Description,
        string SystemPrompt,
        string[] ToolNames,
        bool AllTools);

    /// <summary>两个内置角色共用的一段：说清「你是被选中的角色，不是被派发的子 Agent」。</summary>
    private const string Shared = """
        你是用户在这一轮对话开始时选中的角色，直接和用户对话，不是被派发出去干活的子 Agent。
        """;

    private static readonly Preset[] All =
    {
        new(
            "builtin-role-chat",
            "对话",
            "只聊天不动手：不读写本地文件、不执行命令。适合讨论、答疑、起草文字。",
            Shared + """

                你这一档只负责说清楚，不负责动手：
                - 你手上没有读写文件、执行命令、遍历目录的工具。用户要你做这类事时，说明「这需要切到任务档」，
                  并给出该做什么；不要假装已经看过那些文件，也不要编造命令输出。
                - 需要外部信息可以联网抓取，抓不到就说抓不到。
                - 答案先给结论，再给必要的解释；不要为了显得完整而堆砌小标题和列表。
                - 用户的问题本身已经足够清楚时直接回答，不要反问确认。
                """,
            // 联网 + 交互 + 应用内知识库：都不碰用户的文件系统与命令行。
            new[]
            {
                "webfetch",
                "ask_user_question",
                "ask_user_continue",
                "load_skill",
                "unload_skill",
                "kb_note_list",
                "kb_note_read",
                "kb_note_write",
                "kb_note_edit",
                "kb_recall",
                "kb_add_text",
            },
            false),

        new(
            "builtin-role-task",
            "任务",
            "能动手的那一档：完整的本地工具（读写文件、跑命令、改代码），适合让它把活干完。",
            Shared + """

                你这一档是要把活干完的：
                - 该用的工具都用上，能直接改就直接改，不要只给方案让用户自己去敲。
                - 不读代码不提建议；要改某个文件先把它读明白。
                - 只做用户要求或明显必要的改动，不顺手重构、不补无关文档。
                - 受阻时换一条路，不要反复重试同一个失败动作；确实做不了就说清卡在哪。
                - 干完先给结果，再给必要的说明，不要罗列调用过哪些工具。
                """,
            Array.Empty<string>(),
            true),
    };

    /// <summary>
    /// 补齐缺失的内置角色。<b>幂等</b>：id 已存在就跳过，不覆盖用户改过的提示词与工具勾选。
    /// 整体失败只是页面上少两个默认项，不该拦住启动。
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
                    Kind = Agent.KindRole,
                    Name = preset.Name,
                    Description = preset.Description,
                    SystemPrompt = preset.SystemPrompt,
                    EnabledToolIds = preset.AllTools ? new List<string>() : ResolveToolIds(preset.ToolNames),
                    EnabledSkills = new List<SessionSkill>(),
                    McpServers = new List<string>(),
                    Model = "",
                    KnowledgeBaseId = "",
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
