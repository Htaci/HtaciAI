using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 工作空间窗口：无参构造是「新建」，传入已有工作空间是「编辑」。
/// 工具 / 技能默认值与权限档位落在工作空间，作为新会话的默认值。
/// </summary>
public partial class CreateWorkspaceWindow : Window
{
    private sealed record PermOption(string Label, PermissionMode Mode);

    /// <summary>默认模型下拉项；<see cref="Id"/> 为 null 表示不指定。</summary>
    private sealed record ModelOption(string Label, string? Id);

    /// <summary>默认知识库下拉项；<see cref="Id"/> 为 null 表示不指定。</summary>
    private sealed record KbOption(string Label, string? Id);

    private static readonly PermOption[] PermissionModes =
    {
        new("普通（安全工具自动通过）", PermissionMode.Normal),
        new("严格（所有工具需确认）", PermissionMode.Strict),
        new("宽松（安全/风险自动通过）", PermissionMode.Loose),
        new("自由（所有工具免确认）", PermissionMode.Free),
    };

    private readonly List<ToolDefinition> _tools = new();
    private readonly List<SkillDefinition> _skills = new();

    /// <summary>新建工作空间默认启用的技能 id。</summary>
    private const string DefaultSkillId = "software-engineering-autonomous";

    public WorkspaceConfig? Result { get; private set; }

    /// <summary>正在编辑的工作空间；为 null 表示新建。</summary>
    private WorkspaceConfig? _editing;

    public CreateWorkspaceWindow() : this(null) { }

    /// <summary>
    /// <paramref name="existing"/> 为 null 时是新建；否则进入编辑模式：
    /// 预填各字段，确认时把改动写回<b>同一个实例</b>（Id、创建时间等原样保留）。
    /// </summary>
    public CreateWorkspaceWindow(WorkspaceConfig? existing)
    {
        InitializeComponent();
        _editing = existing;

        if (existing is not null)
        {
            Title = "编辑工作空间";
            HeaderText.Text = "编辑工作空间";
            SubmitText.Text = "保存";
        }

        _tools.AddRange(ToolRegistry.Instance.GetEnabled());
        _skills.AddRange(SkillRegistry.Instance.GetEnabled());
        BuildToolChecks();
        BuildSkillChecks();

        PermissionCombo.ItemsSource = PermissionModes;
        PermissionCombo.DisplayMemberBinding = new Binding(nameof(PermOption.Label));
        PermissionCombo.SelectedIndex = 0;
        UpdatePermissionHint();

        if (existing is not null)
            Prefill(existing);
        else
            ApplyNewWorkspaceDefaults();

        _ = LoadModelOptionsAsync();
        _ = LoadKbOptionsAsync();
    }

    /// <summary>
    /// 拉取已启用模型填「默认模型」下拉框，首项是「不指定」。
    /// 构造函数的同步路径拿不到异步结果，因此这里同时负责按 <see cref="_editing"/> 回填选中项。
    /// </summary>
    private async Task LoadModelOptionsAsync()
    {
        var options = new List<ModelOption> { new("不指定（用上次使用的模型）", null) };
        try
        {
            foreach (var m in await ModelCatalog.ListEnabledAsync())
                options.Add(new ModelOption($"{m.ProviderName} · {m.DisplayName}", m.ModelId));
        }
        catch
        {
            // 数据库未就绪时只留「不指定」
        }

        ModelCombo.ItemsSource = options;
        ModelCombo.DisplayMemberBinding = new Binding(nameof(ModelOption.Label));

        var wanted = _editing?.DefaultModelId;
        var idx = string.IsNullOrWhiteSpace(wanted) ? -1 : options.FindIndex(o => o.Id == wanted);
        ModelCombo.SelectedIndex = idx < 0 ? 0 : idx;   // 模型已被删除时退回「不指定」
    }

    /// <summary>
    /// 拉取知识库列表填「默认知识库」下拉框，首项是「不指定」。
    /// 与模型下拉同样在异步路径里回填，构造函数拿不到结果。
    /// 已删除的知识库同样退回「不指定」—— 留着悬空 id 只会让新会话查不到东西。
    /// </summary>
    private async Task LoadKbOptionsAsync()
    {
        var options = new List<KbOption> { new("不指定（用默认知识库）", null) };
        try
        {
            foreach (var kb in await KnowledgeBaseRepository.GetAllAsync())
                options.Add(new KbOption(kb.Name, kb.Id));
        }
        catch
        {
            // 数据库未就绪时只留「不指定」
        }

        KbCombo.ItemsSource = options;
        KbCombo.DisplayMemberBinding = new Binding(nameof(KbOption.Label));

        var wanted = _editing?.DefaultKnowledgeBaseId;
        var idx = string.IsNullOrWhiteSpace(wanted) ? -1 : options.FindIndex(o => o.Id == wanted);
        KbCombo.SelectedIndex = idx < 0 ? 0 : idx;
    }

    /// <summary>
    /// 新建工作空间的默认值：开满全部工具，并预置「软件工程-自主」技能。
    /// 只在新建时调用 —— 编辑模式套默认值会覆盖用户已保存的勾选。
    /// </summary>
    private void ApplyNewWorkspaceDefaults()
    {
        foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
            cb.IsChecked = true;
        foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
            cb.IsChecked = (string)cb.Tag! == DefaultSkillId;
    }

    /// <summary>编辑模式预填：字段与工具 / 技能勾选，权限档位见末尾。</summary>
    private void Prefill(WorkspaceConfig ws)
    {
        NameBox.Text = ws.Name;
        DescBox.Text = ws.Description;
        PathBox.Text = ws.Path;
        SystemPromptBox.Text = ws.SystemPrompt;
        McpBox.Text = string.Join("\n", ws.McpServers);

        var tools = ws.EnabledToolIds.ToHashSet();
        foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
            cb.IsChecked = tools.Contains((string)cb.Tag!);
        var skills = ws.EnabledSkills.Select(s => s.Id).ToHashSet();
        foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
            cb.IsChecked = skills.Contains((string)cb.Tag!);

        var idx = Array.FindIndex(PermissionModes, p => p.Mode == ws.ToolPermissionMode);
        PermissionCombo.SelectedIndex = idx < 0 ? 0 : idx;
    }

    // ---- 工具 / 技能选框 ----

    private void BuildToolChecks()
    {
        ToolsHost.Children.Clear();
        if (_tools.Count == 0) { ToolsHost.Children.Add(EmptyHint("暂无可用工具")); return; }
        // 显示名而不是调用名：下拉里排的是给人看的，task / create_tool 那种 snake_case
        // 是写给模型看的。脚本 / MCP / 插件工具没有别名，Display 会回退到 Name。
        foreach (var t in _tools)
            ToolsHost.Children.Add(new CheckBox { Content = t.Display, Tag = t.Id, FontSize = 13 });
    }

    private void BuildSkillChecks()
    {
        SkillsHost.Children.Clear();
        if (_skills.Count == 0) { SkillsHost.Children.Add(EmptyHint("暂无可用技能")); return; }
        foreach (var s in _skills)
            SkillsHost.Children.Add(new CheckBox { Content = s.DisplayName, Tag = s.Id, FontSize = 13 });
    }

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
    };

    // ---- 目录选择 ----

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择工作目录" });
        if (folder.Count > 0)
            PathBox.Text = folder[0].Path.LocalPath;
    }

    private void OnPermissionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdatePermissionHint();

    private void UpdatePermissionHint()
    {
        var mode = (PermissionCombo.SelectedItem as PermOption)?.Mode ?? PermissionMode.Normal;
        PermissionHint.Text = "权限说明：" + (mode switch
        {
            PermissionMode.Strict => "所有工具需确认",
            PermissionMode.Normal => "安全工具自动通过，风险/危险需确认",
            PermissionMode.Loose => "安全/风险自动通过，仅危险需确认",
            PermissionMode.Free => "所有工具免确认",
            _ => "",
        });
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close();

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            name = PathBox.Text is { Length: > 0 } p ? System.IO.Path.GetFileName(p.TrimEnd(System.IO.Path.DirectorySeparatorChar)) : "新工作空间";

        // 编辑时写回同一个实例（Id / 创建时间原样保留）；新建时另开一个
        var ws = _editing ?? new WorkspaceConfig();

        ws.Name = name;
        ws.Description = DescBox.Text ?? "";
        ws.Path = PathBox.Text ?? "";
        ws.SystemPrompt = SystemPromptBox.Text ?? "";
        ws.EnabledToolIds = CollectChecked(ToolsHost);
        ws.EnabledSkills = CollectCheckedSkills();
        ws.McpServers = McpBox.Text?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList() ?? new();
        ws.ToolPermissionMode = (PermissionCombo.SelectedItem as PermOption)?.Mode ?? PermissionMode.Normal;
        ws.DefaultModelId = (ModelCombo.SelectedItem as ModelOption)?.Id;
        ws.DefaultKnowledgeBaseId = (KbCombo.SelectedItem as KbOption)?.Id;

        Result = ws;
        Close();
    }

    private static List<string> CollectChecked(StackPanel host)
        => host.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag!)
            .ToList();

    private List<SessionSkill> CollectCheckedSkills()
        => SkillsHost.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => new SessionSkill { Id = (string)cb.Tag!, Status = "loaded" })
            .ToList();
}
