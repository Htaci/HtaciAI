using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>
/// <c>bash</c> 工具背后实际执行命令的 shell。
///
/// <b>为什么要能选</b>：命令语法依 shell 而变（cmd 的 <c>%VAR%</c>、PowerShell 的 <c>$env:VAR</c>、
/// bash 的 <c>$VAR</c> 互不通用）。默认给 cmd 是因为它最保守，但模型看不见执行器时
/// 只能按最常见的写法猜，猜错就是「引号被吃掉」「<c>|</c> 之外全报错」这类难以归因的失败。
/// 所以：让用户选执行器，并把选中的那个写进工具描述里。
/// </summary>
public enum ShellKind
{
    /// <summary>自动：Windows 用 <c>cmd.exe</c>，其他平台用 <c>/bin/bash</c>。</summary>
    Auto,

    /// <summary>Windows 命令提示符（<c>cmd.exe</c>）。</summary>
    Cmd,

    /// <summary>Windows PowerShell 5.1（<c>powershell.exe</c>），随系统自带。</summary>
    WindowsPowerShell,

    /// <summary>PowerShell 7+（<c>pwsh.exe</c>），需单独安装。</summary>
    PowerShell7,

    /// <summary>Git for Windows 自带的 bash。</summary>
    GitBash,

    /// <summary>PATH 里的 bash（WSL / Git Bash / MSYS2 任意一个）。</summary>
    Bash
}

/// <summary>一次 shell 探测的结果。</summary>
public sealed record ShellInfo(
    ShellKind Kind,
    string DisplayName,
    string? FileName,
    string Version,
    bool Found,
    string? Error = null);

/// <summary>当前生效的 shell：执行时只需文件路径 + 参数拼法。</summary>
public sealed record EffectiveShell(ShellKind Kind, string FileName)
{
    /// <summary>
    /// 把整条命令装进 <paramref name="psi"/>。
    ///
    /// <b>不能统一走 <see cref="ProcessStartInfo.ArgumentList"/></b>：cmd 的 <c>/c</c> 自己按
    /// 「剥一层外层引号」的规则解析命令行，而 ArgumentList 会按 C 语言规则把命令里的 <c>"</c>
    /// 转成 <c>\"</c> —— cmd 不认反斜杠转义，于是 <c>dir "C:\Windows"</c> 被撕成
    /// <c>dir \"C:\Windows\</c>，报「文件名、目录名或卷标语法不正确」。
    /// 所以 cmd 必须整串交给 <see cref="ProcessStartInfo.Arguments"/>。
    /// </summary>
    public void Configure(ProcessStartInfo psi, string command)
    {
        psi.FileName = FileName;
        switch (Kind)
        {
            case ShellKind.Cmd:
                // /d：跳过注册表 AutoRun 里被别的软件插进来的命令（插进来的东西卡住时完全看不出原因）
                // /s：只剥一层外层引号、其余原样 —— 命令里自带的引号不会被当成外层引号吃掉，
                //     整条命令本身就是带引号的路径（"C:\Program Files\x.exe" 参数）也能正确还原
                psi.Arguments = "/d /s /c \"" + command + "\"";
                break;

            case ShellKind.WindowsPowerShell or ShellKind.PowerShell7:
                // -NonInteractive：需要用户确认（Remove-Item 的「是否继续」）或 Read-Host 时立即失败，
                //   而不是停在提示上等人 —— 这里的子进程没有窗口，人根本看不见它在等
                // -EncodedCommand：命令走 base64，彻底绕开命令行引号转义
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(ShellEnvironment.PowerShellPrefix + command)));
                break;

            default:
                // POSIX shell 由 argv 拿到完整字符串，ArgumentList 的转义规则在这里是对的
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }
    }
}

/// <summary>
/// shell 的检测、选择与解析。设置项存 <see cref="Models.AppSettingsData.CommandShell"/>，
/// 检测只在环境配置页点「重新检测」时跑，执行路径（<see cref="Resolve"/>）不做探测、不碰磁盘。
/// </summary>
public static class ShellEnvironment
{
    /// <summary>
    /// 交给 PowerShell 的命令前面先垫这一句，两件事：
    /// <list type="number">
    ///   <item>输出编码改成 UTF-8 —— PowerShell 5.1 默认按<b>控制台代码页</b>（中文系统是 GBK）写 stdout，
    ///         而宿主按 UTF-8 解管道，中文会整片变成 U+FFFD（工具结果里就是乱码）；</item>
    ///   <item>关掉进度条 —— 进度记录会以 CLIXML 灌进 stderr，出错时那堆 XML 会把真正的错误埋掉。</item>
    /// </list>
    /// </summary>
    public const string PowerShellPrefix =
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; ";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex VersionPattern = new(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);

    /// <summary>界面上按这个顺序列出（「自动」永远是第一行）。</summary>
    public static IReadOnlyList<ShellKind> AllKinds { get; } = new[]
    {
        ShellKind.Auto,
        ShellKind.Cmd,
        ShellKind.WindowsPowerShell,
        ShellKind.PowerShell7,
        ShellKind.GitBash,
        ShellKind.Bash
    };

    public static string DisplayName(ShellKind kind) => kind switch
    {
        ShellKind.Auto => "自动",
        ShellKind.Cmd => "cmd",
        ShellKind.WindowsPowerShell => "Windows PowerShell",
        ShellKind.PowerShell7 => "PowerShell 7",
        ShellKind.GitBash => "Git Bash",
        ShellKind.Bash => "bash",
        _ => kind.ToString()
    };

    public static string Description(ShellKind kind) => kind switch
    {
        ShellKind.Auto => "Windows 用 cmd，其他平台用 /bin/bash",
        ShellKind.Cmd => "cmd.exe，命令按 cmd 语法写（%VAR% 取变量）",
        ShellKind.WindowsPowerShell => "随系统自带的 PowerShell 5.1（$env:VAR 取变量）",
        ShellKind.PowerShell7 => "独立安装的 PowerShell 7+，支持 && 与 ||",
        ShellKind.GitBash => "Git for Windows 自带的 bash（POSIX 语法）",
        ShellKind.Bash => "PATH 里的 bash（WSL / Git Bash / MSYS2）",
        _ => ""
    };

    // ---- 选择（持久化到 settings.json）----

    /// <summary>用户选的 shell。<c>null</c> / 无法识别的值 → <see cref="ShellKind.Auto"/>。</summary>
    public static ShellKind Preference
    {
        get
        {
            var raw = AppSettingsStore.Current.CommandShell;
            return Enum.TryParse<ShellKind>(raw, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                ? kind
                : ShellKind.Auto;
        }
    }

    /// <summary>改选执行器并立刻落盘（不必重启，工具描述会由调用方刷新）。</summary>
    public static void SetPreference(ShellKind kind)
    {
        var data = AppSettingsStore.Current;
        var value = kind == ShellKind.Auto ? null : kind.ToString();
        if (string.Equals(data.CommandShell, value, StringComparison.Ordinal))
            return;

        data.CommandShell = value;
        AppSettingsStore.Save();
    }

    // ---- 执行 ----

    /// <summary>
    /// 当前生效的 shell。<b>不探测、不读版本</b>，只按设置与固定路径判断，
    /// 因为它在每次跑命令时都会调用。「选中的 shell 其实没装」这种情况由执行失败本身暴露。
    /// </summary>
    public static EffectiveShell Resolve()
    {
        var kind = Preference;
        if (kind == ShellKind.Auto)
            kind = OperatingSystem.IsWindows() ? ShellKind.Cmd : ShellKind.Bash;

        return kind switch
        {
            ShellKind.Cmd => new EffectiveShell(ShellKind.Cmd, ComSpec()),
            ShellKind.WindowsPowerShell => new EffectiveShell(kind, "powershell.exe"),
            ShellKind.PowerShell7 => new EffectiveShell(kind, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"),
            ShellKind.GitBash => new EffectiveShell(kind, FindGitBash() ?? BashCommand()),
            _ => new EffectiveShell(kind, BashCommand())
        };
    }

    /// <summary>
    /// bash 工具发给模型的描述：把当前执行器与对应语法写进去。
    /// 模型据这一句决定用哪种写法——描述与实际执行器不一致时，最容易出现「命令对了但跑不通」。
    /// </summary>
    public static string BashToolDescription()
    {
        var shell = Resolve();
        var (name, syntax) = shell.Kind switch
        {
            ShellKind.Cmd => ("cmd.exe（Windows 命令提示符）",
                "命令按 cmd 语法写：&& 连接、%VAR% 取环境变量、字符串用双引号（内层双引号连写成 \"\"）；" +
                "PowerShell 的 $env:VAR 与单引号字面量在这里无效。"),
            ShellKind.WindowsPowerShell => ("Windows PowerShell 5.1（powershell.exe）",
                "命令按 PowerShell 语法写：$env:VAR 取环境变量、; 分隔语句、字符串用单引号或双引号;" +
                "5.1 的 && 不可靠（用 ; 或分两条）；cmd 的 %VAR% 无效。"),
            ShellKind.PowerShell7 => ("PowerShell 7（pwsh）",
                "命令按 PowerShell 语法写：$env:VAR 取环境变量、; 分隔语句、支持 && 与 ||；cmd 的 %VAR% 无效。"),
            ShellKind.GitBash => ("Git Bash（POSIX bash）",
                "命令按 bash 语法写：$VAR 取环境变量、&& 连接、Windows 路径写成 /c/... 或用引号包住；" +
                "cmd 与 PowerShell 的内建命令不可用。"),
            _ => ("bash（POSIX，" + (OperatingSystem.IsWindows() ? "Windows 上可能是 WSL" : "/bin/bash") + "）",
                "命令按 bash 语法写：$VAR 取环境变量、&& 连接；Windows 路径要转换（WSL 下是 /mnt/c/...）；" +
                "cmd 与 PowerShell 的内建命令不可用。")
        };

        return "执行 shell 命令。" +
               $"当前执行器：{name}，命令由它解释执行。" + syntax +
               "要换用别的 shell 就在命令开头显式调用（如 powershell -NoProfile -Command \"...\"、" +
               "bash -c \"...\"）。命令在无窗口的子进程里跑：不会弹窗，stdin 已关闭" +
               "（pause / choice / Read-Host 这类等输入的命令会立刻失败，别指望交互），" +
               "start / explorer / Start-Process 这类「打开一个东西」的命令会被拒绝。" +
               "可设置工作目录与超时。危险：可影响系统。";
    }

    // ---- 会被系统窗口挡住的命令 ----

    /// <summary>
    /// 会弹出系统窗口、并且<b>一直等用户点「确定」</b>的命令。
    /// Windows 上 <c>start</c> 只是 ShellExecute 的桥：目标找不到时弹「Windows 找不到 … 文件」，
    /// ShellExecute 不返回，cmd 也就不返回，工具跟着吊死 —— 超时都救不回来，因为模态框还在桌面上。
    /// 这类命令对工具没有正当用途，直接拒掉并把原因写给模型。
    /// </summary>
    private static readonly HashSet<string> GuiCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "start-process",
        "explorer", "explorer.exe",
        "rundll32", "rundll32.exe",
        "mshta", "mshta.exe",
        "control", "control.exe",
        "mmc", "mmc.exe"
    };

    private static readonly char[] SegmentSeparators = { '&', '|', ';', '\r', '\n' };

    /// <summary>
    /// 命令里有哪一段以「会弹窗口」的命令开头就返回拒绝理由，否则 <c>null</c>。
    /// 只认每段（<c>&amp;&amp;</c> / <c>||</c> / <c>&amp;</c> / <c>|</c> / <c>;</c> / 换行切开）的
    /// <b>第一个词</b>：<c>npm start</c>、<c>echo start</c> 这类正常写法不受影响。
    /// </summary>
    public static string? RejectGuiCommand(string command)
    {
        foreach (var segment in command.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = FirstToken(segment);
            if (token.Length == 0 || !GuiCommands.Contains(token))
                continue;

            return $"不执行 `{token}`：它会让系统去「打开」一个东西，目标不对时 Windows 会弹一个" +
                   "「Windows 找不到 … 文件」的窗口并一直等用户点确定，工具就永远不返回。" +
                   "要跑程序就直接写程序路径；要看文件内容用 read；要打开窗口请让用户在界面上操作。";
        }
        return null;
    }

    private static string FirstToken(string segment)
    {
        var text = segment.TrimStart();
        if (text.Length == 0)
            return "";
        if (text[0] is '"' or '\'')
        {
            var end = text.IndexOf(text[0], 1);
            return end > 1 ? text[1..end] : text[1..];
        }
        var stop = text.IndexOfAny(new[] { ' ', '\t' });
        return stop < 0 ? text : text[..stop];
    }

    // ---- 检测 ----

    /// <summary>探测全部候选 shell（设置页用）。每项都会返回，装没装看 <see cref="ShellInfo.Found"/>。</summary>
    public static async Task<IReadOnlyList<ShellInfo>> DetectAllAsync()
    {
        // 并发探测：PowerShell 启动一次要几百毫秒，串起来点一次「重新检测」要等好几秒
        return await Task.WhenAll(AllKinds.Select(ProbeAsync));
    }

    /// <summary>探测一个候选 shell；<see cref="ShellKind.Auto"/> 直接给出它此刻会解析到谁。</summary>
    public static async Task<ShellInfo> ProbeAsync(ShellKind kind)
    {
        if (kind == ShellKind.Auto)
        {
            var auto = Resolve();
            var probed = await ProbeAsync(auto.Kind);
            return probed with { Kind = ShellKind.Auto };
        }

        // 非 Windows 平台上这些「Windows 专属」的 shell 根本不可能存在，不做无谓的进程启动
        if (!OperatingSystem.IsWindows() && kind is ShellKind.Cmd or ShellKind.WindowsPowerShell or ShellKind.GitBash)
            return new ShellInfo(kind, DisplayName(kind), null, "", false, "仅 Windows 提供");

        foreach (var (fileName, arguments) in Candidates(kind))
        {
            var (code, output) = await RunAsync(fileName, arguments);
            if (code is null)
                continue;

            var version = VersionPattern.Match(output);
            var resolved = fileName == CommandName(kind) && TryResolveFullPath(fileName) is { } full
                ? full
                : fileName;

            return new ShellInfo(kind, DisplayName(kind), resolved, version.Success ? version.Value : "", true);
        }

        return new ShellInfo(kind, DisplayName(kind), null, "", false, "未找到可执行文件");
    }

    private static IEnumerable<(string FileName, string[] Arguments)> Candidates(ShellKind kind)
    {
        switch (kind)
        {
            case ShellKind.Cmd:
                yield return (ComSpec(), new[] { "/c", "ver" });
                break;

            case ShellKind.WindowsPowerShell:
                // 用 (Get-Host).Version 而不是 $PSVersionTable：后者靠 $ 变量展开，
                // 命令一旦被别的 shell 包一层（cmd 的 () 分组、引号插值）就拿不到值。
                yield return ("powershell.exe", new[]
                {
                    "-NoProfile", "-NonInteractive", "-Command", "(Get-Host).Version.ToString()"
                });
                break;

            case ShellKind.PowerShell7:
                foreach (var exe in new[] { "pwsh.exe", "pwsh" })
                    yield return (exe, new[]
                    {
                        "-NoProfile", "-NonInteractive", "-Command", "(Get-Host).Version.ToString()"
                    });
                break;

            case ShellKind.GitBash:
                foreach (var path in GitBashCandidates())
                    yield return (path, new[] { "--version" });
                break;

            case ShellKind.Bash:
                yield return (BashCommand(), new[] { "--version" });
                break;
        }
    }

    /// <summary>探测候选的「命令名」，用于判断要不要把结果换成完整路径。</summary>
    private static string CommandName(ShellKind kind) => kind switch
    {
        ShellKind.Cmd => ComSpec(),
        ShellKind.WindowsPowerShell => "powershell.exe",
        ShellKind.PowerShell7 => "pwsh.exe",
        ShellKind.GitBash => "bash.exe",
        _ => BashCommand()
    };

    /// <summary>跑一次探测。<c>ExitCode</c> 为 null 表示没能启动或超时。</summary>
    private static async Task<(int? ExitCode, string Output)> RunAsync(string fileName, string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

            using var p = new Process { StartInfo = psi };
            if (!p.Start())
                return (null, "");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(ProbeTimeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return (null, "");
            }

            var output = (await stdoutTask) + "\n" + (await stderrTask);
            return (p.ExitCode, output);
        }
        catch
        {
            // 没装 / 不在 PATH：Win32Exception 之类，一律当成「这个候选不存在」
            return (null, "");
        }
    }

    // ---- 路径与命令名 ----

    private static string ComSpec()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec) ? comspec : "cmd.exe";
    }

    private static string BashCommand() => OperatingSystem.IsWindows() ? "bash.exe" : "/bin/bash";

    /// <summary>Git for Windows 里 bash 的常见位置（按可靠性排序，取第一个存在的）。</summary>
    private static string? FindGitBash() => GitBashCandidates().FirstOrDefault(File.Exists);

    private static List<string> GitBashCandidates()
    {
        var list = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            list.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
            list.Add(Path.Combine(root, "Git", "usr", "bin", "bash.exe"));
        }

        // 兜底：PATH 里那个 bash（可能是 Git Bash，也可能是 WSL）
        list.Add(BashCommand());
        return list;
    }

    /// <summary>
    /// 把 PATH 里的命令名换成完整路径（探测结果要给用户看，光一个 <c>bash.exe</c> 说明不了是谁）。
    /// 交给 <see cref="RuntimeDetector.FindInPath"/>：它读的是当前注册表里的 PATH，
    /// 不会像进程环境变量那样停在启动时的快照上。
    /// </summary>
    private static string? TryResolveFullPath(string fileName) => RuntimeDetector.FindInPath(fileName);
}

