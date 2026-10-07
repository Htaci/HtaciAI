using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using HtaciAI.Models;

namespace HtaciAI.Controls;

/// <summary>
/// 可复用的 MCP 服务选择器控件：按钮显示 MCP 图标 + 已选数量角标，点击弹出菜单逐个勾选。
/// 勾选即代表「本会话启用该服务」——发送前会自动确保它已启动并连上（见
/// <see cref="Services.Mcp.McpConnectionManager.EnsureToolsAsync"/>）。
/// 通过 <see cref="SelectedServerIds"/>（TwoWay）暴露选中结果，通过 <see cref="SelectionChanged"/> 通知变化。
/// </summary>
public partial class McpSelectorControl : UserControl
{
    private List<McpServerConfig> _servers = new();
    private readonly HashSet<string> _selected = new();
    private readonly List<MenuItem> _items = new();

    public McpSelectorControl()
    {
        InitializeComponent();
        RebuildMenu();
    }

    // ============================================================
    // 依赖属性
    // ============================================================

    /// <summary>可选的 MCP 服务列表（含未启动的：勾选后会自动启动）。</summary>
    public static readonly StyledProperty<IEnumerable<McpServerConfig>> ServersProperty =
        AvaloniaProperty.Register<McpSelectorControl, IEnumerable<McpServerConfig>>(
            nameof(Servers),
            defaultValue: Array.Empty<McpServerConfig>(),
            coerce: (_, v) => v ?? Array.Empty<McpServerConfig>());

    public IEnumerable<McpServerConfig> Servers
    {
        get => GetValue(ServersProperty);
        set => SetValue(ServersProperty, value);
    }

    /// <summary>当前启用的 MCP 服务 id 集合。</summary>
    public static readonly StyledProperty<IReadOnlyList<string>> SelectedServerIdsProperty =
        AvaloniaProperty.Register<McpSelectorControl, IReadOnlyList<string>>(
            nameof(SelectedServerIds),
            defaultValue: Array.Empty<string>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<string>());

    public IReadOnlyList<string> SelectedServerIds
    {
        get => GetValue(SelectedServerIdsProperty);
        set => SetValue(SelectedServerIdsProperty, value);
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

        if (change.Property == ServersProperty)
        {
            _servers = (Servers ?? Array.Empty<McpServerConfig>()).ToList();
            RebuildMenu();
        }
        else if (change.Property == SelectedServerIdsProperty)
        {
            SyncFromProperty();
            UpdateCheckedStates();
        }
    }

    private void SyncFromProperty()
    {
        _selected.Clear();
        foreach (var id in SelectedServerIds ?? Array.Empty<string>())
            _selected.Add(id);
    }

    /// <summary>内部变更后提交：回写属性 + 刷新 UI + 触发事件。</summary>
    private void CommitSelection()
    {
        SelectedServerIds = _selected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        UpdateCheckedStates();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 菜单构建
    // ============================================================

    private void RebuildMenu()
    {
        // 通过按钮 Flyout 访问菜单，避免 Flyout 上 x:Name 字段的编译歧义（与 ModelSelector 相同的已知坑）
        if (RootBtn.Flyout is not MenuFlyout menu) return;
        var items = menu.Items;
        items.Clear();
        _items.Clear();

        if (_servers.Count == 0)
        {
            items.Add(new MenuItem { Header = "暂无 MCP 服务", IsEnabled = false });
            UpdateBadge();
            return;
        }

        var clear = new MenuItem { Header = "不使用任何 MCP 服务" };
        clear.Click += (_, _) => { _selected.Clear(); CommitSelection(); };
        items.Add(clear);
        items.Add(new Separator());

        foreach (var server in _servers)
        {
            var id = server.Id;
            var name = string.IsNullOrWhiteSpace(server.Name) ? "(未命名)" : server.Name;
            var mi = new MenuItem
            {
                // 未启动的也能勾：发送前会自动启动它，这里提前告知
                Header = server.Enabled ? name : $"{name}（未启动）",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _selected.Contains(id),
                Tag = id,
            };
            mi.Click += (_, _) => Toggle(id);
            _items.Add(mi);
            items.Add(mi);
        }

        UpdateBadge();
    }

    private void Toggle(string id)
    {
        if (!_selected.Add(id))
            _selected.Remove(id);
        CommitSelection();
    }

    private void UpdateCheckedStates()
    {
        foreach (var mi in _items)
            if (mi.Tag is string id) mi.IsChecked = _selected.Contains(id);
        UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (Badge is null || BadgeText is null) return;
        BadgeText.Text = _selected.Count.ToString();
        Badge.IsVisible = _selected.Count > 0;
    }
}
