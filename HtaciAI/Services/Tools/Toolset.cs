using System.Linq;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 工具集：工具的分组，纯粹为了「一起开关、一起管理」的方便。一个工具可属于多个工具集（多对多）；
/// 同一个工具在多个集合里被启用，也仍然只是同一个工具，不会重复生效。
///
/// 其中「全部」与「内置」是<b>自动集合</b>（<see cref="IsAuto"/>）：成员由规则算出来而不是勾出来，
/// 因此不可编辑归属、不可删除。
/// </summary>
public sealed class Toolset
{
    /// <summary>「全部」的 id。旧版「默认集」沿用了这个 id，所以老库里的归属链接依然有意义。</summary>
    public const string AllId = "default";

    /// <summary>「内置」的 id。</summary>
    public const string BuiltinId = "builtin";

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>内置集合：随程序提供，不可删除。</summary>
    public bool IsBuiltin { get; init; }

    /// <summary>
    /// 自动集合：成员按规则计算（全部 / 内置），用户不能往里手工添加工具，也不能把它当成新工具的归属。
    /// </summary>
    public bool IsAuto { get; init; }

    /// <summary>侧栏排序权重：自动集合固定在最前，用户自建集合按名称排在后面。</summary>
    public int SortOrder { get; init; } = 10;

    /// <summary>「全部」：所有工具，无论在不在别的工具集里。</summary>
    public static readonly Toolset All = new()
    {
        Id = AllId,
        Name = "全部",
        Description = "所有工具，无论属于哪个工具集",
        IsBuiltin = true,
        IsAuto = true,
        SortOrder = 0,
    };

    /// <summary>「内置」：程序自带的 C# 工具（脚本工具与 MCP 工具不在其中）。</summary>
    public static readonly Toolset Builtin = new()
    {
        Id = BuiltinId,
        Name = "内置",
        Description = "程序内置的工具",
        IsBuiltin = true,
        IsAuto = true,
        SortOrder = 1,
    };

    /// <summary>是否为自动集合（成员不可手工编辑）。</summary>
    public static bool IsAutoId(string id) => id is AllId or BuiltinId;

    /// <summary>
    /// 工具是否属于该集合。「全部」「内置」按规则判定，其余查工具的归属链接。
    /// </summary>
    public static bool Contains(string toolsetId, ToolDefinition tool) => toolsetId switch
    {
        AllId => true,
        BuiltinId => tool.Source == ToolSource.Builtin,
        _ => tool.ToolsetIds.Contains(toolsetId),
    };
}
