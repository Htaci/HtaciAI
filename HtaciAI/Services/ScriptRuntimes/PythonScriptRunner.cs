using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>Python 脚本运行时适配器（.py）。</summary>
public sealed class PythonScriptRunner : IScriptRunner
{
    public ScriptRuntimeKind Kind => ScriptRuntimeKind.Python;

    public Task<ScriptRunResult> RunAsync(string scriptPath, string argumentsJson, int? timeoutSeconds, CancellationToken ct)
        => ScriptProcess.RunAsync(ScriptRuntimeKind.Python, scriptPath, argumentsJson, timeoutSeconds, ct);
}
