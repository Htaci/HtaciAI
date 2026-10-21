using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// <see cref="IPluginWindowHost"/> 的宿主实现：一个插件一个实例。
///
/// <b>为什么不让插件自己 <c>new Window()</c> 了事</b>：透明 + 无边框 + 置顶 + 不进任务栏
/// 这四个属性配错任何一个，桌宠就会变成「任务栏多一个条目」「点不到」「跑到屏幕外」；
/// 位置记忆按插件 id 存也得有地方存。宿主把这些一次做对，插件只管往里放内容。
///
/// <b>生命周期</b>：实例登记在 <see cref="PluginManager.PluginEntry.Subscriptions"/> 里，
/// 插件被禁用时 <see cref="Dispose"/> 会关掉它所有窗口 —— 这件事必须发生在
/// <c>AssemblyLoadContext.Unload()</c> 之前，否则窗口还在、画它的代码已经没了。
///
/// <b>线程</b>：与其余 UI 契约一致，只在 UI 线程上使用。
/// </summary>
internal sealed class PluginWindowHost : IPluginWindowHost, IDisposable
{
    private readonly string _pluginId;
    private readonly List<Window> _windows = new();
    private bool _disposed;

    public PluginWindowHost(string pluginId) => _pluginId = pluginId;

    public IReadOnlyList<Window> OpenWindows => _windows.ToList();

    public Window CreateStandalone(WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_disposed)
            throw new ObjectDisposedException(nameof(PluginWindowHost), "插件已停用，不能再建窗口。");

        var window = new Window
        {
            Title = options.Title ?? "",
            Width = options.Width > 0 ? options.Width : 320,
            Height = options.Height > 0 ? options.Height : 320,
            Topmost = options.Topmost,
            ShowInTaskbar = options.ShowInTaskbar,
            CanResize = !options.Borderless,
            Background = options.Transparent ? Brushes.Transparent : Brushes.White,

            // 无边框（自绘）或保留系统标题栏
            WindowDecorations = options.Borderless ? WindowDecorations.None : WindowDecorations.Full,

            // 透明窗口必须让平台允许透明合成，否则背景会被填成黑色
            TransparencyLevelHint = options.Transparent
                ? new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None }
                : new[] { WindowTransparencyLevel.None },
        };

        // 说明：Avalonia 的 Window 没有 owner 概念，所以「归属主窗口」只能靠
        // Topmost / ShowInTaskbar 这两个属性表达（见 WindowOptions 的注释）。
        if (options.Borderless && options.AllowDrag)
            EnableDrag(window);

        ApplyPlacement(window, options);

        window.Closed += (_, _) =>
        {
            SavePlacement(window, options);
            _windows.Remove(window);
        };

        _windows.Add(window);
        return window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var window in _windows.ToList())
        {
            try { window.Close(); }
            catch (Exception ex) { PluginLog.Warn(_pluginId, "关闭插件窗口失败：" + ex.Message); }
        }

        _windows.Clear();
    }

    // ============================================================
    // 拖动
    // ============================================================

    /// <summary>
    /// 按住窗口内容区拖动。加在窗口本身而不是某个控件上：插件里的按钮/输入框自己会吃掉事件，
    /// 空白处才落到窗口这一层，这正是「拖得动但不抢点击」的效果。
    /// </summary>
    private static void EnableDrag(Window window)
    {
        window.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

            try { window.BeginMoveDrag(e); }
            catch { /* 某些平台/状态下不支持拖动，忽略 */ }
        };
    }

    // ============================================================
    // 位置记忆
    // ============================================================

    private string StatePath => Path.Combine(AppPaths.PluginData, _pluginId, "window-state.json");

    /// <summary>位置按「插件 id + 窗口标题」存 —— 同一插件开多个窗口时各记各的。</summary>
    private static string KeyOf(WindowOptions options)
        => string.IsNullOrWhiteSpace(options.Title) ? "default" : options.Title.Trim();

    private void ApplyPlacement(Window window, WindowOptions options)
    {
        if (!options.RememberPosition)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var saved = PluginJson.Load<WindowStateFile>(StatePath);
        if (saved?.Windows is { } map && map.TryGetValue(KeyOf(options), out var placement) &&
            IsOnScreen(window, placement))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(placement.X, placement.Y);
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void SavePlacement(Window window, WindowOptions options)
    {
        if (!options.RememberPosition) return;

        try
        {
            var file = PluginJson.Load<WindowStateFile>(StatePath) ?? new WindowStateFile();
            file.Windows[KeyOf(options)] = new WindowPlacement
            {
                X = window.Position.X,
                Y = window.Position.Y,
            };
            PluginJson.Save(StatePath, file);
        }
        catch
        {
            // 记不住位置不是故障
        }
    }

    /// <summary>
    /// 上次的位置还在屏幕内吗。显示器拔掉、改分辨率之后，记下来的坐标可能整个落在屏幕之外，
    /// 直接还原会得到「窗口启动了但看不见」—— 那种 bug 用户只会说「插件坏了」。
    /// </summary>
    private static bool IsOnScreen(Window window, WindowPlacement placement)
    {
        try
        {
            foreach (var screen in window.Screens.All)
            {
                var area = screen.WorkingArea;
                if (placement.X >= area.X - 200 && placement.X <= area.X + area.Width - 40 &&
                    placement.Y >= area.Y - 40 && placement.Y <= area.Y + area.Height - 40)
                    return true;
            }

            return false;
        }
        catch
        {
            // 拿不到屏幕信息时按「可用」处理：宁可位置奇怪一点，也不要每次都被重置到屏幕中央
            return true;
        }
    }

    /// <summary>窗口位置的落盘形态。<b>宿主自己的类型</b>，不是插件类型 —— 不会拖住插件程序集。</summary>
    private sealed class WindowStateFile
    {
        public Dictionary<string, WindowPlacement> Windows { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
