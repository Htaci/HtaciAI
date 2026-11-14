using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 子 Agent 页：左侧列出子 Agent，右侧编辑配置（名称 / 描述 / 系统提示词 /
/// 可用工具 / 技能 / MCP）。数据来自 <see cref="AgentRepository"/>，
/// 工具 / 技能选框来自全局 <see cref="ToolRegistry"/> 与 <see cref="SkillRegistry"/>。
///
/// 这一页配的是「被 <c>task</c> 派发出去干活的那个角色」，不是主对话的助手 ——
/// 所以没有模型选择（子 Agent 继承父会话的模型），也没有权限档位（同样继承）。
/// </summary>
public partial class AgentsPage : UserControl
{
    private List<Agent> _agents = new();
    private Agent? _selected;
    private bool _editing;
    private readonly List<ToolDefinition> _tools = new();
    private readonly List<SkillDefinition> _skills = new();

    public AgentsPage()
    {
        InitializeComponent();
        SetupButtons();

        // task 不出现在勾选列表里：子 Agent 拿不到派发能力（深度恒为 1），
        // 摆出来只会让人以为「勾上它就能再往下派」。
        _tools.AddRange(ToolRegistry.Instance.GetEnabled()
            .Where(t => t.Name != BuiltinTools.TaskToolName));
        _skills.AddRange(SkillRegistry.Instance.GetEnabled());
        BuildToolChecks();
        BuildSkillChecks();
        _ = ReloadAgentsAsync();
    }

    private void SetupButtons()
    {
        SetupHover(AddAgentBtn, OnAddAgentClick);
        SetupHover(EditBtn, OnEditClick, "#2F2F3C");
        SetupHover(SaveBtn, OnSaveClick, "#2F2F3C");
        SetupHover(CancelBtn, OnCancelClick);
        SetupHover(DeleteBtn, OnDeleteClick, "#FECACA");
    }

    /// <summary>统一的悬停反馈：hover 底色 + 手型光标，移出恢复原背景（避免覆盖深色填充按钮）。</summary>
    private void SetupHover(Border btn, Action onClick, string hoverBg = "#F1F3F5")
    {
        var normalBg = btn.Background;
        btn.PointerEntered += (s, e) => { btn.Background = new SolidColorBrush(Color.Parse(hoverBg)); btn.Cursor = new Cursor(StandardCursorType.Hand); };
        btn.PointerExited += (s, e) => { btn.Background = normalBg; btn.Cursor = new Cursor(StandardCursorType.Arrow); };
        btn.PointerPressed += (s, e) => { onClick(); e.Handled = true; };
    }

    // ---- 工具 / 技能选框（仅构建一次；勾选态在 ShowDetail 中读取写入） ----

    private void BuildToolChecks()
    {
        ToolsHost.Children.Clear();
        if (_tools.Count == 0)
        {
            ToolsHost.Children.Add(EmptyHint("暂无可用工具"));
            return;
        }
        foreach (var t in _tools)
            ToolsHost.Children.Add(new CheckBox { Content = t.Name, Tag = t.Id, FontSize = 13 });
    }

    private void BuildSkillChecks()
    {
        SkillsHost.Children.Clear();
        if (_skills.Count == 0)
        {
            SkillsHost.Children.Add(EmptyHint("暂无可用技能"));
            return;
        }
        foreach (var s in _skills)
            SkillsHost.Children.Add(new CheckBox { Content = s.Name, Tag = s.Id, FontSize = 13 });
    }

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
    };

    // ---- 左侧：Agent 列表 ----

    private async System.Threading.Tasks.Task ReloadAgentsAsync()
    {
        _agents = await AgentRepository.GetAllAsync();
        if (_selected is null || _agents.All(a => a.Id != _selected.Id))
            _selected = _agents.FirstOrDefault();
        RefreshList();
        ShowDetail();
    }

    private void RefreshList()
    {
        AgentList.Children.Clear();
        if (_agents.Count == 0)
        {
            AgentList.Children.Add(new TextBlock
            {
                Text = "暂无子 Agent，点击左上角新建",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(12, 8, 0, 0)
            });
            return;
        }

        foreach (var a in _agents)
            AgentList.Children.Add(CreateAgentItem(a));
    }

    private Border CreateAgentItem(Agent a)
    {
        var isActive = ReferenceEquals(a, _selected);
        var nameBlock = new TextBlock
        {
            Text = a.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var infoBlock = new TextBlock
        {
            // 空集合不是「没有工具」，而是「全继承父会话」—— 写成「0 工具」会被读成没能力。
            Text = (a.EnabledToolIds.Count == 0 ? "继承全部工具" : $"{a.EnabledToolIds.Count} 工具")
                   + (a.EnabledSkills.Count == 0 ? " · 继承全部技能" : $" · {a.EnabledSkills.Count} 技能"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var title = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { nameBlock, infoBlock }
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = title
        };

        item.PointerEntered += (s, e) =>
        {
            if (!isActive) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (s, e) =>
        {
            if (!isActive) item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (s, e) =>
        {
            _selected = a;
            RefreshList();
            ShowDetail();
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧：详情表单 ----

    private void ShowDetail()
    {
        if (_selected is null)
        {
            ConfigRoot.IsVisible = false;
            _editing = false;
            UpdateEditingUi();
            return;
        }

        ConfigRoot.IsVisible = true;
        _editing = false; // 切换选中默认进入只读视图
        DetailTitle.Text = _selected.Name;
        DetailDesc.Text = _selected.Description;
        NameBox.Text = _selected.Name;
        DescBox.Text = _selected.Description;
        PromptBox.Text = _selected.SystemPrompt;
        McpBox.Text = string.Join("\n", _selected.McpServers);
        SetToolChecks(_selected.EnabledToolIds);
        SetSkillChecks(_selected.EnabledSkills);
        UpdateEditingUi();
    }

    /// <summary>根据 <see cref="_editing"/> 切换右上角按钮与表单可编辑态：只读时仅显示「编辑」，编辑时显示删除/取消/保存。</summary>
    private void UpdateEditingUi()
    {
        EditBtn.IsVisible = !_editing;
        SaveBtn.IsVisible = _editing;
        DeleteBtn.IsVisible = _editing;
        CancelBtn.IsVisible = _editing;

        NameBox.IsEnabled = _editing;
        DescBox.IsEnabled = _editing;
        PromptBox.IsEnabled = _editing;
        McpBox.IsEnabled = _editing;
        foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
            cb.IsEnabled = _editing;
        foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
            cb.IsEnabled = _editing;
    }

    private void SetToolChecks(IEnumerable<string>? ids)
    {
        var set = ids is null ? new HashSet<string>() : ids.ToHashSet();
        foreach (CheckBox cb in ToolsHost.Children.OfType<CheckBox>())
            cb.IsChecked = set.Contains((string)cb.Tag!);
    }

    private void SetSkillChecks(IEnumerable<SessionSkill>? skills)
    {
        var set = skills is null ? new HashSet<string>() : skills.Select(s => s.Id).ToHashSet();
        foreach (CheckBox cb in SkillsHost.Children.OfType<CheckBox>())
            cb.IsChecked = set.Contains((string)cb.Tag!);
    }

    // ---- 操作 ----

    private void OnEditClick()
    {
        if (_selected is null) return;
        _editing = true;
        UpdateEditingUi();
    }

    private void OnCancelClick()
    {
        if (_selected is null) return;
        ShowDetail(); // 重新加载已保存值并回到只读
    }

    private async void OnAddAgentClick()
    {
        var a = new Agent { Name = "新子 Agent" };
        await AgentRepository.CreateAsync(a);
        _selected = a;
        await ReloadAgentsAsync();
        _editing = true; // 新建后直接进入编辑态
        UpdateEditingUi();
    }

    private async void OnSaveClick()
    {
        if (_selected is null) return;
        _selected.Name = NameBox.Text ?? "";
        _selected.Description = DescBox.Text ?? "";
        _selected.SystemPrompt = PromptBox.Text ?? "";
        _selected.EnabledToolIds = CollectCheckedTools();
        _selected.EnabledSkills = CollectCheckedSkills();
        _selected.McpServers = McpBox.Text?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList() ?? new();
        await AgentRepository.UpdateAsync(_selected);
        _editing = false;
        await ReloadAgentsAsync();
    }

    private async void OnDeleteClick()
    {
        if (_selected is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;

        // 内置预设删掉后下次启动会被补回来（见 SubagentPresets）：明说一句，
        // 比让用户删完发现它又出现、以为删除按钮坏了要好。
        var isBuiltin = _selected.Id.StartsWith("builtin-", StringComparison.Ordinal);
        var message = isBuiltin
            ? $"「{_selected.Name}」是内置预设，删除后下次启动会自动恢复。确定删除吗？"
            : $"确定删除子 Agent「{_selected.Name}」吗？已经派发出去的子会话不会被删。";

        if (!await ConfirmDialog.ShowAsync(owner, message, "删除子 Agent")) return;
        await AgentRepository.DeleteAsync(_selected.Id);
        _selected = null;
        _editing = false;
        await ReloadAgentsAsync();
    }

    private List<string> CollectCheckedTools()
        => ToolsHost.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag!)
            .ToList();

    private List<SessionSkill> CollectCheckedSkills()
        => SkillsHost.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            // 状态一律 allowed：Agent 预设表达的是「允许用哪些技能」，
            // loaded（全文注入）属于某个具体会话，不该在这里被固定下来。
            .Select(cb => new SessionSkill { Id = (string)cb.Tag!, Status = "allowed" })
            .ToList();
}
