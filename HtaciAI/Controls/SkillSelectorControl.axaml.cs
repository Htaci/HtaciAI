using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using HtaciAI.Models;
using HtaciAI.Services.Skills;

namespace HtaciAI.Controls;

/// <summary>
/// 可复用的技能选择器控件：按钮显示灯泡图标 + 已选数量角标，点击弹出菜单。
/// 技能以两组独立勾选：「已加载」（status=loaded，全文注入）与「允许自动使用」
/// （status=allowed，仅元数据）。通过 <see cref="SelectedSkills"/>（TwoWay）暴露选中结果，
/// 通过 <see cref="SelectionChanged"/> 事件对外通知变化。
/// </summary>
public partial class SkillSelectorControl : UserControl
{
    private List<SkillDefinition> _skills = new();
    private readonly HashSet<string> _loaded = new();
    private readonly HashSet<string> _allowed = new();
    private readonly List<MenuItem> _loadedItems = new();
    private readonly List<MenuItem> _allowedItems = new();

    public SkillSelectorControl()
    {
        InitializeComponent();
        RebuildMenu();
    }

    // ============================================================
    // 依赖属性
    // ============================================================

    /// <summary>可选的技能定义列表。</summary>
    public static readonly StyledProperty<IEnumerable<SkillDefinition>> SkillsProperty =
        AvaloniaProperty.Register<SkillSelectorControl, IEnumerable<SkillDefinition>>(
            nameof(Skills),
            defaultValue: Array.Empty<SkillDefinition>(),
            coerce: (_, v) => v ?? Array.Empty<SkillDefinition>());

    public IEnumerable<SkillDefinition> Skills
    {
        get => GetValue(SkillsProperty);
        set => SetValue(SkillsProperty, value);
    }

    /// <summary>当前启用的技能记录（含 loaded / allowed 两种状态）。</summary>
    public static readonly StyledProperty<IReadOnlyList<SessionSkill>> SelectedSkillsProperty =
        AvaloniaProperty.Register<SkillSelectorControl, IReadOnlyList<SessionSkill>>(
            nameof(SelectedSkills),
            defaultValue: Array.Empty<SessionSkill>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<SessionSkill>());

    public IReadOnlyList<SessionSkill> SelectedSkills
    {
        get => GetValue(SelectedSkillsProperty);
        set => SetValue(SelectedSkillsProperty, value);
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

        if (change.Property == SkillsProperty)
        {
            _skills = (Skills ?? Array.Empty<SkillDefinition>()).ToList();
            RebuildMenu();
        }
        else if (change.Property == SelectedSkillsProperty)
        {
            SyncFromProperty();
            UpdateBadge();
            UpdateCheckedStates();
        }
    }

    // ============================================================
    // 选中集合同步
    // ============================================================

    /// <summary>把外部属性值同步进内部两组工作集（外部设置时调用，不触发事件）。</summary>
    private void SyncFromProperty()
    {
        _loaded.Clear();
        _allowed.Clear();
        foreach (var s in SelectedSkills ?? Array.Empty<SessionSkill>())
        {
            if (s.Status == "loaded") _loaded.Add(s.Id);
            else if (s.Status == "allowed") _allowed.Add(s.Id);
        }
    }

    /// <summary>内部变更后提交：合并两组为技能记录回写属性 + 刷新 UI + 触发事件。</summary>
    private void CommitSelection()
    {
        var list = new List<SessionSkill>();
        foreach (var id in _loaded)
            list.Add(new SessionSkill { Id = id, Status = "loaded" });
        foreach (var id in _allowed)
            list.Add(new SessionSkill { Id = id, Status = "allowed" });
        SelectedSkills = list;
        UpdateBadge();
        UpdateCheckedStates();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 菜单构建
    // ============================================================

    /// <summary>重建菜单：已加载组 + 允许自动使用组（两组各自独立勾选）。</summary>
    private void RebuildMenu()
    {
        if (RootBtn.Flyout is not MenuFlyout menu) return;
        var items = menu.Items;
        items.Clear();
        _loadedItems.Clear();
        _allowedItems.Clear();

        if (_skills.Count == 0)
        {
            items.Add(new MenuItem { Header = "暂无技能", IsEnabled = false });
            return;
        }

        items.Add(GroupHeader("已加载（全文注入）"));
        foreach (var s in _skills)
        {
            var id = s.Id;
            var mi = new MenuItem
            {
                Header = s.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _loaded.Contains(id),
                Tag = id,
            };
            mi.Click += (_, _) => Toggle(id, _loaded);
            _loadedItems.Add(mi);
            items.Add(mi);
        }

        items.Add(new Separator());
        items.Add(GroupHeader("允许自动使用（仅元数据）"));
        foreach (var s in _skills)
        {
            var id = s.Id;
            var mi = new MenuItem
            {
                Header = s.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _allowed.Contains(id),
                Tag = id,
            };
            mi.Click += (_, _) => Toggle(id, _allowed);
            _allowedItems.Add(mi);
            items.Add(mi);
        }
    }

    private static MenuItem GroupHeader(string text) => new() { Header = text, IsEnabled = false };

    private void Toggle(string id, HashSet<string> set)
    {
        if (!set.Add(id))
            set.Remove(id);
        CommitSelection();
    }

    // ============================================================
    // 状态刷新
    // ============================================================

    /// <summary>按内部两组集合刷新所有菜单项勾选态。</summary>
    private void UpdateCheckedStates()
    {
        foreach (var mi in _loadedItems)
            if (mi.Tag is string id) mi.IsChecked = _loaded.Contains(id);
        foreach (var mi in _allowedItems)
            if (mi.Tag is string id) mi.IsChecked = _allowed.Contains(id);
        UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (Badge is null || BadgeText is null) return;
        var count = _loaded.Count + _allowed.Count;
        BadgeText.Text = count.ToString();
        Badge.IsVisible = count > 0;
    }
}
