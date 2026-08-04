using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
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
        RefreshRightPanel();
    }

    /// <summary>
    /// 左侧三个按钮（新建会话/搜索/新建工作空间）使用统一的列表项样式：
    /// 透明背景、圆角、悬停变浅灰、左对齐文字（与智能对话历史记录项一致）
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
        RefreshWorkspaceList();
        RefreshRightPanel();
        UpdateNewSessionBtnState();
    }

    private void OnSearchClick()
    {
        // TODO: 会话搜索功能后续实现
    }

    /// <summary>
    /// 新建会话：在选中的工作空间（或会话所在的工作空间）创建空白会话窗口
    /// </summary>
    private void OnNewSessionClick()
    {
        var target = _activeSession != null ? FindOwner(_activeSession) : _selectedWorkspace;
        if (target == null) return;

        var session = new WorkspaceSession { Title = $"新会话 {++_sessionCounter}" };
        target.Sessions.Add(session);
        OpenSession(session);
        RefreshWorkspaceList();
    }

    private Workspace? FindOwner(WorkspaceSession session)
    {
        foreach (var ws in _workspaces)
            if (ws.Sessions.Contains(session))
                return ws;
        return null;
    }

    private void SelectWorkspace(Workspace ws)
    {
        _selectedWorkspace = ws;
        _activeSession = null;
        RefreshWorkspaceList();
        RefreshRightPanel();
        UpdateNewSessionBtnState();
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
            RefreshWorkspaceList();
        };
        RightPanel.Content = view;

        RefreshWorkspaceList();
        UpdateNewSessionBtnState();
    }

    private void RefreshWorkspaceList()
    {
        WorkspaceList.Children.Clear();
        foreach (var ws in _workspaces)
            WorkspaceList.Children.Add(CreateWorkspaceItem(ws));
    }

    private Border CreateWorkspaceItem(Workspace ws)
    {
        var expanded = ReferenceEquals(ws, _selectedWorkspace);

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
                    Text = $"{ws.Folders.Count} 个文件夹 · {ws.Model}",
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
                sessionsPanel.Children.Add(CreateSessionItem(ws, s));

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

    private Border CreateSessionItem(Workspace ws, WorkspaceSession session)
    {
        var isActive = ReferenceEquals(session, _activeSession);

        var textBlock = new TextBlock
        {
            Text = session.Title,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0)
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

    private void UpdateNewSessionBtnState()
    {
        _newSessionEnabled = _selectedWorkspace != null || _activeSession != null;
        NewSessionText.Foreground = new SolidColorBrush(Color.Parse(_newSessionEnabled ? "#1A1A2E" : "#9CA3AF"));
    }
}
