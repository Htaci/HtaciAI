using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            var r = await TryRunVersionAsync(manual, VersionArguments(kind));
            if (r != null)
                return Ok(kind, manual, r.Value.Version, isManual: true);
            return NotFound(kind, manual, isManual: true, error: $"无法运行指定路径：{manual}");
        }

        // 2) 自动探测系统 PATH
        foreach (var candidate in AutoCandidates(kind))
        {
            var r = await TryRunVersionAsync(candidate, VersionArguments(kind));
            if (r != null)
                return Ok(kind, r.Value.Command, r.Value.Version, isManual: false);
        }

        return NotFound(kind, null, isManual: false, error: "未在系统 PATH 中找到");
    }

    /// <summary>
    /// 解析可执行文件的绝对路径，<b>不启动进程</b>：手动指定优先，其次在 PATH 里找。
    /// 给「马上就要执行很多次」的调用方用（抽帧每次要跑好几个 ffmpeg，
    /// 用 <see cref="ProbeAsync"/> 那套还得先起一个进程读版本）。
    /// </summary>
    public static string? ResolveExecutable(ScriptRuntimeKind kind)
    {
        var manual = GetManualPath(kind);
        if (!string.IsNullOrWhiteSpace(manual))
            return File.Exists(manual) ? manual : null;

        foreach (var candidate in AutoCandidates(kind))
        {
            if (FindInPath(candidate) is { } full)
                return full;
        }
        return null;
    }

    /// <summary>
    /// 把 PATH 里的命令名换成完整路径（找不到返回 null）。按 PATH 顺序逐个目录试，
    /// 无后缀的命令名再按 PATHEXT 补 <c>.exe</c> 之类 —— Windows 上 <c>ffmpeg</c> 实际是 <c>ffmpeg.exe</c>。
    /// </summary>
    public static string? FindInPath(string fileName)
    {
        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName) ? fileName : null;

        var extensions = new List<string> { "" };
        if (Path.GetExtension(fileName).Length == 0)
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            extensions.AddRange(pathext.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), fileName + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 非法路径片段（含通配符之类）直接跳过
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 读版本用的命令行参数。绝大多数工具是 GNU 风格的 <c>--version</c>，
    /// 但 ffmpeg 只认单横线的 <c>-version</c>（给 <c>--version</c> 它只会报 Unrecognized option）。
    /// </summary>
    private static string VersionArguments(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Ffmpeg => "-version",
        _ => "--version"
    };

    public static string GetManualPath(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => AppSettings.RuntimePythonPath ?? "",
        ScriptRuntimeKind.Node => AppSettings.RuntimeNodePath ?? "",
        ScriptRuntimeKind.Git => AppSettings.RuntimeGitPath ?? "",
        ScriptRuntimeKind.Ffmpeg => AppSettings.RuntimeFfmpegPath ?? "",
        _ => ""
    };

    public static void SetManualPath(ScriptRuntimeKind kind, string? path)
    {
        switch (kind)
        {
            case ScriptRuntimeKind.Python: AppSettings.RuntimePythonPath = path; break;
            case ScriptRuntimeKind.Node: AppSettings.RuntimeNodePath = path; break;
            case ScriptRuntimeKind.Git: AppSettings.RuntimeGitPath = path; break;
            case ScriptRuntimeKind.Ffmpeg: AppSettings.RuntimeFfmpegPath = path; break;
        }
    }

    private static string[] AutoCandidates(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python =>
            OperatingSystem.IsWindows()
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" },
        ScriptRuntimeKind.Node => new[] { "node" },
        ScriptRuntimeKind.Git => new[] { "git" },
        ScriptRuntimeKind.Ffmpeg => new[] { "ffmpeg" },
        _ => Array.Empty<string>()
    };

    public static string DisplayName(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => "Python",
        ScriptRuntimeKind.Node => "Node.js",
        ScriptRuntimeKind.Git => "Git",
        ScriptRuntimeKind.Ffmpeg => "FFmpeg",
        _ => kind.ToString()
    };

    public static string Description(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => "Python 解释器，运行 .py 脚本工具",
        ScriptRuntimeKind.Node => "Node 运行时，运行 .js / .ts 脚本工具",
        ScriptRuntimeKind.Git => "版本控制，工作空间用它查看改动与提交",
        ScriptRuntimeKind.Ffmpeg => "视频解码与采样，view_video_frames 抽帧要用它（ffprobe 与 ffmpeg 同目录）",
        _ => ""
    };

    public static string FileExtension(ScriptRuntimeKind kind) => kind switch
    {
        ScriptRuntimeKind.Python => ".py",
        ScriptRuntimeKind.Node => ".js / .ts",
        ScriptRuntimeKind.Git => "git",
        ScriptRuntimeKind.Ffmpeg => "ffmpeg",
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
