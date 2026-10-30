using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 业务设置（默认配置页的那些项），落盘于 <c>&lt;数据根&gt;/settings.json</c>。
///
/// 位置说明：放在数据根而不是固定的 <c>%APPDATA%\HtaciAI\storage.json</c>——
/// 后者只放「根目录怎么选」这类引导信息（见 <see cref="StorageSettings"/>），
/// 业务设置应该跟着数据走，便携版拷到别的机器上时设置也一起过去。
///
/// <b>null 与空列表的区别</b>（靠 JsonFileStore 的 WhenWritingNull 实现）：
/// <list type="bullet">
///   <item><c>null</c> → 用内置的默认行为（如「全部内置工具」）</item>
///   <item><c>[]</c>   → 用户显式清空（如「一个工具都不开」）</item>
/// </list>
/// 两者在 JSON 里可区分：null 的键直接不写，空列表会写成 <c>[]</c>。
/// </summary>
public sealed class AppSettingsData
{
    /// <summary>是否启用自动话题命名。</summary>
    public bool AutoTopicNaming { get; set; } = true;

    /// <summary>话题命名用的模型 id。空 = 不调 AI，直接用首条消息截断。</summary>
    public string? TopicNamingModelId { get; set; }

    /// <summary>新建会话的默认模型 id。空 = 用「上次使用的模型」。</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>默认开启的工具 id。null = 全部可见内置工具。</summary>
    public List<string>? DefaultToolIds { get; set; }

    /// <summary>默认开启的技能。null / 空 = 普通会话默认不开启（AI 仍可主动 load）。</summary>
    public List<SessionSkill>? DefaultSkills { get; set; }

    /// <summary>默认开启的 MCP 服务器 id。null / 空 = 不启用。</summary>
    public List<string>? DefaultMcpServerIds { get; set; }

    // ---- 常规设置 ----

    /// <summary>
    /// 会话窗口切走后保留多久（分钟）。保留期内切回来直接复用原窗口，不重新加载历史。
    /// 与下面那个开关配合：<see cref="SessionCacheKeepForever"/> 为真时本字段被忽略。
    /// </summary>
    public int SessionCacheMinutes { get; set; } = 10;

    /// <summary>会话窗口一直保留（不按时间淘汰）。代价是打开过的会话会持续占用内存。</summary>
    public bool SessionCacheKeepForever { get; set; }

    /// <summary>
    /// 允许 AI 自我扩展：暴露 <c>create_tool</c> / <c>create_skill</c> 两个工具，
    /// 让模型能新建脚本工具与技能。
    ///
    /// 只有「这两个工具是否出现在工具列表里」这一件事受它控制 —— 权限审批、重名检查、
    /// 路径约束都不因此放松。关掉它也不会删掉已经建好的工具（那些东西仍归用户在工具页管理）。
    /// </summary>
    public bool AllowAgentSelfExtension { get; set; } = true;

    /// <summary>
    /// 是否加载插件（<c>&lt;数据根&gt;/plugins</c> 下那些）。
    ///
    /// <b>默认关闭，且这不是安全沙箱</b>：插件在宿主进程里跑，能读写文件、能建窗口，
    /// 宿主只能做到「声明能力、启用前展示、出错隔离、崩溃归因」。默认关是因为
    /// 加载即执行，用户没主动打开之前不该有任何第三方代码跑起来。
    /// </summary>
    public bool EnablePlugins { get; set; }

    /// <summary>
    /// 更新清单地址（形如 <c>{"version":"1.2.0","url":"…","notes":"…"}</c> 的 JSON）。
    /// 空 = 未配置，「检查更新」按钮会引导用户来这里填。
    /// </summary>
    public string? UpdateManifestUrl { get; set; }

    /// <summary>
    /// 上下文占用超过 <see cref="AutoCompressThresholdPercent"/> 时，发请求前先自动压缩一次。
    /// 手动压缩（输入框「更多功能」菜单里那一项）不受本开关影响，随时可用。
    /// </summary>
    public bool AutoCompressContext { get; set; } = true;

    /// <summary>
    /// 自动压缩的阈值（百分比，1–100）。
    /// <b>用 int 不用枚举</b>：JsonFileStore 遇到无法识别的枚举字符串会让整份 settings.json
    /// 静默回退默认值，int 没有这个风险。
    /// </summary>
    public int AutoCompressThresholdPercent { get; set; } = 80;

    /// <summary>
    /// 直接执行任务：在 system 提示词里追加一段「按用户明确要求执行、不空泛推脱」的指令。
    ///
    /// <b>默认关闭</b>：关着时提示词与以前一字不差，行为不会因为升级而变化。
    /// 打开后仍然保留「真实危害」那几条底线（见 <see cref="Services.TaskExecutionDirective"/>）——
    /// 这个开关管的是「少打太极」，不是「闭眼执行」。
    /// </summary>
    public bool ObeyUserTasks { get; set; }

    /// <summary>
    /// 一次用户消息内允许的工具调用最大轮数。
    /// <b>≤ 0 表示不限</b>（哨兵值，UI 上用「不限制」复选框表达）。
    /// 超过上限时该轮对话会被硬截断，模型没机会给出最终回答 ——
    /// 复杂任务（多文件改动、多步排查）很容易超过几十轮，所以默认给到 100。
    /// </summary>
    public int MaxToolRounds { get; set; } = 100;

    /// <summary>
    /// 会话窗口打开时先渲染多少「轮」历史，之后滚到顶部再按同样数量追加上去。
    /// 只影响渲染，不影响发给模型的上下文（始终是完整历史）。
    /// </summary>
    public int HistoryInitialTurns { get; set; } = 10;
}
