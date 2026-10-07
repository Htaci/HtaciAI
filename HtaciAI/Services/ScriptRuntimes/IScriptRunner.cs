using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>
/// 脚本运行时适配器：把一个脚本工具调用转换为结果文本。
/// 每种语言一个实现（Python / Node.js，未来终端可独立扩展），由 ScriptToolExecutor 按工具定义分派。
/// 调用协议统一：参数经 stdin 传 JSON，结果从 stdout 读取，非零退出码视为失败。
/// </summary>
public interface IScriptRunner
{
    ScriptRuntimeKind Kind { get; }

    /// <summary>执行脚本：stdin 写入 argumentsJson，stdout 返回结果。</summary>
    Task<ScriptRunResult> RunAsync(string scriptPath, string argumentsJson, CancellationToken ct);
}

/// <summary>一次脚本运行的结果。</summary>
public sealed class ScriptRunResult
{
    public bool Success { get; init; }

    /// <summary>stdout 输出（成功时的工具结果）。</summary>
    public string Output { get; init; } = "";

    /// <summary>stderr 输出或失败原因（超时/取消/解释器缺失）。</summary>
    public string Error { get; init; } = "";

    /// <summary>实际执行的命令行（调试用）。</summary>
    public string CommandLine { get; init; } = "";
}
