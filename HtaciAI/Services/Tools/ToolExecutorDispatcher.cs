using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Plugins;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 统一工具分派器（<see cref="IToolExecutor"/> 实现）：
/// 解析注册表中的工具定义，按 <see cref="ToolSource"/> 分派执行，并内嵌安全网关：
///  - 权限审批：按档位（<see cref="Mode"/>）与工具等级决定是否需要用户确认；
///  - read 前置：edit / 覆盖 write 必须在本轮已 read 过目标文件；
///  - 超时与强制停止：单次执行默认 5 分钟（工具入参声明了 timeout 则由模型指定），
///    点「停止」时不再等工具自愿退出，拿不到结果就如实写回给模型；
///  - 失败时返回 <see cref="ToolExecution.Fail"/>：未知/被禁用工具标记 <see cref="ToolExecution.IsUnknownTool"/>，
///    由 ChatGateway 收敛为 invalid；真实工具运行失败则由网关作为该 tool_call 的失败结果反馈给模型。
/// </summary>
public sealed class ToolExecutorDispatcher : IToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ScriptToolExecutor _scripts = new();
    private readonly BuiltinToolExecutor _builtin = new();
    private readonly McpToolInvoker _mcp = new();

    /// <summary>当前权限审批档位（由输入框权限图标切换，写自 AppSettings）。</summary>
    public PermissionMode Mode { get; set; } = PermissionMode.Normal;

    /// <summary>
    /// 审批回调：需要用户确认时调用（由会话视图提供卡片），返回 true=允许执行。
    /// 必须把 <paramref name="ct"/> 传下去 —— 用户点「停止」时，挂起的审批不能继续把这一轮吊着。
    /// </summary>
    public Func<ChatToolCall, ToolDefinition, CancellationToken, Task<bool>>? ApprovalGate { get; set; }

    /// <summary>内置工具宿主上下文（基准目录 + 会话技能读写）。</summary>
    public BuiltinToolContext Context { get; set; } = new();

    private readonly HashSet<string> _readFiles = new();

    public ToolExecutorDispatcher(ToolRegistry? registry = null)
        => _registry = registry ?? ToolRegistry.Instance;

    /// <summary>每轮用户消息开始时清空「本轮已读取文件」记录。</summary>
    public void BeginTurn() => _readFiles.Clear();

    public async Task<ToolExecution> ExecuteAsync(ChatToolCall call, CancellationToken ct)
    {
        // 已经开始停止就不要再做任何准备动作（审批、read 校验）了
        ct.ThrowIfCancellationRequested();

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

        // 权限审批：内部辅助工具（invalid 等哨兵）从不进模型视野，也不应触发用户确认；
        // SkipApproval 的工具本身就是一次用户交互（ask_user_question），同样直接放行。
        if (!tool.IsInternal && !tool.SkipApproval && ToolPermission.RequiresApproval(tool.DangerLevel, Mode))
        {
            var allowed = ApprovalGate is null ? true : await ApprovalGate(call, tool, ct);
            ct.ThrowIfCancellationRequested();   // 等待审批期间点了停止
            if (!allowed)
                return ToolExecution.Ok($"用户拒绝执行工具：{tool.Name}");
        }

        // 分派执行，外面裹一层「超时 + 强制停止」的守卫。这两个缺口都只有这一层能兜：
        //  - 超时：以前一层都没有，一个不回应的工具能把整轮对话吊到天荒地老。默认 5 分钟；
        //    工具入参声明了 timeout 就按入参走（AI 想跑长任务就自己抬）。
        //  - 停止：本来只是把令牌传下去、等工具「自愿」退出。压根不看令牌的工具（阻塞在同步 IO
        //    或原生调用里）会让「停止」看起来毫无反应。这里用 WaitAsync 把「我们这边的等待」
        //    与「工具内部执行」拆开：等不下去就丢下它，并把「被强制停止」写成结果回给模型。
        var timeoutSec = ResolveTimeoutSeconds(tool, call);
        var timeoutCts = timeoutSec > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
            : null;
        var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var execTask = DispatchAsync(tool, call, linked.Token);
        var abandoned = false;
        ToolExecution exec;
        try
        {
            exec = await execTask.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点了停止。取消已经发给工具（上面那个令牌），它自己该杀进程就杀掉，
            // 我们不再等它 —— 等它就是这个按钮「没反应」的全部原因。
            Abandon(execTask);
            abandoned = true;
            return ToolExecution.Fail("已被用户强制停止：执行过程中断，未取得结果");
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            Abandon(execTask);
            abandoned = true;
            return ToolExecution.Fail(
                $"执行超时（>{timeoutSec} 秒），已强制终止" +
                (DeclaresTimeoutArg(tool) ? "；确实需要更久就在 timeout 参数里传更大的秒数" : ""));
        }
        catch (Exception ex)
        {
            exec = ToolExecution.Fail($"工具执行失败：{ex.Message}");
        }
        finally
        {
            if (!abandoned)
            {
                linked.Dispose();
                timeoutCts?.Dispose();
            }
            // 丢下的那个任务还攥着这两个令牌：这时释放它们，工具再 register 就会抛
            // ObjectDisposedException。留给 GC 收 —— 只发生在「工具根本不看令牌」这条路径上。
        }

        // read 成功 → 记录已读文件，供后续 edit / 覆盖 write 的前置校验
        if (tool.Name == "read" && exec.Success)
            TryRecordRead(call);

        return exec;
    }

    /// <summary>单个工具执行的默认超时（秒）。工具入参声明了 timeout 时以入参为准。</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>分派到各来源的执行器（超时与取消的包装在 <see cref="ExecuteAsync"/> 那层，这里不做）。</summary>
    private Task<ToolExecution> DispatchAsync(ToolDefinition tool, ChatToolCall call, CancellationToken ct)
        => tool.Source switch
        {
            ToolSource.Script => _scripts.ExecuteAsync(tool, call, ct),
            ToolSource.Builtin => _builtin.ExecuteAsync(tool, call, Context, ct),
            ToolSource.Mcp => _mcp.ExecuteAsync(tool, call, ct),
            ToolSource.Plugin => PluginToolRegistry.InvokeAsync(tool, call, Context.SessionId, ct),
            _ => Task.FromResult(ToolExecution.Fail($"暂不支持的工具来源：{tool.Source}")),
        };

    /// <summary>
    /// 丢下一个不肯收手的工具任务：只把异常观察掉（不观察会在进程里留下未处理异常），
    /// 其余交给它自己 —— 取消指令已经发出去了，该杀的子进程它会杀。
    /// </summary>
    private static void Abandon(Task task)
        => _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// 这次调用最多等多久（秒）。0 = 不限时。三个例外：
    /// ask_user_question 等的是人，人要多慢有多慢，超时等于把一张还在作答的卡片变成失败；
    /// ask_user_continue 同理 —— 而且它自己按 timeout_seconds 管时间，超时后返回的是
    /// 「还在等，要不要再等一次」而不是「被强制终止」，这一层再套一个硬超时就把那份体面毁了；
    /// 入参只在工具<b>自己声明了 timeout</b> 时才认 —— MCP 的入参是原样发给远端服务的，
    /// 塞一个它没声明过的参数会被严格校验的服务拒掉。
    /// </summary>
    private static int ResolveTimeoutSeconds(ToolDefinition tool, ChatToolCall call)
    {
        if (tool.Name == BuiltinTools.AskUserName) return 0;
        if (tool.Name == BuiltinTools.AskContinueName) return 0;

        if (DeclaresTimeoutArg(tool) && BuiltinToolContext.GetIntArg(call, "timeout") is { } seconds)
            return Math.Clamp(seconds, 1, 24 * 3600);

        return DefaultTimeoutSeconds;
    }

    /// <summary>工具入参 schema 里有没有 timeout（模型只会传 schema 里声明过的参数）。</summary>
    private static bool DeclaresTimeoutArg(ToolDefinition tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.InputSchemaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("properties", out var props)
                   && props.ValueKind == JsonValueKind.Object
                   && props.TryGetProperty("timeout", out _);
        }
        catch
        {
            return false;
        }
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
