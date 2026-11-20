using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    /// <summary>菜单重建用的内容指纹（见 <see cref="Signature"/>）。</summary>
    private string _lastSignature = "";

    public McpSelectorControl()
    {
        InitializeComponent();
        MenuTheme.Apply(RootBtn.Flyout as MenuFlyout);
        RebuildMenu();

        // 菜单每次打开都通知宿主重新读一次 MCP 服务列表（服务页里新建 / 改名 / 启停，
        // 切回会话就能看到）。用 Opening 而不是 Click：Click 时菜单已经开始展开。
        if (RootBtn.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// 菜单即将打开。宿主应在此重新注入 <see cref="Servers"/> ——
    /// 只在创建时读一次库的话，之后在服务页改了 MCP 服务，这个选择器会一直显示旧数据。
    /// </summary>
    public event EventHandler? RefreshRequested;

    // ============================================================
    // 属性变化
    // ============================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ServersProperty)
        {
            _servers = (Servers ?? Array.Empty<McpServerConfig>()).ToList();
            RebuildMenuIfChanged();
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

    /// <summary>
    /// 内容指纹：决定菜单结构与每一项文案的那些字段（启停状态影响「（未启动）」后缀，
    /// 所以也算进去）。每次打开菜单宿主都会重新读库，内容没变时靠它跳过重建，否则会闪一下。
    /// </summary>
    private string Signature()
    {
        var builder = new StringBuilder();
        foreach (var server in _servers)
            builder.Append(server.Id).Append('\u0001')
                   .Append(server.Name).Append('\u0001')
                   .Append(server.Enabled ? '1' : '0').Append('\u0002');
        return builder.ToString();
    }

    private void RebuildMenuIfChanged()
    {
        if (Signature() == _lastSignature) return;

        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _lastSignature = Signature();
        // 通过按钮 Flyout 访问菜单，避免 Flyout 上 x:Name 字段的编译歧义（与 ModelSelector 相同的已知坑）
        if (RootBtn.Flyout is not MenuFlyout menu) return;
        menu.Items.Clear();
        _items.Clear();
        _items.AddRange(FillMenu(menu.Items));
        UpdateBadge();
    }

    /// <summary>
    /// 把「不使用 / 各服务」这一组勾选项装配进任意菜单集合，返回其中可切换勾选的项。
    ///
    /// 分成公开方法是因为菜单有两处：控件自己的按钮，以及输入框「更多」菜单里的子菜单。
    /// 建的是新实例 —— 一个 <see cref="MenuItem"/> 只能挂在一个父级下，两处不能共用控件。
    /// 外部的那些项不进 <see cref="_items"/>：它们每次展开菜单都会整批重建，
    /// 不需要跟着控件内部的勾选状态刷新。
    /// </summary>
    public List<MenuItem> FillMenu(ItemCollection items)
    {
        var toggles = new List<MenuItem>();

        if (_servers.Count == 0)
        {
            items.Add(new MenuItem { Header = "暂无 MCP 服务", IsEnabled = false });
            return toggles;
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
            toggles.Add(mi);
            items.Add(mi);
        }

        return toggles;
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
