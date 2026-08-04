using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 工作空间：管理一组项目文件夹及会话
/// </summary>
public class Workspace
{
    public string Name { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public string Model { get; set; } = "";
    public string AgentFramework { get; set; } = "";
    public string PermissionMode { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string EnvVars { get; set; } = "";
    public List<WorkspaceSession> Sessions { get; set; } = new();
}

/// <summary>
/// 工作空间内的会话窗口
/// </summary>
public class WorkspaceSession
{
    public string Title { get; set; } = "";
    public List<string> Messages { get; set; } = new();
}

/// <summary>
/// 创建工作空间时可选的固定选项
/// </summary>
public static class WorkspaceOptions
{
    public static readonly string[] Models =
    {
        "DeepSeek v4 Flash",
        "DeepSeek v4 Pro",
        "GLM 5.2",
        "Kimi K2.6"
    };

    public static readonly string[] AgentFrameworks =
    {
        "HtaciAI",
        "ClaudeCode",
        "OpenAI Codex",
        "QwenCode",
        "OpenCode"
    };

    public static readonly string[] PermissionModes =
    {
        "只读",
        "严格",
        "宽松",
        "自由"
    };

    /// <summary>
    /// 权限模式的提示说明
    /// </summary>
    public static string GetPermissionHint(string mode) => mode switch
    {
        "只读" => "只允许只读工具",
        "严格" => "允许只读，编辑修改操作和命令执行需要审批",
        "宽松" => "允许只读和编辑操作，执行命令需要审批",
        "自由" => "允许所有操作，不需要审批",
        _ => ""
    };
}
