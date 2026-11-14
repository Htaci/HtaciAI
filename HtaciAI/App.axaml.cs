using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Network;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Subagents;
using HtaciAI.Services.Tools;
using HtaciAI.Views;

namespace HtaciAI;

public partial class App : Application
{
    /// <summary>
    /// 退出流程是否已经开始。用它把 <see cref="ShutdownRequestedEvent"/> 变成「取消一次、收完再关」。
    ///
    /// <b>不能省</b>：下面的处理器里最后要调 <c>desktop.Shutdown()</c>，那会再次触发本事件；
    /// 没有这个闸门就会把自己再取消一次，程序永远关不掉。
    /// </summary>
    private static bool _shutdownStarted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 数据根目录必须先确定：DatabaseService 与各注册表都从这里取路径。
        AppPaths.Initialize();

        // 代理开关要在第一个出站请求之前生效（更新检查、插件初始化都可能很早就发请求）。
        // 放这儿是因为 settings.json 就在数据根下，AppPaths.Initialize 之前读不到它。
        HttpClientProvider.UseSystemProxy = AppSettingsStore.Current.UseSystemProxy;

        // 迁移要赶在 DatabaseService.Initialize 之前——那时没有 SQLite 连接，才能安全搬 -wal / -shm。
        // 也赶在 SkillRegistry.EnsureSeeded 之前，迁移过来的 SKILL.md 才不会被内置技能顶掉。
        DataMigrator.RunStartupMigration();

        DatabaseService.Initialize();

        // API 流水（「统计」页）的保留策略：过期记录在启动时清一次。
        // 放后台跑，不挡启动；days ≤ 0（永久保留）时 PurgeAsync 自己什么都不做。
        _ = Task.Run(() => ApiRequestRepository.PurgeAsync(
            AppSettingsStore.Current.ApiRequestLogRetentionDays));

        // 工具注册表：从库加载用户创建的工具（内置工具在加载末尾顺带补齐，均幂等）
        ToolRegistry.Instance.LoadFromDbAsync().GetAwaiter().GetResult();

        // 技能注册表：先写入内置示例技能到磁盘，再从数据根 skills 目录加载
        SkillRegistry.Instance.EnsureSeeded();
        SkillRegistry.Instance.LoadFromDisk();

        // 子 Agent 预设：放在工具注册表之后（预设里存的是工具 id，要先有工具才能解析）
        SubagentPresets.EnsureSeededAsync().GetAwaiter().GetResult();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // 关闭主窗口即结束程序。
            // 必须显式设：Avalonia 默认是 OnLastWindowClose，插件一开窗口这个默认值的行为就变了 ——
            // 主窗口关掉后插件窗口还在，进程不退出，用户以为关了却还在任务管理器里。
            // 与「最小化到托盘」不冲突：托盘模式下主窗口是 Hide() 而不是 Close()，不触发关闭。
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 退出前收掉插件与 MCP。挂 desktop 而不是 MainWindow.Closing：
            // 重启应用走的也是 desktop.Shutdown()（见 AppRestart），挂在窗口上会漏掉那条路径。
            desktop.ShutdownRequested += OnShutdownRequested;

            // 插件最后加载：它可能要用数据库、工具/技能注册表、主窗口。
            // 用 Post 而不是直接 await：让首帧先画出来，插件初始化慢不会把窗口卡成白屏。
            Dispatcher.UIThread.Post(() => _ = PluginManager.Instance.InitializeAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 退出收尾。插件收尾是异步的，而这是一个同步事件 —— 所以取消本次关闭，
    /// 收完再自己发起一次（第二次进来时 <see cref="_shutdownStarted"/> 已为 true，直接放行）。
    ///
    /// 整段必须有 <c>finally</c>：这里抛出去就是「关不掉」或者「崩在退出路径上」，
    /// 两种都比丢一次插件收尾更糟。
    /// </summary>
    private static async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        e.Cancel = true;

        try
        {
            await PluginManager.Instance.ShutdownAllAsync();
        }
        catch (Exception ex)
        {
            PluginLog.Host("退出时停止插件失败：" + ex.Message, ex);
        }
        finally
        {
            try
            {
                McpConnectionManager.Instance.ShutdownAll();
            }
            catch (Exception ex)
            {
                PluginLog.Host("退出时停止 MCP 失败：" + ex.Message, ex);
            }

            // 到这一步才算「正常退出」：删掉运行标记，下次启动就不会误判成崩溃
            PluginCrashReporter.MarkCleanExit();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }
}
