using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services;

/// <summary>工具执行器：把一次工具调用转换为结果文本（或结构化结果）。</summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(ChatToolCall call, CancellationToken ct);
}
