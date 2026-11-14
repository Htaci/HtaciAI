using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Providers;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 添加服务商：左边一排预设（KtariFlow、DeepSeek、OpenAI…），右边是选中后的配置 ——
/// 端点、密钥、「获取模型」，最后点「添加」落库。
///
/// 流程刻意做成一步：官方服务商的地址与协议是常量，让用户手打既慢又容易错；
/// 「获取模型」就地拉一次列表并全部带上，加完就能直接对话。
/// 完全自定义的需求仍留有入口（右上角），那边走的是「建空记录 + 进详情页」的老路。
/// </summary>
public partial class PresetProviderWindow : Window
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush TextAccent = new SolidColorBrush(Color.Parse("#546E7A"));
    private static readonly IBrush TextDanger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush TextOk = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush InputBorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));
    private static readonly IBrush RowSelectedBrush = new SolidColorBrush(Color.Parse("#EEF4FB"));
    private static readonly IBrush RowHoverBrush = new SolidColorBrush(Color.Parse("#F5F7F9"));

    /// <summary>预设 key → 左侧那一行，切换选中时用来改外观。</summary>
    private readonly Dictionary<string, Border> _rows = new(StringComparer.Ordinal);

    private PresetProvider? _selected;

    /// <summary>用户选中的默认端点下标；换服务商时归零。</summary>
    private int _defaultEndpoint;

    /// <summary>「获取模型」拿回来的 id；null = 还没获取过或获取失败（照样允许只加服务商）。</summary>
    private List<string>? _fetched;

    private TextBox? _keyBox;
    private TextBlock? _fetchStatus;
    private StackPanel? _endpointPanel;

    /// <summary>是否真的加了一条服务商（调用方据此刷新列表）。</summary>
    public bool Added { get; private set; }

    /// <summary>用户点了「自定义服务商」——调用方走原来的新建流程。</summary>
    public bool CustomRequested { get; private set; }

    /// <summary>供 XAML 加载器使用（实际由 <see cref="PresetProviderWindow()"/> 创建）。</summary>
    public PresetProviderWindow()
    {
        InitializeComponent();

        // 密钥框失焦即收起高亮：点到卡片 / 空白处时不该一直亮着边框
        TextBoxFocusCleanup.Attach(this);

        BuildList();
        ShowPlaceholder();
    }

    // ============================================================
    // 左侧：预设列表
    // ============================================================

    private void BuildList()
    {
        foreach (var preset in PresetProviders.All)
        {
            var row = CreateRow(preset);
            _rows[preset.Key] = row;
            PresetList.Children.Add(row);
        }
    }

    private Border CreateRow(PresetProvider preset)
    {
        var host = HostOf(preset.Endpoints[0].Endpoint);

        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock
                {
                    Text = preset.Name,
                    FontSize = 13,
                    Foreground = TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = host,
                    FontSize = 11,
                    Foreground = TextFaint,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(ProviderAvatar.Create(preset.Name, preset.Icon, 26));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var row = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(8),
            // 未选中也留一圈透明描边：选中时只换颜色，行高不会跳。
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        row.PointerEntered += (_, _) =>
        {
            if (_selected?.Key != preset.Key) row.Background = RowHoverBrush;
        };
        row.PointerExited += (_, _) =>
        {
            if (_selected?.Key != preset.Key) row.Background = Brushes.Transparent;
        };
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Select(preset);
        };

        ToolTip.SetTip(row, preset.Description ?? preset.Name);
        return row;
    }

    /// <summary>列表里显示的主机名：完整地址太长，挤掉了反而看不出是哪家。</summary>
    private static string HostOf(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : endpoint;

    private void Select(PresetProvider preset)
    {
        _selected = preset;
        _defaultEndpoint = 0;
        _fetched = null;

        foreach (var (key, row) in _rows)
        {
            var active = key == preset.Key;
            row.Background = active ? RowSelectedBrush : Brushes.Transparent;
            row.BorderBrush = active ? new SolidColorBrush(Color.Parse("#BFD3E6")) : Brushes.Transparent;
        }

        AddButton.IsEnabled = true;
        SetStatus("");
        BuildDetail(preset);
    }

    // ============================================================
    // 右侧：选中后的配置
    // ============================================================

    private void ShowPlaceholder()
    {
        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "从左侧选择一家服务商",
            FontSize = 13,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0),
        });
    }

    private void BuildDetail(PresetProvider preset)
    {
        DetailPanel.Children.Clear();

        // ---- 头部：头像 + 名称 + 说明 ----
        var info = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
        };
        info.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontSize = 16,
            FontWeight = FontWeight.DemiBold,
            Foreground = TextPrimary,
        });

        if (!string.IsNullOrWhiteSpace(preset.Description))
            info.Children.Add(new TextBlock
            {
                Text = preset.Description,
                FontSize = 12,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
            });

        if (preset.ConsoleUrl is { } url)
            info.Children.Add(CreateLink("去控制台获取密钥 ↗", url));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(ProviderAvatar.Create(preset.Name, preset.Icon, 44));
        Grid.SetColumn(info, 1);
        header.Children.Add(info);
        DetailPanel.Children.Add(header);

        // ---- API 地址 ----
        DetailPanel.Children.Add(CreateSectionLabel("API 地址",
            preset.Endpoints.Count > 1 ? "点击「设为默认」切换请求时默认使用的协议" : null));
        _endpointPanel = new StackPanel { Spacing = 6 };
        DetailPanel.Children.Add(_endpointPanel);
        RefreshEndpoints();

        // ---- API 密钥 ----
        DetailPanel.Children.Add(CreateSectionLabel("API 密钥"));
        _keyBox = new TextBox
        {
            Height = 34,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            FontSize = 12,
            // 正文与占位文字都上下居中（见 App.axaml 里的 .center-text）
            Classes = { "center-text" },
            Background = new SolidColorBrush(Color.Parse("#80FFFFFF")),
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            // 本地服务默认不需要密钥，但框照样可填：LM Studio / Ollama 加上密钥后
            // 才能让局域网里的其他人访问，禁用它等于把这条路堵死。
            PlaceholderText = preset.RequiresKey ? "sk-..." : "本地服务通常不需要密钥（开放给局域网时再填）",
        };
        // 换了密钥，之前那次「获取模型」的结果就不再可信，让它重拉一次
        _keyBox.TextChanged += (_, _) => _fetched = null;
        DetailPanel.Children.Add(_keyBox);

        // ---- 模型 ----
        DetailPanel.Children.Add(CreateSectionLabel("模型", "可选：获取可用模型列表"));
        var fetchRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        fetchRow.Children.Add(CreateOutlineButton("获取模型", () => _ = FetchModelsAsync()));
        _fetchStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(_fetchStatus, 1);
        fetchRow.Children.Add(_fetchStatus);
        DetailPanel.Children.Add(fetchRow);

        if (!preset.RequiresKey)
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "记得先把本地推理服务跑起来，否则添加后会连不上。",
                FontSize = 11,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
    }

    /// <summary>端点行：协议胶囊 + 地址；多端点时可点切换默认。</summary>
    private void RefreshEndpoints()
    {
        if (_endpointPanel is null || _selected is not { } preset) return;

        _endpointPanel.Children.Clear();

        for (var i = 0; i < preset.Endpoints.Count; i++)
        {
            var index = i;
            var endpoint = preset.Endpoints[i];
            var isDefault = i == _defaultEndpoint;

            var text = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = ProtocolLabel(endpoint.Protocol),
                        FontSize = 11,
                        Foreground = TextMuted,
                    },
                    new TextBlock
                    {
                        Text = endpoint.Endpoint,
                        FontSize = 12,
                        Foreground = TextPrimary,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(text);
            var badge = CreateDefaultBadge(isDefault);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);

            var row = new Border
            {
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = isDefault ? InputBorderBrush : LineBrush,
                Background = Brushes.Transparent,
                Child = grid,
            };

            // 单端点时不需要「切换默认」，点它也没意义，光标就别装成可点的样子
            if (preset.Endpoints.Count > 1 && !isDefault)
            {
                row.Cursor = new Cursor(StandardCursorType.Hand);
                row.PointerEntered += (_, _) => row.Background = RowHoverBrush;
                row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
                row.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _defaultEndpoint = index;
                    _fetched = null;   // 换了端点，模型列表得重拉
                    RefreshEndpoints();
                };
            }

            _endpointPanel.Children.Add(row);
        }
    }

    private static Control CreateDefaultBadge(bool isDefault) => new Border
    {
        Background = isDefault ? new SolidColorBrush(Color.Parse("#F0F1F3")) : Brushes.Transparent,
        BorderBrush = isDefault ? Brushes.Transparent : InputBorderBrush,
        BorderThickness = new Thickness(isDefault ? 0 : 1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = isDefault ? "默认" : "设为默认",
            FontSize = 11,
            Foreground = isDefault ? TextMuted : new SolidColorBrush(Color.Parse("#374151")),
        },
    };

    // ============================================================
    // 动作
    // ============================================================

    private async Task FetchModelsAsync()
    {
        if (_selected is not { } preset || _fetchStatus is null) return;

        var key = _keyBox?.Text?.Trim();

        // 需要密钥的服务商，空着就别发请求：那种请求必然 401，报「获取失败：服务端返回 401」
        // 远不如直接说清楚缺什么。本地服务（LM Studio / Ollama）本来就不需要密钥，不拦。
        if (preset.RequiresKey && string.IsNullOrWhiteSpace(key))
        {
            _fetched = null;
            _fetchStatus.Text = "获取模型请求需填写 API Key。";
            _fetchStatus.Foreground = TextDanger;
            _keyBox?.Focus();
            return;
        }

        _fetchStatus.Text = "正在获取模型列表…";
        _fetchStatus.Foreground = TextMuted;

        var result = await ModelDiscovery.FetchAsync(preset.Endpoints[_defaultEndpoint].Endpoint, key);

        if (!result.Ok)
        {
            _fetched = null;
            _fetchStatus.Text = $"获取失败：{result.Error}";
            _fetchStatus.Foreground = TextDanger;
            return;
        }

        _fetched = result.ModelIds;
        _fetchStatus.Text = _fetched.Count == 0
            ? "服务端没有返回模型，可以先添加服务商，之后在详情页里同步。"
            : $"已获取 {_fetched.Count} 个模型，添加时一并导入：{Preview(_fetched)}";
        _fetchStatus.Foreground = _fetched.Count == 0 ? TextMuted : TextOk;
    }

    /// <summary>前三个 id + 「等」，够确认拉的是这家，又不会把界面撑爆。</summary>
    private static string Preview(List<string> ids)
        => ids.Count <= 3 ? string.Join("、", ids) : string.Join("、", ids.Take(3)) + " 等";

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } preset) return;

        // 密钥可以为空：本地服务不需要，官方服务商也允许先建好、之后在详情页里再填。
        // 服务商名不能重复 —— 重名之后卡片、模型归属、选择器里全是两个一样的名字，没法分辨。
        var key = _keyBox?.Text?.Trim();
        AddButton.IsEnabled = false;
        SetStatus("正在添加…");

        var provider = preset.ToProvider(key, _defaultEndpoint);

        try
        {
            // 重名会让卡片、模型归属和选择器里出现两个一模一样的服务商，事后没法分辨。
            // 放在 try 里：查重失败（数据库打不开等）与创建失败一样，都只是「这次没加成」。
            var existing = await ProviderRepository.GetAllAsync();
            if (existing.Any(p => string.Equals(p.Name.Trim(), preset.Name, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"已存在同名服务商「{preset.Name}」，如需再加一个请先改名。", error: true);
                AddButton.IsEnabled = true;
                return;
            }

            await ProviderRepository.CreateAsync(provider);

            var added = 0;
            if (_fetched is { Count: > 0 })
            {
                // 服务端偶尔会在同一份列表里重复给同一个 id，入库前先去重，
                // 否则第二条会撞唯一索引，把整次「添加」拖失败。
                foreach (var callId in _fetched.Distinct(StringComparer.Ordinal))
                {
                    await ModelRepository.CreateAsync(BuiltinModelConfigs.ForCallId(provider.Id, callId));
                    added++;
                }
            }

            Added = true;
            SetStatus(added == 0 ? $"已添加「{preset.Name}」。" : $"已添加「{preset.Name}」与 {added} 个模型。");
            Close();
        }
        catch (Exception ex)
        {
            // 建服务商与导模型不在一个事务里：半途失败时把服务商留下来反而是好事 ——
            // 用户能进详情页点「同步模型」重试，不必从头填一遍密钥。
            SetStatus($"添加失败：{ex.Message}", error: true);
            AddButton.IsEnabled = true;
        }
    }

    private void OnCustomClick(object? sender, RoutedEventArgs e)
    {
        CustomRequested = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? TextDanger : TextMuted;
    }

    // ============================================================
    // 小控件
    // ============================================================

    private static Control CreateSectionLabel(string text, string? hint = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
        };
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (hint is not null)
            row.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = TextFaint,
                VerticalAlignment = VerticalAlignment.Center,
            });

        return row;
    }

    private Control CreateOutlineButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, Foreground = TextAccent },
            Background = Brushes.Transparent,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            Height = 32,
            Padding = new Thickness(14, 0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>看起来像链接的按钮：用系统浏览器打开，失败就静默 —— 用户自己也能去搜。</summary>
    private static Control CreateLink(string text, string url)
    {
        var link = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TextAccent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                // 没有默认浏览器 / 被策略拦下：不该因此打断添加流程
            }
        };
        return link;
    }

    private static string ProtocolLabel(ModelProtocol protocol) => protocol switch
    {
        ModelProtocol.OpenAIResponses => "OpenAI Responses 协议",
        ModelProtocol.LMStudio => "LM Studio 协议",
        ModelProtocol.Anthropic => "Anthropic 协议",
        _ => "OpenAI 兼容协议",
    };
}
