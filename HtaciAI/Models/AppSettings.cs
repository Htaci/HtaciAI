using HtaciAI.Services.Tools;

namespace HtaciAI.Models;

/// <summary>
/// 工作空间列表的展示方式
/// </summary>
public enum WorkspaceDisplayMode
{
    /// <summary>双列表：工作空间列表与会话列表分开两级</summary>
    DualList,
    /// <summary>树状结构：工作空间下直接展开会话</summary>
    Tree
}

/// <summary>
/// 应用设置（暂存于内存，持久化后续实现）
/// </summary>
public static class AppSettings
{
    /// <summary>工作空间展示方式，默认双列表</summary>
    public static WorkspaceDisplayMode DisplayMode { get; set; } = WorkspaceDisplayMode.DualList;

    /// <summary>树状结构下是否自动折叠（选中一个工作空间时收起其他）</summary>
    public static bool TreeAutoCollapse { get; set; }

    // ---- 脚本工具运行时（null = 自动检测系统 PATH）----

    /// <summary>Python 解释器路径（如 C:\Python312\python.exe，null = 自动检测）</summary>
    public static string? RuntimePythonPath { get; set; }

    /// <summary>Node.js 可执行文件路径（如 C:\Program Files\nodejs\node.exe，null = 自动检测）</summary>
    public static string? RuntimeNodePath { get; set; }

    /// <summary>Git 可执行文件路径（如 C:\Program Files\Git\cmd\git.exe，null = 自动检测）</summary>
    public static string? RuntimeGitPath { get; set; }

    // ---- 工具权限审批档位 ----

    /// <summary>工具权限审批档位，默认「普通」（安全工具自动通过）。</summary>
    public static PermissionMode ToolPermissionMode { get; set; } = PermissionMode.Normal;
}
