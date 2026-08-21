using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>
/// 脚本子进程的共享执行逻辑：解析解释器 → 启动 → stdin 传参 → 收集 stdout/stderr → 超时/取消。
/// </summary>
public static class ScriptProcess
{
    /// <summary>脚本执行默认超时（秒）。</summary>
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static async Task<ScriptRunResult> RunAsync(
        ScriptRuntimeKind kind,
        string scriptPath,
        string argumentsJson,
        CancellationToken ct)
    {
        var probe = await RuntimeDetector.ProbeAsync(kind);
        if (!probe.Found || string.IsNullOrWhiteSpace(probe.ResolvedPath))
            return new ScriptRunResult
            {
                Success = false,
                Error = $"未检测到 {RuntimeDetector.DisplayName(kind)} 运行时，请到「设置 → 环境配置」检查",
            };

        return await RunProcessAsync(probe.ResolvedPath, scriptPath, argumentsJson, ct);
    }

    private static async Task<ScriptRunResult> RunProcessAsync(
        string interpreter,
        string scriptPath,
        string argumentsJson,
        CancellationToken ct)
    {
        var commandLine = $"{interpreter} {scriptPath}";

        var psi = new ProcessStartInfo(interpreter, scriptPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = new Process { StartInfo = psi };
        if (!p.Start())
            return new ScriptRunResult { Success = false, Error = $"无法启动解释器：{interpreter}", CommandLine = commandLine };

        try
        {
            await p.StandardInput.WriteAsync(argumentsJson);
            p.StandardInput.Close();
        }
        catch
        {
            // 子进程提前退出导致管道关闭则忽略
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return new ScriptRunResult { Success = false, Error = "工具调用已取消", CommandLine = commandLine };
            try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            return new ScriptRunResult
            {
                Success = false,
                Error = $"脚本执行超时（>{DefaultTimeout.TotalSeconds:0}s）",
                CommandLine = commandLine,
            };
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ScriptRunResult
        {
            Success = p.ExitCode == 0,
            Output = stdout.Trim(),
            Error = stderr.Trim(),
            CommandLine = commandLine,
        };
    }
}
