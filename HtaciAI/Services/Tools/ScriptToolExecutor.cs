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

    public async Task<ToolExecution> ExecuteAsync(ToolDefinition tool, ChatToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tool.Target))
            return ToolExecution.Fail("工具配置错误：缺少脚本路径");

        var runner = tool.Runtime switch
        {
            ScriptRuntimeKind.Python => (IScriptRunner)_python,
            ScriptRuntimeKind.Node => _node,
            _ => null,
        };
        if (runner is null)
            return ToolExecution.Fail($"暂不支持的运行时：{tool.Runtime}");

        // 工具入参声明了 timeout 就按它设子进程超时（AI 想跑长时间脚本就自己抬），
        // 与分派器那一层的超时守卫取同一个值，先触发的一定是这条更具体的。
        var result = await runner.RunAsync(tool.Target, call.Arguments ?? "{}",
            BuiltinToolContext.GetIntArg(call, "timeout"), ct);
        if (result.Success)
            return ToolExecution.Ok(string.IsNullOrWhiteSpace(result.Output) ? "（脚本无输出）" : result.Output);

        var reason = string.IsNullOrWhiteSpace(result.Error) ? $"退出码非 0：{result.CommandLine}" : result.Error;
        return ToolExecution.Fail($"脚本执行失败：{reason}");
    }
}
