using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>
/// 脚本运行时的自动检测与手动路径验证。
/// 探测顺序：用户手动指定的解释器 → 系统 PATH 中依次查找候选命令。
/// </summary>
public static class RuntimeDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>探测某运行时的可用状态（手动指定优先，其次自动检测 PATH）。</summary>
    public static async Task<RuntimeProbeResult> ProbeAsync(ScriptRuntimeKind kind)
    {
        // 1) 手动指定优先
        var manual = GetManualPath(kind);
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var r = await TryRunVersionAsync(manual, "--version");
            if (r != null)
                return Ok(kind, manual, r.Value.Version, isManual: true);
            return NotFound(kind, manual, isManual: true, error: $"无法运行指定路径：{manual}");
        }

        // 2) 自动探测系统 PATH
        foreach (var candidate in AutoCandidates(kind))
        {
            var r = await TryRunVersionAsync(candidate, "--version");
            if (r != null)
                return Ok(kind, r.Value.Command, r.Value.Version, isManual: false);
        }

        return NotFound(kind, null, isManual: false, error: "未在系统 PATH 中找到");
    }

    public static string GetManualPath(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => AppSettings.RuntimePythonPath ?? "",
        ScriptRuntimeKind.Node => AppSettings.RuntimeNodePath ?? "",
        _ => ""
    };

    public static void SetManualPath(ScriptRuntimeKind kind, string? path)
    {
        switch (kind)
        {
            case ScriptRuntimeKind.Python: AppSettings.RuntimePythonPath = path; break;
            case ScriptRuntimeKind.Node: AppSettings.RuntimeNodePath = path; break;
        }
    }

    private static string[] AutoCandidates(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python =>
            OperatingSystem.IsWindows()
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" },
        ScriptRuntimeKind.Node => new[] { "node" },
        _ => Array.Empty<string>()
    };

    public static string DisplayName(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => "Python",
        ScriptRuntimeKind.Node => "Node.js",
        _ => kind.ToString()
    };

    public static string Description(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => "Python 解释器，运行 .py 脚本工具",
        ScriptRuntimeKind.Node => "Node 运行时，运行 .js / .ts 脚本工具",
        _ => ""
    };

    public static string FileExtension(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => ".py",
        ScriptRuntimeKind.Node => ".js / .ts",
        _ => ""
    };

    private static RuntimeProbeResult Ok(ScriptRuntimeKind kind, string path, string version, bool isManual)
        => new()
        {
            Kind = kind,
            Found = true,
            Version = version,
            ResolvedPath = path,
            IsManual = isManual
        };

    private static RuntimeProbeResult NotFound(ScriptRuntimeKind kind, string? path, bool isManual, string? error)
        => new()
        {
            Kind = kind,
            Found = false,
            ResolvedPath = path,
            IsManual = isManual,
            Error = error
        };

    /// <summary>执行 `command --version`，能解析出版本号才算成功。失败/超时返回 null。</summary>
    private static async Task<(string Command, string Version)?> TryRunVersionAsync(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };

            if (!p.Start())
                return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return null;
            }

            var all = (await stdoutTask) + "\n" + (await stderrTask);
            var m = Regex.Match(all, @"\d+\.\d+(?:\.\d+)?");
            var version = m.Success ? m.Value : all.Trim();

            if (string.IsNullOrEmpty(version))
                return null;
            return (command, version);
        }
        catch
        {
            return null;
        }
    }
}
