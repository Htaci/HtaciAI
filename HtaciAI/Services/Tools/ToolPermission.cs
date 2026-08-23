namespace HtaciAI.Services.Tools;

/// <summary>
/// 权限审批档位（输入框右下盾牌图标可切换）。决定某个等级的工人在当前档位下是否需要用户确认。
/// </summary>
public enum PermissionMode
{
    /// <summary>严格：所有工具（含安全工具）都必须经用户确认。</summary>
    Strict,
    /// <summary>普通：安全工具自动通过，风险/危险工具需确认。</summary>
    Normal,
    /// <summary>宽松：安全、风险工具自动通过，仅危险工具需确认。</summary>
    Loose,
    /// <summary>自由：所有等级的工具都不需要确认。</summary>
    Free,
}

/// <summary>
/// 权限判定：根据工具的危险等级与当前档位，得出是否需要用户确认。
/// </summary>
public static class ToolPermission
{
    public static bool RequiresApproval(ToolDangerLevel level, PermissionMode mode) => mode switch
    {
        // 严格：不分等级，全部确认
        PermissionMode.Strict => true,
        // 普通：安全自动，其余确认
        PermissionMode.Normal => level != ToolDangerLevel.Safe,
        // 宽松：仅危险确认
        PermissionMode.Loose => level == ToolDangerLevel.Danger,
        // 自由：全部自动
        PermissionMode.Free => false,
        _ => false,
    };
}
