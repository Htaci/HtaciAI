using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using HtaciAI.Data;
using HtaciAI.Services.Skills;
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
        DatabaseService.Initialize();

        // 工具注册表：先从库加载用户创建的工具，再补充内置示例脚本工具（均幂等）
        ToolRegistry.Instance.LoadFromDbAsync().GetAwaiter().GetResult();
        ToolRegistry.Instance.SeedExamples();

        // 技能注册表：先写入内置示例技能到磁盘，再从 ~/.htaci/skills 加载
        SkillRegistry.Instance.EnsureSeeded();
        SkillRegistry.Instance.LoadFromDisk();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}