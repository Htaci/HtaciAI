using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    /// <summary>菜单重建用的内容指纹（见 <see cref="Signature"/>）。</summary>
    private string _lastSignature = "";

    public SkillSelectorControl()
    {
        InitializeComponent();
        MenuTheme.Apply(RootBtn.Flyout as MenuFlyout);
        RebuildMenu();

        // 菜单每次打开都通知宿主重新注入技能列表（技能页里新建 / 改名，切回会话就能看到）。
        // 用 Opening 而不是 Click：Click 时菜单已经开始展开，事件顺序不可控
        // （与 ModelSelector 同一个理由）。
        if (RootBtn.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// 菜单即将打开。宿主应在此重新注入 <see cref="Skills"/> ——
    /// 只在创建时注入一次的话，之后在技能页改了技能，这个选择器会一直显示旧数据。
    /// </summary>
    public event EventHandler? RefreshRequested;

    // ============================================================
    // 属性变化
    // ============================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SkillsProperty)
        {
            _skills = (Skills ?? Array.Empty<SkillDefinition>()).ToList();
            RebuildMenuIfChanged();
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

    /// <summary>
    /// 内容指纹：决定菜单结构与每一项文案的那些字段。每次打开菜单宿主都会重新注入一次列表，
    /// 内容没变时靠它跳过重建，否则菜单刚展开就被清空重填，会闪一下。
    /// </summary>
    private string Signature()
    {
        var builder = new StringBuilder();
        foreach (var s in _skills)
            builder.Append(s.Id).Append('\u0001').Append(s.DisplayName).Append('\u0002');
        return builder.ToString();
    }

    private void RebuildMenuIfChanged()
    {
        if (Signature() == _lastSignature) return;

        RebuildMenu();
    }

    /// <summary>重建菜单：已加载组 + 允许自动使用组（两组各自独立勾选）。</summary>
    private void RebuildMenu()
    {
        _lastSignature = Signature();
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
                Header = s.DisplayName,
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
                Header = s.DisplayName,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _allowed.Contains(id),
                Tag = id,
            };
            mi.Click += (_, _) => Toggle(id, _allowed);
            _allowedItems.Add(mi);
            items.Add(mi);
        }
    }

    /// <summary>分组标题：禁用的菜单项，样式见 App.axaml 的 <c>MenuItem.menu-header</c>。</summary>
    private static MenuItem GroupHeader(string text) => new()
    {
        Header = text,
        IsEnabled = false,
        Classes = { "menu-header" },
    };

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
