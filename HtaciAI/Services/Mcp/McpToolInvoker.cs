using System;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Mcp;

/// <summary>把 MCP 远端工具调用接到统一工具执行链路上（由 <see cref="ToolExecutorDispatcher"/> 分派）。</summary>
public sealed class McpToolInvoker
{
    public async Task<ToolExecution> ExecuteAsync(ToolDefinition tool, ChatToolCall call, CancellationToken ct)
    {
        var serverId = tool.Target;
        if (string.IsNullOrWhiteSpace(serverId))
            return ToolExecution.Fail($"MCP 工具 {tool.Name} 缺少所属服务信息");

        var client = McpConnectionManager.Instance.GetClient(serverId);
        if (client is null)
            return ToolExecution.Fail("MCP 服务未连接，请到「MCP服务」页面检查配置后重试");

        try
        {
            var content = await client.CallToolAsync(
                McpConnectionManager.RemoteName(tool.Id),
                call.Arguments,
                ct);

            return ToolExecution.Ok(string.IsNullOrWhiteSpace(content) ? "(工具没有返回内容)" : content);
        }
        catch (OperationCanceledException)
        {
            throw;   // 用户点了停止，交给上层处理
        }
        catch (Exception ex)
        {
            // McpException 的消息已经是给人看的（子进程退了、长流断了、超时…），原样带出来
            return ToolExecution.Fail($"MCP 工具调用失败：{ex.Message}");
        }
    }
}
