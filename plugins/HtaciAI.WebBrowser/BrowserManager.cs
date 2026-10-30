using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 所有浏览器窗口的管理者：分配 id、开/关宿主标签页、把工具调用路由到对应窗口。
///
/// <b>为什么要有 id</b>：多个 Agent 各自需要一个互不干扰的浏览器。没有 id 时，
/// 第二个 Agent 调 <c>web_open</c> 会把第一个 Agent 正在看的页面导航走 ——
/// 而这种「串台」在日志里毫无痕迹，排查起来极难。
///
/// <b>一个 Environment 管所有窗口</b>：同一 user data folder 下所有窗口共享 cookie 与登录态。
/// 这是「同一个浏览器开了几个窗口」该有的行为，但要注意它意味着
/// 两个 Agent 不能同时登录同一站点的不同账号。
///
/// <b>线程</b>：公开方法可以从任意线程进，内部切到 UI 线程；内核对象只能在 UI 线程碰。
/// </summary>
internal sealed class BrowserManager : IDisposable
{
    private readonly IPluginContext _context;
    private readonly Dictionary<string, BrowserWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPluginTab> _tabs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「哪个窗口授权给了哪个会话」。规则见 <see cref="BrowserGrants"/>。</summary>
    private readonly BrowserGrants _grants = new();

    private CoreWebView2Environment? _environment;
    private int _nextId = 1;
    private bool _disposed;

    public BrowserManager(IPluginContext context)
    {
        _context = context;

        // 历史存成插件私有文件（不是 Chromium 的 History 库），理由见 BrowserHistory 的注释。
        // 用 LoadText/SaveText 而不是泛型版本：后者会让插件禁用后无法卸载。
        History = new BrowserHistory(
            () => _context.Storage.LoadText("history"),
            json => _context.Storage.SaveText("history", json),
            message => _context.Log.Warn(message));
    }

    private string UserDataFolder => Path.Combine(_context.Storage.DataDirectory, "webview-data");

    /// <summary>WebView2 的 user data folder，也就是这份 Chromium profile。设置页要显示/清理它。</summary>
    public string DataFolder => UserDataFolder;

    /// <summary>截图输出目录（<c>web_screenshot</c> 往这里写）。</summary>
    public string ShotsFolder => Path.Combine(_context.Storage.DataDirectory, "shots");

    /// <summary>访问历史。设置页可查看与清空。</summary>
    public BrowserHistory History { get; }

    /// <summary>当前开着的浏览器 id，按打开顺序。</summary>
    public IReadOnlyList<string> OpenIds => _windows.Keys.ToList();

    // ============================================================
    // 开关窗口
    // ============================================================

    /// <summary>
    /// 开一个新的浏览器窗口并返回它的 id。
    /// <paramref name="url"/> 为空时只开一个空白标签页（起始页）。
    /// </summary>
    /// <param name="sessionId">
    /// 谁开的这个窗口。给 null 表示用户手动打开（不需要授权 —— 用户自己当然能用自己的浏览器）。
    /// 非 null 时窗口默认就授权给这个会话：它已经在用了，再要一次授权只是噪音。
    /// </param>
    public Task<string> OpenAsync(string? url, string? sessionId, CancellationToken ct) => UiAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var environment = await EnsureEnvironmentAsync();

        var id = "browser-" + _nextId++;
        var window = new BrowserWindow(id, environment, _context.Log);

        window.ShareRequested = ShareAsync;
        window.UnshareRequested = UnshareAsync;

        var tab = OpenHostTab(window);
        _windows[id] = window;
        _tabs[id] = tab;

        await window.AddPageAsync(url, ct);
        tab.Activate();

        // Agent 自己开的窗口默认归它：不开这条，模型每次 web_open 之后
        // 都得再被用户授权一次才能继续操作，那等于把自动化拆成两半。
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _grants.Grant(id, sessionId, out _);
            _ownerTitles[id] = "当前会话";
            window.SetSharedState("当前会话");
        }
        else
        {
            // 用户手动开的窗口：给一颗「共享给智能体」按钮，否则他没法把窗口交给会话
            window.SetSharedState(null);
        }

        _context.Log.Info($"已打开浏览器窗口 {id}（当前共 {_windows.Count} 个）");
        return id;
    }, ct);

    /// <summary>
    /// 如果 <paramref name="sessionId"/> 正在使用这个窗口而它还没有主人，就补一条授权。
    ///
    /// <b>为什么需要兜底</b>：授权有两条进入路径（AI 自己开、用户手动共享），
    /// 任何一条忘了建授权，界面与真实权限就会不一致。与其在每条路径上各写一次，
    /// 不如在每个「AI 要用这个窗口」的入口统一补一次。
    ///
    /// <b>不会抢占</b>：已经有主人的窗口原样返回，由调用方的 <see cref="RequireAccess"/> 拦截。
    /// </summary>
    private void EnsureGranted(string browserId, BrowserWindow window, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        if (_grants.OwnerOf(browserId) is not null) return;
        if (!_windows.ContainsKey(browserId)) return;

        _grants.Grant(browserId, sessionId, out _);

        // 标题只在授权那一刻拿得到，不再查库（见 OwnerTitleOf 的说明）
        _ownerTitles[browserId] = "当前会话";
        window.SetSharedState("当前会话");

        _context.Log.Info($"浏览器 {browserId} 已自动授权给会话 {sessionId}（AI 正在使用）");
    }

    /// <summary>
    /// 校验「这个会话能不能操作这个窗口」。没权限就抛异常 ——
    /// 由工具层翻成给模型看的一句话，让它知道该请用户授权而不是反复重试。
    /// </summary>
    public void RequireAccess(string browserId, string? sessionId)
    {
        if (_grants.CheckAccess(browserId, sessionId) is { } reason)
            throw new UnauthorizedAccessException(reason);
    }

    /// <summary>
    /// 取一个浏览器窗口；不存在时抛异常（由工具层翻成给模型看的失败原因）。
    ///
    /// <b>只能在 UI 线程调用</b>：调用点都在 <see cref="UiAsync{T}"/> 的 lambda 里。
    /// 不做跨线程调度 —— 那需要在这里阻塞等待 UI 线程，而 UI 线程若正等着调用方就会死锁。
    /// </summary>
    public BrowserWindow Require(string browserId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(browserId))
            throw new InvalidOperationException("缺少 browser_id。先用 web_open 打开一个浏览器，它会把 id 告诉你。");

        if (_windows.TryGetValue(browserId, out var window)) return window;

        var known = string.Join("、", OpenIds.Count == 0 ? new[] { "（没有开着的浏览器）" } : OpenIds.ToArray());
        throw new InvalidOperationException($"找不到浏览器 {browserId}。当前开着的是：{known}");
    }

    /// <summary>打开（或复用）一个浏览器并导航。不给 id 就新开一个。</summary>
    public Task<(string BrowserId, string Url)> NavigateAsync(
        string? browserId, string url, string? sessionId, CancellationToken ct) => UiAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        BrowserWindow window;
        string id;

        if (string.IsNullOrWhiteSpace(browserId))
        {
            // 没指定就用最近打开的那个 —— 但要能操作它，否则新开一个。
            // 「别人的窗口」直接复用会把那个会话正在看的页面导航走。
            var existing = OpenIds.LastOrDefault();
            if (existing is not null
                && _windows.TryGetValue(existing, out var w)
                && _grants.CheckAccess(existing, sessionId) is null)
            {
                window = w;
                id = existing;
            }
            else
            {
                // 必须把 sessionId 传下去：早先这里传的是 null，
                // 结果窗口建出来了、CheckAccess 也放行（没人占就允许），
                // 但授权表里没有记录 —— 界面上一直显示「共享给智能体」，
                // 而 AI 明明已经在操作这一页了。
                id = await OpenAsync(null, sessionId, ct);
                window = _windows[id];
            }
        }
        else
        {
            window = Require(browserId);
            id = window.Id;
        }

        // 兜底：不管走新建还是复用，只要是「AI 在用这个窗口」而它还没有主人，
        // 就补一条授权。否则 UI 与真实权限会长期不一致 ——
        // 那种不一致最坏的情形是「界面说没共享，实际 AI 一直在用」。
        EnsureGranted(id, window, sessionId);

        _tabs.TryGetValue(id, out var tab);
        tab?.Activate();

        var loaded = await window.AddPageAsync(url, ct);
        return (id, loaded);
    }, ct);

    /// <summary>截图：优先截当前标签页。</summary>
    public Task<(string Path, string Url, long Bytes)> ScreenshotAsync(
        string browserId, string? sessionId, CancellationToken ct) => UiAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RequireAccess(browserId, sessionId);
        var window = Require(browserId);
        var surface = window.ActiveSurface
            ?? throw new InvalidOperationException($"浏览器 {browserId} 里没有可截图的标签页。");

        var (bytes, source) = await surface.CaptureAsync(ct);

        var folder = Path.Combine(_context.Storage.DataDirectory, "shots");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        await File.WriteAllBytesAsync(path, bytes, ct);

        return (path, source, bytes.LongLength);
    }, ct);

    // ============================================================
    // 宿主标签页
    // ============================================================

    private IPluginTab OpenHostTab(BrowserWindow window)
    {
        if (_context.Tabs is null)
            throw new InvalidOperationException("插件清单里没有声明 ui.tab 能力，没法在宿主里开标签页。");

        var tab = _context.Tabs.OpenTab("内置浏览器", window);

        // 标签页标题跟着当前网页走：用户在一堆标签里能认出是哪个窗口
        window.TitleChanged += title =>
        {
            try { tab.SetTitle(title); }
            catch { /* 标签可能已经被关掉了，改标题失败不是故障 */ }
        };

        // 用户点 × 关掉宿主标签页 → 内核跟着释放；工具再调这个 id 就会得到「找不到」
        tab.Closed += () => Forget(window.Id);

        // 记访问历史。挂在这里而不是每个导航入口：工具导航、地址栏、页面内跳转、
        // target="_blank" 全都最终经过 SourceChanged，漏掉任何一条都会让历史不可信。
        window.Navigated += (url, title) => History.Record(url, title);

        return tab;
    }

    private void Forget(string browserId)
    {
        // 这里可能来自宿主的关闭回调，不一定在 UI 线程上做清理 ——
        // 字典本身只在 UI 线程访问，所以要投递回去
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Forget(browserId));
            return;
        }

        _windows.Remove(browserId);
        _tabs.Remove(browserId);

        // 窗口没了，授权必须跟着没：留一条指向不存在窗口的授权，
        // 下次用户想共享同一批窗口时会撞上一条幽灵记录。
        _ownerTitles.Remove(browserId);
        var hadGrant = _grants.Revoke(browserId);

        _context.Log.Info(
            $"浏览器窗口 {browserId} 已关闭（剩余 {_windows.Count} 个）" +
            (hadGrant ? "，授权已一并清除" : ""));
    }

    // ============================================================
    // 共享 / 授权
    // ============================================================

    /// <summary>
    /// 用户点了「共享给智能体」：列出会话让他选，选定后授权。
    ///
    /// <b>失败必须抛出去</b>：这条路径的调用方是工具栏按钮，它只会在 catch 里
    /// 记日志 + 显示状态。静默 return 的结果就是「点了没反应」——
    /// 而用户完全没有线索知道是缺能力、没会话，还是代码压根没跑到。
    /// </summary>
    private async Task ShareAsync(string browserId)
    {
        if (_windows.TryGetValue(browserId, out var window) is false)
            throw new InvalidOperationException($"找不到浏览器窗口 {browserId}（可能已被关闭）。");

        if (_context.Sessions is null)
            throw new InvalidOperationException(
                "插件清单里没有声明 chat.control 能力，读不到会话列表，无法共享。");

        var sessions = await _context.Sessions.ListSessionsAsync(50);
        if (sessions.Count == 0)
            throw new InvalidOperationException("还没有任何会话。先在「智能对话」里创建一个会话，再回来共享。");

        var owner = _grants.OwnerOf(browserId);
        ShowSessionPicker(window, sessions, owner);
    }

    /// <summary>弹一个会话列表让用户挑。选中后授权并刷新状态。</summary>
    private void ShowSessionPicker(
        BrowserWindow window, IReadOnlyList<SessionSummary> sessions, string? currentOwner)
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 320 };

        panel.Children.Add(new TextBlock
        {
            Text = "把这个浏览器窗口交给哪个会话？",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            Margin = new Thickness(4, 0, 4, 6),
            TextWrapping = TextWrapping.Wrap,
        });

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };

        foreach (var session in sessions)
        {
            var isCurrent = string.Equals(currentOwner, session.SessionId, StringComparison.Ordinal);

            var row = new Button
            {
                Background = isCurrent ? new SolidColorBrush(Color.Parse("#EEF4FF")) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8),
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(session.Title) ? "(未命名会话)" : session.Title,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 280,
                },
            };

            var target = session;
            row.Click += (_, _) =>
            {
                flyout.Hide();
                _ = GuardAsync("授权浏览器", () => GrantAsync(window, target));
            };

            panel.Children.Add(row);
        }

        // 浮层从共享状态条上弹：它就在工具栏下面一行，视觉上紧挨着按钮
        FlyoutBase.SetAttachedFlyout(window.ShareAnchor, flyout);
        flyout.ShowAt(window.ShareAnchor);
    }

    /// <summary>把窗口授权给某个会话。已被别的会话占着时如实告知，不静默抢占。</summary>
    private Task GrantAsync(BrowserWindow window, SessionSummary session)
    {
        if (_grants.Grant(window.Id, session.SessionId, out var conflict))
        {
            _ownerTitles[window.Id] = string.IsNullOrWhiteSpace(session.Title)
                ? "(未命名会话)"
                : session.Title;

            window.SetSharedState(_ownerTitles[window.Id]);
            _context.Log.Info($"浏览器 {window.Id} 已授权给会话「{session.Title}」");
            return Task.CompletedTask;
        }

        _context.Log.Warn(
            $"浏览器 {window.Id} 已授权给会话 {conflict}，不能同时授权给 {session.SessionId}");
        window.SetSharedState(OwnerTitleOf(window.Id));
        return Task.CompletedTask;
    }

    /// <summary>取消共享。</summary>
    private Task UnshareAsync(string browserId)
    {
        _ownerTitles.Remove(browserId);

        if (_grants.Revoke(browserId))
            _context.Log.Info($"浏览器 {browserId} 的共享已取消");

        if (_windows.TryGetValue(browserId, out var window))
            window.SetSharedState(null);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 窗口当前授权给谁（拿标题用于显示）。
    ///
    /// 标题只在授权那一刻记下来，之后不再查库：它只影响一处提示文案，
    /// 为它每次再跑一遍会话查询不值得。记不到时退回会话 id 前几位，
    /// 至少能让人分清「不是我这个会话」。
    /// </summary>
    private string? OwnerTitleOf(string browserId)
    {
        var owner = _grants.OwnerOf(browserId);
        if (owner is null) return null;

        if (_ownerTitles.TryGetValue(browserId, out var title)) return title;

        return owner.Length <= 8 ? owner : owner[..8] + "…";
    }

    /// <summary>窗口 id → 授权给它的会话标题。取消授权时一并清掉。</summary>
    private readonly Dictionary<string, string> _ownerTitles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 执行一段界面动作并吞掉异常 —— <b>但一定记日志</b>。
    /// 静默 catch 的后果是「点了没反应」，而那是最难查的一类问题：
    /// 用户说按钮坏了，日志里什么都没有。
    /// </summary>
    private async Task GuardAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // 用户自己取消的，不需要提示
        }
        catch (Exception ex)
        {
            _context.Log.Error($"浏览器操作失败：{what}", ex);
        }
    }

    /// <summary>
    /// 补协议头。<c>about:</c> / <c>data:</c> / <c>file:</c> / <c>http(s):</c> 原样放行。
    ///
    /// 模型给的地址常常是 <c>example.com</c> 这种形式 —— 直接交给内核会被当成
    /// 相对路径或搜索词，表现成「打开了但页面不对」，比报错更难查。
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0) return "about:blank";

        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return "https://" + trimmed;
    }

    // ============================================================
    // 环境
    // ============================================================

    /// <summary>
    /// 所有窗口共用一个 Environment。重复创建 Environment 会让同一 user data folder
    /// 被打开多次，WebView2 会直接报错（一个文件夹只能有一个浏览器进程组）。
    /// </summary>
    private async Task<CoreWebView2Environment> EnsureEnvironmentAsync()
    {
        if (_environment is not null) return _environment;

        Directory.CreateDirectory(UserDataFolder);

        _environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        _context.Log.Info($"WebView2 环境已创建：{_environment.BrowserVersionString}，数据目录 {UserDataFolder}");
        return _environment;
    }

    // ============================================================
    // 线程
    // ============================================================

    /// <summary>
    /// 后台线程 → UI 线程，并把取消令牌一路带过去。
    ///
    /// <b>整个插件里只有这一处做线程切换</b>：所有内核调用最终都从这条路径进来。
    /// 分散写的话总有一处会忘，而「忘了切线程」的表现是偶发的跨线程异常。
    /// </summary>
    internal static async Task<T> UiAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await work().WaitAsync(ct);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await work()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });

        return await completion.Task.WaitAsync(ct);
    }

    /// <summary>
    /// 关掉所有浏览器窗口（不动数据）。设置页在清理缓存/重置之前要先调它 ——
    /// profile 目录被浏览器进程占着时，删文件只会得到一堆「另一个程序正在使用」。
    ///
    /// <b>不是 Dispose</b>：关完之后插件照常可用，下次打开浏览器会重建环境。
    /// </summary>
    public Task<int> CloseAllAsync(CancellationToken ct) => UiAsync(() =>
    {
        var count = _windows.Count;
        CloseAll();   // 里面会把 _environment 置空，下次打开浏览器重建
        _context.Log.Info($"已关闭全部浏览器窗口（{count} 个，设置页请求）");
        return Task.FromResult(count);
    }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            // 宿主给 ShutdownAsync 只有 3 秒，不等关闭结果
            Dispatcher.UIThread.Post(CloseAll);
            return;
        }

        CloseAll();
    }

    private void CloseAll()
    {
        // 先关宿主标签页（会触发 tab.Closed → Forget），再清理残留
        foreach (var tab in _tabs.Values.ToList())
        {
            try { tab.Close(); }
            catch { /* 退出路径上不抛 */ }
        }

        _tabs.Clear();
        _windows.Clear();
        _environment = null;
    }
}
