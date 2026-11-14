using System;
using System.Collections.Generic;
using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 插件会话（plugin_sessions 表）。插件调 <c>ISessionFacade.CreateSessionAsync</c> 时创建，
/// 一行 = 一个只属于这个插件的会话区。消息复用 <see cref="ChatMessage"/> 那套（session_id 即本会话 Id）。
///
/// <b>为什么不挤进 chat_sessions</b>：见 <c>DatabaseService.RunMigrationV27</c> 与
/// <see cref="SubagentSession"/> 的说明 —— 单开一张表，用户界面那些查询一行都不用改。
///
/// <b>归属不可改</b>：<see cref="PluginId"/> 建好之后没有任何写入路径会改它，
/// 插件的「我只能删自己的会话」这条规则就落在这一个字段上。
/// </summary>
public sealed class PluginSession
{
    /// <summary>
    /// id 前缀。<b>四套会话共用一套 id 空间，靠前缀保证不会撞车</b>，
    /// <c>SessionProfileResolver</c> 也靠它判断要不要去查本表。
    /// </summary>
    public const string IdPrefix = "plugin-";

    public string Id { get; set; } = IdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>创建它的插件 id（插件目录名）。</summary>
    public string PluginId { get; set; } = "";

    public string Title { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>思考模式（<see cref="HtaciAI.Services.ThinkingMode"/> 的整型值），理由同 <see cref="ChatSession.Thinking"/>。</summary>
    public int Thinking { get; set; } = (int)HtaciAI.Services.ThinkingMode.Default;

    public string? SystemPrompt { get; set; }

    public List<string> EnabledToolIds { get; set; } = new();

    public List<SessionSkill> EnabledSkills { get; set; } = new();

    public List<string> McpServers { get; set; } = new();

    public PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;

    public long? LastMessageAt { get; set; }

    public bool IsDeleted { get; set; }

    public long? DeletedAt { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
