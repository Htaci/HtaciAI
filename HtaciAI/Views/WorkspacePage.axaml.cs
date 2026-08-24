using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;

namespace HtaciAI.Views;

/// <summary>
/// 工作空间页（DB 驱动）：左侧为工作空间列表 / 会话列表，右侧挂工作区会话的专属聊天视图
/// <see cref="WorkspaceChatView"/>。新建工作空间时在设置里选 Agent，新建会话直接创建（非发送后创建）。
/// </summary>
public partial class WorkspacePage : UserControl
{
    private readonly List<WorkspaceConfig> _workspaces = new();
    private readonly Dictionary<string, Agent> _agentsById = new();
    private WorkspaceConfig? _selectedWorkspace;
    private List<WorkspaceChatSession> _sessions = new();
    private string? _activeSessionId;

    public WorkspacePage()
    {
        InitializeComponent();
        SetupButtons();
        _ = LoadAsync();
    }

    private void SetupButtons()
    {
        CreateWorkspaceBtn.PointerEntered += (s, e) => { CreateWorkspaceBtn.Background = new SolidColorBrush(Color.Parse("#F1F3F5")); CreateWorkspaceBtn.Cursor = new Cursor(StandardCursorType.Hand); };
        CreateWorkspaceBtn.PointerExited += (s, e) => { CreateWorkspaceBtn.Background = Brushes.Transparent; CreateWorkspaceBtn.Cursor = new Cursor(StandardCursorType.Arrow); };
        CreateWorkspaceBtn.PointerPressed += (s, e) => { OnCreateWorkspaceClick(); e.Handled = true; };

        NewSessionBtn.PointerEntered += (s, e) => { NewSessionBtn.Background = new SolidColorBrush(Color.Parse("#F1F3F5")); NewSessionBtn.Cursor = new Cursor(StandardCursorType.Hand); };
        NewSessionBtn.PointerExited += (s, e) => { NewSessionBtn.Background = Brushes.Transparent; NewSessionBtn.Cursor = new Cursor(StandardCursorType.Arrow); };
        NewSessionBtn.PointerPressed += (s, e) => { OnNewSessionClick(); e.Handled = true; };
    }

    private async Task LoadAsync()
    {
        foreach (var a in await AgentRepository.GetAllAsync())
            _agentsById[a.Id] = a;
        _workspaces.Clear();
        _workspaces.AddRange(await WorkspaceRepository.GetAllAsync());
        if (_selectedWorkspace is not null)
            await RefreshSessionsAsync();
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    // ---- 左侧导航 ----

    private void RefreshLeftList()
    {
        ListHost.Content = _selectedWorkspace is null ? BuildWorkspaceList() : BuildSessionList();
    }

    private Control BuildWorkspaceList()
    {
        var panel = new StackPanel { Spacing = 4 };
        if (_workspaces.Count == 0)
        {
            panel.Children.Add(EmptyHint("暂无工作空间，点击「+ 新建工作空间」创建"));
            return PanelToScroll(panel);
        }
        foreach (var ws in _workspaces)
            panel.Children.Add(CreateWorkspaceItem(ws));
        return PanelToScroll(panel);
    }

    private Control BuildSessionList()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(CreateBackItem("← 返回工作空间列表", BackToWorkspaceList));
        foreach (var s in _sessions)
            panel.Children.Add(CreateSessionItem(s));
        return PanelToScroll(panel);
    }

    private Control PanelToScroll(StackPanel panel) => new ScrollViewer
    {
        Content = panel,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
        Margin = new Thickness(12, 10, 0, 0),
    };

    private Border CreateWorkspaceItem(WorkspaceConfig ws)
    {
        var isActive = ReferenceEquals(ws, _selectedWorkspace);
        var nameBlock = new TextBlock
        {
            Text = ws.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var infoBlock = new TextBlock
        {
            Text = ws.EnabledToolIds.Count + "工具 · " + ws.EnabledSkills.Count + "技能",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Spacing = 2, Children = { nameBlock, infoBlock } },
        };
        item.PointerEntered += (s, e) => { if (!isActive) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5")); };
        item.PointerExited += (s, e) => { if (!isActive) item.Background = Brushes.Transparent; };
        item.PointerPressed += (s, e) => { _ = OpenWorkspaceAsync(ws); e.Handled = true; };
        return item;
    }

    private Border CreateSessionItem(WorkspaceChatSession s)
    {
        var isActive = s.Id == _activeSessionId;
        var title = new TextBlock
        {
            Text = s.Title,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var time = new TextBlock
        {
            Text = FormatTime(s.UpdatedAt),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var delBtn = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")) },
            Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        delBtn.Click += async (_, _) => await DeleteSessionAsync(s);

        var rightHost = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto") };
        rightHost.Children.Add(time);
        rightHost.Children.Add(delBtn);

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, rightHost } },
        };
        Grid.SetColumn(rightHost, 1);
        item.PointerEntered += (_, e) => { if (!isActive) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5")); time.IsVisible = false; delBtn.IsVisible = true; };
        item.PointerExited += (_, e) => { if (!isActive) item.Background = Brushes.Transparent; time.IsVisible = true; delBtn.IsVisible = false; };
        item.PointerPressed += (_, e) => { OpenSession(s); e.Handled = true; };
        return item;
    }

    private Border CreateBackItem(string text, Action onClick)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#546E7A")) },
        };
        item.PointerEntered += (s, e) => item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        item.PointerExited += (s, e) => item.Background = Brushes.Transparent;
        item.PointerPressed += (s, e) => { onClick(); e.Handled = true; };
        return item;
    }

    // ---- 右侧内容 ----

    private void RefreshRightPanel()
    {
        if (_selectedWorkspace is null)
        {
            RightPanel.Content = EmptyHint("选择左侧工作空间，或点击「+ 新建工作空间」");
            return;
        }
        if (_activeSessionId is null)
        {
            RightPanel.Content = EmptyHint("选择左侧会话，或点击「+ 新建会话」");
            return;
        }
        var session = _sessions.FirstOrDefault(s => s.Id == _activeSessionId);
        if (session is null)
        {
            RightPanel.Content = EmptyHint("会话不存在或已删除");
            return;
        }
        var agent = _selectedWorkspace.AgentId is not null && _agentsById.TryGetValue(_selectedWorkspace.AgentId, out var a) ? a : null;
        var view = new WorkspaceChatView(_selectedWorkspace, agent, session);
        view.Updated += () => _ = RefreshSessionsAsync();
        RightPanel.Content = view;
    }

    private void UpdateSidebarButtons()
    {
        CreateWorkspaceBtn.IsVisible = _selectedWorkspace is null;
        NewSessionBtn.IsVisible = _selectedWorkspace is not null;
        if (NewSessionText is not null)
            NewSessionText.Foreground = new SolidColorBrush(Color.Parse(_selectedWorkspace is null ? "#9CA3AF" : "#1A1A2E"));
    }

    // ---- 导航动作 ----

    private async Task OpenWorkspaceAsync(WorkspaceConfig ws)
    {
        _selectedWorkspace = ws;
        _activeSessionId = null;
        await RefreshSessionsAsync();
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    private void BackToWorkspaceList()
    {
        _selectedWorkspace = null;
        _activeSessionId = null;
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    private void OpenSession(WorkspaceChatSession s)
    {
        _activeSessionId = s.Id;
        RefreshLeftList();
        RefreshRightPanel();
    }

    private async Task RefreshSessionsAsync()
    {
        if (_selectedWorkspace is null) { _sessions = new(); return; }
        _sessions = await WorkspaceSessionRepository.GetByWorkspaceAsync(_selectedWorkspace.Id);
    }

    // ---- 动作 ----

    private async void OnCreateWorkspaceClick()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dlg = new CreateWorkspaceWindow();
        if (owner != null)
            await dlg.ShowDialog(owner);
        if (dlg.Result is null) return;

        await WorkspaceRepository.CreateAsync(dlg.Result);
        _selectedWorkspace = dlg.Result;
        _activeSessionId = null;
        await LoadAsync();
    }

    private async void OnNewSessionClick()
    {
        if (_selectedWorkspace is null) return;
        var agent = _selectedWorkspace.AgentId is not null && _agentsById.TryGetValue(_selectedWorkspace.AgentId, out var a) ? a : null;
        var session = WorkspaceAgentPipeline.CreateSessionDefaults(_selectedWorkspace, agent);
        await WorkspaceSessionRepository.CreateAsync(session);
        _activeSessionId = session.Id;
        await RefreshSessionsAsync();
        RefreshLeftList();
        RefreshRightPanel();
        UpdateSidebarButtons();
    }

    private async Task DeleteSessionAsync(WorkspaceChatSession s)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!await ConfirmDialog.ShowAsync(owner, $"确定删除会话「{s.Title}」吗？", "删除会话")) return;
        try { await WorkspaceSessionRepository.SoftDeleteAsync(s.Id); } catch { /* 忽略 */ }
        if (_activeSessionId == s.Id) { _activeSessionId = null; }
        await RefreshSessionsAsync();
        RefreshLeftList();
        RefreshRightPanel();
    }

    private static string FormatTime(long ms)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var now = DateTimeOffset.Now;
        if (dt.Date == now.Date) return dt.ToString("HH:mm");
        if (dt.Date == now.AddDays(-1).Date) return "昨天";
        return dt.ToString("M/d");
    }
}
