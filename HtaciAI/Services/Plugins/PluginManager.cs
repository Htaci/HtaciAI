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
    public IReadOnlyList<PluginInfo> GetAll() => _entries.Select(ToInfo).ToList();

    /// <summary>插件 id 是否已发现。</summary>
    public PluginInfo? Find(string id) => _entries.Where(e => e.Id == id).Select(ToInfo).FirstOrDefault();

    /// <summary>加载成功且正在运行的插件 id。</summary>
    public IEnumerable<string> GetLoadedIds()
        => _entries.Where(e => e.RunState == PluginRunState.Loaded).Select(e => e.Id);

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

        if (!IsMasterEnabled)
        {
            // 总开关关着：连加载都不尝试，但列表仍要能显示出来，方便用户看到有什么
            foreach (var entry in _entries)
                entry.RunState = entry.Record.Enabled ? PluginRunState.NotLoaded : PluginRunState.Disabled;

            Raise();
            return;
        }

        foreach (var entry in _entries.ToList())
        {
            if (_stopping) break;
            await TryLoadOnStartupAsync(entry, ct);
        }

        Raise();
    }

    private async Task TryLoadOnStartupAsync(PluginEntry entry, CancellationToken ct)
    {
        // 扫描阶段已经判定过的问题（清单坏了、版本不够、入口缺失）保持原状
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

        await LoadAsync(entry, ct);
        Raise();
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

            var context = new PluginContext(manifest, entry.Directory, AppVersion.Current);

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
        var assembly = alc.LoadFromAssemblyPath(entryPath);

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

        _ = Task.Run(async () =>
        {
            try
            {
                await call();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
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
    /// GC 在另一个帧里做。实测过：在同一个方法里 Unload 完就地 <c>GC.Collect</c>，
    /// 那个仍然存着 ALC 的局部变量会让回收永远失败，判定就永远偏向「需重启」——
    /// 表现为所有插件都提示需要重启，而其实它们都能卸掉。
    /// </summary>
    /// <returns>true = 插件文件仍被占用 / 程序集仍在内存里（没能卸载）。</returns>
    private static bool UnloadLoadContext(PluginEntry entry)
    {
        // 注意：这里【不能】有 alc 的局部变量。它必须只在 DetachAndUnload 那一帧里存在，
        // 而那一帧在返回前就结束了 —— 这是实测出来的可靠写法。
        var weak = DetachAndUnload(entry);
        if (weak is null) return false;

        var collected = IsCollected(weak);

        // 判定以「插件文件还能不能被替换」为准，而不是「ALC 对象有没有被回收」。
        // 用户真正在意的就是这件事：需不需要重启才能更新文件。
        // 而且它比 GC 判定稳定 —— 实测过一种情况：GC 已经收掉了 ALC 对象，
        // 程序集映射却还在，此时按 GC 判会说「可以重载了」，实际却会再映射一份。
        return !collected || IsPluginFileLocked(entry);
    }

    /// <summary>
    /// 插件目录里的 dll 是否仍被映射在进程里。
    ///
    /// 用一个「独占读」去试探：已加载的程序集会一直握着文件句柄，
    /// 此时任何 <see cref="FileShare.None"/> 的打开都会抛 <see cref="IOException"/>；
    /// 没加载过则能正常打开。
    ///
    /// 只读打开就够了，不要求写权限 —— 插件装在只读目录里时也不该被误判成「已加载」。
    /// </summary>
    private static bool IsPluginFileLocked(PluginEntry entry)
    {
        try
        {
            if (Directory.Exists(entry.Directory))
            {
                foreach (var file in Directory.EnumerateFiles(entry.Directory, "*.dll"))
                {
                    try
                    {
                        using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    catch (IOException)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            // 探测本身失败时按「没锁」处理：这种情况多半是目录权限问题，
            // 报「需重启」会误导用户。
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
    /// 收集若干轮：卸载的回收要跨多轮 GC —— 先回收外壳，再跑 ALC 自己的终结器，
    /// 然后才轮到这个对象本身被回收。实测三轮不够（判定会误报「需重启」，
    /// 但紧接着的一次 GC 又能把它收掉），所以这里多给几轮。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollected(WeakReference weak)
    {
        for (var i = 0; i < 6; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (!weak.IsAlive) return true;
        }

        return !weak.IsAlive;
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

        var root = AppPaths.Plugins;
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            PluginLog.Host("插件目录不可用：" + ex.Message, ex);
            return;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            PluginLog.Host("读取插件目录失败：" + ex.Message, ex);
            return;
        }

        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            var id = Path.GetFileName(dir);

            // 以点开头的是宿主自己的东西（或用户有意隐藏），不当插件
            if (id.StartsWith('.')) continue;

            var entry = new PluginEntry(id, dir) { Record = PluginStateStore.GetOrCreate(id) };
            _entries.Add(entry);

            if (!TryReadManifest(entry)) continue;
            ValidateManifest(entry);
        }
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

    private static void Block(PluginEntry entry, string error)
    {
        entry.RunState = PluginRunState.Blocked;
        entry.Error = error;
        PluginLog.Warn(entry.Id, "无法加载：" + error);
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
            CrashLogPath: entry.Record.LastCrashLog);
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
