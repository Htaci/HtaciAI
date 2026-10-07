using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Models;
using HtaciAI.Controls;

namespace HtaciAI.Controls;

/// <summary>
/// 工作空间选择器。
///
/// 折叠态是一张卡片：图标块 + 工作空间名 + 「当前工作空间 · N 个会话」+ 展开箭头。
/// 点开后面板里有搜索框、「+ 新建」按钮和「最近使用」列表，当前项带勾。
///
/// 面板走 <see cref="Flyout"/> 悬浮层而不是塞进布局流：展开/收起不改变页面高度，
/// 上面的标题、分段控件、输入框都不会被顶动。若改成布局流内展开，整块内容会重新居中。
/// </summary>
public class WorkspacePickerControl : UserControl
{
    /// <summary>用户选定了另一个工作空间。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>面板里点了「+ 新建」。</summary>
    public event EventHandler? CreateRequested;

    /// <summary>当前选中的工作空间；列表为空时为 null。</summary>
    public WorkspaceConfig? SelectedWorkspace { get; private set; }

    // 与侧栏/输入框同一套配色
    private const string Accent = "#4A90D9";
    private const string CardBorder = "#E5E7EB";
    private const string CardBorderHover = "#D1D5DB";
    private const string TitleFg = "#1A1A2E";
    private const string SubFg = "#9CA3AF";
    private const string RowHoverBg = "#14000000";
    private const string RowSelectedBg = "#EEF4FB";

    private readonly Border _card;
    private readonly TextBlock _chevron;
    private readonly TextBlock _nameText;
    private readonly TextBlock _subText;

    private readonly TextBox _search;
    private readonly StackPanel _listPanel;
    private readonly Border _panelBody;
    private readonly Flyout _panel;

    private IReadOnlyList<WorkspaceConfig> _workspaces = Array.Empty<WorkspaceConfig>();
    private Dictionary<string, (int Count, long LastAt)> _stats = new();

    public WorkspacePickerControl()
    {
        // ================= 折叠态：卡片 =================
        var headerTile = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#E9F2EC")),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "",                          // Package（立方体）
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.Parse("#4F7A61")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        _nameText = new TextBlock
        {
            Text = "未选择工作空间",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TitleFg)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _subText = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse(SubFg)),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var nameCol = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { _nameText, _subText },
        };

        _chevron = new TextBlock
        {
            Text = "",                              // ChevronDown
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        cardGrid.Children.Add(headerTile);
        Grid.SetColumn(nameCol, 1);
        cardGrid.Children.Add(nameCol);
        Grid.SetColumn(_chevron, 2);
        cardGrid.Children.Add(_chevron);

        _card = new Border
        {
            Height = 72,
            CornerRadius = new CornerRadius(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse(CardBorder)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = cardGrid,
        };
        // 走方法而不是内联 lambda：_panel 在卡片之后才构造，直接在这里解引用
        // 会被编译器的可空流分析判为「可能为 null」。
        _card.PointerEntered += (_, _) => OnCardEnter();
        _card.PointerExited += (_, _) => OnCardExit();
        _card.PointerPressed += (_, e) => { TogglePanel(); e.Handled = true; };

        // ================= 展开态：面板 =================
        _search = new TextBox
        {
            FontSize = 13,
            Height = 34,
            Padding = new Thickness(32, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            PlaceholderText = "搜索工作空间",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => RebuildList();

        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        searchGrid.Children.Add(new TextBlock
        {
            Text = "",                              // Search
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(SubFg)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(11, 0, 0, 0),
            IsHitTestVisible = false,
        });

        var newBtnText = new TextBlock
        {
            Text = "+ 新建",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse(TitleFg)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var newBtn = new Border
        {
            Height = 34,
            MinWidth = 74,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse(CardBorder)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = newBtnText,
        };
        newBtn.PointerEntered += (_, _) => newBtn.Background = new SolidColorBrush(Color.Parse("#F3F4F6"));
        newBtn.PointerExited += (_, _) => newBtn.Background = Brushes.White;

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        searchRow.Children.Add(searchGrid);
        Grid.SetColumn(newBtn, 1);
        searchRow.Children.Add(newBtn);

        _listPanel = new StackPanel { Spacing = 2 };

        _panelBody = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse(CardBorder)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Children =
                {
                    searchRow,
                    new TextBlock
                    {
                        Text = "最近使用",
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.Parse(SubFg)),
                        Margin = new Thickness(2, 12, 0, 6),
                    },
                    new SmoothScrollViewer()
                    {
                        MaxHeight = 264,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = _listPanel,
                    },
                },
            },
        };

        _panel = new Flyout
        {
            Content = _panelBody,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
        _panel.Opened += (_, _) =>
        {
            _search.Text = "";
            RebuildList();
            SetOpenVisual(true);
        };
        _panel.Closed += (_, _) => SetOpenVisual(false);

        // 放在 _panel 构造之后：这里要直接解引用它
        newBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _panel.Hide();
            CreateRequested?.Invoke(this, EventArgs.Empty);
        };

        Content = _card;
        UpdateCard();
    }

    private void OnCardEnter()
    {
        if (!_panel.IsOpen) _card.BorderBrush = new SolidColorBrush(Color.Parse(CardBorderHover));
    }

    private void OnCardExit()
    {
        if (!_panel.IsOpen) _card.BorderBrush = new SolidColorBrush(Color.Parse(CardBorder));
    }

    /// <summary>注入工作空间列表与统计（统计来自 <c>WorkspaceSessionRepository.GetStatsAsync</c>）。</summary>
    public void SetData(IReadOnlyList<WorkspaceConfig> workspaces,
                        Dictionary<string, (int Count, long LastAt)> stats)
    {
        _workspaces = workspaces;
        _stats = stats;

        // 尽量保留原来的选择，否则退回第一项
        var keepId = SelectedWorkspace?.Id;
        SelectedWorkspace = keepId is null
            ? _workspaces.FirstOrDefault()
            : _workspaces.FirstOrDefault(w => w.Id == keepId) ?? _workspaces.FirstOrDefault();

        UpdateCard();
        RebuildList();
    }

    public void SelectWorkspace(string? id)
        => SelectedWorkspace = id is null ? null : _workspaces.FirstOrDefault(w => w.Id == id);

    private void UpdateCard()
    {
        if (SelectedWorkspace is { } ws)
        {
            var (count, _) = _stats.TryGetValue(ws.Id, out var s) ? s : (0, 0L);
            _nameText.Text = ws.Name;
            _subText.Text = count == 0 ? "当前工作空间 · 暂无会话" : $"当前工作空间 · {count} 个会话";
        }
        else
        {
            _nameText.Text = "未选择工作空间";
            _subText.Text = "点击选择一个工作空间";
        }
    }

    private void TogglePanel()
    {
        if (_panel.IsOpen)
        {
            _panel.Hide();
            return;
        }

        // 面板宽度对齐卡片；Flyout 自身有 4px 内容边距，这里扣掉，
        // 让面板左右边缘和卡片对齐（App.axaml 把 FlyoutContentThemePadding 收紧到了 4）。
        _panelBody.Width = Math.Max(260, _card.Bounds.Width - 8);
        _panel.ShowAt(_card);
    }

    private void SetOpenVisual(bool open)
    {
        _chevron.Text = open ? "" : "";       // ChevronUp / ChevronDown
        _card.BorderBrush = new SolidColorBrush(Color.Parse(open ? Accent : CardBorder));
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();

        var keyword = _search.Text?.Trim() ?? "";
        var items = _workspaces
            .Where(w => keyword.Length == 0
                        || w.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(w => _stats.TryGetValue(w.Id, out var s) ? s.LastAt : 0)
            .ThenBy(w => w.Name, StringComparer.CurrentCulture)
            .ToList();

        if (items.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = _workspaces.Count == 0 ? "还没有工作空间，点右上角「+ 新建」" : "没有匹配的工作空间",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(SubFg)),
                Margin = new Thickness(8, 10, 8, 10),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var ws in items)
            _listPanel.Children.Add(BuildRow(ws));
    }

    private Control BuildRow(WorkspaceConfig ws)
    {
        var selected = ws.Id == SelectedWorkspace?.Id;
        var (count, lastAt) = _stats.TryGetValue(ws.Id, out var s) ? s : (0, 0L);

        var tile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(selected ? "#DCE8F8" : "#EEF1F6")),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#4A5B6D")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = ws.Name,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(TitleFg)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = SubtitleFor(count, lastAt),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse(SubFg)),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var check = new TextBlock
        {
            Text = "",                              // CheckMark
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse(Accent)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = selected,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(check, 2);
        grid.Children.Add(check);

        var row = new Border
        {
            Height = 52,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0, 10, 0),
            Background = new SolidColorBrush(Color.Parse(selected ? RowSelectedBg : "#00000000")),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        if (!selected)
        {
            row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse(RowHoverBg));
            row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Color.Parse("#00000000"));
        }
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectWorkspace(ws.Id);
            UpdateCard();
            _panel.Hide();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        return row;
    }

    /// <summary>把统计转成「12 个会话 · 最近使用」这样的副标题。</summary>
    private static string SubtitleFor(int count, long lastAt)
    {
        var sessions = count == 0 ? "暂无会话" : $"{count} 个会话";
        if (lastAt <= 0) return sessions;

        var dt = DateTimeOffset.FromUnixTimeMilliseconds(lastAt).ToLocalTime();
        var now = DateTimeOffset.Now;
        var when = dt.Date == now.Date ? "最近使用"
            : dt.Date == now.AddDays(-1).Date ? "昨天使用"
            : $"{dt.Month}月{dt.Day}日使用";

        return $"{sessions} · {when}";
    }
}
