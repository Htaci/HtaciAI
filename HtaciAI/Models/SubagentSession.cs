using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 子 Agent 会话（subagent_sessions 表）。主 Agent 通过 <c>task</c> 工具派发任务时创建，
/// 一次派发 = 一行。消息复用 <see cref="chat_message"/> 表（session_id 即本会话 Id），
/// 上下文压缩、工具调用修复、持久化都直接走现有那套。
///
/// <b>为什么单独一张表</b>：会话列表、默认模型、「已删除会话」恢复页这些查询都是整表扫描，
/// 与普通会话混在一张表里就得给每处查询补类型过滤，漏一处子会话就会冒进用户界面。
/// 单独建表则一处都不用改，解析入口只有 <see cref="Services.Plugins.SessionProfileResolver"/>。
///
/// 工具/技能/MCP/权限在创建那一刻按「父会话 ∩ Agent 预设」算好并落到本行（快照），
/// 之后构建请求只读本行 —— 与工作区会话同一套约定。
/// </summary>
public sealed class SubagentSession
{
    /// <summary>id 前缀。<b>三套会话共用一套 id 空间，靠前缀保证不会撞车。</b></summary>
    public const string IdPrefix = "sub-";

    public string Id { get; set; } = IdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>派发时用的子 Agent 定义（续用时校验一致：换了 Agent 就不许复用旧上下文）。</summary>
    public string AgentId { get; set; } = "";

    /// <summary>Agent 名称快照：列表与通知标题直接用，不必回查 agents 表（Agent 被删也不影响历史）。</summary>
    public string AgentName { get; set; } = "";

    /// <summary>派发它的会话 id（普通会话 / 工作空间会话都可能是父）。用于级联删除与来源展示。</summary>
    public string ParentSessionId { get; set; } = "";

    public string Title { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>思考模式（<see cref="HtaciAI.Services.ThinkingMode"/> 的整型值），理由同 <see cref="ChatSession.Thinking"/>。</summary>
    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;

    /// <summary>Agent 角色提示词快照（拼接在基础身份之后）。存快照是为了 BuildSystemPrompt 保持同步、不查库。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>工具基准目录快照 = 父会话的 BaseDirectory（子会话可能比父界面活得久，不能现场问）。</summary>
    public string? BaseDirectory { get; set; }

    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>只含 allowed：已加载（loaded）的技能属于父会话，不往子会话里再注入一遍全文。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    public List<string> McpServers { get; set; } = new();

    /// <summary>权限档位快照：创建时取「父会话档位」，子 Agent 预设只能更严不能更松。</summary>
    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    /// <summary>本轮任务允许的最大轮次，超过即收尾并如实上报 <see cref="SubagentStatus.MaxTurns"/>。</summary>
    public int MaxTurns { get; set; } = 20;

    /// <summary>已经跑过的轮次（每轮请求前 +1，用于 MaxTurns 判定）。</summary>
    public int TurnCount { get; set; }

    /// <summary>见 <see cref="SubagentStatus"/>。</summary>
    public string Status { get; set; } = SubagentStatus.Running;

    public long? LastMessageAt { get; set; }

    public bool IsDeleted { get; set; }

    public long? DeletedAt { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>
/// 子会话状态。<b>用字符串而不是枚举</b>：这一列要能在库里直接看得懂，
/// 而且它只是给主 Agent 判断「要不要续用」用的，不参与任何计算。
/// </summary>
public static class SubagentStatus
{
    /// <summary>还在跑（或建好还没跑完）。</summary>
    public const string Running = "running";

    /// <summary>正常收尾：子 Agent 不再调用工具、交出了报告。</summary>
    public const string Ok = "ok";

    /// <summary>撞上 MaxTurns 或 token 预算，报告是被截断的。</summary>
    public const string MaxTurns = "max_turns";

    /// <summary>出错（模型调用失败、子会话写不进库等），可以续用重试。</summary>
    public const string Failed = "failed";
}
