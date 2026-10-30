using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件生命周期管理：扫描目录、加载、卸载、崩溃归因。
///
/// <b>线程约定</b>：所有公开方法都假定在 UI 线程上调用（插件可以建控件、开关窗口），
/// 内部因此不加锁。唯一例外是 <see cref="Changed"/> 的订阅方，也在 UI 线程上收到通知。
///
/// <b>卸装的顺序是有讲究的</b>（见 <see cref="UnloadAsync"/>）：先关投递、再等插件收尾、
/// 最后才卸载程序集。反过来做的话，程序集已经没了而插件代码还在跑，会直接崩。
///
/// <b>「是否需要重启」是探测出来的</b>：`Unload()` 之后用 <see cref="WeakReference"/> 看上下文
/// 是否真的被回收。回收不掉就把状态标成 <see cref="PluginRunState.NeedsRestart"/>，
/// 并且不再尝试第二次解析卸载 —— 重复 Unload 只会让状态更混乱。
/// </summary>
public sealed partial class PluginManager
{
    public static PluginManager Instance { get; } = new();

    /// <summary>插件初始化的超时。超过就不再等它，按加载失败处理。</summary>
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>插件收尾的超时。卡住的插件不能拖着整个退出流程。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly List<PluginEntry> _entries = new();

    private bool _initialized;
    private bool _stopping;

    private PluginManager() { }

    /// <summary>插件列表或其状态发生变化。设置在 UI 线程上刷新。</summary>
    public event Action? Changed;

    /// <summary>宿主总开关（<c>AppSettingsData.EnablePlugins</c>），默认关闭。</summary>
    public static bool IsMasterEnabled => AppSettingsStore.Current.EnablePlugins;

    /// <summary>插件目录的绝对路径，供设置页「打开目录」使用。</summary>
    public static string PluginsDirectory => AppPaths.Plugins;

    /// <summary>当前所有已发现的插件快照（含加载失败的）。</summary>
    /// <summary>全部插件快照，<b>按用户设定的加载顺序</b>（小的在前）。</summary>
    public IReadOnlyList<PluginInfo> GetAll() => OrderedEntries().Select(ToInfo).ToList();

    /// <summary>插件 id 是否已发现。</summary>
    public PluginInfo? Find(string id) => _entries.Where(e => e.Id == id).Select(ToInfo).FirstOrDefault();

    /// <summary>加载成功且正在运行的插件 id。</summary>
    public IEnumerable<string> GetLoadedIds()
        => _entries.Where(e => e.RunState == PluginRunState.Loaded).Select(e => e.Id);

    /// <summary>
    /// 取插件的设置页工厂（插件实现 <see cref="IPluginSettingsProvider"/> 才有）。
    ///
    /// <b>没跑起来的插件返回 null</b>：设置界面也是插件代码，实例都卸了就没法调；
    /// 调用方据此显示「插件未运行」，而不是留一片空白让人以为界面坏了。
    /// </summary>
    public IPluginSettingsProvider? GetSettingsProvider(string id)
    {
        var entry = FindEntry(id);
        if (entry is null || entry.RunState != PluginRunState.Loaded) return null;
        return entry.Instance as IPluginSettingsProvider;
    }

    // ============================================================
    // 启动 / 退出
    // ============================================================

    /// <summary>
    /// 首次初始化：崩溃归因 → 扫描目录 → 按策略加载。
    /// <b>必须排在主程序全部初始化之后调用</b>（插件可能要用数据库、注册表、主窗口）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        PluginCrashReporter.Install();
        PluginCrashReporter.RecordPreviousRun();

        ScanDirectory();

        // 扫描之后、加载之前把清单里描述的关系过一遍：
        // 平台不符 / 前置没装 / 互相依赖成环 —— 这三种到这里就能判定，不必等加载失败。
        ApplyManifestRules();

        if (!IsMasterEnabled)
        {
            // 总开关关着：连加载都不尝试，但列表仍要能显示出来，方便用户看到有什么
            foreach (var entry in _entries)
                entry.RunState = entry.Record.Enabled ? PluginRunState.NotLoaded : PluginRunState.Disabled;

            Raise();
            return;
        }

        foreach (var entry in LoadOrder())
        {
            if (_stopping) break;
            await TryLoadOnStartupAsync(entry, ct);
        }

        Raise();
    }

    private async Task TryLoadOnStartupAsync(PluginEntry entry, CancellationToken ct)
    {
        // 扫描阶段已经判定过的问题（清单坏了、版本不够、入口缺失、平台不符、循环依赖）保持原状
        if (entry.RunState is PluginRunState.Blocked or PluginRunState.Failed) return;

        if (!entry.Record.Enabled)
        {
            entry.RunState = PluginRunState.Disabled;
            return;
        }

        // 上次疑似崩过、用户又没开「崩溃后仍自动加载」：跳过。
        // 这是防「一启动就崩、连设置页都进不去」的唯一闸门。
        if (entry.Record.CrashSuspected && !entry.Record.AutoLoadAfterCrash)
        {
            entry.RunState = PluginRunState.SkippedByCrash;
            entry.Error = "上次退出时疑似由该插件导致崩溃，已跳过加载。确认后可手动启用。";
            PluginLog.Warn(entry.Id, "因疑似崩溃标记跳过加载");
            return;
        }

        // 前置没起来就别加载它：起不来的前置几乎必然让依赖方在半路炸掉，
        // 而那种崩溃在日志里看起来会像是依赖方自己的问题。
        if (CheckDependencies(entry)) return;

        await LoadAsync(entry, ct);
    }

    /// <summary>
    /// 停止全部插件。退出流程与「重载」都用它。
    /// 注意它<b>不删运行标记</b> —— 那由 <see cref="PluginCrashReporter.MarkCleanExit"/> 负责，
    /// 因为「关掉插件」不等于「程序正常退出」。
    /// </summary>
    public async Task ShutdownAllAsync()
    {
        _stopping = true;
        try
        {
            foreach (var entry in _entries.ToList())
            {
                if (entry.Instance is null && entry.LoadContext is null) continue;

                try
                {
                    await UnloadAsync(entry);
                }
                catch (Exception ex)
                {
                    // 单个插件收尾失败不能拦住其余插件与程序退出
                    PluginLog.Error(entry.Id, "卸载时发生异常", ex);
                }
            }
        }
        finally
        {
            Raise();
        }
    }

    /// <summary>
    /// 重新扫描并加载（总开关切换、用户手动刷新时用）。
    /// 先把所有插件停掉，再走一遍完整初始化。
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await ShutdownAllAsync();

        _entries.Clear();
        _initialized = false;
        _stopping = false;

        PluginStateStore.Reload();
        await InitializeAsync(ct);
    }

    // ============================================================
    // 单个插件：启用 / 禁用
    // ============================================================

    /// <summary>
    /// 启用插件。用户对「疑似崩溃」的插件点确认后也走这里 —— 进来时就会清掉疑似标记。
    /// </summary>
    public async Task EnableAsync(string id, CancellationToken ct = default)
    {
        if (FindEntry(id) is not { } entry) return;

        entry.Record.Enabled = true;

        // 用户主动启用 = 确认要继续（不论之前是否疑似崩溃）
        entry.Record.CrashSuspected = false;
        PluginStateStore.Save();

        if (entry.RunState == PluginRunState.NeedsRestart)
        {
            // 旧实例的程序集还在内存里，再加载会变成两份同时跑。
            // 只能等重启 —— 提示文案由 PluginInfo.StateText 负责。
            entry.Error = "该插件上次禁用时未能完全卸载，需要重启程序后才能重新加载。";
            Raise();
            return;
        }

        if (!IsMasterEnabled)
        {
            entry.RunState = PluginRunState.NotLoaded;
            entry.Error = "宿主总开关「启用插件」当前是关闭的。";
            PluginStateStore.Save();
            Raise();
            return;
        }

        if (entry.RunState == PluginRunState.Loaded)
        {
            Raise();
            return;
        }

        // 清单本身有问题的（Blocked）重新再校验一次：用户可能刚把文件补上
        if (entry.Manifest is null && !TryReadManifest(entry))
        {
            Raise();
            return;
        }
        if (!ValidateManifest(entry))
        {
            Raise();
            return;
        }

        // 手动启用时也要过一遍依赖：前置可能正是刚被禁用的那个
        ApplyManifestRules();
        if (entry.RunState == PluginRunState.Blocked) { Raise(); return; }

        // 用户手动启用 = 明确表示要用它，但前置确实没运行起来还是不能启用
        // （绕过这条只会让插件在半路炸掉，那种崩溃在日志里看起来像它自己的 bug）
        if (CheckDependencies(entry)) { Raise(); return; }

        await LoadAsync(entry, ct);

        // 之前因为「前置没起来」被挡住的依赖方，现在可以试着加载了
        await LoadWaitingDependentsAsync(entry.Id, ct);

        Raise();
    }

    /// <summary>某个前置刚加载成功：把因为它而没起来的依赖方重新试一遍（只试一层，且只在确实被挡过时）。</summary>
    private async Task LoadWaitingDependentsAsync(string loadedId, CancellationToken ct)
    {
        foreach (var entry in _entries.ToList())
        {
            if (entry.RunState != PluginRunState.DependencyBlocked) continue;
            if (entry.Manifest is not { } manifest) continue;
            if (!manifest.Requires.Contains(loadedId, StringComparer.Ordinal)) continue;
            if (!entry.Record.Enabled) continue;

            if (CheckDependencies(entry)) continue;

            await LoadAsync(entry, ct);
        }
    }

    /// <summary>禁用插件：停止投递 → 收尾 → 摘引用 → 尝试卸载程序集。</summary>
    public async Task DisableAsync(string id)
    {
        if (FindEntry(id) is not { } entry) return;

        entry.Record.Enabled = false;
        PluginStateStore.Save();

        if (entry.Instance is not null || entry.LoadContext is not null)
            await UnloadAsync(entry);
        else if (entry.RunState != PluginRunState.NeedsRestart)
            entry.RunState = PluginRunState.Disabled;

        // 前置停了，依赖它的那些也得停：让它们继续跑，很快会变成「前置已经不存在」的怪错
        await StopDependentsAsync(entry.Id);

        Raise();
    }

    /// <summary>设置「崩溃后仍自动加载」。默认关闭，只有用户明确勾选才打开。</summary>
    public void SetAutoLoadAfterCrash(string id, bool value)
    {
        if (FindEntry(id) is not { } entry) return;

        entry.Record.AutoLoadAfterCrash = value;
        PluginStateStore.Save();
        Raise();
    }

    // ============================================================
    // 加载
    // ============================================================

    private async Task LoadAsync(PluginEntry entry, CancellationToken ct)
    {
        var manifest = entry.Manifest!;

        WarnAboutBundledFramework(entry);

        PluginBreadcrumb.Enter(entry.Id, "initialize", critical: true);

        PluginLoadContext? alc = null;
        IPlugin? plugin = null;
        try
        {
            alc = new PluginLoadContext(entry.Id, entry.Directory);

            // 加载程序集、找类型、建实例都在一个**同步**帧里做完。
            // 不能让 Assembly / Type 出现在本方法里：这是个 async 方法，它返回的 Task
            // 就是状态机本身，那些引用会被它一直留到 Task 被回收为止 ——
            // 实测后果是插件永远判成「需重启」，文件也一直锁着。
            plugin = CreatePluginInstance(alc, entry.Manifest!, Path.Combine(entry.Directory, manifest.Entry));

            // 只给声明过的能力：没声明的注入 null，让插件在第一次使用时立刻发现
            // （空实现会让「我忘了声明」一路伪装成「插件就是不工作」，那种问题最难查）。
            //
            // 每个门面都登记进 Subscriptions：禁用插件时宿主统一收掉它们的订阅与贡献，
            // 不指望插件自己记得撤销。
            var context = new PluginContext(manifest, entry.Directory, AppVersion.Current, BuildFacades(entry));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InitializeTimeout);

            // 走 ExecuteDetached 而不是直接 await：直接 await 会把插件返回的 Task
            // （它自己就是插件状态机）挂在我们的 awaiter 字段上，等于一直吊着插件程序集。
            var initPlugin = plugin;
            await ExecuteDetached(() => initPlugin.InitializeAsync(context, cts.Token), InitializeTimeout);
            initPlugin = null;

            entry.Instance = plugin;
            entry.LoadContext = alc;
            entry.RunState = PluginRunState.Loaded;
            entry.Error = null;

            // 转让所有权：catch 里不再重复卸载
            alc = null;
            plugin = null;

            PluginLog.Info(entry.Id, $"已加载 {manifest.Name} {manifest.Version}");
        }
        catch (Exception ex)
        {
            entry.RunState = PluginRunState.Failed;
            entry.Error = Simplified(ex);
            PluginLog.Error(entry.Id, "加载失败", ex);

            // 初始化到一半炸掉的插件可能已经起了定时器或窗口：给它一次收尾机会
            if (plugin is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(ShutdownTimeout);
                    await ShutdownPluginAsync(plugin, cts.Token);
                }
                catch
                {
                    // 加载已经失败了，收尾再失败也没别的补救手段
                }
            }

            if (alc is not null) DiscardLoadContext(entry, alc);
        }
        finally
        {
            PluginBreadcrumb.Clear();
        }
    }

    /// <summary>
    /// 加载程序集、找到实现 <see cref="IPlugin"/> 的类型、建一个实例。
    ///
    /// <b>必须是一个同步方法</b>：调用它的 <see cref="LoadAsync"/> 是 async 的，
    /// 而 async 方法返回的 Task 就是状态机对象本身 —— 任何写在 async 方法里的
    /// <c>Assembly</c> / <c>Type</c> 局部变量都会被那个 Task 一直抓到它自己被回收为止。
    /// 那会让插件程序集永远无法卸载（禁用后始终提示「需重启」），dll 文件也一直被锁。
    /// </summary>
    /// <returns>插件实例；程序集不合法时抛异常，由调用方按加载失败处理。</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlugin CreatePluginInstance(PluginLoadContext alc, PluginManifest manifest, string entryPath)
    {
        // 入口程序集也走流加载：LoadFromAssemblyPath 会把 dll 映射成映像节，
        // 之后文件就一直不能写（插件没法更新、也没法删）。原因见 PluginLoadContext.ReadIntoMemory。
        var assembly = alc.LoadPluginAssembly(entryPath);

        var type = assembly.GetType(manifest.EntryType, throwOnError: false)
            ?? throw new InvalidOperationException($"程序集里找不到类型 {manifest.EntryType}");

        if (!typeof(IPlugin).IsAssignableFrom(type))
            throw new InvalidOperationException($"{manifest.EntryType} 没有实现 IPlugin");

        if (Activator.CreateInstance(type) is not IPlugin created)
            throw new InvalidOperationException($"{manifest.EntryType} 无法创建实例（需要公共无参构造函数）");

        return created;
    }

    /// <summary>插件目录里带了框架 dll 时提示一句。它会被跳过（用的是宿主那份），但作者可能不知道。</summary>
    private static void WarnAboutBundledFramework(PluginEntry entry)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(entry.Directory, "*.dll"))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("HtaciAI.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                PluginLog.Warn(entry.Id,
                    $"目录里包含 {name}，已忽略并改用宿主的同名程序集。" +
                    "插件不需要自带框架 dll，带上反而会造成两套类型。");
            }
        }
        catch
        {
            // 只是提示，枚举失败无所谓
        }
    }

    // ============================================================
    // 卸载
    // ============================================================

    /// <summary>
    /// 卸载一个插件的六步：
    /// <list type="number">
    ///   <item>静默：标记停止，新调用一律拒绝；</item>
    ///   <item>等插件收尾（<see cref="ShutdownTimeout"/> 封顶）；</item>
    ///   <item>撤销它登记过的订阅；</item>
    ///   <item>把 Dispatcher 上已排队的回调冲干净；</item>
    ///   <item>断开宿主持有的全部强引用，再 <c>Unload()</c>；</item>
    ///   <item>验证是否真的回收，决定状态是「已禁用」还是「需重启」。</item>
    /// </list>
    /// 第 1～4 步必须 100% 生效（功能层面它确实停了），第 6 步只是尽力而为。
    /// </summary>
    private async Task UnloadAsync(PluginEntry entry)
    {
        entry.Stopping = true;

        var instance = entry.Instance;
        if (instance is not null)
        {
            PluginBreadcrumb.Enter(entry.Id, "shutdown", critical: true);
            try
            {
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                await ShutdownPluginAsync(instance, cts.Token);
            }
            catch (TimeoutException)
            {
                // 超时不再等：卡住的插件不能拖着整个禁用/退出流程
                PluginLog.Warn(entry.Id, "ShutdownAsync 超时，不再等待，继续卸载");
            }
            catch (OperationCanceledException)
            {
                PluginLog.Warn(entry.Id, "ShutdownAsync 已取消");
            }
            catch (Exception ex)
            {
                PluginLog.Error(entry.Id, "ShutdownAsync 抛异常（已忽略）", ex);
            }
            finally
            {
                PluginBreadcrumb.Clear();
            }
        }

        // 插件登记过的订阅即使它的 ShutdownAsync 忘了撤销，这里也要收掉
        foreach (var subscription in entry.Subscriptions.ToArray())
        {
            try { subscription.Dispose(); } catch { /* 单个失败不影响其余 */ }
        }
        entry.Subscriptions.Clear();

        // 插件页显示的是「最近一次贡献」，禁用之后不该再挂着旧数字
        PluginPromptUsage.Clear(entry.Id);

        await FlushDispatcherAsync();

        entry.Instance = null;

        // 关键：本方法是个 async 方法，局部变量活在这个状态机对象上。
        // 不清掉的话，`instance` 这个强引用会让插件程序集的实例一直活着，
        // Unload 之后的回收判定就永远偏向「需重启」。
        instance = null;

        entry.Stopping = false;

        // 卸载与回收判定要在一个「干净的栈」上做：本方法是 async 状态机，
        // 即使把局部变量置空，JIT 仍可能在寄存器/栈槽里留一份旧值。
        var leaked = await Task.Run(() => UnloadLoadContext(entry));

        entry.RunState = leaked ? PluginRunState.NeedsRestart : PluginRunState.Disabled;
        entry.Error = leaked ? "插件文件仍被占用，本次运行内无法完全卸载。" : null;

        PluginLog.Info(entry.Id, leaked
            ? "已禁用，但插件文件仍被占用（需重启才能完全更新启用状态）"
            : "已禁用并卸载");
    }

    /// <summary>加载失败时丢掉这个上下文。不关心能不能回收，只要求别继续吊着。</summary>
    private static void DiscardLoadContext(PluginEntry entry, PluginLoadContext alc)
    {
        entry.LoadContext = null;
        try { alc.Unload(); } catch { /* 已经卸载过就忽略 */ }
    }

    /// <summary>
    /// 调用插件的 <see cref="IPlugin.ShutdownAsync"/>。
    ///
    /// <b>刻意是同步方法</b>（返回 Task 但不 await）：调用方 await 的是这里返回的
    /// <em>宿主</em> Task，插件自己的 Task 不进调用方的状态机 —— 否则插件实例会被
    /// 那条 await 链吊住，程序集永远卸不掉。
    /// </summary>
    private static Task ShutdownPluginAsync(IPlugin plugin, CancellationToken ct)
        => ExecuteDetached(() => plugin.ShutdownAsync(ct), ShutdownTimeout);

    /// <summary>
    /// 在独立的 Task 里跑一段插件代码，只把「成功 / 异常 / 超时」这个结果交回来。
    ///
    /// <b>为什么必须这么绕</b>：async 方法返回的 Task 就是它的状态机对象。
    /// 如果我们直接 <c>await plugin.SomeAsync()</c>，那个插件状态机会被我们自己的
    /// awaiter 字段一直引用，直到整条 await 链上的 Task 全被回收 ——
    /// 实测后果是禁用插件后程序集永远回收不掉（提示「需重启」，dll 也一直被锁）。
    /// 把插件调用封在一个「跑完就没人引用」的 Task 里，交回来的只有一个宿主 TCS。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ExecuteDetached(Func<Task> call, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 【必须在 UI 线程上「开始」执行】插件契约（IPlugin）承诺 InitializeAsync 与
        // ShutdownAsync 在 UI 线程上调用，插件因此敢在初始化里直接建控件、调 AddLauncherCard。
        // 之前这里用 Task.Run，等于把承诺改成了「线程池上调用」——
        // 表现为 AddLauncherCard 触发视图重建时抛
        // 「The calling thread cannot access this object because a different thread owns it」，
        // 而且插件页会显示「加载失败」，成因和现象完全对不上。
        //
        // 【但插件自己的 Task 仍然不能被我们的状态机持有】否则程序集永远回收不掉。
        // 所以：Post 到 UI 线程只负责「起跑」，跑完的结果用 ContinueWith 摘到一个局部里，
        // 交回来的只有宿主 tcs —— 那条 await 链上不存在插件类型。
        Dispatcher.UIThread.Post(() =>
        {
            // 起跑本身抛异常（同步抛出）也要接住，否则会被 Dispatcher 当成未处理异常
            Task running;
            try
            {
                running = call();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                return;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }

            _ = running.ContinueWith(t =>
            {
                if (t.IsCanceled) tcs.TrySetCanceled();
                else if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                else tcs.TrySetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });

        // WaitAsync 是宿主侧的实现：超时到了立刻放手，不等插件真正结束
        return tcs.Task.WaitAsync(timeout);
    }

    /// <summary>把 Dispatcher 队列里已排队的回调跑完，避免它们在下一次 GC 检查时还握着插件对象。</summary>
    private static async Task FlushDispatcherAsync()
    {
        try
        {
            // 带超时：Dispatcher 上没有正在跑的消息循环时（单元测试、控制台宿主），
            // 这次投递永远不会完成，不设上限就会把卸载流程永远挂在这里。
            var flush = Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
            await Task.WhenAny(flush, Task.Delay(500));
        }
        catch
        {
            // 没有 UI 线程时忽略
        }
    }

    /// <summary>
    /// 断开引用并尝试卸载。
    ///
    /// <b>必须是两个独立的栈帧</b>：<c>Unload()</c> 要在一个「之后立刻返回」的帧里做，
    /// GC 在另一个帧里做。在同一个方法里 Unload 完就地 <c>GC.Collect</c> 时，
    /// 那个仍然存着 ALC 的局部变量会让回收永远失败 —— 表现为所有插件都提示需要重启。
    /// </summary>
    /// <returns>true = 程序集仍在内存里（没能卸载）。</returns>
    private static bool UnloadLoadContext(PluginEntry entry)
    {
        // 这里【不能】有 alc 的局部变量：它必须只在 DetachAndUnload 那一帧里存在，
        // 而那一帧在返回前就结束了。
        var weak = DetachAndUnload(entry);
        if (weak is null) return false;

        Collect();

        // 对象被回收 = 最彻底的卸载
        if (!weak.IsAlive) return false;

        // 对象还活着，但可能只是宿主某处留了引用（比如日志里存过异常、某个缓存按 Type 建过表）。
        // 只要它的<b>程序集都卸掉了</b>，重新加载就是安全的 —— 那时已经不存在两份静态状态。
        return weak.Target is not PluginLoadContext alc || !IsFullyUnloaded(alc);
    }

    /// <summary>
    /// 这个上下文里的程序集是否都已卸载。
    ///
    /// 卸载完成后 <see cref="AssemblyLoadContext.Assemblies"/> 为空；
    /// 对它调这个属性在已卸载状态下可能抛 <see cref="InvalidOperationException"/>，
    /// 那种情况同样算「已卸完」。
    /// </summary>
    private static bool IsFullyUnloaded(PluginLoadContext alc)
    {
        try
        {
            return !alc.Assemblies.Any();
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            // 读不出来时不冒险：按「还有东西没卸掉」处理，宁可提示重启
            return false;
        }
    }

    /// <summary>
    /// 取出上下文、清空字段、发起卸载，然后把弱引用交出去。
    /// 本帧返回之后，对 ALC 的强引用就全没了，外层的 GC 才可能真正回收。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? DetachAndUnload(PluginEntry entry)
    {
        var alc = entry.LoadContext;
        entry.LoadContext = null;
        if (alc is null) return null;

        var weak = new WeakReference(alc);

        try
        {
            alc.Unload();
        }
        catch (Exception ex)
        {
            // 已经卸载过、或不是可回收上下文：都不该把禁用流程整个搞崩
            PluginLog.Warn(entry.Id, "Unload() 抛异常：" + ex.Message);
        }

        return weak;
    }

    /// <summary>
    /// 强制 GC 若干轮。程序集卸载不是同步完成的：先标记、再跑终结器、
    /// 最后在下一轮 GC 里真正释放，单轮不够。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // ============================================================
    // 扫描与清单校验
    // ============================================================

    /// <summary>插件 id = 目录名。只允许字母数字开头，后接字母数字与 <c>.</c> <c>_</c> <c>-</c>。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex PluginIdPattern();

    private void ScanDirectory()
    {
        _entries.Clear();

        foreach (var (id, dir) in DiscoverDirectories())
            AddEntry(id, dir);

        EnsureLoadOrder();
    }

    /// <summary>读插件根目录，返回 (id, 绝对路径)。以点开头的一律跳过。</summary>
    private static IEnumerable<(string Id, string Directory)> DiscoverDirectories()
    {
        var root = AppPaths.Plugins;
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            PluginLog.Host("插件目录不可用：" + ex.Message, ex);
            return Array.Empty<(string, string)>();
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            PluginLog.Host("读取插件目录失败：" + ex.Message, ex);
            return Array.Empty<(string, string)>();
        }

        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

        var found = new List<(string, string)>(dirs.Length);
        foreach (var dir in dirs)
        {
            var id = Path.GetFileName(dir);

            // 以点开头的是宿主自己的东西（或用户有意隐藏），不当插件
            if (id.StartsWith('.')) continue;

            found.Add((id, dir));
        }

        return found;
    }

    /// <summary>把一个目录变成一个条目（读清单 + 校验）。已存在的条目不要走这里。</summary>
    private void AddEntry(string id, string directory)
    {
        var entry = new PluginEntry(id, directory) { Record = PluginStateStore.GetOrCreate(id) };
        _entries.Add(entry);

        if (!TryReadManifest(entry)) return;
        ValidateManifest(entry);
    }

    private bool TryReadManifest(PluginEntry entry)
    {
        var path = Path.Combine(entry.Directory, "plugin.json");
        if (!File.Exists(path))
        {
            Block(entry, "缺少 plugin.json，不把它当作插件。");
            return false;
        }

        var manifest = JsonFileStore.Load<PluginManifest>(path);
        if (manifest is null)
        {
            Block(entry, "plugin.json 无法解析：不是有效的 JSON。");
            return false;
        }

        entry.Manifest = manifest;
        return true;
    }

    private bool ValidateManifest(PluginEntry entry)
    {
        var manifest = entry.Manifest!;

        // id 以目录名为准：状态记录、数据目录都按它索引，两边不一致会让配置错位
        if (string.IsNullOrWhiteSpace(manifest.Id))
            manifest.Id = entry.Id;
        else if (!string.Equals(manifest.Id, entry.Id, StringComparison.Ordinal))
        {
            Block(entry, $"plugin.json 的 id（{manifest.Id}）与目录名（{entry.Id}）不一致。");
            return false;
        }

        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            Block(entry, "插件 id 只能由字母、数字、. _ - 组成，且以字母或数字开头（最长 64）。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            Block(entry, "plugin.json 缺少 entry（入口程序集文件名）。");
            return false;
        }

        if (!manifest.Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Block(entry, "entry 必须是一个 .dll 文件。");
            return false;
        }

        if (!File.Exists(Path.Combine(entry.Directory, manifest.Entry)))
        {
            Block(entry, "入口程序集不存在：" + manifest.Entry);
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            Block(entry, "plugin.json 缺少 entryType（实现 IPlugin 的类型全名）。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            var required = AppVersion.Parse(manifest.MinHostVersion);
            var current = AppVersion.Parsed;

            // 解析不出来就当没写：插件作者笔误不该让插件永远无法加载
            if (required is not null && current is not null && current < required)
            {
                Block(entry, $"需要宿主 {manifest.MinHostVersion} 及以上，当前为 {AppVersion.Current}。");
                return false;
            }
        }

        entry.RunState = entry.Record.Enabled ? PluginRunState.NotLoaded : PluginRunState.Disabled;
        entry.Error = null;
        return true;
    }

    // ============================================================
    // 清单里描述的关系：平台 / 前置 / 互斥 / 循环
    // ============================================================

    /// <summary>
    /// 扫描之后、加载之前把「不需要跑代码就能判定」的问题一次性判掉：
    /// 平台不符、前置没装、前置关系成环。判定结果一律是 <see cref="PluginRunState.Blocked"/>
    /// （插件自己或它的清单要改），而不是 <see cref="PluginRunState.DependencyBlocked"/>
    /// （那是「插件没问题、环境不满足」，只有前置<b>没运行起来</b>才归那一类）。
    /// </summary>
    private void ApplyManifestRules()
    {
        var ids = _entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in _entries)
        {
            if (entry.Manifest is not { } manifest) continue;
            if (entry.RunState is PluginRunState.Blocked or PluginRunState.Failed) continue;

            // 已经在跑的不动：重扫目录时把它的状态改成「无法加载」不会卸载它，
            // 界面就会显示「无法加载」而桌宠窗口还开着 —— 那种不一致比规则本身更难查。
            if (entry.Instance is not null) continue;

            if (!IsPlatformSupported(manifest, out var platformError))
            {
                Block(entry, platformError!);
                continue;
            }

            if (manifest.Requires.FirstOrDefault(r => !ids.Contains(r)) is { } missing)
            {
                Block(entry, $"前置插件 {missing} 没有安装（plugins/{missing}/ 不存在）。");
                continue;
            }

            if (manifest.Requires.Any(r => string.Equals(r, entry.Id, StringComparison.Ordinal)))
            {
                Block(entry, "清单里的 requires 写了自己。");
            }
        }

        MarkDependencyCycles();
    }

    /// <summary>平台检查：<c>platforms</c> 为空表示所有平台。</summary>
    private static bool IsPlatformSupported(PluginManifest manifest, out string? error)
    {
        error = null;
        if (manifest.Platforms.Count == 0) return true;

        var current = CurrentPlatform();
        foreach (var platform in manifest.Platforms)
            if (string.Equals(platform?.Trim(), current, StringComparison.OrdinalIgnoreCase))
                return true;

        // 也接受常见的别名（win / osx），省得作者为一个字符串反复试
        if (manifest.Platforms.Any(p => IsPlatformAlias(p, current))) return true;

        error = $"该插件只支持 {string.Join(" / ", manifest.Platforms)}，当前系统是 {current}。";
        return false;
    }

    private static string CurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    private static bool IsPlatformAlias(string? platform, string current)
    {
        var value = platform?.Trim().ToLowerInvariant();
        return current switch
        {
            "windows" => value is "win" or "win32" or "win64",
            "macos" => value is "osx" or "mac" or "darwin",
            "linux" => value is "unix",
            _ => false,
        };
    }

    /// <summary>
    /// 找前置关系里的环。整组都标 Blocked 并写出环路（<c>a → b → a</c>）：
    /// 只标其中一个的话，用户改完那一个会发现另一个又报同样的错。
    /// </summary>
    private void MarkDependencyCycles()
    {
        // 0 = 未访问，1 = 在栈上，2 = 已完成
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var entry in _entries)
            Visit(entry.Id, entry, state, stack);

        void Visit(string id, PluginEntry? entry, Dictionary<string, int> state, List<string> stack)
        {
            if (entry?.Manifest is not { } manifest) return;

            state.TryGetValue(id, out var mark);
            if (mark == 2) return;

            if (mark == 1)
            {
                // 回到栈上的节点 = 找到环；把从它开始的那一段摘出来
                var start = stack.IndexOf(id);
                if (start < 0) return;

                var cycle = stack.Skip(start).ToList();
                var path = string.Join(" → ", cycle.Append(id));
                var message = $"前置关系成环：{path}。这些插件都无法加载，请先改掉其中一环的 requires。";

                foreach (var cycleId in cycle)
                    if (FindEntry(cycleId) is { } member && member.RunState != PluginRunState.Blocked)
                        Block(member, message);

                return;
            }

            state[id] = 1;
            stack.Add(id);

            foreach (var required in manifest.Requires)
                Visit(required, FindEntry(required), state, stack);

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
    }

    // ============================================================
    // 加载顺序
    // ============================================================

    /// <summary>
    /// 按用户设定的顺序排列（<c>Order</c> 小的在前），没设过的排在后面、按 id 兜底。
    /// 界面上的列表与卡片、以及真正的加载顺序都以它为准。
    /// </summary>
    private List<PluginEntry> OrderedEntries()
        => _entries
            .OrderBy(e => e.Record.Order ?? int.MaxValue)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 给还没排过序的插件补上顺序号（按目录名的自然顺序）。
    /// 之后用户拖动 / 点箭头就只交换这些数字。
    /// </summary>
    private void EnsureLoadOrder()
    {
        var missing = _entries.Where(e => e.Record.Order is null).ToList();
        if (missing.Count == 0) return;

        var next = _entries.Where(e => e.Record.Order is not null)
            .Select(e => e.Record.Order!.Value)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        foreach (var entry in missing.OrderBy(e => e.Id, StringComparer.Ordinal))
            entry.Record.Order = next++;

        PluginStateStore.Save();
    }

    /// <summary>
    /// 把某个插件上移 / 下移一位（<paramref name="delta"/> = -1 / +1）。
    /// 只在列表里交换位置并落盘，<b>不打断正在运行的插件</b> —— 新顺序在下一次加载时生效。
    /// </summary>
    /// <returns>顺序是否真的变了（到顶 / 到底 / 找不到时返回 false）。</returns>
    public bool Move(string id, int delta)
    {
        if (delta == 0) return false;

        var ordered = OrderedEntries();
        var index = ordered.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;

        var target = index + delta;
        if (target < 0 || target >= ordered.Count) return false;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

        PluginStateStore.SaveOrder(ordered.Select(e => e.Id).ToList());
        Raise();
        return true;
    }

    // ============================================================
    // 重新扫描（用户点「刷新」）
    // ============================================================

    /// <summary>
    /// 重新扫描插件目录，并把新出现的插件加载起来。<b>不必重启程序。</b>
    ///
    /// 与 <see cref="ReloadAsync"/> 的区别：那个会先把所有插件停掉再重来（换总开关时该这么做），
    /// 这个只处理「目录变了」—— 正在运行的插件一个都不动，所以不会打断桌宠之类的东西。
    ///
    /// 三件事按顺序做：目录消失的卸载掉 → 新目录建条目、老目录重读清单 →
    /// 该加载的加载（前置排前面，用户禁用的跳过）。
    /// </summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        var found = DiscoverDirectories().ToList();
        var diskIds = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

        // 1. 目录被删掉的：先卸载（否则实例还活着、内存与文件一直占着），再从列表里移除
        foreach (var entry in _entries.Where(e => !diskIds.Contains(e.Id)).ToList())
        {
            if (entry.Instance is not null || entry.LoadContext is not null)
            {
                try
                {
                    await UnloadAsync(entry);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(entry.Id, "插件目录已消失，卸载失败", ex);
                }
            }

            _entries.Remove(entry);
            PluginLog.Info(entry.Id, "插件目录已不存在，已从列表移除");
        }

        // 2. 新目录建条目；还没跑起来的老条目重读一次清单（用户可能刚改过 plugin.json）
        foreach (var (id, directory) in found)
        {
            var existing = FindEntry(id);
            if (existing is null)
            {
                AddEntry(id, directory);
                continue;
            }

            // 正在跑 / 需重启 / 用户禁用的保持原状：重读清单只会把状态搅乱
            if (existing.Instance is not null) continue;
            if (existing.RunState is PluginRunState.NeedsRestart or PluginRunState.Disabled) continue;

            existing.Manifest = null;
            existing.Error = null;
            existing.RunState = PluginRunState.NotLoaded;

            if (!TryReadManifest(existing)) continue;
            ValidateManifest(existing);
        }

        EnsureLoadOrder();
        ApplyManifestRules();

        if (!IsMasterEnabled)
        {
            Raise();
            return;
        }

        // 3. 该加载的加载起来（按加载顺序，前置先）
        foreach (var entry in LoadOrder())
        {
            if (_stopping) break;
            if (entry.Instance is not null) continue;
            if (!entry.Record.Enabled) continue;
            if (entry.RunState is PluginRunState.Blocked or PluginRunState.Failed
                or PluginRunState.SkippedByCrash or PluginRunState.NeedsRestart)
                continue;

            await TryLoadOnStartupAsync(entry, ct);
        }

        Raise();
    }

    /// <summary>
    /// 加载顺序：<b>前置排在前面</b>（对 <c>requires</c> 做拓扑排序），其余保持扫描顺序。
    /// 不排序的话，「A 依赖 B、但 B 的目录名排在 A 后面」时 A 会因为 B 还没加载而被判成依赖未满足。
    /// </summary>
    private List<PluginEntry> LoadOrder()
    {
        // 基准顺序 = 用户设定的加载顺序（前置再插到它前面）
        var ordered = OrderedEntries();
        var result = new List<PluginEntry>(ordered.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in ordered)
            AddWithDependencies(entry);

        // 环里的节点不会被 Visit 到两次；剩下的按原顺序补进来，保证列表完整
        foreach (var entry in ordered)
            if (visited.Add(entry.Id))
                result.Add(entry);

        return result;

        void AddWithDependencies(PluginEntry entry)
        {
            if (!visited.Add(entry.Id)) return;

            if (entry.Manifest is { } manifest)
                foreach (var required in manifest.Requires)
                    if (FindEntry(required) is { } dependency && !visited.Contains(dependency.Id))
                        AddWithDependencies(dependency);

            result.Add(entry);
        }
    }

    /// <summary>
    /// 运行时依赖检查：前置都<b>真的在运行</b>吗、有没有互斥插件正在运行。
    /// 返回 true 表示不该加载（原因已经写到 <see cref="PluginEntry.Error"/>）。
    /// </summary>
    private bool CheckDependencies(PluginEntry entry)
    {
        if (entry.Manifest is not { } manifest) return false;

        foreach (var required in manifest.Requires)
        {
            var dependency = FindEntry(required);
            if (dependency is null)
            {
                Block(entry, $"前置插件 {required} 没有安装。");
                return true;
            }

            if (dependency.RunState != PluginRunState.Loaded)
            {
                var reason = dependency.RunState switch
                {
                    PluginRunState.Disabled => "已被禁用",
                    PluginRunState.SkippedByCrash => "因疑似崩溃被跳过",
                    PluginRunState.DependencyBlocked => "自己也没能运行",
                    PluginRunState.Blocked => "清单无法加载",
                    PluginRunState.Failed => "加载失败",
                    _ => "未在运行",
                };

                BlockByDependency(entry, $"前置插件 {required} {reason}，本插件因此没有加载。");
                return true;
            }
        }

        foreach (var conflict in manifest.Conflicts)
        {
            if (FindEntry(conflict) is { RunState: PluginRunState.Loaded })
            {
                BlockByDependency(entry, $"与正在运行的插件 {conflict} 冲突（清单里写了 conflicts），本插件没有加载。");
                return true;
            }
        }

        // 反方向也要查：互斥是双向的，但只有一方可能写了 conflicts。
        // 不查这一遍的话，「先加载谁」会决定谁赢 —— 而目录名的字母序不该有这种权力。
        foreach (var other in _entries)
        {
            if (other.RunState != PluginRunState.Loaded) continue;
            if (other.Manifest is not { } otherManifest) continue;
            if (!otherManifest.Conflicts.Contains(entry.Id, StringComparer.Ordinal)) continue;

            BlockByDependency(entry, $"与正在运行的插件 {other.Id} 冲突（对方清单里写了 conflicts），本插件没有加载。");
            return true;
        }

        return false;
    }

    private static void BlockByDependency(PluginEntry entry, string reason)
    {
        entry.RunState = PluginRunState.DependencyBlocked;
        entry.Error = reason;
        PluginLog.Warn(entry.Id, "未加载：" + reason);
    }

    /// <summary>
    /// 某个插件停下来之后，它（直接或间接）的依赖方也要停 —— 让依赖方继续跑，
    /// 它很快就会在一个「前置已经不存在」的状态下出错，而那个错看起来像是它自己的 bug。
    /// </summary>
    private async Task StopDependentsAsync(string stoppedId)
    {
        foreach (var entry in _entries.ToList())
        {
            if (entry.Manifest is not { } manifest) continue;
            if (!manifest.Requires.Contains(stoppedId, StringComparer.Ordinal)) continue;
            if (entry.RunState != PluginRunState.Loaded) continue;

            try
            {
                await UnloadAsync(entry);
            }
            catch (Exception ex)
            {
                PluginLog.Error(entry.Id, "前置停止后卸载失败", ex);
            }

            BlockByDependency(entry, $"前置插件 {stoppedId} 已停止，本插件也跟着停了。");
            await StopDependentsAsync(entry.Id);
        }
    }

    private static void Block(PluginEntry entry, string error)
    {
        entry.RunState = PluginRunState.Blocked;
        entry.Error = error;
        PluginLog.Warn(entry.Id, "无法加载：" + error);
    }

    /// <summary>清单里是否声明了某项能力。大小写不敏感：这不是安全边界，没必要为大小写让插件加载失败。</summary>
    private static bool HasCapability(PluginManifest manifest, string capability)
        => manifest.Capabilities.Any(c => string.Equals(c?.Trim(), capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>按声明建门面。<b>没声明就是 null</b>，理由见 <see cref="PluginFacades"/>。</summary>
    private static PluginFacades BuildFacades(PluginEntry entry)
    {
        var manifest = entry.Manifest!;

        ISessionFacade? sessions = null;
        if (HasCapability(manifest, PluginCapabilities.ChatControl))
        {
            var facade = new PluginSessionFacade(manifest.Id);
            entry.Subscriptions.Add(facade);
            sessions = facade;
        }

        // 窗口宿主：禁用插件时 Dispose 会关掉它所有窗口。
        // 这一步排在「卸订阅」里而不是别处，是为了保证它一定发生在 ALC 卸载之前。
        IPluginWindowHost? windows = null;
        if (HasCapability(manifest, PluginCapabilities.UiWindow))
        {
            var host = new PluginWindowHost(manifest.Id);
            entry.Subscriptions.Add(host);
            windows = host;
        }

        // 标签页宿主：禁用插件时 Dispose 会关掉它开的所有标签页。
        // 与窗口一样排在「卸订阅」里，保证发生在 ALC 卸载之前 ——
        // 标签还在、画它的代码已卸载，就是崩溃而不是「需重启」。
        IPluginTabHost? tabs = null;
        if (HasCapability(manifest, PluginCapabilities.UiTab))
        {
            var host = new PluginTabHost(manifest.Id);
            entry.Subscriptions.Add(host);
            tabs = host;
        }

        // 会话视图借出：禁用插件时 Dispose 把借走的会话全部收回。
        // 也必须发生在 ALC 卸载之前 —— 视图里还挂着插件的按钮回调。
        IConversationDock? dock = null;
        if (HasCapability(manifest, PluginCapabilities.ConversationDock))
        {
            var facade = new PluginConversationDockFacade(manifest.Id);
            entry.Subscriptions.Add(facade);
            dock = facade;
        }

        var hasTools = HasCapability(manifest, PluginCapabilities.ToolsProvide);

        IUiExtensionRegistry? slots = null;
        if (HasCapability(manifest, PluginCapabilities.UiSlot))
        {
            var facade = new PluginUiFacade(manifest.Id);

            // 登记进订阅表：禁用插件时它会把该插件的控件与卡片一次性摘掉，
            // 视图收到 Changed 后重建插槽 —— 控件树里不能再有指向插件程序集的对象，
            // 否则 ALC 永远回收不掉（表现为「需重启」）。
            entry.Subscriptions.Add(facade);
            slots = facade;
        }

        return new PluginFacades(
            Sessions: sessions,
            Prompts: HasCapability(manifest, PluginCapabilities.PromptContribute)
                ? new PluginPromptFacade(manifest.Id, entry.Subscriptions)
                : null,
            Chat: HasCapability(manifest, PluginCapabilities.ChatObserve)
                ? new PluginChatFacade(manifest.Id, entry.Subscriptions)
                : null,
            Windows: windows,
            Slots: slots,
            Tabs: tabs,
            Tools: hasTools ? CreateToolFacade(entry) : null,
            Dock: dock);
    }

    /// <summary>
    /// 建工具门面并登记进订阅表：禁用插件时它把该插件的工具全部从注册表里摘掉，
    /// 会话里选着的 id 于是自然解析不到（模型看不见、也调不到）。
    /// </summary>
    private static IToolRegistryApi CreateToolFacade(PluginEntry entry)
    {
        var facade = new PluginToolFacade(entry.Id);
        entry.Subscriptions.Add(facade);
        return facade;
    }

    // ============================================================
    // 杂项
    // ============================================================

    private PluginEntry? FindEntry(string id)
        => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static PluginInfo ToInfo(PluginEntry entry)
    {
        var manifest = entry.Manifest;
        return new PluginInfo(
            Id: entry.Id,
            Name: manifest?.Name ?? entry.Id,
            Version: manifest?.Version ?? "",
            Description: manifest?.Description,
            Directory: entry.Directory,
            State: entry.RunState,
            Error: entry.Error,
            Capabilities: manifest?.Capabilities ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Enabled: entry.Record.Enabled,
            AutoLoadAfterCrash: entry.Record.AutoLoadAfterCrash,
            CrashCount: entry.Record.CrashCount,
            CrashSuspected: entry.Record.CrashSuspected,
            CrashLogPath: entry.Record.LastCrashLog,
            ContributionChars: PluginPromptUsage.Get(entry.Id));
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>把异常压成一行给人看的短句（完整栈在日志里）。</summary>
    private static string Simplified(Exception ex)
    {
        var text = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) text = ex.GetType().Name;
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary>运行时条目：持久状态 + 这一刻的加载产物。</summary>
    private sealed class PluginEntry(string id, string directory)
    {
        public string Id { get; } = id;
        public string Directory { get; } = directory;

        /// <summary>持久状态（用户意图、崩溃计数）。</summary>
        public PluginStateRecord Record { get; set; } = new();

        /// <summary>清单；读不出来时为 null（此时状态必是 Blocked）。</summary>
        public PluginManifest? Manifest { get; set; }

        public PluginRunState RunState { get; set; } = PluginRunState.NotLoaded;

        /// <summary>失败 / 受阻 / 需重启的原因，直接展示给用户。</summary>
        public string? Error { get; set; }

        /// <summary>插件实例。卸载时必须置空，否则这个引用会让程序集永远回收不掉。</summary>
        public IPlugin? Instance { get; set; }

        /// <summary>加载上下文。卸载时必须置空，理由同上。</summary>
        public PluginLoadContext? LoadContext { get; set; }

        /// <summary>正在卸载：拒绝新的投递（后续里程碑的事件/请求都先看它）。</summary>
        public bool Stopping { get; set; }

        /// <summary>插件通过上下文登记的订阅，卸载时统一撤销。</summary>
        public List<IDisposable> Subscriptions { get; } = new();
    }
}
