using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>工具桩实现：当前无真实工具，返回占位结果，保证工具循环可跑通。</summary>
public sealed class StubToolExecutor : IToolExecutor
{
    public Task<ToolExecution> ExecuteAsync(ChatToolCall call, CancellationToken ct)
        => Task.FromResult(ToolExecution.Ok($"工具「{call.Name}」尚未实现。入参：{call.Arguments ?? "（无）"}"));

    public void BeginTurn() { }
}
