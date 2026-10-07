using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using HtaciAI.Services.Tools;

namespace HtaciAI.Controls;

/// <summary>
/// 可复用的工具选择器控件：按钮显示扳手图标 + 已选数量角标，点击弹出菜单
/// 混合勾选"工具集"（全选/全不选该集下工具）与"单个工具"。
/// 通过 <see cref="SelectedToolIds"/>（TwoWay）暴露激活的工具 id 集合（按 id 去重），
/// 通过 <see cref="SelectionChanged"/> 事件对外通知变化。
/// </summary>
public partial class ToolSelectorControl : UserControl
{
    private List<Toolset> _toolsets = new();
    private List<ToolDefinition> _tools = new();
    private readonly HashSet<string> _selected = new();
    private readonly List<MenuItem> _toolsetItems = new();
    private readonly List<MenuItem> _toolItems = new();

    public ToolSelectorControl()
    {
        InitializeComponent();
        RebuildMenu();
    }

    // ============================================================
    // 依赖属性
    // ============================================================

    /// <summary>可选工具集列表（含「全部」「内置」两个自动集合）。</summary>
    public static readonly StyledProperty<IEnumerable<Toolset>> ToolsetsProperty =
        AvaloniaProperty.Register<ToolSelectorControl, IEnumerable<Toolset>>(
            nameof(Toolsets),
            defaultValue: Array.Empty<Toolset>(),
            coerce: (_, v) => v ?? Array.Empty<Toolset>());

    public IEnumerable<Toolset> Toolsets
    {
        get => GetValue(ToolsetsProperty);
        set => SetValue(ToolsetsProperty, value);
    }

    /// <summary>可选工具定义列表（全部，组件内部按工具集分组展示）。</summary>
    public static readonly StyledProperty<IEnumerable<ToolDefinition>> ToolsProperty =
        AvaloniaProperty.Register<ToolSelectorControl, IEnumerable<ToolDefinition>>(
            nameof(Tools),
            defaultValue: Array.Empty<ToolDefinition>(),
            coerce: (_, v) => v ?? Array.Empty<ToolDefinition>());

    public IEnumerable<ToolDefinition> Tools
    {
        get => GetValue(ToolsProperty);
        set => SetValue(ToolsProperty, value);
    }

    /// <summary>当前激活的工具 id 集合（去重）。</summary>
    public static readonly StyledProperty<IReadOnlyList<string>> SelectedToolIdsProperty =
        AvaloniaProperty.Register<ToolSelectorControl, IReadOnlyList<string>>(
            nameof(SelectedToolIds),
            defaultValue: Array.Empty<string>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<string>());

    public IReadOnlyList<string> SelectedToolIds
    {
        get => GetValue(SelectedToolIdsProperty);
        set => SetValue(SelectedToolIdsProperty, value);
    }

    // ============================================================
    // 事件
    // ============================================================

    /// <summary>选中集合变化时触发。</summary>
    public event EventHandler? SelectionChanged;

    // ============================================================
    // 属性变化
    // ============================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ToolsetsProperty)
        {
            _toolsets = (Toolsets ?? Array.Empty<Toolset>()).ToList();
            RebuildMenu();
        }
        else if (change.Property == ToolsProperty)
        {
            _tools = (Tools ?? Array.Empty<ToolDefinition>()).ToList();
            RebuildMenu();
        }
        else if (change.Property == SelectedToolIdsProperty)
        {
            SyncFromProperty();
            UpdateBadge();
            UpdateCheckedStates();
        }
    }

    // ============================================================
    // 选中集合同步
    // ============================================================

    /// <summary>把外部属性值同步进内部工作集（组件外部设置时调用，不触发事件）。</summary>
    private void SyncFromProperty()
    {
        _selected.Clear();
        foreach (var id in SelectedToolIds ?? Array.Empty<string>())
            _selected.Add(id);
    }

    /// <summary>内部变更后提交：回写属性 + 刷新 UI + 触发事件。</summary>
    private void CommitSelection()
    {
        SelectedToolIds = _selected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        UpdateBadge();
        UpdateCheckedStates();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 菜单构建
    // ============================================================

    /// <summary>重建菜单：快捷操作 → 工具集勾选区 → 工具勾选区。</summary>
    private void RebuildMenu()
    {
        // 通过按钮 Flyout 访问菜单，避免 Flyout 上 x:Name 字段的编译歧义（与 ModelSelector 相同的已知坑）
        if (RootBtn.Flyout is not MenuFlyout menu) return;
        var items = menu.Items;
        items.Clear();
        _toolsetItems.Clear();
        _toolItems.Clear();

        if (_tools.Count == 0)
        {
            items.Add(new MenuItem { Header = "暂无工具", IsEnabled = false });
            return;
        }

        var enableAll = new MenuItem { Header = "启用全部工具" };
        enableAll.Click += (_, _) => { _selected.UnionWith(_tools.Select(t => t.Id)); CommitSelection(); };
        items.Add(enableAll);

        var clear = new MenuItem { Header = "不启用任何工具" };
        clear.Click += (_, _) => { _selected.Clear(); CommitSelection(); };
        items.Add(clear);

        items.Add(new Separator());

        // 工具集区：勾选 = 全选/全不选该集下所有工具
        foreach (var ts in _toolsets)
        {
            var groupTools = _tools.Where(t => Toolset.Contains(ts.Id, t)).ToList();
            if (groupTools.Count == 0) continue;

            var mi = new MenuItem
            {
                Header = $"{ts.Name}（{groupTools.Count}）",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = groupTools.All(t => _selected.Contains(t.Id)),
                Tag = ts.Id,
            };
            var tsId = ts.Id;
            mi.Click += (_, _) => ToggleToolset(tsId);
            _toolsetItems.Add(mi);
            items.Add(mi);
        }

        if (_toolsetItems.Count > 0)
            items.Add(new Separator());

        // 工具区：单个工具勾选
        foreach (var tool in _tools)
        {
            var mi = new MenuItem
            {
                Header = tool.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _selected.Contains(tool.Id),
                Tag = tool.Id,
            };
            var toolId = tool.Id;
            mi.Click += (_, _) => ToggleTool(toolId);
            _toolItems.Add(mi);
            items.Add(mi);
        }
    }

    private void ToggleToolset(string toolsetId)
    {
        var groupTools = _tools.Where(t => Toolset.Contains(toolsetId, t)).ToList();
        if (groupTools.Count == 0) return;

        var allSelected = groupTools.All(t => _selected.Contains(t.Id));
        if (allSelected)
            foreach (var t in groupTools) _selected.Remove(t.Id);
        else
            foreach (var t in groupTools) _selected.Add(t.Id);

        CommitSelection();
    }

    private void ToggleTool(string id)
    {
        if (!_selected.Add(id))
            _selected.Remove(id);
        CommitSelection();
    }

    // ============================================================
    // 状态刷新
    // ============================================================

    /// <summary>按内部选中集合刷新所有菜单项的勾选态（保持与 SelectedToolIds 一致）。</summary>
    private void UpdateCheckedStates()
    {
        foreach (var mi in _toolsetItems)
        {
            if (mi.Tag is not string tsId) continue;
            var group = _tools.Where(t => Toolset.Contains(tsId, t)).ToList();
            mi.IsChecked = group.Count > 0 && group.All(t => _selected.Contains(t.Id));
        }
        foreach (var mi in _toolItems)
        {
            if (mi.Tag is not string id) continue;
            mi.IsChecked = _selected.Contains(id);
        }
        UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (Badge is null || BadgeText is null) return;
        BadgeText.Text = _selected.Count.ToString();
        Badge.IsVisible = _selected.Count > 0;
    }
}
