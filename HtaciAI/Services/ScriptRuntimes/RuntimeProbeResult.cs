namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>一次运行时探测的结果。</summary>
public sealed class RuntimeProbeResult
{
    public ScriptRuntimeKind Kind { get; init; }

    /// <summary>是否找到可用的解释器/运行时。</summary>
    public bool Found { get; init; }

    /// <summary>解析出的版本号（如 3.12.4 / v20.11.1，未解析到时为原始输出）。</summary>
    public string Version { get; init; } = "";

    /// <summary>实际生效的解释器路径（手动指定或自动探测到的命令名）。</summary>
    public string? ResolvedPath { get; init; }

    /// <summary>true = 用户手动指定；false = 自动检测 PATH。</summary>
    public bool IsManual { get; init; }

    public string? Error { get; init; }
}
