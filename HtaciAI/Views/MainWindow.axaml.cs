using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Updates;

namespace HtaciAI.Views;

public partial class MainWindow : Window
{
    private int _selectedIndex;
    private readonly List<Control> _tabContents = new();
    private readonly List<Border> _tabHeaders = new();
    private readonly List<TextBlock> _tabTitleBlocks = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        AddTab("智能对话", new ConversationPage());

        // 最大化按钮的图标要随窗口状态变化（最大化后变成「还原」）。
        // 用属性监听而不是只在自己点击时改，这样双击标题栏、系统快捷键等
        // 任何方式触发的状态变化都能同步到图标。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateMaximizeGlyph();
        };
        UpdateMaximizeGlyph();

        // 静默检查一次：不这么做的话，「有新版本才显示绿色」在用户不主动点击时永远看不到。
        // 没配置地址时 CheckAsync 直接返回 NotConfigured，不会发请求。
        _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>按当前窗口状态切换「最大化 / 还原」的图标。</summary>
    private void UpdateMaximizeGlyph()
    {
        // Segoe Fluent Icons：E922 = 最大化，E923 = 还原。
        MaximizeGlyph.Text = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }

    // ============================================================
    // 检查更新（标题栏按钮）
    // ============================================================

    private enum UpdateButtonState
    {
        /// <summary>平时：灰色的「检查更新」。</summary>
        Idle,

        /// <summary>正在请求清单。</summary>
        Checking,

        /// <summary>发现新版本：绿色的「立即更新」，点击启动更新程序。</summary>
        UpdateAvailable,

        /// <summary>已经是最新。</summary>
        UpToDate,

        /// <summary>没配地址或检查失败。</summary>
        Problem,
    }

    private UpdateButtonState _updateState = UpdateButtonState.Idle;
    private UpdateCheckResult? _lastCheck;
    private DispatcherTimer? _updateResetTimer;

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        switch (_updateState)
        {
            case UpdateButtonState.UpdateAvailable:
                _ = ApplyUpdateAsync();
                return;

            case UpdateButtonState.Checking:
                return;

            case UpdateButtonState.Problem when string.IsNullOrWhiteSpace(AppSettingsStore.Current.UpdateManifestUrl):
                // 还没配更新地址：直接把人送到「设置」去填，比弹个「请先配置」有用
                OnSettingsClick(sender, e);
                return;

            default:
                _ = CheckForUpdatesAsync(manual: true);
                return;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateState == UpdateButtonState.Checking) return;

        _updateResetTimer?.Stop();

        var url = AppSettingsStore.Current.UpdateManifestUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            SetUpdateState(UpdateButtonState.Problem, "检查更新", "还没有配置更新地址，点击前往「设置 → 常规设置」填写");
            return;
        }

        SetUpdateState(UpdateButtonState.Checking, "检查中…", null);

        var result = await UpdateChecker.CheckAsync(url);
        _lastCheck = result;

        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable:
                SetUpdateState(UpdateButtonState.UpdateAvailable, "立即更新",
                    $"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）" +
                    (result.Notes is null ? "" : "\n" + result.Notes));
                break;

            case UpdateStatus.UpToDate:
                SetUpdateState(UpdateButtonState.UpToDate, "已是最新", $"当前已是最新版本 {result.CurrentVersion}");
                ResetUpdateButtonLater();
                break;

            default:
                // 失败原因放 ToolTip 里，手动检查时至少按钮会变成「检查失败」提示用户去看
                SetUpdateState(UpdateButtonState.Problem, "检查更新", "检查更新失败：" + (result.Error ?? "未知原因"));
                if (manual) SetUpdateLabel("检查失败");
                break;
        }
    }

    /// <summary>「立即更新」：交给更新程序接管，然后退出自身。</summary>
    private async Task ApplyUpdateAsync()
    {
        if (_lastCheck is null) return;

        var result = await UpdateLauncher.LaunchAsync(_lastCheck);
        if (result.Started) return;

        // 更新程序还没做出来：如实说明，并让按钮回到「可再次点击」的状态
        SetUpdateLabel("更新程序未接入");
        ToolTip.SetTip(UpdateBtn, result.Message);
        ResetUpdateButtonLater();
    }

    /// <summary>把按钮恢复成当前该有的样子（「已是最新」这类瞬时状态用完要退回去）。</summary>
    private void ResetUpdateButtonLater()
    {
        _updateResetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };

        // 每次都换一份处理器，避免多次点击后累积重复回调
        // 定时器只建一次、只挂一个处理器：反复点击不该累积回调
        if (_updateResetTimer is null)
        {
            _updateResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _updateResetTimer.Tick += (_, _) =>
            {
                _updateResetTimer!.Stop();
                SetUpdateState(
                    _lastCheck?.HasUpdate == true ? UpdateButtonState.UpdateAvailable : UpdateButtonState.Idle,
                    _lastCheck?.HasUpdate == true ? "立即更新" : "检查更新",
                    ToolTip.GetTip(UpdateBtn) as string);
            };
        }

        _updateResetTimer.Stop();
        _updateResetTimer.Start();
    }

    private void SetUpdateState(UpdateButtonState state, string label, string? tip)
    {
        _updateState = state;

        var (glyph, fg, bg) = state switch
        {
            UpdateButtonState.UpdateAvailable => ("\uE896", "#FFFFFF", "#16A34A"),   // Download
            UpdateButtonState.Checking => ("\uE72C", "#9CA3AF", null),              // Refresh
            UpdateButtonState.UpToDate => ("\uE73E", "#16A34A", null),              // CheckMark
            UpdateButtonState.Problem => ("\uE72C", "#9CA3AF", null),
            _ => ("\uE72C", "#374151", null),
        };

        UpdateLabel.Text = label;
        UpdateLabel.Foreground = new SolidColorBrush(Color.Parse(fg));
        UpdateIcon.Text = glyph;
        UpdateIcon.Foreground = new SolidColorBrush(Color.Parse(fg));
        UpdateBtn.Background = bg is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(bg));
        UpdateBtn.IsEnabled = state != UpdateButtonState.Checking;
        UpdateBtn.Opacity = state == UpdateButtonState.Checking ? 0.6 : 1;

        ToolTip.SetTip(UpdateBtn, tip);
    }

    private void SetUpdateLabel(string label) => UpdateLabel.Text = label;

    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        var newTabPage = new NewTabPage();
        newTabPage.SmartChatSelected += OnConversationSelected;
        newTabPage.ToolsSelected += OnToolsSelected;
        newTabPage.SkillsSelected += OnSkillsSelected;
        newTabPage.McpSelected += OnMcpSelected;
        AddTab("新标签页", newTabPage);
    }

    /// <summary>新建页的「智能对话」卡片：把占位页替换为统一会话页。</summary>
    private void OnConversationSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SmartChatSelected -= OnConversationSelected;

            var index = _tabContents.IndexOf(newTabPage);
            if (index < 0) return;

            _tabContents[index] = new ConversationPage();
            _tabTitleBlocks[index].Text = "智能对话";

            if (index == GetSelectedIndex())
                TabContent.Content = _tabContents[index];
        }
    }

    private void OnToolsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.ToolsSelected -= OnToolsSelected;

            var index = _tabContents.IndexOf(newTabPage);
            if (index < 0) return;

            _tabContents[index] = new ToolsPage();
            _tabTitleBlocks[index].Text = "工具";

            if (index == GetSelectedIndex())
                TabContent.Content = _tabContents[index];
        }
    }

    private void OnSkillsSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SkillsSelected -= OnSkillsSelected;

            var index = _tabContents.IndexOf(newTabPage);
            if (index < 0) return;

            _tabContents[index] = new SkillsPage();
            _tabTitleBlocks[index].Text = "技能";

            if (index == GetSelectedIndex())
                TabContent.Content = _tabContents[index];
        }
    }

    private void OnMcpSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.McpSelected -= OnMcpSelected;

            var index = _tabContents.IndexOf(newTabPage);
            if (index < 0) return;

            _tabContents[index] = new McpPage();
            _tabTitleBlocks[index].Text = "MCP服务";

            if (index == GetSelectedIndex())
                TabContent.Content = _tabContents[index];
        }
    }

    /// <summary>
    /// 添加新标签页
    /// </summary>
    /// <param name="title"></param>
    /// <param name="content"></param>
    private void AddTab(string title, Control content)
    {
        var index = _tabHeaders.Count;
        var header = CreateTabHeader(title, index);
        _tabHeaders.Add(header);
        _tabContents.Add(content);

        TabHeaderPanel.Children.Insert(TabHeaderPanel.Children.Count - 1, header);
        SelectTab(index);
    }
    /// <summary>
    /// 创建标签页头部
    /// </summary>
    /// <param name="title"></param>
    /// <param name="index"></param>
    /// <returns></returns>
    private Border CreateTabHeader(string title, int index)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0),
            FontSize = 12,
        };
        _tabTitleBlocks.Add(titleBlock);

        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\u00d7",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, -1, 0, 0)
            },
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            Width = 0,
            Height = 16,
            CornerRadius = new Avalonia.CornerRadius(12),
            Padding = new Avalonia.Thickness(0),
            Margin = new Avalonia.Thickness(0),
            Tag = index
        };
        closeBtn.Click += OnTabCloseClick;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { titleBlock, closeBtn }
        };
        Grid.SetColumn(closeBtn, 1);

        var border = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Avalonia.Thickness(5),
            CornerRadius = new Avalonia.CornerRadius(10),
            Tag = index,
            Child = headerGrid,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            BorderThickness = new Avalonia.Thickness(0)
        };

        border.PointerEntered += (s, e) =>
        {
            if (index > 0)
            {
                closeBtn.Width = 16;
                closeBtn.Margin = new Avalonia.Thickness(6, 0, 6, 0);
                titleBlock.Margin = new Avalonia.Thickness(10, 0, 2, 0);
                closeBtn.Background = Brushes.Transparent;
                closeBtn.Foreground = Brushes.Gray;
            }

            if ((int)border.Tag != _selectedIndex)
            {
                border.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
            }
        };

        border.PointerExited += (s, e) =>
        {
            if (index > 0)
            {
                closeBtn.Width = 0;
                closeBtn.Margin = new Avalonia.Thickness(0);
                titleBlock.Margin = new Avalonia.Thickness(10, 0);
                closeBtn.Background = Brushes.Transparent;
                closeBtn.Foreground = Brushes.Gray;
            }

            if ((int)border.Tag != _selectedIndex)
            {
                border.Background = Brushes.Transparent;
            }
        };

        border.PointerPressed += OnTabHeaderClick;
        return border;
    }

    /// <summary>
    /// 关闭标签页
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
        {
            if (index == 0) return;
            RemoveTab(index);
            e.Handled = true;
        }
    }

    private void RemoveTab(int index)
    {
        _tabHeaders.RemoveAt(index);
        _tabContents.RemoveAt(index);
        _tabTitleBlocks.RemoveAt(index);
        TabHeaderPanel.Children.RemoveAt(index);

        for (int i = 0; i < _tabHeaders.Count; i++)
            _tabHeaders[i].Tag = i;

        if (_selectedIndex > index)
            _selectedIndex--;
        else if (_selectedIndex >= _tabContents.Count)
            _selectedIndex = _tabContents.Count - 1;

        SelectTab(_selectedIndex);
    }

    private void OnTabHeaderClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border header && header.Tag is int index)
            SelectTab(index);
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabHeaders.Count) return;

        for (int i = 0; i < _tabHeaders.Count; i++)
        {
            var header = _tabHeaders[i];
            if (i == index)
            {
                header.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
                header.BorderBrush = new SolidColorBrush(Color.Parse("#D9DDE3"));
                header.BorderThickness = new Avalonia.Thickness(1);
                header.BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0, OffsetY = 1, Blur = 2, Spread = 0,
                    Color = Color.FromArgb(30, 0, 0, 0)
                });
            }
            else
            {
                header.Background = Brushes.Transparent;
                header.BorderBrush = null;
                header.BorderThickness = new Avalonia.Thickness(0);
                header.BoxShadow = default;
            }
        }

        _selectedIndex = index;
        TabContent.Content = _tabContents[index];
    }

    private int GetSelectedIndex() => _selectedIndex;

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var index = _tabContents.FindIndex(c => c is SettingsPage);
        if (index >= 0)
        {
            SelectTab(index);
        }
        else
        {
            AddTab("设置", new SettingsPage());
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    /// <summary>最小化窗口。</summary>
    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>最大化 / 还原窗口（点一下在两种状态之间切换）。</summary>
    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Form_OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) this.BeginMoveDrag(e);
    }
}
