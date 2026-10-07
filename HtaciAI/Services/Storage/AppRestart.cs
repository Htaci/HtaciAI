using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 重启应用。切换存储模式必须重启才生效——<c>DatabaseService.DbPath</c> 与各注册表的根路径
/// 都在启动时一次性解析并加载。
/// </summary>
public static class AppRestart
{
    /// <summary>重启当前应用。无法重启时返回 false，调用方应提示用户手动重启。</summary>
    public static bool TryRestart()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 等父进程完全退出后再拉起新进程：SQLite 用 WAL，过早启动另一实例会争抢 -wal/-shm。
            desktop.Exit += (_, _) => Launch();
            desktop.Shutdown();
            return true;
        }

        Launch();
        return true;
    }

    private static void Launch()
    {
        try
        {
            var psi = BuildStartInfo();
            if (psi is not null) Process.Start(psi);
        }
        catch
        {
            // 重启失败不该让应用崩在退出流程里
        }
    }

    private static ProcessStartInfo? BuildStartInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return null;

        var commandLine = Environment.GetCommandLineArgs();
        var args = new List<string>();

        // 开发态（dotnet run / dotnet exec）下 ProcessPath 是 dotnet.exe，
        // 真正的入口是命令行第一个参数里的 dll；不特别处理的话重启会得到一个空的 dotnet host。
        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        var firstArgIsDll = commandLine.Length > 0
            && commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        var startIndex = 1;
        if (isDotnetHost && firstArgIsDll)
        {
            args.Add(commandLine[0]);
        }

        for (var i = startIndex; i < commandLine.Length; i++)
            args.Add(commandLine[i]);

        var psi = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }
}
