using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace HtaciAI.Services;

/// <summary>
/// 让 DWM 把窗口<b>本身</b>裁成圆角（Windows 11 22000+，更早的系统会忽略这个设置）。
///
/// 为什么要自己 P/Invoke：Avalonia 12.0.4 的 <c>Win32Properties</c> 只暴露了窗口样式回调与
/// WndProc 回调，还没有 <c>SetWindowCornerPreference</c>。而 Mica 这类毛玻璃是 DWM 铺满
/// <b>整个窗口矩形</b>的 —— 只给内部 Border 画圆角不够，四角会露出方的底。
/// 让系统把窗口裁圆才是正解，所以这里直接调 Win32 的文档化接口。
///
/// 非 Windows、或系统版本不够时都是安静的空操作。
/// </summary>
public static class Win32WindowCorners
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
    private const int CornerPreference = 33;

    /// <summary>DWMWCP_ROUND</summary>
    private const int Round = 2;

    /// <summary>给窗口加上圆角偏好。需在窗口句柄已经创建之后调用（即显示之后）。</summary>
    [SupportedOSPlatform("windows")]
    public static void Apply(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            var value = Round;
            DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
        }
        catch
        {
            // 圆角只是观感，拿不到就算了，不值得让它影响提醒能不能弹出来
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
