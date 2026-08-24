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
/// 新建工作空间窗口：生成一个 <see cref="WorkspaceConfig"/>（含 Agent 选择与二次配置）。
/// 选择 Agent 后自动带入其默认工具/技能（可调整）；权限档位落在工作空间，作为新会话的默认值。
/// </summary>
public partial class CreateWorkspaceWindow : Window
{
    private sealed record PermOption(string Label, PermissionMode Mode);

    private static readonly PermOption[] PermissionModes =
    {
        new("普通（安全工具自动通过）", PermissionMode.Normal),
        new("严格（所有工具需确认）", PermissionMode.Strict),
        new("宽松（安全/风险自动通过）", PermissionMode.Loose),
        new("自由（所有工具免确认）", PermissionMode.Free),
    };

    private List<Agent> _agents = new();
    private readonly List<ToolDefinition> _tools = new();
    private readonly List<SkillDefinition> _skills = new();

    public WorkspaceConfig? Result { get; private set; }

    public CreateWorkspaceWindow()
    {
        InitializeComponent();

        _tools.AddRange(ToolRegistry.Instance.GetEnabled());
        _skills.AddRange(SkillRegistry.Instance.GetEnabled());
        BuildToolChecks();
        BuildSkillChecks();

        PermissionCombo.ItemsSource = PermissionModes;
        PermissionCombo.DisplayMemberBinding = new Binding(nameof(PermOption.Label));
        PermissionCombo.SelectedIndex = 0;
        UpdatePermissionHint();

        _ = LoadAgentsAsync();
    }

    private async Task LoadAgentsAsync()
    {
        try
        {
            _agents = await AgentRepository.GetAllAsync();
            AgentCombo.ItemsSource = _agents;
            AgentCombo.DisplayMemberBinding = new Binding(nameof(Agent.Name));
        }
        catch
        {
            // 数据库未就绪时无 Agent 可选
        }
    }

    // ---- 工具 / 技能选框 ----

    private void BuildToolChecks()
    {
        ToolsHost.Children.Clear();
        if (_tools.Count == 0) { ToolsHost.Children.Add(EmptyHint("暂无可用工具")); return; }
        foreach (var t in _tools)
            ToolsHost.Children.Add(new CheckBox { Content = t.Name, Tag = t.Id, FontSize = 13 });
    }

    private void BuildSkillChecks()
    {
        SkillsHost.Children.Clear();
        if (_skills.Count == 0) { SkillsHost.Children.Add(EmptyHint("暂无可用技能")); return; }
        foreach (var s in _skills)
            SkillsHost.Children.Add(new CheckBox { Content = s.Name, Tag = s.Id, FontSize = 13 });
    }

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
    };

    private void OnAgentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AgentCombo.SelectedItem is Agent agent)
        {
            var toolSet = agent.EnabledToolIds.ToHashSet();
            foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
                cb.IsChecked = toolSet.Contains((string)cb.Tag!);
            var skillSet = agent.EnabledSkills.Select(s => s.Id).ToHashSet();
            foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
                cb.IsChecked = skillSet.Contains((string)cb.Tag!);
        }
        else
        {
            foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
                cb.IsChecked = false;
            foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
                cb.IsChecked = false;
        }
    }

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

        var agent = AgentCombo.SelectedItem as Agent;
        var ws = new WorkspaceConfig
        {
            Name = name,
            Description = DescBox.Text ?? "",
            Path = PathBox.Text ?? "",
            AgentId = agent?.Id,
            SystemPrompt = SystemPromptBox.Text ?? "",
            EnabledToolIds = CollectChecked(ToolsHost),
            EnabledSkills = CollectCheckedSkills(),
            McpServers = McpBox.Text?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList() ?? new(),
            ToolPermissionMode = (PermissionCombo.SelectedItem as PermOption)?.Mode ?? PermissionMode.Normal,
        };
        if (agent is not null)
            WorkspaceAgentPipeline.SeedWorkspaceFromAgent(ws, agent);

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
