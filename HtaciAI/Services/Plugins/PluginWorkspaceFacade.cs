using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// <see cref="IWorkspaceFacade"/> 的宿主实现：让插件在用户真实的工作空间里派任务。
///
/// <b>为什么允许插件建这里的会话</b>：工作空间才有目录、工具、MCP 与模型 ——
/// 想「让机器人替我在某个项目里干活」，就只能落在这里。新建的会话与用户在界面上
/// 「在工作空间里新建会话」完全相同（同一套继承规则），所以它在用户的侧栏里也看得见，
/// 用完可以由用户自己清理，不需要插件做额外的事。
///
/// <b>不做的事</b>：改工作空间配置、删会话、改别人的会话。那些是用户在界面上的选择。
///
/// <b>线程</b>：只读库 / 只写库，不碰运行时，因此在任意线程上都可以跑。
/// </summary>
internal sealed class PluginWorkspaceFacade(string pluginId) : IWorkspaceFacade
{
    /// <summary>一次最多列多少个会话。工作空间的会话数可能很多，插件画列表用不上全部。</summary>
    private const int MaxListedSessions = 200;

    public async Task<IReadOnlyList<PluginWorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            var workspaces = await WorkspaceRepository.GetAllAsync();
            ct.ThrowIfCancellationRequested();

            // 会话数与最近活动一次分组聚合取回，不逐个工作空间查一遍
            var stats = await WorkspaceSessionRepository.GetStatsAsync();

            return workspaces
                .Select(w =>
                {
                    stats.TryGetValue(w.Id, out var stat);
                    return new PluginWorkspaceInfo(
                        w.Id,
                        w.Name,
                        string.IsNullOrWhiteSpace(w.Path) ? null : w.Path,
                        stat.Count,
                        stat.LastAt > 0 ? stat.LastAt : w.UpdatedAt);
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PluginWorkspaceInfo>();
        }
        catch (Exception ex)
        {
            PluginLog.Error(pluginId, "列出工作空间失败", ex);
            return Array.Empty<PluginWorkspaceInfo>();
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> ListWorkspaceSessionsAsync(
        string workspaceId, int take = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || take <= 0)
            return Array.Empty<SessionSummary>();

        try
        {
            var sessions = await WorkspaceSessionRepository.GetByWorkspaceAsync(workspaceId);
            ct.ThrowIfCancellationRequested();

            // 仓储已按「最近活动倒序」排好，这里只截断
            var picked = sessions.Take(Math.Min(take, MaxListedSessions)).ToList();
            var models = picked.ToDictionary(s => s.Id, s => s.Model ?? "", StringComparer.Ordinal);
            var usage = await PluginUsageReader.ReadContextUsageAsync(
                picked.Select(s => s.Id).ToList(), id => models[id], ct);

            return picked
                .Select(s =>
                {
                    usage.TryGetValue(s.Id, out var context);
                    return new SessionSummary(
                        s.Id, s.Title ?? "", s.LastMessageAt ?? s.CreatedAt, context.Used, context.Window);
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SessionSummary>();
        }
        catch (Exception ex)
        {
            PluginLog.Error(pluginId, $"列出工作空间会话失败：{workspaceId}", ex);
            return Array.Empty<SessionSummary>();
        }
    }

    /// <summary>
    /// 在工作空间里新建会话。继承规则与界面上的「新建会话」同一套
    /// （<see cref="WorkspaceAgentPipeline.CreateSessionDefaults"/>），
    /// 模型取工作空间默认模型 → 上次使用的模型。
    /// </summary>
    public async Task<string?> CreateWorkspaceSessionAsync(
        string workspaceId, string? title = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return null;

        try
        {
            var workspace = await WorkspaceRepository.GetAsync(workspaceId);
            if (workspace is null) return null;

            ct.ThrowIfCancellationRequested();

            var session = WorkspaceAgentPipeline.CreateSessionDefaults(workspace, null);

            var trimmed = (title ?? "").Trim();
            if (trimmed.Length > 0)
                session.Title = trimmed.Length <= 100 ? trimmed : trimmed[..100];

            session.Model = (await AppDefaults.ResolveDefaultModelAsync(workspace.DefaultModelId))?.ModelName ?? "";

            await WorkspaceSessionRepository.CreateAsync(session);
            PluginLog.Info(pluginId, $"在工作空间 {workspaceId} 里新建会话 {session.Id}");
            return session.Id;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(pluginId, $"新建工作空间会话失败：{workspaceId}", ex);
            return null;
        }
    }
}
