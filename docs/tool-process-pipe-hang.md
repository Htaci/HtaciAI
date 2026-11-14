# bash 工具在「子进程派生 GUI / 长驻进程」时会永久卡住不返回

来自 QQ 机器人插件的实测反馈（2026-11-12）。这份文档只描述宿主侧的缺陷与修法，
插件侧不打算（也不该）绕过它 —— `CreateNoWindow` 之类的设置改变不了下面的问题。

## 现象

- 模型调用 `bash` 跑了某条会弹出系统窗口的命令（编辑框、确认框、GUI 程序、需要用户点确定的分支）。
- 那个窗口没人关之前，`bash` 工具**一直不返回**，不会在 60 秒（`bash` 的默认超时）时失败。
- 整轮对话就此停住。QQ 侧只能靠插件自己的上限放哨，此时错误信息是
  「这一轮超过 N 分钟还没结束」而不是「命令执行超时」—— 用户看不出真正原因。

日志里的表现：会话最后一条消息是 `role=tool / status=interrupted` 或干脆停在某个
`assistant` 后不再有 `RoundCompleted`，之后该会话任何新消息都撞 `Busy`。

## 根因（两处，都在 `HtaciAI/Services/Tools/BuiltinToolExecutor.cs`）

`RunProcessAsync`（约 280-330 行）：

```csharp
var stdoutTask = p.StandardOutput.ReadToEndAsync();
var stderrTask = p.StandardError.ReadToEndAsync();

using var cts = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)) : null;
using var linked = ...;

await p.WaitForExitAsync(linked.Token);   // ← 只等「直接子进程」

// ...超时分支会 Kill(entireProcessTree: true) 后 return

var stdout = await stdoutTask;            // ← 这里没有超时、没有取消，也不看令牌
var stderr = await stderrTask;
```

问题链条：

1. `CreateNoWindow = true` 只影响**直接子进程**（`cmd.exe`）。它派生出来的孙进程
   （`notepad`、`start xxx`、Python 的 tkinter、Electron、`.exe` 安装器…）照样有自己的窗口。
2. `ReadToEndAsync` 等的不是「进程退出」，而是**管道写端的 EOF**。孙进程会继承
   stdout/stderr 句柄，于是：直接子进程 `cmd.exe` 已经退出（`WaitForExitAsync` 正常返回），
   但管道写端还被孙进程开着 → `stdoutTask` 永不完成。
3. `await stdoutTask` 只是 `await` 一个 Task，**不响应任何 `CancellationToken`**。
   上面那个 `timeoutSec` 计时器触发后也只能取消 `WaitForExitAsync` 的等待，
   而代码此时已经走过了那一步。所以 `bash` 自己的超时设置在这里完全失效。
4. `p.Kill(entireProcessTree: true)` 在直接子进程退出后已经杀不到孙进程 —— 进程树从
   `cmd.exe` 往下断掉了，`Kill` 只能作用于活着的那个 `Process` 对象。

结果：方法永不返回。外层 `ToolExecutorDispatcher.ExecuteAsync` 的
`execTask.WaitAsync(linked.Token)` 会在 5 分钟（或该工具配置值）时 `Abandon` 掉，
所以对话最终不会被永久吊死 —— 但：
- 用户等的是 5 分钟（或 `task` 场景下的 30 分钟），而不是 `bash` 声明的 60 秒；
- 被 abandon 的那条执行链仍然持有孙进程，窗口还挂着，磁盘句柄也没释放；
- 模型收到的是「执行超时（>300 秒），已停止等待」，它无法得知真正原因是「有个窗口在等确认」。

`HtaciAI/Services/Tools/ToolSchemaProbe.cs` 是同一份写法的复制品（第 43-44 行），
它只跑 `--describe`，风险低，但建议一起改掉，免得以后被别的解释器踩中。

## 建议的修法

### 1. 不要用 `ReadToEndAsync` 等 EOF，改成事件式收流

```csharp
var stdout = new StringBuilder();
var stderr = new StringBuilder();
p.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };

p.Start();
p.BeginOutputReadLine();
p.BeginErrorReadLine();

try
{
    await p.WaitForExitAsync(linked.Token);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    TryKillTree(p);
    throw;
}
catch (OperationCanceledException)
{
    TryKillTree(p);
    return ToolExecution.Fail($"命令执行超时（>{timeoutSec}s）");
}

// EOF 不重要：进程已经退出，已经读到的就是全部
// 给「还没送完的缓冲区」一小段时间，然后无论如何继续
await Task.WhenAny(Task.Delay(200), p.WaitForExitAsync(CancellationToken.None));
```

要点：**「进程退出」与「管道 EOF」必须解耦**。退出就收工，不再等 EOF。

### 2. `Kill` 之前先把整棵树装进 Job Object（Windows）

`Kill(entireProcessTree: true)` 依赖「父子关系还在」，一旦 `cmd.exe` 先退出就失效。
可靠做法是把子进程放进 Job Object，退出时 `TerminateJobObject`：

```csharp
var job = CreateJobObject(IntPtr.Zero, null);
// JOBOBJECT_EXTENDED_LIMIT_INFORMATION.LimitFlags |= JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size);
// p.Start() 之后再 AssignProcessToJobObject(job, p.Handle)；
// 这些 API 在 .NET 里没有托管封装，P/Invoke 即可（约 40 行）。
```

这样即使 `cmd.exe` 已经退出，孙进程仍然在 job 里，`TerminateJobObject` 一定连根拔掉。

### 3. 至少给 `await stdoutTask` 补一层超时（最小改动）

如果暂时不想动上面两处，最小修法是把「收流」也纳入同一套令牌：

```csharp
try
{
    var all = Task.WhenAll(stdoutTask, stderrTask);
    await all.WaitAsync(linked.Token);     // 与 WaitForExitAsync 同一令牌
}
catch (OperationCanceledException) { TryKillTree(p); return ToolExecution.Fail(...); }
```

这样 `bash` 自己声明的 60 秒才会真正生效，而不是要等外层分派器的 5 分钟。
但它仍然治不了孙进程残留，只能让「看不到结果」变成「按时失败」。

## 另外两条建议（跟这个缺陷直接相关）

1. **把「为什么超时」如实写进失败结果**。现在超时文案统一是「执行超时（>N 秒），已停止等待」，
   模型和用户都不知道是卡在交互式窗口上。若能在 `Kill` 前后判断「进程已退出但管道未 EOF」，
   就可以写「命令已退出，但派生的子进程仍在运行并占用输出管道（多半是弹出的窗口没有关闭）」。
   这一句能省掉大量排查。
2. **给工具提供一个「强制不再等待并杀掉整棵树」的开关**。`Abandon(execTask)` 现在是
   丢下不管，副作用（窗口、句柄、CPU）留在机器上。Job Object 方案天然解决这一点。

## 给插件侧的说明（不需要宿主配合的部分）

QQ 机器人插件已经做了自己能做的兜底，不必等这个缺陷修完：

- 等一轮的上限从 10 分钟改成可配置的 `TurnTimeoutSeconds`（默认 30 分钟，与 `task` 工具的
  30 分钟对齐），超时后**主动调用 `ISessionFacade.CancelAsync(sessionId)`** 停掉那一轮，
  避免会话被永久占住、后续消息全部撞 `Busy`。
- 插件启动时会检查已绑定会话的最后一条消息，若某一轮断在半路（最后一条是用户消息、
  工具结果、或 `status=interrupted` 的 assistant），补一条 `[system]` 说明给用户。

这两条都只是「出事之后收场」，不等同于修好本缺陷。
