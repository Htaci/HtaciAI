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
    /// <b>0 = 不缓存</b>：切走的窗口立刻释放（还在跑任务的除外），切回来重新读库重建。
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
    /// 出站请求是否走系统代理（Windows 的代理设置，以及 <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c> 环境变量）。
    ///
    /// <b>默认关闭（直连）</b>：装了 Clash 这类工具的机器上，系统代理往往指向一个本地端口，
    /// 默认跟随它会让「第一次启动就连不上」变成很难排查的问题。打开后由
    /// <see cref="Services.Network.HttpClientProvider"/> 统一把关，对话、列模型、检查更新、
    /// web_fetch、MCP 全部一起切过去（它们共用同一批 HttpClient）。
    /// </summary>
    public bool UseSystemProxy { get; set; }

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
    /// 回复总结的总开关。<b>默认关闭</b>：关着时构建上下文、system 提示词、界面上的标记
    /// 全部与升级前一致 —— 下面几项即使填了也不生效。
    ///
    /// 打开后每一轮 AI 回复（含它的几十次工具调用）会被压成一段摘要，
    /// 构建上下文时用它替代那一轮的原文。原文不删，仍在库里和界面上。
    /// </summary>
    public bool TurnSummaryEnabled { get; set; }

    /// <summary>
    /// 一轮成功结束后自动总结它的回复（只在 <see cref="TurnSummaryEnabled"/> 打开时生效）。
    /// 关掉后仍可在操作栏上手动点「总结」。
    /// </summary>
    public bool AutoSummarizeReply { get; set; } = true;

    /// <summary>
    /// 构建上下文时末尾保留几轮原文，更早的轮换成它们各自的总结。
    /// <b>0 = 全换成总结</b>（用户消息仍原样保留），此时模型完全失去最近的行为样本。
    /// </summary>
    public int TurnSummaryKeepTurns { get; set; } = 3;

    /// <summary>
    /// 一轮至少要有多大（token）才值得自动总结；0 = 不设阈值。
    /// 只有一两次小工具调用的轮次压了也省不下什么，反而是白花一次模型请求。
    /// </summary>
    public int TurnSummaryThresholdTokens { get; set; } = 8000;

    /// <summary>
    /// 距最后一条消息超过这么多分钟，就认为服务端的提示词缓存已经过期：
    /// 这时候保留最近几轮原文只换来一次实实在在的缓存写入，不如只发总结。0 = 不做这个降级。
    /// </summary>
    public int TurnSummaryIdleMinutes { get; set; } = 5;

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

    /// <summary>
    /// 记录 API 请求流水（「统计」页的数据来源）。<b>默认开启</b>：
    /// 不记的话「这个月花了多少钱」这类问题永远回答不了，而记录本身只是几十字节一行。
    ///
    /// 关掉后新的请求不再入库，<b>已有的历史不删</b> —— 想清空用统计页的「清空记录」。
    /// 只记元数据（模型、耗时、token、金额快照），不记请求体、响应体和 api_key。
    /// </summary>
    public bool ApiRequestLogEnabled { get; set; } = true;

    /// <summary>
    /// API 流水保留多少天，超期在启动时清一次。
    /// <b>0 = 永久保留</b>；统计页可选 30 / 90 / 180 / 365，默认 365。
    /// 一年重度使用约几十万行、几十 MB，永久保留也不是不能承受，
    /// 只是 SQLite 的库文件会一直长。
    /// </summary>
    public int ApiRequestLogRetentionDays { get; set; } = 365;

    // ---- 命令行工具 ----

    /// <summary>
    /// <c>bash</c> 工具背后执行命令的 shell（<see cref="Services.ScriptRuntimes.ShellKind"/> 的名字，
    /// 如 <c>"Cmd"</c> / <c>"PowerShell7"</c> / <c>"GitBash"</c>）。<c>null</c> = 自动：
    /// Windows 用 cmd，其他平台用 /bin/bash。
    ///
    /// <b>用字符串不用枚举</b>：JsonFileStore 遇到无法识别的枚举值会让整份 settings.json
    /// 静默回退默认值；字符串解析失败只影响这一项，别的设置不受牵连。
    /// </summary>
    public string? CommandShell { get; set; }
}
