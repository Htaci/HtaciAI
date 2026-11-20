using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HtaciAI.Models;

namespace HtaciAI.Controls;

/// <summary>
/// 角色选择器：按钮上直接写当前角色名（与模型 / 知识库选择器同一形态）。
///
/// 选中值的语义是「把这份角色配置套一次」，不是「会话属于这份角色」——
/// 套完之后用户在工具 / 模型 / 知识库上的改动都还是会话自己的，角色不再参与。
/// 因此选中角色时<b>不写会话存档</b>，只抛 <see cref="SelectionChanged"/> 让宿主去套用。
///
/// <see cref="SelectedRoleId"/> 用空字符串表示「不使用角色」（不是 null）：
/// 与知识库选择器同一个理由，让「还没加载完」和「用户明确不选」区分得开。
/// </summary>
public partial class RoleSelectorControl : UserControl
{
    private List<Agent> _roles = new();

    /// <summary>菜单内容指纹，避免每帧重建（与技能 / 知识库选择器同一个理由）。</summary>
    private string _lastSignature = "";

    public RoleSelectorControl()
    {
        InitializeComponent();
        MenuTheme.Apply(RoleBtn.Flyout as MenuFlyout);
        RebuildMenu();

        // 每次打开菜单都通知宿主重新注入列表：在「助手」页刚建的角色，切回会话就能看到。
        if (RoleBtn.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>可选的角色列表（已由宿主筛过 kind=role 且启用的）。</summary>
    public IReadOnlyList<Agent> Roles
    {
        get => _roles;
        set
        {
            _roles = value?.ToList() ?? new List<Agent>();
            RebuildMenuIfChanged();
            UpdateLabel();
        }
    }

    /// <summary>当前选中的角色 id；空字符串 = 不使用角色。</summary>
    public string SelectedRoleId { get; private set; } = "";

    /// <summary>当前选中的角色对象；未选或列表里已经没有它时为 null。</summary>
    public Agent? SelectedRole => _roles.FirstOrDefault(r => r.Id == SelectedRoleId);

    /// <summary>用户在选择器里改了选择。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>菜单即将展开，宿主应重新注入 <see cref="Roles"/>。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>由宿主设置当前值（不触发 <see cref="SelectionChanged"/>）。</summary>
    public void SetSelected(string? roleId)
    {
        SelectedRoleId = roleId ?? "";
        RebuildMenuIfChanged();
        UpdateLabel();
    }

    private void Select(string roleId)
    {
        if (SelectedRoleId == roleId) return;
        SelectedRoleId = roleId;
        UpdateLabel();
        RebuildMenu();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Signature()
        => string.Join('\u0001', _roles.Select(r => r.Id + "=" + r.Name + "=" + r.Description));

    private void RebuildMenuIfChanged()
    {
        if (Signature() == _lastSignature) return;
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _lastSignature = Signature();
        if (RoleBtn.Flyout is not MenuFlyout menu) return;

        var items = menu.Items;
        items.Clear();

        var none = new MenuItem
        {
            Header = "不使用角色",
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = SelectedRoleId.Length == 0,
            GroupName = "role",
        };
        none.Click += (_, _) => Select("");
        items.Add(none);

        if (_roles.Count == 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = "还没有角色，到「助手」页新建", IsEnabled = false });
            return;
        }

        items.Add(new Separator());
        foreach (var role in _roles)
        {
            var id = role.Id;
            var item = new MenuItem
            {
                Header = role.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = SelectedRoleId == id,
                GroupName = "role",
            };

            // 菜单项只有一行字，角色是干什么的放在悬停提示里
            if (!string.IsNullOrWhiteSpace(role.Description))
                ToolTip.SetTip(item, role.Description);

            item.Click += (_, _) => Select(id);
            items.Add(item);
        }
    }

    private void UpdateLabel()
    {
        var current = SelectedRole;
        LabelText.Text = current?.Name ?? "角色";
        ToolTip.SetTip(RoleBtn, current is null
            ? "不使用角色：沿用会话自己的设置"
            : string.IsNullOrWhiteSpace(current.Description)
                ? $"角色：{current.Name}"
                : $"角色：{current.Name}\n{current.Description}");
    }
}
