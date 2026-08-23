using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Tools;
using HtaciAI.Views.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 工具页：管理脚本工具与工具集。数据来自全局 <see cref="ToolRegistry"/>，
/// "创建新工具"引导用户选择脚本 + --describe 自动生成 schema 并入库。
/// </summary>
public partial class ToolsPage : UserControl
{
    private List<Toolset> _toolsets = new();
    private Toolset? _selectedToolset;

    public ToolsPage()
    {
        InitializeComponent();
        SetupButtons();
        ReloadFromRegistry();
    }

    /// <summary>从注册表重新拉取工具集列表并刷新两侧面板。</summary>
    private void ReloadFromRegistry()
    {
        _toolsets = ToolRegistry.Instance.GetToolsets().ToList();
        if (_selectedToolset is null || _toolsets.All(t => t.Id != _selectedToolset.Id))
            _selectedToolset = _toolsets.FirstOrDefault();
        RefreshToolsetList();
        UpdateRightPanel();
    }

    private void SetupButtons()
    {
        SetupHoverAction(AddToolsetBtn, OnAddToolsetClick);            // 透明图标按钮：浅灰 hover
        SetupHoverAction(CreateToolBtn, OnCreateToolClick, "#2F2F3C"); // 深色填充按钮：深一档 hover
    }

    /// <summary>
    /// 统一的悬停反馈（hover 底色 + 手型光标）。移出时恢复到按钮的初始背景，
    /// 而非固定 Transparent——否则会覆盖掉像「创建新工具」这类深色填充按钮的原背景。
    /// </summary>
    private void SetupHoverAction(Border btn, Action onClick, string hoverBg = "#F1F3F5")
    {
        var normalBg = btn.Background;
        btn.PointerEntered += (s, e) =>
        {
            btn.Background = new SolidColorBrush(Color.Parse(hoverBg));
            btn.Cursor = new Cursor(StandardCursorType.Hand);
        };
        btn.PointerExited += (s, e) =>
        {
            btn.Background = normalBg;
            btn.Cursor = new Cursor(StandardCursorType.Arrow);
        };
        btn.PointerPressed += (s, e) =>
        {
            onClick();
            e.Handled = true;
        };
    }

    private void OnAddToolsetClick()
    {
        // TODO: 新建工具集窗口（名称/描述），创建后入库 + 注册表
        SetHint("新建工具集功能开发中，敬请期待");
    }

    /// <summary>打开创建工具对话框，保存后刷新列表。</summary>
    private async void OnCreateToolClick()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new CreateToolWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);

        if (dialog.Result is null) return;
        ReloadFromRegistry();
    }

    // ---- 左侧：工具集列表 ----

    private void RefreshToolsetList()
    {
        ToolsetList.Children.Clear();

        if (_toolsets.Count == 0)
        {
            ToolsetList.Children.Add(new TextBlock
            {
                Text = "暂无工具集",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(12, 8, 0, 0)
            });
            return;
        }

        foreach (var ts in _toolsets)
            ToolsetList.Children.Add(CreateToolsetItem(ts));
    }

    private Border CreateToolsetItem(Toolset ts)
    {
        var isActive = ReferenceEquals(ts, _selectedToolset);
        var toolCount = ToolRegistry.Instance.GetByToolset(ts.Id).Count;

        var nameBlock = new TextBlock
        {
            Text = ts.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var infoBlock = new TextBlock
        {
            Text = $"{toolCount} 个工具",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"))
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
            Padding = new Thickness(12, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = title
        };

        item.PointerEntered += (s, e) =>
        {
            if (!isActive)
                item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (s, e) =>
        {
            if (!isActive)
                item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (s, e) =>
        {
            _selectedToolset = ts;
            RefreshToolsetList();
            UpdateRightPanel();
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧：工具列表 ----

    private void UpdateRightPanel()
    {
        if (_selectedToolset is null)
        {
            SetHint("选择左侧工具集查看其中的工具");
            return;
        }

        var tools = ToolRegistry.Instance.GetByToolset(_selectedToolset.Id);
        if (tools.Count == 0)
        {
            SetHint($"工具集「{_selectedToolset.Name}」还没有工具，点击右上角创建新工具");
            return;
        }

        BuildToolList(tools);
    }

    private void BuildToolList(IReadOnlyList<ToolDefinition> tools)
    {
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        foreach (var tool in tools)
            wrap.Children.Add(CreateToolCard(tool));

        ToolListHost.Content = new ScrollViewer
        {
            Content = wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private Border CreateToolCard(ToolDefinition tool)
    {
        var runtimeText = tool.Source == ToolSource.Builtin ? "内置"
            : tool.Runtime == ScriptRuntimeKind.Node ? "Node.js" : "Python";

        var levelBadge = BuildLevelBadge(tool.DangerLevel);

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = tool.Name,
                    FontSize = 14.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#EDF4FC")),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = runtimeText,
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(Color.Parse("#3A7BC8"))
                    }
                },
                levelBadge,
            }
        };
        var descBlock = new TextBlock
        {
            Text = tool.Description,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var icon = new TextBlock
        {
            Text = "", // Segoe Fluent Icons：扳手（工具）
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.Parse("#3A7BC8")),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var card = new Border
        {
            Width = 240,
            Height = 118,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 14),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.White,
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #0A000000, 0 1 2 0 #06000000"),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { icon, nameRow, descBlock }
            }
        };

        card.PointerEntered += (s, e) =>
        {
            card.Background = new SolidColorBrush(Color.Parse("#F8FAFC"));
        };
        card.PointerExited += (s, e) =>
        {
            card.Background = Brushes.White;
        };
        card.PointerPressed += (s, e) =>
        {
            // TODO: 打开工具详情/编辑/删除
            e.Handled = true;
        };

        return card;
    }

    private void SetHint(string text)
    {
        ToolListHost.Content = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>权限等级小标签：安全=绿 / 风险=橙 / 危险=红。</summary>
    private static Border BuildLevelBadge(ToolDangerLevel level)
    {
        var (color, text) = level switch
        {
            ToolDangerLevel.Safe => ("#16A34A", "安全"),
            ToolDangerLevel.Risk => ("#D97706", "风险"),
            _ => ("#DC2626", "危险"),
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(color)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            },
        };
    }
}
