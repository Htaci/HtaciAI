using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 智能对话会话（chat_sessions 表，会话级"当前配置"）
/// </summary>
public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Model { get; set; } = "";
    public string? SystemPrompt { get; set; }
    /// <summary>
    /// 思考模式（<see cref="HtaciAI.Services.ThinkingMode"/> 的整型值）。
    /// 默认 Default(none)：不向请求写入任何思考字段，走模型自身默认行为。
    /// 注意不能是 0 —— 枚举里 0 是 NoThink（显式关闭思考），旧代码正是因为这个默认值
    /// 让每个新会话都悄悄变成了 NoThink。
    /// </summary>
    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    /// <summary>是否软删除（隐藏于会话列表，供删除会话管理页恢复）。</summary>
    public bool IsDeleted { get; set; }
    public long? DeletedAt { get; set; }

    /// <summary>会话启用的技能列表（含 allowed / loaded 两种状态），构建请求时注入 system。</summary>
    public List<SessionSkill> EnabledSkills { get; set; } = new();

    /// <summary>会话激活的工具 id 集合（工具选择器勾选），重开会话时恢复。</summary>
    public List<string> EnabledToolIds { get; set; } = new();

    /// <summary>会话启用的 MCP 服务 id 集合，发送时其工具会并入请求。</summary>
    public List<string> McpServers { get; set; } = new();

    /// <summary>实际对话请求时间（用户最后发消息）的 unix 毫秒；配置变更不更新它，用于会话排序。</summary>
    public long? LastMessageAt { get; set; }

    /// <summary>输入框草稿：随输入实时落库，重开会话时恢复。</summary>
    public string? Draft { get; set; }

    /// <summary>会话独立的工具权限审批档位（严格/普通/宽松/自由），重开会话时恢复。</summary>
    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;
}
