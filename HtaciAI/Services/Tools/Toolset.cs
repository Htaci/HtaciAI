namespace HtaciAI.Services.Tools;

/// <summary>
/// 工具集：工具的分组。一个工具可属于多个工具集（多对多）；
/// "默认集"内置、不可删除，创建工具时若未指定归属则自动归入默认集。
/// </summary>
public sealed class Toolset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsBuiltin { get; init; }

    /// <summary>内置默认集：未指定归属的工具自动归入此集合。</summary>
    public static readonly Toolset Default = new()
    {
        Id = "default",
        Name = "默认集",
        Description = "未指定归属的工具默认所在的工具集",
        IsBuiltin = true,
    };
}
