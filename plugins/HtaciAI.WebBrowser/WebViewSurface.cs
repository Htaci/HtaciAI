using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using Microsoft.Web.WebView2.Core;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 一个网页标签页的内核承载。每个实例 = 一个 <see cref="CoreWebView2Controller"/>。
///
/// <b>必须返回「新建的子窗口」而不是传进来的 parent</b>：
/// <see cref="CreateNativeControlCore"/> 拿到的 <c>parent</c> 是宿主的顶层窗口。
/// 若把它原样返回，attachment 会认为「我的原生控件就是宿主窗口」，
/// 于是用 <c>SetWindowPos</c> 去挪整个主窗口 —— 布局与尺寸立刻错乱。
///
/// <b>多个标签页共享一个 Environment</b>：同一个 user data folder 下创建多个 controller，
/// cookie 与登录态因此是通的（这是「同一浏览器的多个标签页」该有的行为），
/// 但每个标签页有自己的渲染进程，所以内存按标签数线性增长。
/// </summary>
internal sealed class WebViewSurface : NativeControlHost
{
    private readonly CoreWebView2Environment _environment;
    private readonly IPluginLogger _log;
    /// <summary>
    /// 当前这一次「原生控件 → 内核」的完成信号。
    ///
    /// <b>必须是每次创建都新建一个，不能是 readonly 的一次性成员</b>：
    /// 控件被移出可视树时 Avalonia 会销毁原生子窗口，我们跟着关掉控制器；
    /// 再挂回来时 <see cref="CreateNativeControlCore"/> 会重新跑一遍。
    /// 若沿用同一个已完成的 TCS，<c>await</c> 会立刻返回，而此时
    /// <see cref="_controller"/> 还是 null —— 于是调用方拿到 NRE，
    /// 表现成「点了按钮什么都没发生」（异常被工具栏的兜底吞掉了）。
    /// </summary>
    private TaskCompletionSource<CoreWebView2Controller>? _pending;

    private CoreWebView2Controller? _controller;
    private bool _startPageLoaded;

    /// <summary>
    /// 当前挂起的 alert / confirm / prompt。
    ///
    /// <b>为什么要记下来而不是让它自己弹</b>：CDP 触发的对话框会阻塞页面脚本，
    /// 而我们的工具调用可能正等着一个 <c>Runtime.evaluate</c> 返回 —— 两边互等就是死锁。
    /// 所以订阅 <c>ScriptDialogOpening</c> 记下来，由 web_handle_dialog 显式应答。
    /// </summary>
    private PendingDialog? _pendingDialog;

    /// <summary>一个等待处理的页面对话框。</summary>
    private sealed record PendingDialog(string Type, string Message, string? DefaultText);

    /// <summary>
    /// 控制台消息环形缓冲。
    ///
    /// <b>为什么要缓冲</b>：<see cref="CoreWebView2.ConsoleMessageReceived"/> 只在消息产生的
    /// 那一刻给一次，模型事后调 <c>web_console</c> 时那些消息早就过去了。
    /// 顺手收着是唯一能看到「刚才那个按钮为什么没反应」的办法。
    ///
    /// <b>为什么限长</b>：一个记录了大量日志的页面能刷出几十万条，全留着是内存泄漏。
    /// 保留最近 200 条对排查足够。
    /// </summary>
    private readonly Queue<string> _console = new();

    private const int ConsoleCapacity = 200;

    public WebViewSurface(CoreWebView2Environment environment, IPluginLogger log)
    {
        _environment = environment;
        _log = log;
    }

    /// <summary>内核是否已就绪。等待时用 <see cref="RequireCoreAsync"/> 更稳（它会等信号出现）。</summary>
    public Task ReadyTask => _pending?.Task ?? Task.CompletedTask;

    /// <summary>
    /// 拿到当前的 <c>CoreWebView2</c> 供自动化层调用 CDP。
    /// <b>只能在 UI 线程使用</b>：所有 CDP 调用最终都在这里落到内核对象上。
    /// </summary>
    public async Task<CoreWebView2> RequireCoreAsync(CancellationToken ct)
        => (await RequireControllerAsync(ct)).CoreWebView2;

    /// <summary>
    /// 等这一次的控制器就绪，并把它返回。
    ///
    /// <b>为什么要「等信号出现」而不是直接判失败</b>：<see cref="_pending"/> 是在
    /// <see cref="CreateNativeControlCore"/> 里建的，而那一次调用发生在「控件被挂上可视树」
    /// 的时候 —— 与我们调用这里的时机之间可能隔着一次布局调度。刚 new 出来就调用的场景
    /// （点了工具栏的「+」）正好踩在这个缝上，直接判失败会让按钮永远没反应。
    ///
    /// <b>为什么要循环</b>：等的时候控件可能被卸载又重建（切换标签页），
    /// 那会换一个新的信号与控制器；拿到「已经不是当前那一个」就再等一轮。
    /// </summary>
    private async Task<CoreWebView2Controller> RequireControllerAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pending = _pending;

            if (pending is null)
            {
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException(
                        $"标签页在 {ReadyTimeout.TotalSeconds:0} 秒内没有显示出来（原生控件未创建）。");

                // 50ms 足够细，又不是忙等
                await Task.Delay(50, ct);
                continue;
            }

            var controller = await pending.Task.WaitAsync(ct);

            // 期间被卸载重建过：换的是新控制器，继续等当前这个
            if (ReferenceEquals(_controller, controller)) return controller;

            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException("标签页反复被重建，拿不到可用的内核。");
        }
    }

    /// <summary>等内核的上限。冷启动一次环境实测 1 秒左右，15 秒足够覆盖慢机器。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>页面地址变化（导航完成、重定向、SPA 路由）。<b>在 UI 线程触发。</b></summary>
    public event Action<string>? SourceChanged;

    /// <summary>内核创建失败。窗口拿它显示一行错误，而不是静默白屏。</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// 页面要求打开新窗口（<c>target="_blank"</c>、<c>window.open</c>、链接被中键点击）。
    ///
    /// <b>不接管会怎样</b>：WebView2 的默认行为是自己弹一个<b>独立窗口</b> ——
    /// 那个窗口不在我们的标签体系里，没有工具栏、没有共享授权、也不会被
    /// 「关掉宿主标签页就清理」的逻辑收走。用户看到的是「点一下链接，冒出来一个新窗口」，
    /// 而且完全不受插件管理。
    ///
    /// <b>返回什么</b>：返回一个<b>新建且还没导航过</b>的 <c>CoreWebView2</c>，
    /// 内核会把目标页面渲染到它里面（由内核负责导航，我们不重复 nav）；
    /// 返回 null 表示放弃处理（此时调用方应自己决定要不要在当前页导航）。
    /// </summary>
    public Func<string, Task<CoreWebView2?>>? NewWindowRequested { get; set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var child = base.CreateNativeControlCore(parent);

        var pending = new TaskCompletionSource<CoreWebView2Controller>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;

        _log.Info($"标签页原生控件已创建：0x{child.Handle.ToInt64():X}（Bounds={Bounds}）");

        Dispatcher.UIThread.Post(() => _ = CreateControllerAsync(child.Handle, pending));
        return child;
    }

    private async Task CreateControllerAsync(IntPtr hwnd, TaskCompletionSource<CoreWebView2Controller> pending)
    {
        try
        {
            var controller = await _environment.CreateCoreWebView2ControllerAsync(hwnd);

            controller.DefaultBackgroundColor = Color.White;
            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SourceChanged += (_, _) => SourceChanged?.Invoke(core.Source ?? "");

            // 记下挂起的对话框，交给 web_handle_dialog 处理（见 _pendingDialog 的说明）
            core.ScriptDialogOpening += (_, e) =>
            {
                var type = e.Kind switch
                {
                    CoreWebView2ScriptDialogKind.Alert => "alert",
                    CoreWebView2ScriptDialogKind.Confirm => "confirm",
                    CoreWebView2ScriptDialogKind.Prompt => "prompt",
                    _ => "beforeunload",
                };

                _pendingDialog = new PendingDialog(type, e.Message ?? "", e.DefaultText);
                e.Accept();

                _log.Info($"页面打开了 {type} 对话框：{Shorten(e.Message, 80)}");
            };

            // 页面要开新窗口时把它收进我们的标签体系，而不是让内核自己弹一个
            // 不受管理的独立窗口（见 NewWindowRequested 的说明）
            core.NewWindowRequested += (_, e) =>
            {
                if (NewWindowRequested is null) return;   // 没人接管，按内核默认行为

                // 必须显式 Handled：否则内核在事件返回后仍会弹自己的窗口，
                // 我们会同时得到一个标签页和一个野窗口。
                e.Handled = true;
                _ = HandleNewWindowAsync(e);
            };

            // 收着控制台消息，供 web_console 事后查（见 _console 的说明）。
            //
            // WebView2 的托管层**没有** ConsoleMessageReceived 事件，只能走 CDP：
            // 订阅 Runtime.consoleAPICalled，并先 Runtime.enable 打开该域。
            // 这是一次性设置，失败不影响内核其他功能（web_console 会返回「没有消息」）。
            _ = HookConsoleAsync(core);

            _controller = controller;
            UpdateBounds();

            _log.Info($"标签页内核就绪：{core.Environment.BrowserVersionString}，Bounds={controller.Bounds}（DIP={Bounds}）");
            pending.TrySetResult(controller);
        }
        catch (Exception ex)
        {
            _log.Error("创建标签页内核失败", ex);
            pending.TrySetException(ex);
            Failed?.Invoke(ex);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // 先关控制器，再让 base 销毁子窗口 —— 顺序反了会让内核去碰一个已消失的父窗口。
        var controller = _controller;
        _controller = null;

        _log.Info($"标签页原生控件已销毁（有控制器：{controller is not null}）");

        // 作废本次信号：否则「控件已销毁」之后再 await 会拿到那个已经被关掉的控制器
        _pending?.TrySetException(new InvalidOperationException("标签页已从界面移除。"));
        _pending = null;

        try { controller?.Close(); }
        catch { /* 卸载路径上不抛异常 */ }

        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 切到别的标签页时 Avalonia 会把这里设成不可见。内核不销毁，
        // 回来时把尺寸补回去即可 —— 销毁重建会丢页面状态、还得重走导航。
        if (change.Property == BoundsProperty || change.Property == IsVisibleProperty)
        {
            _log.Info($"标签页 {change.Property.Name} 变化：IsVisible={IsVisible}，Bounds={Bounds}");
            UpdateBounds();
        }
    }

    /// <summary>
    /// 让内核重新对齐当前布局尺寸。
    ///
    /// 切到某个标签页时显式调一次：后台标签页的控制器是以 1x1 建的，
    /// 而「变成可见」不一定会带来一次 <c>Bounds</c> 变化（布局早就跑过了），
    /// 于是只靠属性回调可能永远补不上尺寸 —— 表现就是「切过去一片空白」。
    /// </summary>
    public void RefreshBounds() => UpdateBounds();

    /// <summary>WebView2 的 Bounds 是物理像素，Avalonia 的 Bounds 是 DIP，要按缩放比换算。</summary>
    private void UpdateBounds()
    {
        if (_controller is null) return;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

        if (_controller.Bounds.Width == width && _controller.Bounds.Height == height) return;

        _controller.Bounds = new Rectangle(0, 0, width, height);
        _log.Info($"标签页尺寸同步为 {width}x{height}（缩放 {scale}）");
    }

    // ============================================================
    // 导航
    // ============================================================

    /// <summary>
    /// 导航并等它完成。<b>只能在 UI 线程调用。</b>
    ///
    /// <b>不能用「第一个 NavigationCompleted」下结论</b>：一次跳转经常会产生多个导航条目
    /// （301/302 重定向、HSTS 升级、页面自己的 JS 跳转）。前面那几个会以
    /// <c>IsSuccess=false</c> + <c>ConnectionAborted</c> 结束 —— 那不是失败，是「被下一次导航接替了」。
    /// 按第一个事件判断的结果是：页面明明已经渲染出来，却报「页面加载失败」。
    ///
    /// 所以这里用 <c>NavigationId</c> 圈定「本次跳转引发的导航」，并按错误类型区分：
    /// 只有「不是被打断」的失败才算真失败。
    /// </summary>
    public async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        var controller = await RequireControllerAsync(ct);
        var core = controller.CoreWebView2;

        // 本次跳转引发的全部导航 id。NavigationStarting 一定早于对应的 Completed，
        // 而在我们订阅之前就已经开始的旧导航不会进来 —— 这就是「本次」的边界。
        var scope = new HashSet<ulong>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2WebErrorStatus? lastError = null;

        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) => scope.Add(e.NavigationId);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!scope.Contains(e.NavigationId)) return;

            if (e.IsSuccess)
            {
                done.TrySetResult(true);
                return;
            }

            lastError = e.WebErrorStatus;

            // 被打断 = 后面还有一次导航（重定向、HSTS、JS 跳转）。继续等。
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted) return;

            done.TrySetResult(false);
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);

            bool ok;
            try
            {
                ok = await done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (TimeoutException)
            {
                // 一直没等到终态。SPA 或长轮询页面可能始终不触发「完成」，
                // 这时以「地址已经变了」为成功判据，而不是一律报超时。
                var current = core.Source ?? "";
                if (current is "" or "about:blank")
                    throw new InvalidOperationException("页面加载超时（30 秒内没有完成，且地址仍是空白）。");

                _log.Warn($"导航超时，但地址已是 {current}，按成功处理");
                return current;
            }

            if (!ok)
                throw new InvalidOperationException(
                    $"页面加载失败：{core.Source}（{Describe(lastError)}）");

            return core.Source;
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>把 WebErrorStatus 翻成能给人看的一句话。模型看到「错误码 7」是没法判断下一步的。</summary>
    private static string Describe(CoreWebView2WebErrorStatus? status) => status switch
    {
        null => "未知原因",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "域名解析失败",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务器",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.Disconnected => "连接被断开",
        CoreWebView2WebErrorStatus.ServerUnreachable => "服务器不可达",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "证书域名不匹配",
        CoreWebView2WebErrorStatus.CertificateExpired => "证书已过期",
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "客户端证书有问题",
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "服务器返回了非法响应",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
        CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
        CoreWebView2WebErrorStatus.UnexpectedError => "浏览器内部错误",
        CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "需要身份验证",
        CoreWebView2WebErrorStatus.RedirectFailed => "重定向失败",
        _ => status.Value.ToString(),
    };

    public async Task GoBackAsync(CancellationToken ct)
    {
        var core = (await RequireControllerAsync(ct)).CoreWebView2;
        if (core.CanGoBack) core.GoBack();
    }

    public async Task GoForwardAsync(CancellationToken ct)
    {
        var core = (await RequireControllerAsync(ct)).CoreWebView2;
        if (core.CanGoForward) core.GoForward();
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        (await RequireControllerAsync(ct)).CoreWebView2.Reload();
    }

    /// <summary>
    /// 应答挂起的对话框。返回是否真的有一个对话框被处理。
    /// <paramref name="promptText"/> 只对 prompt 有意义。
    /// </summary>
    public Task<bool> HandleDialogAsync(bool accept, string? promptText, CancellationToken ct)
    {
        var pending = _pendingDialog;
        if (pending is null) return Task.FromResult(false);

        _pendingDialog = null;

        _log.Info($"对话框 {pending.Type} 已处理：{(accept ? "接受" : "取消")}" +
                  (promptText is null ? "" : $"，输入「{Shorten(promptText, 40)}」"));

        return Task.FromResult(true);
    }

    /// <summary>
    /// 把 NewWindowRequested 转给我们的事件处理器，并把内核的 deferral 正确收尾。
    ///
    /// <b>deferral 必须 Complete</b>：不调它内核会一直认为这次请求没处理完，
    /// 后续所有新窗口请求都会排在它后面 —— 表现是「第一次点击能开，之后再也不开」。
    /// </summary>
    private async Task HandleNewWindowAsync(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var target = await NewWindowRequested!(e.Uri);

            if (target is not null)
            {
                // 内核会把目标页面导航进这个 CoreWebView2 —— 我们不要再 nav 一次
                e.NewWindow = target;
                return;
            }

            // 处理器放弃了：退回「在当前标签导航」，总比什么都不发生好
            _log.Info($"新窗口请求未被接管，改为在当前标签打开：{e.Uri}");
            try
            {
                var core = await RequireCoreAsync(CancellationToken.None);
                core.Navigate(e.Uri);
            }
            catch (Exception ex)
            {
                _log.Error("退回当前标签导航时失败", ex);
            }
        }
        catch (Exception ex)
        {
            // 这里抛出去会变成未处理异常，把整个内核拖进不确定状态
            _log.Error("处理新窗口请求失败", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// 订阅 CDP 的 <c>Runtime.consoleAPICalled</c> 并把消息收进缓冲。
    ///
    /// <b>为什么只留 warn/error</b>：一个正常的应用页面光 info 就能刷几百条，
    /// 全收会把「真正有问题的那条」埋掉，而模型查控制台就是为了找那个。
    /// </summary>
    private async Task HookConsoleAsync(CoreWebView2 core)
    {
        try
        {
            var receiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");

            receiver.DevToolsProtocolEventReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);

                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type is not ("warning" or "error")) return;

                    // args 是 RemoteObject 数组，值一般在 value 或 description 里
                    var parts = new List<string>();
                    if (doc.RootElement.TryGetProperty("args", out var args) &&
                        args.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arg in args.EnumerateArray())
                        {
                            if (arg.TryGetProperty("value", out var v))
                                parts.Add(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString());
                            else if (arg.TryGetProperty("description", out var d))
                                parts.Add(d.GetString() ?? "");
                        }
                    }

                    var message = string.Join(" ", parts).Trim();
                    if (message.Length == 0) return;

                    var level = type == "error" ? "ERROR" : "WARN";
                    var line = $"[{level}] {Shorten(message, 300)}";

                    if (doc.RootElement.TryGetProperty("stackTrace", out var st) &&
                        st.TryGetProperty("callFrames", out var frames) &&
                        frames.ValueKind == JsonValueKind.Array && frames.GetArrayLength() > 0)
                    {
                        var frame = frames[0];
                        var url = frame.TryGetProperty("url", out var u) ? u.GetString() : "";
                        var lineNo = frame.TryGetProperty("lineNumber", out var ln) ? ln.GetInt32() + 1 : 0;

                        // 过滤浏览器扩展注入的脚本：那不是页面作者的问题，只会干扰判断
                        if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("chrome-extension://", StringComparison.Ordinal))
                            line += $"  ({url}:{lineNo})";
                    }

                    _console.Enqueue(line);
                    while (_console.Count > ConsoleCapacity) _console.Dequeue();
                }
                catch
                {
                    // 解析失败就丢掉这一条：控制台收集不该影响页面正常运行
                }
            };

            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            _log.Info("控制台消息订阅已启用");
        }
        catch (Exception ex)
        {
            // 订阅失败不致命：web_console 会退回「没有消息」，其余功能照常
            _log.Warn("订阅控制台消息失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 取出最近的控制台消息（错误优先）。
    /// <b>只读不清空</b>：模型可能连着问两次，清空会让第二次什么都没有。
    /// </summary>
    public List<string> DrainConsole(int limit)
    {
        var all = _console.ToList();

        // 错误排在前面：排查时最想看的是它
        var errors = all.Where(l => l.StartsWith("[ERROR]", StringComparison.Ordinal)).ToList();
        var warns = all.Where(l => !l.StartsWith("[ERROR]", StringComparison.Ordinal)).ToList();

        return errors.Concat(warns).Take(limit).ToList();
    }

    /// <summary>当前是否挂着对话框。工具用它提示模型「先处理弹窗」。</summary>
    public (string Type, string Message)? PeekDialog()
        => _pendingDialog is { } d ? (d.Type, d.Message) : null;

    private static string Shorten(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>导航能力快照，工具栏用它决定按钮是否可点。</summary>
    public (bool CanGoBack, bool CanGoForward, string Url) GetNavigationState()
    {
        if (_controller is null) return (false, false, "");

        var core = _controller.CoreWebView2;
        return (core.CanGoBack, core.CanGoForward, core.Source ?? "");
    }

    /// <summary>
    /// 新建标签页时显示起始页。用 <c>NavigateToString</c> 而不是远程地址：
    /// 不联网也能看见东西，断网时不会表现成「插件坏了」。
    /// </summary>
    public async Task<string> EnsureStartPageAsync(CancellationToken ct)
    {
        var core = (await RequireControllerAsync(ct)).CoreWebView2;

        // 已经有真实页面就别覆盖用户的现场
        if (core.Source is not (null or "" or "about:blank")) return core.Source;
        if (_startPageLoaded) return "起始页";

        core.NavigateToString(StartPageHtml);
        _startPageLoaded = true;
        return "起始页";
    }

    /// <summary>走 CDP 截图。</summary>
    public async Task<(byte[] Bytes, string Source)> CaptureAsync(CancellationToken ct)
    {
        var core = (await RequireControllerAsync(ct)).CoreWebView2;
        var json = await core
            .CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}")
            .WaitAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var base64 = doc.RootElement.GetProperty("data").GetString() ?? "";
        return (Convert.FromBase64String(base64), core.Source);
    }

    public string? CurrentUrl => _controller?.CoreWebView2.Source;

    private const string StartPageHtml = """
        <!doctype html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <title>新标签页</title>
        <style>
          html,body { height:100%; margin:0; }
          body { display:flex; align-items:center; justify-content:center;
                 font:15px/1.7 "Segoe UI","Microsoft YaHei",system-ui,sans-serif;
                 color:#333a49; background:#fbfcfe; }
          .box { max-width:520px; padding:0 24px; }
          h1 { font-size:20px; margin:0 0 10px; }
          p { margin:0 0 10px; color:#5a6273; }
          code { background:#eef1f6; border-radius:4px; padding:1px 5px;
                 font-family:Consolas,monospace; font-size:13px; }
        </style></head>
        <body><div class="box">
          <h1>新标签页</h1>
          <p>在地址栏输入网址，或让 AI 用 <code>web_open</code> 打开页面。</p>
          <p>这个窗口里的所有标签页共享登录态。</p>
        </div></body></html>
        """;
}
