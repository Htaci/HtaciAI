using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using HtaciAI.Data;
using HtaciAI.Services.Mcp;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;
using HtaciAI.Views;

namespace HtaciAI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 数据根目录必须先确定：DatabaseService 与各注册表都从这里取路径。
        AppPaths.Initialize();

        // 迁移要赶在 DatabaseService.Initialize 之前——那时没有 SQLite 连接，才能安全搬 -wal / -shm。
        // 也赶在 SkillRegistry.EnsureSeeded 之前，迁移过来的 SKILL.md 才不会被内置技能顶掉。
        DataMigrator.RunStartupMigration();

        DatabaseService.Initialize();

        // 工具注册表：先从库加载用户创建的工具，再补充内置示例脚本工具（均幂等）
        ToolRegistry.Instance.LoadFromDbAsync().GetAwaiter().GetResult();
        ToolRegistry.Instance.SeedExamples();

        // 技能注册表：先写入内置示例技能到磁盘，再从数据根 skills 目录加载
        SkillRegistry.Instance.EnsureSeeded();
        SkillRegistry.Instance.LoadFromDisk();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 退出前收掉 MCP 连接。stdio 传输会拉起本地子进程，不主动杀就会变成孤儿进程留在系统里。
            // 挂 desktop 而不是 MainWindow.Closing：重启应用走的也是 desktop.Shutdown()（见 AppRestart），
            // 挂在窗口上会漏掉那条路径。
            desktop.ShutdownRequested += (_, _) => McpConnectionManager.Instance.ShutdownAll();

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}