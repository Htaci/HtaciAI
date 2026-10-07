using System;
using System.Threading.Tasks;

namespace HtaciAI.Services.Updates;

/// <summary>启动更新程序的结果。</summary>
public sealed record LaunchResult(bool Started, string Message);

/// <summary>
/// 「立即更新」的落地动作：启动一个**独立的更新程序**，然后退出自身。
///
/// <b>更新程序尚未实现</b>，所以这里现在只返回「未接入」，不做任何实际动作。
/// 点击链路（标题栏按钮 → <see cref="MainWindow"/> 的处理器 → 这里）已经打通，
/// 等更新程序做出来之后，只需要在这个方法里：
/// <list type="number">
///   <item>把 <paramref name="result"/> 里的下载地址（或当前 exe 路径）传给更新程序，
///     <c>Process.Start</c> 它（独立进程，不能是子进程——本程序马上要退出）；</item>
///   <item>拿到句柄后用 <c>desktop.Shutdown()</c> 退出自己，把文件锁让出去。</item>
/// </list>
/// 更新程序负责等本进程退出、替换 exe、再把自己删掉或覆盖。
/// </summary>
public static class UpdateLauncher
{
    /// <summary>更新程序的文件名，放在程序目录下。</summary>
    public const string UpdaterFileName = "HtaciAI.Updater.exe";

    public static Task<LaunchResult> LaunchAsync(UpdateCheckResult result)
    {
        // TODO: 更新程序做出来之后，在这里替换成真正的启动 + 退出逻辑。
        _ = result;
        return Task.FromResult(new LaunchResult(false, "更新程序尚未接入，请先手动下载新版本"));
    }
}
