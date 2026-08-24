using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 统一工具分派器（<see cref="IToolExecutor"/> 实现）：
/// 解析注册表中的工具定义，按 <see cref="ToolSource"/> 分派执行，并内嵌安全网关：
///  - 权限审批：按档位（<see cref="Mode"/>）与工具等级决定是否需要用户确认；
///  - read 前置：edit / 覆盖 write 必须在本轮已 read 过目标文件；
///  - 失败时返回 <see cref="ToolExecution.Fail"/>：未知/被禁用工具标记 <see cref="ToolExecution.IsUnknownTool"/>，
///    由 ChatGateway 收敛为 invalid；真实工具运行失败则由网关作为该 tool_call 的失败结果反馈给模型。
/// </summary>
public sealed class ToolExecutorDispatcher : IToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ScriptToolExecutor _scripts = new();
    private readonly BuiltinToolExecutor _builtin = new();

    /// <summary>当前权限审批档位（由输入框权限图标切换，写自 AppSettings）。</summary>
    public PermissionMode Mode { get; set; } = PermissionMode.Normal;

    /// <summary>审批回调：需要用户确认时调用（ChatView 提供弹窗），返回 true=允许执行。</summary>
    public Func<ChatToolCall, ToolDefinition, Task<bool>>? ApprovalGate { get; set; }

    /// <summary>内置工具宿主上下文（基准目录 + 会话技能读写）。</summary>
    public BuiltinToolContext Context { get; set; } = new();

    private readonly HashSet<string> _readFiles = new();

    public ToolExecutorDispatcher(ToolRegistry? registry = null)
        => _registry = registry ?? ToolRegistry.Instance;

    /// <summary>每轮用户消息开始时清空「本轮已读取文件」记录。</summary>
    public void BeginTurn() => _readFiles.Clear();

    public async Task<ToolExecution> ExecuteAsync(ChatToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(call.Name))
            return ToolExecution.Fail("工具调用缺少名称");

        var tool = _registry.ResolveByName(call.Name);
        if (tool is null)
            return ToolExecution.FailUnknownTool($"未知工具：{call.Name}");
        if (!tool.Enabled)
            return ToolExecution.FailUnknownTool($"工具未启用：{call.Name}");

        // read 前置：edit / 覆盖 write 必须已 read
        var prereq = CheckReadPrerequisite(tool, call);
        if (prereq is not null)
            return ToolExecution.Fail(prereq);

        // 权限审批：内部辅助工具（invalid 等哨兵）从不进模型视野，也不应触发用户确认，直接放行。
        if (!tool.IsInternal && ToolPermission.RequiresApproval(tool.DangerLevel, Mode))
        {
            var allowed = ApprovalGate is null ? true : await ApprovalGate(call, tool);
            if (!allowed)
                return ToolExecution.Ok($"用户拒绝执行工具：{tool.Name}");
        }

        // 分派执行
        ToolExecution exec;
        try
        {
            exec = tool.Source switch
            {
                ToolSource.Script => await _scripts.ExecuteAsync(tool, call, ct),
                ToolSource.Builtin => await _builtin.ExecuteAsync(tool, call, Context, ct),
                _ => ToolExecution.Fail($"暂不支持的工具来源：{tool.Source}"),
            };
        }
        catch (Exception ex)
        {
            exec = ToolExecution.Fail($"工具执行失败：{ex.Message}");
        }

        // read 成功 → 记录已读文件，供后续 edit / 覆盖 write 的前置校验
        if (tool.Name == "read" && exec.Success)
            TryRecordRead(call);

        return exec;
    }

    /// <summary>校验 read 前置：edit 必须已 read；write 覆盖已存在文件必须已 read（新建无需）。</summary>
    private string? CheckReadPrerequisite(ToolDefinition tool, ChatToolCall call)
    {
        if (tool.Name is not ("edit" or "write")) return null;

        var filePath = BuiltinToolContext.GetArg(call, "filePath");
        if (string.IsNullOrWhiteSpace(filePath))
            return $"工具 {tool.Name} 缺少 filePath，无法校验读取前置";
        var full = BuiltinToolContext.ResolvePath(Context, filePath);

        if (tool.Name == "edit")
        {
            if (!_readFiles.Contains(full))
                return $"必须先读取一次：编辑 {full} 前需先用 read 工具在该轮内读取该文件";
        }
        else // write
        {
            if (File.Exists(full) && !_readFiles.Contains(full))
                return $"必须先读取一次：覆盖 {full} 前需先用 read 工具在该轮内读取该文件";
        }
        return null;
    }

    /// <summary>read 成功后，把解析后的绝对路径加入本轮已读集合。</summary>
    private void TryRecordRead(ChatToolCall call)
    {
        var filePath = BuiltinToolContext.GetArg(call, "filePath");
        if (string.IsNullOrWhiteSpace(filePath)) return;
        _readFiles.Add(BuiltinToolContext.ResolvePath(Context, filePath));
    }
}
