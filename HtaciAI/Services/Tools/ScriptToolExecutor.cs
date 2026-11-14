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

        // 子进程等多久：入参（模型为这一次申请）> 工具页配的值（0 = 一直等）> 运行器的默认值。
        // 必须与分派器那层取同一个值：配了 15 分钟却让子进程 5 分钟就杀掉，
        // 用户看到的仍然是「超时」，还会以为设置没生效。
        var timeout = BuiltinToolContext.GetIntArg(call, "timeout") ?? tool.TimeoutSeconds;
        var result = await runner.RunAsync(tool.Target, call.Arguments ?? "{}", timeout, ct);
        if (result.Success)
            return ToolExecution.Ok(string.IsNullOrWhiteSpace(result.Output) ? "（脚本无输出）" : result.Output);

        var reason = string.IsNullOrWhiteSpace(result.Error) ? $"退出码非 0：{result.CommandLine}" : result.Error;
        return ToolExecution.Fail($"脚本执行失败：{reason}");
    }
}
