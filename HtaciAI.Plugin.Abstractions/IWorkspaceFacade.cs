using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件操作工作空间的门面（需要声明 <see cref="PluginCapabilities.WorkspaceDelegate"/>）。
///
/// <b>为什么和 <see cref="ISessionFacade"/> 分开</b>：那边是「本插件自己的会话区」，
/// 这边是「用户的真实工作空间」—— 语义不同，混在一个接口里会让「只能改自己的会话」
/// 这条规则变得难以说清。
///
/// <b>能做什么、不能做什么</b>：这里只开放「看有哪些工作空间」「看某个工作空间里有哪些会话」
/// 「在工作空间里新建一个会话」。改工作空间的配置、删工作空间里的会话都不开放 ——
/// 那是用户在界面上的选择。
///
/// <b>新建的会话登记着工作空间的配置</b>（模型、工具、技能、MCP、权限档位），
/// 与用户在界面上「在工作空间里新建会话」完全一样，所以它在用户的会话列表里也看得见。
///
/// <b>线程</b>：任意线程可调用，宿主自己调度。
/// </summary>
public interface IWorkspaceFacade
{
    /// <summary>全部工作空间，按创建顺序。</summary>
    Task<IReadOnlyList<PluginWorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default);

    /// <summary>某个工作空间里的会话（最近活动在前），最多 <paramref name="take"/> 个。</summary>
    Task<IReadOnlyList<SessionSummary>> ListWorkspaceSessionsAsync(
        string workspaceId, int take = 10, CancellationToken ct = default);

    /// <summary>
    /// 在某个工作空间里新建一个会话，返回新会话 id。工作空间不存在返回 <c>null</c>。
    ///
    /// <paramref name="title"/> 留空时用宿主的默认标题（「新会话」）。
    /// </summary>
    Task<string?> CreateWorkspaceSessionAsync(
        string workspaceId, string? title = null, CancellationToken ct = default);
}

/// <summary>工作空间列表里的一项。</summary>
/// <param name="Id">工作空间 id。</param>
/// <param name="Name">名字。</param>
/// <param name="Path">基准目录（bash / read / write 的相对路径都基于它）。</param>
/// <param name="SessionCount">未删除的会话数。</param>
/// <param name="UpdatedAt">最近一次活动时间，Unix 毫秒（与 <see cref="SessionSummary.UpdatedAt"/> 同口径）。</param>
public sealed record PluginWorkspaceInfo(
    string Id,
    string Name,
    string? Path,
    int SessionCount,
    long UpdatedAt);
