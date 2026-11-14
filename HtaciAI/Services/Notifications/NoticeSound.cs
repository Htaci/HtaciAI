using System;
using System.Runtime.InteropServices;

namespace HtaciAI.Services.Notifications;

/// <summary>
/// 提醒弹出时的提示音。
///
/// 用系统的 <c>MessageBeep</c>（即 .NET 里 <c>SystemSounds.Asterisk</c> 的底层调用）而不是自己塞一个 wav：
/// 音量、开关、声音方案都跟着用户在 Windows「声音」设置里的选择走 —— 用户把提示音关掉了，
/// 这里自然也不响，不需要我们再维护一套静音开关。
///
/// 非 Windows 平台是空操作（目前只有桌面版会弹提醒）。
/// </summary>
public static class NoticeSound
{
    /// <summary>MB_ICONASTERISK：Windows 默认方案里的「信息」提示音。</summary>
    private const uint Info = 0x00000040;

    /// <summary>节流间隔（毫秒）。</summary>
    private const long ThrottleMs = 1200;

    private static long _lastTick = -ThrottleMs;

    /// <summary>响一声。可从任意线程调用。</summary>
    public static void Play()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 一次响应里连续来好几条提醒只响第一声：声音的用处是「叫住人」，
        // 连成一串的嘟嘟声只是噪音，而屏幕上已经堆了对应的卡片。
        var now = Environment.TickCount64;
        if (now - _lastTick < ThrottleMs) return;
        _lastTick = now;

        try
        {
            // 异步播放，不阻塞 UI 线程
            MessageBeep(Info);
        }
        catch
        {
            // 声音拿不到就算了，不该因为没响而弹不出提醒
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
