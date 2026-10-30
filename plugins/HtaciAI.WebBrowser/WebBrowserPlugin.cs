using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 内置浏览器插件。
///
/// <b>形态</b>：宿主主窗口里的标签页（<c>ui.tab</c>），不是独立窗口 ——
/// 浏览器是要和会话来回切的工作区，开成独立窗口只会多一层 Alt+Tab。
///
/// <b>一个浏览器窗口 = 一个宿主标签页 = 一个 id</b>。id 让多个 Agent 各用各的：
/// 没有 id 时第二个 Agent 调 <c>web_open</c> 会把第一个正在看的页面导航走，
/// 而且日志里不留痕迹。
/// </summary>
public sealed class WebBrowserPlugin : IPlugin, IPluginSettingsProvider
{
    private static bool _nativeResolverInstalled;

    private readonly List<IDisposable> _registrations = new();
    private BrowserManager? _manager;

    /// <summary>设置页要用到日志与存储（它在插件页被反复创建）。</summary>
    private IPluginContext? _context;

    /// <summary>
    /// 本插件所有工具共用的工具集。
    ///
    /// <b>为什么要有它</b>：会话里的工具选择器支持整组勾选（「内置浏览器（3）」一下全开）。
    /// 不声明归属时，这几个工具只会散落在「全部」里，用户得一个一个勾 ——
    /// 而它们本来就是一件事（操作浏览器），分开开关没有意义。
    ///
    /// <b>Key 只写短名</b>：宿主会拼成 <c>plugin:htaci.browser:browser</c> 这类全局 id，
    /// 两个插件用同一个 key 也不会撞。
    ///
    /// <b>为什么是属性而不是 <c>static readonly</c> 字段</b>：字段初值由静态构造函数执行，
    /// 一旦契约里缺这个类型（宿主版本比插件旧），异常会被包成
    /// <c>TypeInitializationException</c> —— 那是个「看不出问题在哪」的错误。
    /// 写成属性则是每次登记工具时才解析类型，失败只是一次
    /// 「工具没登记上」，日志里能直接看到真正缺的是什么。
    /// </summary>
    internal static PluginToolset Tools => new()
    {
        Key = "browser",
        Name = "内置浏览器",
        Description = "打开网页、截图，以及（后续）点击与填表",
    };

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        InstallNativeLoader(context.PluginDirectory);

        // 内核没装的话，这里能第一时间给出可读原因，而不是等第一次调用工具才炸
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            context.Log.Info($"WebView2 内核可用：{version}");
        }
        catch (Exception ex)
        {
            context.Log.Error("没有找到 WebView2 Runtime，浏览器打不开。", ex);
        }

        _context = context;
        _manager = new BrowserManager(context);

        // 工具必须声明了 tools.provide 才有门面；没声明时 context.Tools 是 null，
        // 直接调会把整个插件变成「加载失败」，而它其实只是少了个能力。
        if (context.Tools is not null)
        {
            RegisterTools(context);

            // 其余页面自动化工具（snapshot / click / fill / evaluate …）
            WebTools.Register(context, _manager, _registrations);
        }
        else
        {
            context.Log.Warn("清单里没有声明 tools.provide，浏览器工具全部不可用。");
        }

        if (context.Slots is not null)
        {
            // 界面操作必须回 UI 线程：宿主把 InitializeAsync 放在 UI 线程上跑（插件契约的要求），
            // 但这里仍然显式切一次 —— 插件不该依赖调用方的线程，尤其是这段代码会被反复搬来搬去。
            await Dispatcher.UIThread.InvokeAsync(() =>
                _registrations.Add(context.Slots.AddLauncherCard(new LauncherCard
                {
                    Title = "内置浏览器",
                    Description = "在宿主里打开 Chromium 窗口，也可以让 AI 自己开网页",
                    Glyph = "\uE774",
                    AccentColor = "#4A90D9",
                    OnClick = () => OpenFromCard(context),
                })));
        }
    }

    // ============================================================
    // 工具
    // ============================================================

    private void RegisterTools(IPluginContext context)
    {
        _registrations.Add(context.Tools!.Register(
            new PluginToolDefinition
            {
                Name = "web_open",
                Description =
                    "在内置浏览器里打开一个网址。不给 browser_id 时会新开一个浏览器窗口，" +
                    "返回的 id 之后要一直带上（多个 Agent 各用各的互不干扰）。" +
                    "给了 browser_id 就在那个浏览器里新开一个标签页。" +
                    "新开的窗口默认授权给你；操作别人已授权的窗口会被拒绝，需要请用户先授权。",
                InputSchemaJson =
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"url\":{\"type\":\"string\",\"description\":\"要打开的网址，可以省略 https://\"}," +
                    "\"browser_id\":{\"type\":\"string\",\"description\":\"可选。之前 web_open 返回的浏览器 id；不给就新开一个\"}" +
                    "},\"required\":[\"url\"]}",
                Toolset = Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, token) =>
            {
                var url = ReadString(call.ArgumentsJson, "url");
                if (string.IsNullOrWhiteSpace(url))
                    return PluginToolResult.Fail("缺少 url 参数。");

                try
                {
                    var (browserId, loaded) = await _manager!.NavigateAsync(
                        ReadString(call.ArgumentsJson, "browser_id"), url, call.SessionId, token);

                    return PluginToolResult.Ok(
                        $"已打开：{loaded}\n浏览器 id：{browserId}\n" +
                        $"（之后调其他浏览器工具都带上 browser_id={browserId}，多个 Agent 才不会互相抢页面）");
                }
                catch (OperationCanceledException)
                {
                    return PluginToolResult.Fail("已取消。");
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 权限问题不是"故障"，是"需要用户点头"。回给模型的话要能指导它下一步。
                    return PluginToolResult.Fail(
                        ex.Message + "\n可以请用户点工具栏上的「共享给智能体」把这个窗口交给当前会话，" +
                        "或者直接不带 browser_id 调 web_open 新开一个（新窗口自动归你）。");
                }
                catch (Exception ex)
                {
                    context.Log.Error("web_open 失败", ex);
                    return PluginToolResult.Fail($"打开失败：{ex.Message}");
                }
            }));

        _registrations.Add(context.Tools.Register(
            new PluginToolDefinition
            {
                Name = "web_screenshot",
                Description =
                    "给指定浏览器的当前标签页截图，返回 PNG 文件的绝对路径。" +
                    "要让模型看到画面，紧接着用 view_image 读这个路径。",
                InputSchemaJson =
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"browser_id\":{\"type\":\"string\",\"description\":\"浏览器 id（web_open 返回的那个）\"}" +
                    "},\"required\":[\"browser_id\"]}",
                Toolset = Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, token) =>
            {
                var browserId = ReadString(call.ArgumentsJson, "browser_id");
                if (string.IsNullOrWhiteSpace(browserId))
                    return PluginToolResult.Fail("缺少 browser_id。");

                try
                {
                    var (path, url, bytes) = await _manager!.ScreenshotAsync(browserId, call.SessionId, token);
                    return PluginToolResult.Ok(
                        $"已截图：{path}\n大小：{bytes / 1024} KB\n当前页面：{url}\n" +
                        "用 view_image 读取上面的路径即可看到画面。");
                }
                catch (OperationCanceledException)
                {
                    return PluginToolResult.Fail("已取消。");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return PluginToolResult.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    context.Log.Error("web_screenshot 失败", ex);
                    return PluginToolResult.Fail($"截图失败：{ex.Message}");
                }
            }));

        _registrations.Add(context.Tools.Register(
            new PluginToolDefinition
            {
                Name = "web_list_browsers",
                Description =
                    "列出当前开着的浏览器窗口及其 id 和标签页，用来找回之前用过的 browser_id。" +
                    "不确定有哪些浏览器可用时先调这个。",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}",
                Toolset = Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            (call, token) =>
            {
                var ids = _manager!.OpenIds;
                if (ids.Count == 0)
                    return Task.FromResult(PluginToolResult.Ok("现在没有开着的浏览器。用 web_open 打开一个。"));

                var lines = new List<string> { $"共 {ids.Count} 个浏览器：" };
                foreach (var id in ids)
                {
                    try
                    {
                        var window = _manager.Require(id);
                        lines.Add($"- {id}（{window.PageCount} 个标签，当前 {window.CurrentUrl}）");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"- {id}（读取失败：{ex.Message}）");
                    }
                }

                return Task.FromResult(PluginToolResult.Ok(string.Join("\n", lines)));
            }));
    }

    // ============================================================
    // 设置页（插件页里的「设置」分页）
    // ============================================================

    /// <summary>
    /// 插件页 → 内置浏览器 → 设置。内容见 <see cref="BrowserSettingsView"/>。
    ///
    /// <b>每次进这个分页都会调用一次</b>（宿主的行为）。所以这里只做「拿现有对象画一屏」，
    /// 不在这里创建或销毁任何长期状态 —— 状态都在 <see cref="BrowserManager"/> 里。
    ///
    /// 插件没跑起来时宿主根本不会调到这里（它显示「插件未运行」），
    /// 但为稳妥仍然给了兜底：<c>_manager</c> 为空时返回一句说明而不是抛异常。
    /// </summary>
    public Control CreateSettingsView()
    {
        var manager = _manager;
        if (manager is null)
        {
            return new TextBlock
            {
                Text = "插件当前没有运行，浏览器数据不可用。请先启用插件。",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        return new BrowserSettingsView(_context!, manager);
    }

    // ============================================================
    // 入口卡片
    // ============================================================

    /// <summary>
    /// 入口卡片点击。单独立一个方法而不是 `_ = _manager!.OpenAsync(...)`：
    /// 「插件已停用但旧卡片还在界面上」时 <c>_manager</c> 是 null，那种写法直接 NRE，
    /// 而且 fire-and-forget 会吞掉窗口创建失败的异常，日志里什么都留不下。
    /// </summary>
    private void OpenFromCard(IPluginContext context)
    {
        var manager = _manager;
        if (manager is null)
        {
            context.Log.Warn("入口卡片被点击，但插件当前没有可用的浏览器实例（可能刚被停用）。");
            return;
        }

        _ = OpenFromCardAsync(manager, context);
    }

    private static async Task OpenFromCardAsync(BrowserManager manager, IPluginContext context)
    {
        try
        {
            await manager.OpenAsync(null, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.Log.Error("从入口卡片打开浏览器失败", ex);
        }
    }

    // ============================================================
    // 生命周期
    // ============================================================

    public Task ShutdownAsync(CancellationToken ct)
    {
        foreach (var registration in _registrations)
        {
            try { registration.Dispose(); }
            catch { /* 注销失败也没法补救，不能拖住卸载 */ }
        }
        _registrations.Clear();

        _manager?.Dispose();
        _manager = null;
        _context = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 插件程序集是「读成字节流」加载的（<c>Assembly.Location</c> 为空、依赖按目录手动解析），
    /// 所以 WebView2 的原生加载器不会自己找到 —— 这里把 P/Invoke 解析钉到插件目录。
    /// </summary>
    private static void InstallNativeLoader(string pluginDirectory)
    {
        if (_nativeResolverInstalled) return;
        _nativeResolverInstalled = true;

        var loaderPath = Path.Combine(pluginDirectory, "WebView2Loader.dll");

        NativeLibrary.SetDllImportResolver(typeof(CoreWebView2).Assembly, (name, assembly, searchPath) =>
        {
            if (!name.Contains("WebView2Loader", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            return File.Exists(loaderPath) && NativeLibrary.TryLoad(loaderPath, out var handle)
                ? handle
                : IntPtr.Zero;
        });
    }

    /// <summary>只取一个字符串参数，参数坏掉就当没有。</summary>
    private static string? ReadString(string argumentsJson, string property)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
