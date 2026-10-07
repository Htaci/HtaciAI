using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>Node.js 脚本运行时适配器（.js / .ts，.ts 依赖 Node 22.6+ 原生剥类型或 tsx）。</summary>
public sealed class NodeScriptRunner : IScriptRunner
{
    public ScriptRuntimeKind Kind => ScriptRuntimeKind.Node;

    public Task<ScriptRunResult> RunAsync(string scriptPath, string argumentsJson, CancellationToken ct)
        => ScriptProcess.RunAsync(ScriptRuntimeKind.Node, scriptPath, argumentsJson, ct);
}
