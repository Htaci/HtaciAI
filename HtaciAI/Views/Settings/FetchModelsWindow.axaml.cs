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
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 「获取模型」对话框：向服务商的 OpenAI 兼容 <c>GET /models</c> 拉一份可用模型列表，
/// 点某一行的加号就把它加进该服务商（配置取 <see cref="BuiltinModelConfigs"/> 的默认值）。
///
/// 加号是<b>立即入库</b>而不是「勾选后统一保存」：这个窗体的用途是快速批量补全，
/// 点完即生效最省事，也省掉一个「保存」按钮。已经在本服务商里的模型直接标成「已添加」并置灰，
/// 避免重复添加。
///
/// 窗外（详情页）靠 <see cref="AddedCount"/> 判断要不要刷新 —— 一个都没加就不必重绘表格。
/// </summary>
public partial class FetchModelsWindow : Window
{
    private readonly AiProvider _provider;
    private readonly HashSet<string> _added;
    private readonly HashSet<string> _existing;

    /// <summary>本次新加进服务商的模型数（父页面据此决定是否刷新）。</summary>
    public int AddedCount { get; private set; }

    /// <summary>已拉回的全部模型 id（过滤前）。</summary>
    private List<string> _allIds = new();

    /// <summary>正在加载 / 已失败时不让过滤框反复触发重绘。</summary>
    private bool _busy;

    /// <summary>供 XAML 加载器使用（实际均由带参构造创建）。</summary>
    public FetchModelsWindow() : this(new AiProvider(), Array.Empty<string>()) { }

    public FetchModelsWindow(AiProvider provider, IEnumerable<string> existingCallIds)
    {
        InitializeComponent();
        _provider = provider;
        _existing = new HashSet<string>(existingCallIds, StringComparer.Ordinal);
        _added = new HashSet<string>(StringComparer.Ordinal);

        Title = "获取模型";
        SubtitleText.Text = $"{provider.Name} · {OpenAiExClient.ModelsUrl(provider.Endpoint)}";
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = LoadAsync();
    }

    // ---- 拉取 ----

    private async Task LoadAsync()
    {
        _busy = true;
        FilterBox.IsEnabled = false;
        CountText.Text = "";
        StatusText.Text = "正在获取模型列表…";
        StatusText.Foreground = new SolidColorBrush(Color.Parse("#6B7280"));

        ListPanel.Children.Clear();
        ListPanel.Children.Add(new TextBlock
        {
            Text = "正在获取…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var result = await ModelDiscovery.FetchAsync(_provider);

        _busy = false;
        FilterBox.IsEnabled = true;

        if (!result.Ok)
        {
            _allIds = new List<string>();
            ListPanel.Children.Clear();
            ListPanel.Children.Add(BuildErrorPanel(result.Error ?? "获取失败"));

            StatusText.Text = "获取失败";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
            return;
        }

        _allIds = result.ModelIds;
        StatusText.Text = $"共获取到 {_allIds.Count} 个模型，点击加号添加。";
        StatusText.Foreground = new SolidColorBrush(Color.Parse("#6B7280"));
        RenderList();
    }

    private Control BuildErrorPanel(string message)
    {
        var retry = new Button
        {
            Content = new TextBlock { Text = "重试", FontSize = 13 },
            Height = 30,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        retry.Click += (_, _) => _ = LoadAsync();

        return new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
                    TextWrapping = TextWrapping.Wrap,
                },
                retry,
            },
        };
    }

    // ---- 列表 ----

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_busy) return;
        RenderList();
    }

    private void RenderList()
    {
        ListPanel.Children.Clear();

        var keyword = FilterBox.Text?.Trim();
        var ids = string.IsNullOrEmpty(keyword)
            ? _allIds
            : _allIds.Where(id => id.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        CountText.Text = string.IsNullOrEmpty(keyword)
            ? $"{_allIds.Count} 个"
            : $"{ids.Count} / {_allIds.Count} 个";

        if (ids.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = _allIds.Count == 0 ? "服务端没有返回任何模型。" : "没有匹配的模型。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        foreach (var id in ids)
            ListPanel.Children.Add(CreateRow(id));
    }

    private Control CreateRow(string callId)
    {
        var text = new TextBlock
        {
            Text = callId,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var action = new Button
        {
            Height = 26,
            Width = 30,
            MinWidth = 0,       // Fluent 的 Button 自带 MinWidth，不清掉就把 30 顶回去了
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        Grid.SetColumn(action, 1);

        // 已在服务商里、或本次刚加过：一律显示成不可点的「已添加」
        if (_existing.Contains(callId) || _added.Contains(callId))
            MarkAdded(action, alreadyPresent: true);
        else
        {
            ApplyAddStyle(action);
            ToolTip.SetTip(action, "添加到该服务商");
            action.Click += async (_, _) => await AddAsync(callId, action);
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 1),
        };
        row.Children.Add(new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Child = text,
        });
        row.Children.Add(action);
        return row;
    }

    /// <summary>加号按钮的样子（可点状态）。</summary>
    private static void ApplyAddStyle(Button action)
    {
        action.Background = new SolidColorBrush(Color.Parse("#EEF4FB"));
        action.Cursor = new Cursor(StandardCursorType.Hand);
        // 与 MarkAdded 对应：把「已添加」撑开的宽度收回到加号的固定宽度
        action.Width = 30;
        action.Padding = new Thickness(0);
        action.Content = new TextBlock
        {
            Text = "\uF10A",              // Fluent System Icons：add
            FontFamily = AppFonts.Icons,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#2F6FB5")),
        };
    }

    private static void MarkAdded(Button action, bool alreadyPresent)
    {
        action.IsEnabled = false;
        action.Background = Brushes.Transparent;
        action.Cursor = new Cursor(StandardCursorType.Arrow);

        // 加号态是固定 30 宽，而「已添加」三个字放不下 —— 放开宽度让它按内容撑开，
        // 否则会被裁成「已添」。
        action.Width = double.NaN;
        action.Padding = new Thickness(8, 0);

        action.Content = new TextBlock
        {
            Text = "已添加",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
        };
        if (alreadyPresent) ToolTip.SetTip(action, "该模型已在这个服务商里");
    }

    private async Task AddAsync(string callId, Button action)
    {
        // 先置灰，避免连点两次塞进两条重复记录
        MarkAdded(action, alreadyPresent: false);

        try
        {
            await ModelRepository.CreateAsync(BuiltinModelConfigs.ForCallId(_provider.Id, callId));
            _added.Add(callId);
            AddedCount++;

            StatusText.Text = $"已添加 {AddedCount} 个模型。";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#16A34A"));
        }
        catch (Exception ex)
        {
            // 入库失败就恢复成可点，让用户能重试
            action.IsEnabled = true;
            ApplyAddStyle(action);

            StatusText.Text = $"添加失败：{ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
