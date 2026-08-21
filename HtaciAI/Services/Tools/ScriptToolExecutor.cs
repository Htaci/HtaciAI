using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 脚本工具执行器：按工具定义的运行时，把一次工具调用分派给对应的 <see cref="IScriptRunner"/>。
/// </summary>
public sealed class ScriptToolExecutor
{
    private readonly PythonScriptRunner _python = new();
    private readonly NodeScriptRunner _node = new();

    public async Task<string> ExecuteAsync(ToolDefinition tool, ChatToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tool.Target))
            return "工具配置错误：缺少脚本路径";

        var runner = tool.Runtime switch
        {
            ScriptRuntimeKind.Python => (IScriptRunner)_python,
            ScriptRuntimeKind.Node => _node,
            _ => null,
        };
        if (runner is null)
            return $"暂不支持的运行时：{tool.Runtime}";

        var result = await runner.RunAsync(tool.Target, call.Arguments ?? "{}", ct);
        if (result.Success)
            return string.IsNullOrWhiteSpace(result.Output) ? "（脚本无输出）" : result.Output;

        var reason = string.IsNullOrWhiteSpace(result.Error) ? $"退出码非 0：{result.CommandLine}" : result.Error;
        return $"脚本执行失败：{reason}";
    }
}
