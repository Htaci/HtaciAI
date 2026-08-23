using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>工具执行器：把一次工具调用转换为结果文本（或结构化结果）。</summary>
public interface IToolExecutor
{
    Task<ToolExecution> ExecuteAsync(ChatToolCall call, CancellationToken ct);

    /// <summary>
    /// 每轮用户消息开始时调用，清零本轮的工具状态（如「已读取文件」记录）。
    /// </summary>
    void BeginTurn();
}
