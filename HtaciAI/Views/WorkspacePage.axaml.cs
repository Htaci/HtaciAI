using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using HtaciAI.Models;

namespace HtaciAI.Views;

public partial class WorkspacePage : UserControl
{
    private readonly List<Workspace> _workspaces = new();
    private Workspace? _selectedWorkspace;
    private WorkspaceSession? _activeSession;
    private int _sessionCounter;
    private bool _newSessionEnabled;

    public WorkspacePage()
    {
        InitializeComponent();
        SetupSidebarButtons();
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    /// <summary>
    /// 左侧三个按钮（新会话/搜索/新建工作空间）使用统一的列表项样式
    /// </summary>
    private void SetupSidebarButtons()
    {
        NewSessionBtn.PointerEntered += (s, e) =>
        {
            if (_newSessionEnabled)
            {
                NewSessionBtn.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
                NewSessionBtn.Cursor = new Cursor(StandardCursorType.Hand);
            }
        };
        NewSessionBtn.PointerExited += (s, e) =>
        {
            NewSessionBtn.Background = Brushes.Transparent;
            NewSessionBtn.Cursor = new Cursor(StandardCursorType.Arrow);
        };
        NewSessionBtn.PointerPressed += (s, e) =>
        {
            if (_newSessionEnabled)
                OnNewSessionClick();
            e.Handled = true;
        };

        SetupHoverAction(SearchBtn, OnSearchClick);
        SetupHoverAction(CreateWorkspaceBtn, OnCreateWorkspaceClick);
    }

    private void SetupHoverAction(Border btn, Action onClick)
    {
        btn.PointerEntered += (s, e) =>
        {
            btn.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
            btn.Cursor = new Cursor(StandardCursorType.Hand);
        };
        btn.PointerExited += (s, e) =>
        {
            btn.Background = Brushes.Transparent;
            btn.Cursor = new Cursor(StandardCursorType.Arrow);
        };
        btn.PointerPressed += (s, e) =>
        {
            onClick();
            e.Handled = true;
        };
    }

    private async void OnCreateWorkspaceClick()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new CreateWorkspaceWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);

        if (dialog.Result == null) return;

        _workspaces.Add(dialog.Result);
        _selectedWorkspace = dialog.Result;
        _activeSession = null;
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    private void OnSearchClick()
    {
        // TODO: 会话搜索功能后续实现
    }

    /// <summary>
    /// 新建会话：在当前进入的工作空间中创建空白会话窗口
    /// </summary>
    private void OnNewSessionClick()
    {
        if (_selectedWorkspace == null) return;

        var session = new WorkspaceSession { Title = $"新会话 {++_sessionCounter}" };
        _selectedWorkspace.Sessions.Add(session);
        OpenSession(session);
        RefreshLeftList();
    }

    private Workspace? FindOwner(WorkspaceSession session)
    {
        foreach (var ws in _workspaces)
            if (ws.Sessions.Contains(session))
                return ws;
        return null;
    }

    /// <summary>
    /// 进入工作空间（双列表模式下左侧切换为会话列表）
    /// </summary>
    private void SelectWorkspace(Workspace ws)
    {
        _selectedWorkspace = ws;
        _activeSession = null;
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    /// <summary>
    /// 返回工作空间列表（双列表模式）
    /// </summary>
    private void BackToWorkspaceList()
    {
        _selectedWorkspace = null;
        _activeSession = null;
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    /// <summary>
    /// 打开会话窗口：右侧显示该会话的工作对话界面
    /// </summary>
    private void OpenSession(WorkspaceSession session)
    {
        _selectedWorkspace = FindOwner(session);
        _activeSession = session;

        var view = new NewChatView();
        view.SendRequested += (s, text) =>
        {
            session.Title = text.Length > 14 ? text[..14] + "…" : text;
            RightPanel.Content = new ChatView();
            RefreshLeftList();
        };
        RightPanel.Content = view;

        RefreshLeftList();
        UpdateSidebarButtons();
    }

    // ---- 左侧列表渲染 ----

    private void RefreshLeftList()
    {
        if (AppSettings.DisplayMode == WorkspaceDisplayMode.Tree)
            BuildTreeWorkspaceList();
        else
            BuildDualList();
    }

    /// <summary>
    /// 双列表：未进入工作空间时显示工作空间列表，进入后显示该工作空间的会话列表
    /// </summary>
    private void BuildDualList()
    {
        if (_selectedWorkspace == null)
            BuildWorkspaceListDual();
        else
            BuildSessionList();
    }

    private Control WrapInScroll(Control content)
    {
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void BuildWorkspaceListDual()
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var ws in _workspaces)
            list.Children.Add(CreateDualWorkspaceItem(ws));
        ListHost.Content = WrapInScroll(list);
    }

    private Border CreateDualWorkspaceItem(Workspace ws)
    {
        var nameBlock = new TextBlock
        {
            Text = ws.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var infoBlock = new TextBlock
        {
            Text = $"{ws.AgentFramework} · {ws.Sessions.Count} 个会话",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"))
        };
        var title = new StackPanel
        {
            Spacing = 2,
            Margin = new Avalonia.Thickness(0, 0, 12, 0),
            Children = { nameBlock, infoBlock }
        };

        var arrow = new TextBlock
        {
            Text = "\u203A",
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Padding = new Avalonia.Thickness(12, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { title, arrow }
            }
        };
        Grid.SetColumn(arrow, 1);

        item.PointerEntered += (s, e) =>
        {
            item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (s, e) =>
        {
            item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (s, e) =>
        {
            SelectWorkspace(ws);
            e.Handled = true;
        };

        return item;
    }

    private void BuildSessionList()
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(CreateBackHeader());
        foreach (var s in _selectedWorkspace!.Sessions)
            list.Children.Add(CreateSessionItem(s));
        ListHost.Content = WrapInScroll(list);
    }

    /// <summary>
    /// 会话列表顶部的返回头：‹ 工作空间名
    /// </summary>
    private Border CreateBackHeader()
    {
        var backArrow = new TextBlock
        {
            Text = "\u2039",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var nameBlock = new TextBlock
        {
            Text = _selectedWorkspace?.Name ?? "",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var header = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Children = { backArrow, nameBlock }
            }
        };

        header.PointerEntered += (s, e) =>
        {
            header.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        header.PointerExited += (s, e) =>
        {
            header.Background = Brushes.Transparent;
        };
        header.PointerPressed += (s, e) =>
        {
            BackToWorkspaceList();
            e.Handled = true;
        };

        return header;
    }

    private void BuildTreeWorkspaceList()
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var ws in _workspaces)
            list.Children.Add(CreateTreeWorkspaceItem(ws));
        ListHost.Content = WrapInScroll(list);
    }

    private Border CreateTreeWorkspaceItem(Workspace ws)
    {
        var expanded = AppSettings.TreeAutoCollapse
            ? ReferenceEquals(ws, _selectedWorkspace)
            : true;

        var arrow = new TextBlock
        {
            Text = expanded ? "\u25BC" : "\u25B6",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var title = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock
                {
                    Text = ws.Name,
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#1A1A2E"))
                },
                new TextBlock
                {
                    Text = $"{ws.AgentFramework} · {ws.Sessions.Count} 个会话",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"))
                }
            }
        };

        var header = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = expanded ? Brushes.White : Brushes.Transparent,
            Padding = new Avalonia.Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children = { arrow, title }
            }
        };
        Grid.SetColumn(title, 1);

        header.PointerEntered += (s, e) =>
        {
            if (!expanded)
                header.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        header.PointerExited += (s, e) =>
        {
            if (!expanded)
                header.Background = Brushes.Transparent;
        };
        header.PointerPressed += (s, e) =>
        {
            SelectWorkspace(ws);
            e.Handled = true;
        };

        var sessionsPanel = new StackPanel
        {
            Spacing = 2,
            Margin = new Avalonia.Thickness(20, 4, 0, 4)
        };
        if (expanded)
            foreach (var s in ws.Sessions)
                sessionsPanel.Children.Add(CreateSessionItem(s));

        return new Border
        {
            Margin = new Avalonia.Thickness(2, 1),
            Child = new StackPanel
            {
                Spacing = 0,
                Children = { header, sessionsPanel }
            }
        };
    }

    private Border CreateSessionItem(WorkspaceSession session)
    {
        var isActive = ReferenceEquals(session, _activeSession);

        var textBlock = new TextBlock
        {
            Text = session.Title,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(12, 0)
        };

        var item = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = textBlock
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
            OpenSession(session);
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧内容 ----

    private void RefreshRightPanel()
    {
        if (_workspaces.Count == 0)
        {
            SetHint("暂时还没有工作空间，去添加一个吧");
            return;
        }

        if (_selectedWorkspace == null)
        {
            SetHint("选择一个工作空间中的会话开始任务吧");
            return;
        }

        if (_selectedWorkspace.Sessions.Count == 0)
        {
            SetHint("暂时还没有会话，去添加一个吧");
            return;
        }

        SetHint("选择一个工作空间中的会话开始任务吧");
    }

    private void SetHint(string text)
    {
        RightPanel.Content = new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    /// <summary>
    /// 切换顶部按钮：工作空间列表显示"新建工作空间"，会话列表显示"新建会话"
    /// </summary>
    private void UpdateSidebarButtons()
    {
        var inWorkspace = _selectedWorkspace != null;
        NewSessionBtn.IsVisible = inWorkspace;
        CreateWorkspaceBtn.IsVisible = !inWorkspace;
        _newSessionEnabled = inWorkspace;
        NewSessionText.Foreground = new SolidColorBrush(Color.Parse(inWorkspace ? "#1A1A2E" : "#9CA3AF"));
    }
}
