using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Conversations;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// system 提示词的拼装：档案自己的提示词 + 各插件登记的贡献，最后统一截断。
///
/// <b>为什么不是改 <see cref="IConversationProfile.BuildSystemPrompt"/></b>：
/// 那两个实现只是纯字符串拼接，改成 <c>Task&lt;string&gt;</c> 会把接口、两个实现、
/// 以及「工作空间那套三层拼装不能与普通对话合流」的注释一起搅动，收益为零。
/// 这里包一层就够了。
///
/// <b>配额是硬约束</b>：插件这条路能悄悄改变模型行为，所以既有单插件上限也有单轮合计上限，
/// 超了就截断并记日志 —— 而不是让某一个插件把整个 system 撑爆。
/// </summary>
public static class PromptComposer
{
    /// <summary>单个插件每轮最多贡献多少字符。</summary>
    public const int PerPluginLimit = 4 * 1024;

    /// <summary>一轮所有插件合计最多贡献多少字符。</summary>
    public const int TotalLimit = 8 * 1024;

    /// <summary>单个贡献者的超时。慢插件不能拖住发送。</summary>
    private static readonly TimeSpan ContributorTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 组装本轮 system：先取档案自己的提示词，再按 <see cref="IPromptContributor.Order"/>
    /// 依次追加插件贡献，最后统一截断。
    ///
    /// <b>永远返回可用字符串</b>：贡献者抛异常、超时、返回空白，都只是少一段内容，
    /// 绝不会让这一轮发送失败。
    /// </summary>
    public static async Task<string> ComposeAsync(
        IConversationProfile profile, string modelId, string sessionId, CancellationToken ct)
    {
        var builder = new StringBuilder(profile.BuildSystemPrompt(modelId));

        var contributors = PluginPromptRegistry.Snapshot();
        if (contributors.Count == 0) return builder.ToString();

        var context = new PromptContext(sessionId, modelId);
        var total = 0;

        foreach (var entry in contributors)
        {
            var text = await BuildAsync(entry, context, ct);

            // 这一轮没贡献（或失败）也要把统计归零：插件页显示的是「最近一次」而不是「曾经最多」
            if (string.IsNullOrWhiteSpace(text))
            {
                PluginPromptUsage.Set(entry.PluginId, 0);
                continue;
            }

            var pluginChars = text.Length;
            if (pluginChars > PerPluginLimit)
            {
                PluginLog.Warn(entry.PluginId,
                    $"上下文贡献超过单插件上限（{pluginChars} > {PerPluginLimit} 字），已截断");
                text = text[..PerPluginLimit];
                pluginChars = PerPluginLimit;
            }

            var remaining = TotalLimit - total;
            if (remaining <= 0)
            {
                PluginLog.Warn(entry.PluginId, $"本轮上下文贡献已达合计上限 {TotalLimit} 字，跳过该贡献者");
                PluginPromptUsage.Set(entry.PluginId, 0);
                continue;
            }

            if (pluginChars > remaining)
            {
                PluginLog.Warn(entry.PluginId,
                    $"本轮上下文贡献合计将超过 {TotalLimit} 字，该贡献者截断到 {remaining} 字");
                text = text[..remaining];
                pluginChars = remaining;
            }

            builder.AppendLine();
            builder.AppendLine($"<!-- plugin: {entry.PluginId} -->");
            builder.AppendLine(text.TrimEnd());
            total += pluginChars;
            PluginPromptUsage.Set(entry.PluginId, pluginChars);
        }

        return builder.ToString();
    }

    /// <summary>取一个贡献者的文本。任何失败都收成 null（并记日志），调用方按「没贡献」处理。</summary>
    private static async Task<string?> BuildAsync(
        PluginPromptRegistry.Entry entry, PromptContext context, CancellationToken ct)
    {
        try
        {
            // ValueTask.AsTask()：同一个贡献者被并发调用（两个会话同时发送）时也能安全地 WaitAsync
            return await entry.Contributor.BuildAsync(context, ct).AsTask().WaitAsync(ContributorTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点了停止：这一轮本来就不发了，安静跳过
            return null;
        }
        catch (TimeoutException)
        {
            // Task.WaitAsync 的超时抛的是 TimeoutException 而不是 OperationCanceledException，
            // 不单独接住就会落到下面的「抛异常」分支，日志读起来像是贡献者代码有 bug。
            PluginLog.Warn(entry.PluginId,
                $"上下文贡献超时（>{ContributorTimeout.TotalMilliseconds:0} ms），已跳过");
            return null;
        }
        catch (OperationCanceledException)
        {
            PluginLog.Warn(entry.PluginId,
                $"上下文贡献超时（>{ContributorTimeout.TotalMilliseconds:0} ms），已跳过");
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(entry.PluginId, "上下文贡献抛异常，已跳过", ex);
            return null;
        }
    }
}

/// <summary>
/// 全进程的贡献者名单。插件的 <see cref="IPromptRegistry"/> 就是往这里增删。
///
/// <b>线程</b>：登记/撤销可能来自任意线程（插件自己的定时器、UI 线程），读取发生在发送前，
/// 所以内部加锁并用快照读取。
/// </summary>
public static class PluginPromptRegistry
{
    internal sealed record Entry(string PluginId, IPromptContributor Contributor);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();

    /// <summary>
    /// 登记一个贡献者。返回的 <see cref="IDisposable"/> 撤销登记（重复 Dispose 安全）。
    /// </summary>
    internal static IDisposable Register(string pluginId, IPromptContributor contributor)
    {
        var entry = new Entry(pluginId, contributor);
        lock (Gate) Entries.Add(entry);
        return new Registration(entry);
    }

    /// <summary>
    /// 当前名单的副本，按 <see cref="IPromptContributor.Order"/> 排；同序按插件 id 排，
    /// 保证每轮顺序稳定（否则同样的插件组合会拼出不同的 system，模型行为跟着抖）。
    /// </summary>
    internal static IReadOnlyList<Entry> Snapshot()
    {
        lock (Gate)
        {
            return Entries
                .OrderBy(e => e.Contributor.Order)
                .ThenBy(e => e.PluginId, StringComparer.Ordinal)
                .ThenBy(e => e.Contributor.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    private sealed class Registration(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;

        public void Dispose()
        {
            var current = _entry;
            if (current is null) return;

            _entry = null;
            lock (Gate) Entries.Remove(current);
        }
    }
}

/// <summary>
/// 每个插件「最近一次实际贡献了多少字符」，供插件页显示。
///
/// <b>刻意只在内存里</b>：这是「上一次发生了什么」，重启后没有意义；
/// 也不该为它加数据库字段。
/// </summary>
public static class PluginPromptUsage
{
    private static readonly ConcurrentDictionary<string, int> Chars = new(StringComparer.Ordinal);

    internal static void Set(string pluginId, int chars)
    {
        if (chars <= 0) Chars.TryRemove(pluginId, out _);
        else Chars[pluginId] = chars;
    }

    /// <summary>最近一次贡献的字符数；0 表示最近一次没有贡献。</summary>
    public static int Get(string pluginId) => Chars.TryGetValue(pluginId, out var chars) ? chars : 0;

    /// <summary>插件被卸载 / 禁用时清掉它的记录。</summary>
    public static void Clear(string pluginId) => Chars.TryRemove(pluginId, out _);
}

/// <summary>
/// <see cref="IPromptRegistry"/> 的宿主实现：一个插件一个实例。
///
/// 登记出来的 <see cref="IDisposable"/> 会同时进 <see cref="PluginEntry.Subscriptions"/>，
/// 这样插件忘了撤销也不会继续生效 —— 禁用插件时宿主统一收掉。
/// </summary>
public sealed class PluginPromptFacade(string pluginId, List<IDisposable> subscriptions) : IPromptRegistry
{
    public IDisposable Add(IPromptContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);

        var registration = PluginPromptRegistry.Register(pluginId, contributor);
        subscriptions.Add(registration);
        return registration;
    }
}
