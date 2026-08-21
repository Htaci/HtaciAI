using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 统一工具分派器（<see cref="IToolExecutor"/> 实现）：
/// 按调用名解析注册表中的工具定义，再按 <see cref="ToolSource"/> 分派执行。
/// 替换 StubToolExecutor 后，ChatGateway 多轮循环即可真实执行工具。
/// </summary>
public sealed class ToolExecutorDispatcher : IToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ScriptToolExecutor _scripts = new();

    public ToolExecutorDispatcher(ToolRegistry? registry = null)
        => _registry = registry ?? ToolRegistry.Instance;

    public async Task<string> ExecuteAsync(ChatToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(call.Name))
            return "工具调用缺少名称";

        var tool = _registry.ResolveByName(call.Name);
        if (tool is null)
            return $"未知工具：{call.Name}";
        if (!tool.Enabled)
            return $"工具未启用：{call.Name}";

        return tool.Source switch
        {
            ToolSource.Script => await _scripts.ExecuteAsync(tool, call, ct),
            ToolSource.Builtin => "内置工具尚未实现",
            _ => $"暂不支持的工具来源：{tool.Source}",
        };
    }
}
