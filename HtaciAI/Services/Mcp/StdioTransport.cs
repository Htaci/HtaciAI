using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// <c>stdio</c> 传输：把 MCP 服务端当作本地子进程拉起来，JSON-RPC 报文走它的 stdin/stdout，
/// <b>换行分隔</b>（一行一条完整报文，不是 SSE 帧）。
///
/// 服务端通常把日志写到 stderr，所以 stderr 单独接一条读线转进 <see cref="McpLog"/>，
/// 同时留最后一段用于「进程为什么退了」的解释。
///
/// <b>子进程必须显式释放</b>：连接失效与应用退出时都要收掉，否则会留下孤儿进程。
/// 见 <see cref="McpConnectionManager.Invalidate"/> 与 <see cref="McpConnectionManager.ShutdownAll"/>。
/// </summary>
public sealed class StdioTransport : ChannelTransportBase
{
    /// <summary>写 stdin 要串行化：两个报文交错写进管道会拼出一行非法 JSON。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly CancellationTokenSource _loopCts = new();
    private readonly StringBuilder _stderrTail = new();

    private const int StderrTailLimit = 600;

    private Process? _process;

    public StdioTransport(McpServerConfig config) : base(config) { }

    /// <summary>子进程还活着才算通道可用。它退出了就必须让管理器重新拉一个。</summary>
    public override bool IsAlive => !Disposed && _process is { HasExited: false };

    public override Task StartAsync(CancellationToken ct)
    {
        var command = Config.Command?.Trim() ?? "";
        if (command.Length == 0) throw new McpException("未填写启动命令");

        var (fileName, arguments) = BuildLauncher(command, McpConfigParsing.ParseArguments(Config.ArgumentsRaw));

        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in McpConfigParsing.ParseKeyValues(Config.EnvRaw))
            info.Environment[key] = value;
        if (!string.IsNullOrWhiteSpace(Config.WorkingDirectory))
            info.WorkingDirectory = Config.WorkingDirectory.Trim();

        try
        {
            _process = Process.Start(info);
        }
        catch (Exception ex)
        {
            // 最常见的是命令不存在（Win32Exception）。把用户填的命令原样回显，便于他自己核对。
            throw new McpException($"无法启动命令「{command}」：{ex.Message}");
        }

        if (_process is null) throw new McpException($"无法启动命令「{command}」");

        McpLog.Info(Config.Id, $"已启动子进程 {fileName}（PID {_process.Id}）");

        _ = Task.Run(() => ReadLoopAsync(_process.StandardOutput, _loopCts.Token), CancellationToken.None);
        _ = Task.Run(() => ReadErrorLoopAsync(_process.StandardError, _loopCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    protected override async Task WriteAsync(string payload, CancellationToken ct)
    {
        var process = _process ?? throw new McpException("子进程尚未启动");
        if (process.HasExited) throw new McpException(ExitReason());

        await _writeGate.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;   // stdout 关闭 = 进程结束

                line = line.Trim();
                if (line.Length == 0) continue;

                Dispatch(line);
            }

            Fault(ExitReason());
        }
        catch (OperationCanceledException)
        {
            // 自己 Dispose 时取消的，正常路径
        }
        catch (Exception ex)
        {
            Fault($"读取子进程输出失败：{ex.Message}");
        }
    }

    /// <summary>stderr 是服务端的日志通道，逐行转进日志面板，同时留个尾巴用于解释退出原因。</summary>
    private async Task ReadErrorLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Trim().Length == 0) continue;

                McpLog.Info(Config.Id, $"stderr: {line.Trim()}");
                AppendStderr(line.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            // 忽略
        }
        catch
        {
            // stderr 读不到不影响主流程
        }
    }

    private void AppendStderr(string line)
    {
        lock (_stderrTail)
        {
            _stderrTail.AppendLine(line);
            if (_stderrTail.Length > StderrTailLimit)
                _stderrTail.Remove(0, _stderrTail.Length - StderrTailLimit);
        }
    }

    /// <summary>进程为什么没了。带上 stderr 的尾巴——不然只剩一句「进程已退出」，什么也查不出来。</summary>
    private string ExitReason()
    {
        var head = "子进程已退出";
        if (_process is { HasExited: true } process)
        {
            try { head = $"子进程已退出（退出码 {process.ExitCode}）"; }
            catch { /* 拿不到退出码就用默认文案 */ }
        }

        string tail;
        lock (_stderrTail) tail = _stderrTail.ToString().Trim();

        return tail.Length == 0 ? head : $"{head}，最后输出：{tail}";
    }

    /// <summary>
    /// 收掉子进程。**必须可重入**：连接失效（<see cref="McpConnectionManager.Invalidate"/>）、
    /// 握手失败、应用退出三条路径都可能释放同一个传输，重复释放不该炸。
    /// </summary>
    public override void Dispose()
    {
        if (Disposed) return;
        Disposed = true;

        try { _loopCts.Cancel(); } catch (ObjectDisposedException) { /* 已经释放过 */ }

        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // 先好好说：关掉 stdin 一般就能让服务端自己退出
                process.StandardInput.Close();
                if (!process.WaitForExit(1500))
                {
                    // 不听话就整棵树一起收掉：npx / uvx 这类会再拉一层子进程
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // 释放路径上的异常没有必要往上抛
        }
        finally
        {
            process.Dispose();
            _writeGate.Dispose();
            _loopCts.Dispose();
        }
    }

    /// <summary>
    /// 把用户填的命令变成真正能启动的东西：可执行文件 + <b>完整的</b>参数表（含用户填的那些）。
    ///
    /// 返回完整参数表而不是「前缀」，是为了让调用方没法忘记拼用户参数 ——
    /// 那样写出来的 bug 很隐蔽：子进程照样能起来，只是收不到任何参数，
    /// 表现成「握手超时」，完全看不出是参数没传过去。
    ///
    /// Windows 上还有个绕不开的坑：<c>UseShellExecute = false</c> 走的是 CreateProcess，
    /// <b>不会</b>按 PATHEXT 解析 <c>.cmd</c> / <c>.bat</c>。于是用户写 <c>npx</c> 会直接报
    /// 「系统找不到指定的文件」——而 <c>npx</c> 恰恰是官方文档里最常出现的写法。
    /// 所以这里自己按 PATH + PATHEXT 找一遍，命中批处理就用 <c>cmd.exe /c</c> 包一层。
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) BuildLauncher(
        string command, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
            return (command, arguments);

        var resolved = ResolveOnPath(command) ?? command;
        if (!IsBatch(resolved))
            return (resolved, arguments);

        // cmd /c <批处理> <参数…>：/c 与脚本路径之后照原样接用户参数
        var full = new List<string> { "/c", resolved };
        full.AddRange(arguments);
        return ("cmd.exe", full);
    }

    private static bool IsBatch(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在 PATH 里按 PATHEXT 找可执行文件。找不到返回 null（交给 Process.Start 去报它自己的错）。</summary>
    private static string? ResolveOnPath(string command)
    {
        // 带路径分隔符就是明确的路径，不查 PATH
        if (command.IndexOf('\\') >= 0 || command.IndexOf('/') >= 0)
            return File.Exists(command) ? command : null;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
        {
            try
            {
                var bare = Path.Combine(directory, command);
                foreach (var extension in extensions)
                {
                    var candidate = bare + extension;
                    if (File.Exists(candidate)) return candidate;
                }

                // PATHEXT 里没写全的情况（少见，但便宜）
                if (File.Exists(bare)) return bare;
            }
            catch
            {
                // PATH 里混进非法路径是常事，跳过
            }
        }

        return null;
    }
}
