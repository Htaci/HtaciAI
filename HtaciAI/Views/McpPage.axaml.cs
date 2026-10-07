using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Mcp;
using HtaciAI.Controls;

namespace HtaciAI.Views;

/// <summary>
/// MCP 服务管理页：左侧服务列表，右侧「介绍 / 工具 / 配置 / 日志」四分类详情。
/// 分类标签是自绘的——项目里没有用 TabControl，沿用与主窗口标签栏一致的选中态观感。
/// </summary>
public partial class McpPage : UserControl
{
    private static readonly string[] TabNames = { "介绍", "工具", "配置", "日志" };

    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush SelectedBg = new SolidColorBrush(Color.Parse("#E8F0FA"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#546E7A"));

    private readonly List<(Border Tab, TextBlock Label)> _tabs = new();
    private readonly List<ServerRow> _serverRows = new();
    private readonly Action<McpLogEntry> _logHandler;

    private List<McpServerConfig> _servers = new();
    private McpServerConfig? _selected;
    private bool _isNew;
    private int _selectedTab;

    // 四个分类视图只建一次：切换标签时不重建，避免用户在「配置」里填了一半的内容被冲掉
    private Control? _introView;
    private Control? _toolsView;
    private Control? _configView;
    private Control? _logView;

    private TextBlock? _introBody;
    private StackPanel? _toolsHost;
    private TextBlock? _toolsStatus;

    private TextBox? _nameBox;
    private TextBox? _urlBox;
    private ComboBox? _transportCombo;
    private TextBox? _headersBox;
    private TextBox? _timeoutBox;
    private TextBlock? _configStatus;

    // stdio 专用
    private TextBox? _commandBox;
    private TextBox? _argsBox;
    private TextBox? _envBox;
    private TextBox? _cwdBox;

    /// <summary>字段宿主（含标题那一行）。显隐切的是一整行，所以要留着它们。</summary>
    private Control? _urlField;
    private Control? _headersField;
    private Control? _commandField;
    private Control? _argsField;
    private Control? _envField;
    private Control? _cwdField;

    private TextBlock? _logBody;
    private ScrollViewer? _logScroll;

    public McpPage()
    {
        InitializeComponent();

        BuildTabBar();
        AddServerButton.Click += (_, _) => StartNewServer();

        _logHandler = OnLogEntry;
        McpLog.EntryAdded += _logHandler;

        SelectTab(0);
        _ = InitializeAsync();
    }

    /// <summary>装载列表，并把已启动的服务连起来——列表上的绿点要有意义，就得真去连一次。</summary>
    private async Task InitializeAsync()
    {
        await ReloadAsync();

        var enabled = _servers.Where(s => s.Enabled).Select(s => s.Id).ToList();
        if (enabled.Count == 0) return;

        await McpConnectionManager.Instance.EnsureToolsAsync(enabled, CancellationToken.None);

        // 连完回来刷新状态点与当前标签页
        RefreshServerRowStates();
        RefreshHeaderActions();
        RefreshDetail();
    }

    // ---- 左侧列表 ----

    private async Task ReloadAsync()
    {
        try
        {
            _servers = await McpServerRepository.GetAllAsync();
        }
        catch
        {
            _servers = new List<McpServerConfig>();
        }

        // 尽量保持当前选中项；它被删了就落到第一个
        var keepId = _selected?.Id;
        _selected = _servers.FirstOrDefault(s => s.Id == keepId) ?? _servers.FirstOrDefault();
        _isNew = false;

        RefreshServerList();
        RefreshDetail();
    }

    private void RefreshServerList()
    {
        ServerList.Children.Clear();
        _serverRows.Clear();

        if (_servers.Count == 0 && !_isNew)
        {
            ServerList.Children.Add(new TextBlock
            {
                Text = "还没有 MCP 服务",
                FontSize = 12,
                Foreground = TextFaint,
                Margin = new Thickness(12, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var server in _servers)
            ServerList.Children.Add(CreateServerItem(server));
    }

    private Border CreateServerItem(McpServerConfig server)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.Name) ? "(未命名)" : server.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var subtitle = new TextBlock
        {
            // 带上「连到哪里」：同名服务好几个时，光看类型分不出谁是谁
            Text = $"{McpTransportInfo.Label(server.Transport)} · {McpTransportInfo.Describe(server)}",
            FontSize = 11,
            Foreground = TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 3, 0),
            IsVisible = false
        };

        var text = new StackPanel { Children = { title, subtitle } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        grid.Children.Add(dot);
        Grid.SetColumn(dot, 1);

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        ToolTip.SetTip(item, $"{server.Name}\n{DescribeStatus(StatusOf(server), server.Id)}");

        var row = new ServerRow(server, item, title, dot);
        item.PointerPressed += (_, e) =>
        {
            _selected = server;
            _isNew = false;
            RefreshServerList();
            RefreshDetail();
            e.Handled = true;
        };
        item.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(_selected, server)) item.Background = Surface;
        };
        item.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(_selected, server)) item.Background = Brushes.Transparent;
        };

        _serverRows.Add(row);
        return item;
    }

    private void RefreshServerRowStates()
    {
        foreach (var row in _serverRows)
        {
            var selected = ReferenceEquals(row.Server, _selected);
            row.Item.Background = selected ? SelectedBg : Brushes.Transparent;
            row.Title.Foreground = selected ? TextPrimary : TextMuted;

            // 未启动不显示点；已启动看连接结果——绿=正常，黄=连接异常
            var status = StatusOf(row.Server);
            row.Dot.IsVisible = status != ServerStatus.Stopped;

            var (color, _) = StatusColors(status);
            row.Dot.Background = new SolidColorBrush(color);
            row.Dot.BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 0, Blur = 4, Spread = 0,
                Color = Color.FromArgb(0x4D, color.R, color.G, color.B)
            });

            ToolTip.SetTip(row.Item, $"{row.Server.Name}\n{DescribeStatus(status, row.Server.Id)}");
        }
    }

    private ServerStatus StatusOf(McpServerConfig server)
    {
        if (!server.Enabled) return ServerStatus.Stopped;
        return McpConnectionManager.Instance.IsConnected(server.Id) ? ServerStatus.Running : ServerStatus.Failed;
    }

    private static (Color Dot, Color Text) StatusColors(ServerStatus status) => status switch
    {
        ServerStatus.Running => (Color.Parse("#16A34A"), Color.Parse("#15803D")),
        ServerStatus.Failed => (Color.Parse("#F59E0B"), Color.Parse("#B45309")),
        _ => (Color.Parse("#9CA3AF"), Color.Parse("#6B7280")),
    };

    private static string DescribeStatus(ServerStatus status, string serverId)
    {
        if (status == ServerStatus.Stopped) return "未启动";

        if (status == ServerStatus.Running)
        {
            var count = McpConnectionManager.Instance.GetTools(serverId)?.Count ?? 0;
            return $"已启动，连接正常（{count} 个工具）";
        }

        var error = McpConnectionManager.Instance.GetLastError(serverId);
        return string.IsNullOrWhiteSpace(error) ? "已启动，尚未连接" : $"已启动，连接异常：{error}";
    }

    // ---- 分类标签 ----

    private void BuildTabBar()
    {
        for (var i = 0; i < TabNames.Length; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = TabNames[index],
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            var tab = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 0),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = label
            };

            tab.PointerPressed += (_, e) =>
            {
                SelectTab(index);
                e.Handled = true;
            };
            tab.PointerEntered += (_, _) =>
            {
                if (index != _selectedTab) tab.Background = Surface;
            };
            tab.PointerExited += (_, _) =>
            {
                if (index != _selectedTab) tab.Background = Brushes.Transparent;
            };

            _tabs.Add((tab, label));
            TabBar.Children.Add(tab);
        }
    }

    private void SelectTab(int index)
    {
        _selectedTab = index;

        for (var i = 0; i < _tabs.Count; i++)
        {
            var (tab, label) = _tabs[i];
            var selected = i == index;

            tab.Background = selected ? Brushes.White : Brushes.Transparent;
            tab.BorderBrush = selected ? LineBrush : null;
            tab.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            label.Foreground = selected ? TextPrimary : TextMuted;
            label.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }

        RefreshDetail();
    }

    // ---- 右侧详情 ----

    private void RefreshDetail()
    {
        RefreshServerRowStates();

        if (_selected is null)
        {
            DetailTitle.Text = "MCP 服务";
            DetailDesc.Text = "点击左上角的 + 新增一个 MCP 服务";
            TabBar.IsVisible = false;
            HeaderActions.IsVisible = false;
            TabHost.Content = CreateEmptyState("还没有 MCP 服务", "新增后可以在这里配置地址、类型、请求头与超时时间。");
            return;
        }

        TabBar.IsVisible = true;
        DetailTitle.Text = string.IsNullOrWhiteSpace(_selected.Name) ? "(未命名)" : _selected.Name;
        DetailDesc.Text = _isNew
            ? "尚未保存"
            : $"{McpTransportInfo.Label(_selected.Transport)} · {McpTransportInfo.Describe(_selected)}";
        RefreshHeaderActions();

        TabHost.Content = _selectedTab switch
        {
            1 => _toolsView ??= CreateToolsView(),
            2 => _configView ??= CreateConfigView(),
            3 => _logView ??= CreateLogView(),
            _ => _introView ??= CreateIntroView(),
        };

        switch (_selectedTab)
        {
            case 0: RefreshIntro(); break;
            case 1: RefreshTools(); break;
            case 2: LoadConfigFields(); break;
            default: RefreshLog(); break;
        }
    }

    // ---- 启动 / 停止 ----

    /// <summary>标题右侧的状态胶囊 + 启动/停止按钮。</summary>
    private void RefreshHeaderActions()
    {
        HeaderActions.Children.Clear();

        if (_selected is null)
        {
            HeaderActions.IsVisible = false;
            return;
        }

        HeaderActions.IsVisible = true;

        if (_isNew)
        {
            HeaderActions.Children.Add(CreateStatusPill("未保存", ServerStatus.Stopped));
            return;
        }

        var status = StatusOf(_selected);
        HeaderActions.Children.Add(CreateStatusPill(DescribeStatus(status, _selected.Id), status));

        var (label, stop) = status == ServerStatus.Stopped ? ("启动", false) : ("停止", true);
        var button = stop ? CreateActionButton(label) : CreatePrimaryButton(label);
        button.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            if (stop) await StopServerAsync();
            else await StartServerAsync();
        };
        HeaderActions.Children.Add(button);
    }

    private static Control CreateStatusPill(string text, ServerStatus status)
    {
        var (dotColor, textColor) = StatusColors(status);

        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                dot,
                new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(textColor),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5),
            Background = new SolidColorBrush(Color.FromArgb(0x14, dotColor.R, dotColor.G, dotColor.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        ToolTip.SetTip(pill, text);
        return pill;
    }

    private async Task StartServerAsync()
    {
        if (_selected is null || _isNew) return;

        var id = _selected.Id;
        // 手动启动：跳过失败冷却立刻重试
        var ok = await McpConnectionManager.Instance.EnsureConnectedAsync(
            id, CancellationToken.None, autoStart: true, ignoreCooldown: true);

        // 启动标记与失败原因都可能变了，重新读一遍列表再刷新界面
        await ReloadAsync();
        if (!ok) McpLog.Warn(id, "启动未成功，详见日志");
    }

    private async Task StopServerAsync()
    {
        if (_selected is null || _isNew) return;

        await McpConnectionManager.Instance.StopAsync(_selected.Id);
        await ReloadAsync();
    }

    // ---- 介绍 ----

    private Control CreateIntroView()
    {
        _introBody = new TextBlock
        {
            FontSize = 13,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24
        };

        return CreateCard(_introBody);
    }

    private void RefreshIntro()
    {
        if (_introBody is null || _selected is null) return;

        var status = McpConnectionManager.Instance.IsConnected(_selected.Id) ? "已连接" : "未连接";
        var tools = McpConnectionManager.Instance.GetTools(_selected.Id)?.Count;

        // stdio 没有地址，改成显示命令 —— 否则这一行永远是空的
        var targetLabel = _selected.Transport == McpTransport.Stdio ? "命令" : "地址";

        _introBody.Text =
            $"名称：{_selected.Name}\n" +
            $"类型：{McpTransportInfo.Label(_selected.Transport)}\n" +
            $"{targetLabel}：{McpTransportInfo.Describe(_selected)}\n" +
            $"超时：{_selected.TimeoutSeconds} 秒\n" +
            $"状态：{status}" + (tools is null ? "" : $"（{tools} 个工具）");
    }

    // ---- 工具 ----

    private Control CreateToolsView()
    {
        _toolsStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        _toolsHost = new StackPanel { Spacing = 8 };

        var connect = CreateActionButton("连接 / 刷新");
        connect.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await ConnectAsync(force: true);
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(connect);
        body.Children.Add(_toolsStatus);
        body.Children.Add(_toolsHost);

        return CreateCard(new SmoothScrollViewer()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        });
    }

    private void RefreshTools()
    {
        if (_toolsHost is null || _toolsStatus is null || _selected is null) return;

        _toolsHost.Children.Clear();

        if (_isNew)
        {
            _toolsStatus.Text = "先保存配置，再连接并查看工具。";
            _toolsStatus.Foreground = TextFaint;
            return;
        }

        if (!McpConnectionManager.Instance.IsConnected(_selected.Id))
        {
            _toolsStatus.Text = "尚未连接。点击上方按钮连接；失败原因会记录在「日志」里。";
            _toolsStatus.Foreground = TextFaint;
            return;
        }

        var tools = McpConnectionManager.Instance.GetTools(_selected.Id) ?? new List<McpToolInfo>();
        _toolsStatus.Text = $"已连接，共 {tools.Count} 个工具。";
        _toolsStatus.Foreground = Accent;

        if (tools.Count == 0)
        {
            _toolsHost.Children.Add(CreateEmptyState("服务没有提供工具", "连接成功，但 tools/list 返回空。"));
            return;
        }

        foreach (var tool in tools)
            _toolsHost.Children.Add(CreateToolCard(tool));
    }

    private static Control CreateToolCard(McpToolInfo tool)
    {
        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = tool.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            FontFamily = new FontFamily("Consolas")
        });

        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tool.Description) ? "(无描述)" : tool.Description,
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap
        });

        var parameters = McpToolSchema.ParseParameters(tool.InputSchemaJson);
        if (parameters.Count > 0)
            body.Children.Add(CreateParameterTable(parameters));

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = body
        };
    }

    /// <summary>把 inputSchema 渲染成「参数名 / 类型徽标 / 说明」的表格。</summary>
    private static Control CreateParameterTable(IReadOnlyList<McpToolParameter> parameters)
    {
        var rows = new StackPanel();

        rows.Children.Add(new TextBlock
        {
            Text = "输入参数",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextMuted,
            Margin = new Thickness(0, 6, 0, 2)
        });

        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
                rows.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(0, 2) });

            rows.Children.Add(CreateParameterRow(parameters[i]));
        }

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#FCFCFD")),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 6, 0, 0),
            Child = rows
        };
    }

    private static Control CreateParameterRow(McpToolParameter parameter)
    {
        var name = new TextBlock
        {
            Text = parameter.Name,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Margin = new Thickness(0, 7)
        };
        row.Children.Add(name);

        if (parameter.Required)
        {
            var star = new TextBlock
            {
                Text = "*",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };
            row.Children.Add(star);
            Grid.SetColumn(star, 1);
        }

        var badge = CreateTypeBadge(parameter.Type);
        var description = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(parameter.Description) ? "" : parameter.Description,
            FontSize = 12,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(10, 0, 0, 0)
        };
        content.Children.Add(badge);
        content.Children.Add(description);
        Grid.SetColumn(description, 1);

        row.Children.Add(content);
        Grid.SetColumn(content, 2);
        return row;
    }

    private static Control CreateTypeBadge(string type)
    {
        var (background, foreground) = TypeColors(type);

        return new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = type,
                FontSize = 11,
                Foreground = new SolidColorBrush(foreground),
                FontFamily = new FontFamily("Consolas")
            }
        };
    }

    private static (Color Background, Color Foreground) TypeColors(string type)
    {
        var key = type.ToLowerInvariant();

        if (key.Contains("string")) return (Color.Parse("#DCFCE7"), Color.Parse("#15803D"));
        if (key.Contains("bool")) return (Color.Parse("#FEF3C7"), Color.Parse("#B45309"));
        if (key.Contains("int") || key.Contains("number") || key.Contains("float") || key.Contains("double"))
            return (Color.Parse("#DBEAFE"), Color.Parse("#1D4ED8"));
        if (key.Contains("array")) return (Color.Parse("#EDE9FE"), Color.Parse("#6D28D9"));
        if (key.Contains("object")) return (Color.Parse("#CCFBF1"), Color.Parse("#0F766E"));
        if (key.Contains("enum")) return (Color.Parse("#FCE7F3"), Color.Parse("#BE185D"));

        return (Color.Parse("#F1F3F5"), Color.Parse("#4B5563"));
    }

    private async Task ConnectAsync(bool force)
    {
        if (_selected is null || _isNew)
        {
            ShowConfigStatus("请先保存配置。", Danger);
            return;
        }

        if (_toolsStatus is not null)
        {
            _toolsStatus.Text = "连接中…";
            _toolsStatus.Foreground = TextFaint;
        }

        if (force) McpConnectionManager.Instance.Invalidate(_selected.Id);

        var id = _selected.Id;
        // 手动操作：跳过失败冷却立刻重试
        var ok = await McpConnectionManager.Instance.EnsureConnectedAsync(
            id, CancellationToken.None, autoStart: true, ignoreCooldown: true);

        RefreshDetail();

        if (!ok && force)
            McpLog.Warn(id, "连接未成功，详见上方记录");
    }

    // ---- 配置 ----

    private Control CreateConfigView()
    {
        _nameBox = CreateTextBox();
        _urlBox = CreateTextBox();
        _headersBox = CreateTextBox(multiline: true);
        _headersBox.PlaceholderText = "每行一条，例如：\nAuthorization=Bearer sk-pleasetestapikey";
        _timeoutBox = CreateTextBox();

        // stdio 专用的四项
        _commandBox = CreateTextBox();
        _argsBox = CreateTextBox(multiline: true);
        _argsBox.PlaceholderText = "一行一个参数，例如：\n-y\n@modelcontextprotocol/server-everything";
        _envBox = CreateTextBox(multiline: true);
        _envBox.PlaceholderText = "一行一条，例如：\nAPI_KEY=sk-xxx";
        _cwdBox = CreateTextBox();

        _transportCombo = new ComboBox
        {
            Width = 280,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            FontSize = 13,
            ItemsSource = McpTransportInfo.All.Select(t => new TransportOption(McpTransportInfo.Label(t), t)).ToList(),
            DisplayMemberBinding = new Binding(nameof(TransportOption.Label))
        };
        // 切换类型时立刻换掉用不上的字段：stdIO 要命令，另外两个要地址与请求头。
        // 四个视图是建一次缓存复用的（切标签不重建），所以只能改 IsVisible，不能靠重建。
        _transportCombo.SelectionChanged += (_, _) => ApplyTransportVisibility();

        _configStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false
        };

        var save = CreatePrimaryButton("保存");
        save.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await SaveAsync();
        };

        var test = CreateActionButton("保存并测试连接");
        test.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            if (await SaveAsync()) await ConnectAsync(force: true);
        };

        var remove = CreateActionButton("删除");
        remove.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await DeleteAsync();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0)
        };
        actions.Children.Add(save);
        actions.Children.Add(test);
        actions.Children.Add(remove);

        // 字段宿主单独存一份：显隐要整行连标题一起切
        _urlField = CreateField("地址 *", _urlBox);
        _headersField = CreateField("请求头", _headersBox);
        _commandField = CreateField("命令 *", _commandBox);
        _argsField = CreateField("参数", _argsBox);
        _envField = CreateField("环境变量", _envBox);
        _cwdField = CreateField("工作目录", _cwdBox);

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateField("名称 *", _nameBox),
                CreateField("类型 *", _transportCombo),
                _urlField,
                _headersField,
                _commandField,
                _argsField,
                _envField,
                _cwdField,
                CreateField("超时（秒）", _timeoutBox),
                actions,
                _configStatus
            }
        };

        ApplyTransportVisibility();

        return CreateCard(new SmoothScrollViewer()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        });
    }

    /// <summary>按当前选中的传输方式，把用不上的输入项整行藏掉。</summary>
    private void ApplyTransportVisibility()
    {
        var transport = SelectedTransport();
        var usesUrl = McpTransportInfo.UsesUrl(transport);

        if (_urlField is not null) _urlField.IsVisible = usesUrl;
        if (_headersField is not null) _headersField.IsVisible = usesUrl;
        if (_commandField is not null) _commandField.IsVisible = !usesUrl;
        if (_argsField is not null) _argsField.IsVisible = !usesUrl;
        if (_envField is not null) _envField.IsVisible = !usesUrl;
        if (_cwdField is not null) _cwdField.IsVisible = !usesUrl;
    }

    private McpTransport SelectedTransport()
        => (_transportCombo?.SelectedItem as TransportOption)?.Transport ?? McpTransport.StreamableHttp;

    private void LoadConfigFields()
    {
        if (_selected is null) return;

        if (_nameBox is not null) _nameBox.Text = _selected.Name;
        if (_urlBox is not null) _urlBox.Text = _selected.Url;
        if (_headersBox is not null) _headersBox.Text = _selected.HeadersRaw;
        if (_timeoutBox is not null) _timeoutBox.Text = _selected.TimeoutSeconds.ToString();
        if (_commandBox is not null) _commandBox.Text = _selected.Command;
        if (_argsBox is not null) _argsBox.Text = _selected.ArgumentsRaw;
        if (_envBox is not null) _envBox.Text = _selected.EnvRaw;
        if (_cwdBox is not null) _cwdBox.Text = _selected.WorkingDirectory;

        if (_transportCombo is not null)
        {
            var options = _transportCombo.ItemsSource!.Cast<TransportOption>().ToList();
            var index = options.FindIndex(o => o.Transport == _selected.Transport);
            _transportCombo.SelectedIndex = index < 0 ? 0 : index;
        }

        // 上面设 SelectedIndex 会触发 SelectionChanged，但那一次可能发生在还没读回配置之前，
        // 所以这里再显式来一遍，保证显隐与真正选中的类型一致。
        ApplyTransportVisibility();

        if (_configStatus is not null) _configStatus.IsVisible = false;
    }

    /// <summary>把界面上的值写回对象并落库。返回是否成功。</summary>
    private async Task<bool> SaveAsync()
    {
        if (_selected is null) return false;

        var name = _nameBox?.Text?.Trim() ?? "";
        var url = _urlBox?.Text?.Trim() ?? "";
        var command = _commandBox?.Text?.Trim() ?? "";
        var transport = SelectedTransport();

        if (name.Length == 0)
        {
            ShowConfigStatus("名称不能为空。", Danger);
            return false;
        }

        if (transport == McpTransport.Stdio)
        {
            if (command.Length == 0)
            {
                ShowConfigStatus("stdio 需要填写启动命令（例如 npx、uvx、python）。", Danger);
                return false;
            }
        }
        else if (url.Length == 0)
        {
            ShowConfigStatus("地址不能为空。", Danger);
            return false;
        }

        if (!int.TryParse(_timeoutBox?.Text?.Trim(), out var timeout) || timeout <= 0)
        {
            ShowConfigStatus("超时时间需要是大于 0 的整数（秒）。", Danger);
            return false;
        }

        _selected.Name = name;
        _selected.Url = url;
        _selected.Transport = transport;
        _selected.HeadersRaw = _headersBox?.Text ?? "";
        _selected.TimeoutSeconds = timeout;
        _selected.Command = command;
        _selected.ArgumentsRaw = _argsBox?.Text ?? "";
        _selected.EnvRaw = _envBox?.Text ?? "";
        _selected.WorkingDirectory = _cwdBox?.Text?.Trim() ?? "";

        try
        {
            if (_isNew)
            {
                await McpServerRepository.CreateAsync(_selected);
                _isNew = false;
            }
            else
            {
                await McpServerRepository.UpdateAsync(_selected);
                // 配置改了，旧连接与旧工具都不能再用
                McpConnectionManager.Instance.Invalidate(_selected.Id);
            }
        }
        catch (Exception ex)
        {
            ShowConfigStatus($"保存失败：{ex.Message}", Danger);
            return false;
        }

        await ReloadAsync();
        ShowConfigStatus("已保存。", Accent);
        return true;
    }

    private async Task DeleteAsync()
    {
        if (_selected is null) return;

        if (_isNew)
        {
            _selected = _servers.FirstOrDefault();
            _isNew = false;
            await ReloadAsync();
            return;
        }

        var id = _selected.Id;
        try
        {
            McpConnectionManager.Instance.Invalidate(id);
            await McpServerRepository.DeleteAsync(id);
        }
        catch (Exception ex)
        {
            ShowConfigStatus($"删除失败：{ex.Message}", Danger);
            return;
        }

        _selected = null;
        await ReloadAsync();
    }

    private void StartNewServer()
    {
        _selected = new McpServerConfig
        {
            Name = "新 MCP 服务",
            Transport = McpTransport.StreamableHttp,
            TimeoutSeconds = 60
        };
        _isNew = true;

        RefreshServerList();
        RefreshDetail();
        SelectTab(2);
    }

    private void ShowConfigStatus(string text, IBrush color)
    {
        if (_configStatus is null) return;
        _configStatus.Text = text;
        _configStatus.Foreground = color;
        _configStatus.IsVisible = true;
    }

    // ---- 日志 ----

    private Control CreateLogView()
    {
        _logBody = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };

        _logScroll = new SmoothScrollViewer()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _logBody
        };

        var clear = CreateActionButton("清空");
        clear.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_logBody is not null) _logBody.Text = "";
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(clear);
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            MinHeight = 240,
            Child = _logScroll
        });

        return CreateCard(body);
    }

    private void RefreshLog()
    {
        if (_logBody is null || _selected is null) return;

        var entries = McpLog.Snapshot(_selected.Id);
        _logBody.Text = entries.Count == 0
            ? "暂无日志。连接或调用后会实时显示在这里。"
            : string.Join('\n', entries.Select(FormatEntry));

        ScrollLogToEnd();
    }

    private void OnLogEntry(McpLogEntry entry)
    {
        // 日志可能来自后台线程；且用户可能正停在别的标签页或别的服务上
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedTab != 3 || _logBody is null || _selected is null) return;
            if (entry.ServerId != _selected.Id) return;

            var current = _logBody.Text;
            if (string.IsNullOrEmpty(current) || current.StartsWith("暂无日志", StringComparison.Ordinal))
                _logBody.Text = FormatEntry(entry);
            else
                _logBody.Text = current + "\n" + FormatEntry(entry);

            ScrollLogToEnd();
        });
    }

    private void ScrollLogToEnd() => Dispatcher.UIThread.Post(() => _logScroll?.ScrollToEnd());

    private static string FormatEntry(McpLogEntry entry)
        => $"[{entry.At:HH:mm:ss}] {entry.Message}";

    // ---- 通用小控件 ----

    private static Border CreateCard(Control body) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = Brushes.White,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(20, 16),
        Child = body
    };

    private static Control CreateEmptyState(string title, string description) => new StackPanel
    {
        Spacing = 8,
        Margin = new Thickness(0, 40, 0, 0),
        Children =
        {
            new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        }
    };

    private static TextBox CreateTextBox(bool multiline = false) => new()
    {
        Width = 420,
        Height = multiline ? 110 : 34,
        CornerRadius = new CornerRadius(6),
        FontSize = 13,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        Padding = multiline ? new Thickness(10, 8) : new Thickness(10, 0)
    };

    private static Control CreateField(string label, Control field)
    {
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = TextMuted,
            Margin = new Thickness(0, 0, 0, 6)
        };

        field.HorizontalAlignment = HorizontalAlignment.Left;

        return new StackPanel { Children = { caption, field } };
    }

    private static Border CreateActionButton(string text)
    {
        var button = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.PointerEntered += (_, _) => button.Background = Surface;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static Border CreatePrimaryButton(string text)
    {
        var button = new Border
        {
            Background = Primary,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.PointerEntered += (_, _) => button.Opacity = 0.88;
        button.PointerExited += (_, _) => button.Opacity = 1;
        return button;
    }

    private sealed record TransportOption(string Label, McpTransport Transport);

    /// <summary>服务在列表里的三态。未启动不画点，其余两态用颜色区分。</summary>
    private enum ServerStatus
    {
        Stopped,
        Running,
        Failed,
    }

    private sealed class ServerRow(McpServerConfig server, Border item, TextBlock title, Border dot)
    {
        public McpServerConfig Server { get; } = server;
        public Border Item { get; } = item;
        public TextBlock Title { get; } = title;
        public Border Dot { get; } = dot;
    }
}
