using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Updates;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 常规设置页：界面行为类设置。目前是「会话窗口缓存保留时长」与「更新检查地址」两项。
/// 与「默认配置」页一样<b>改动即存</b>，写回 <see cref="AppPaths.Settings"/>。
/// </summary>
public partial class GeneralSettingsPage : UserControl
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#16A34A"));

    private readonly AppSettingsData _data;
    private readonly List<OptionRow> _cacheRows = new();
    private readonly List<OptionRow> _thresholdRows = new();

    /// <summary>装载数据源期间置位，避免「程序改控件」被当成「用户改设置」而反复写盘。</summary>
    private bool _suppressSave = true;

    private TextBlock? _updateStatus;
    private TextBox? _updateUrlBox;
    private DispatcherTimer? _urlDebounce;

    public GeneralSettingsPage()
    {
        InitializeComponent();

        _data = AppSettingsStore.Current;
        Build();
        _suppressSave = false;
    }

    private void Build()
    {
        ContentPanel.Children.Add(CreateSessionCacheCard());
        ContentPanel.Children.Add(CreateToolRoundsCard());
        ContentPanel.Children.Add(CreateCompressionCard());
        ContentPanel.Children.Add(CreateUpdateCard());
        RefreshCacheRows();
        RefreshThresholdRows();
    }

    // ---- 卡片 2：最大工具循环次数 ----

    private TextBox? _toolRoundsBox;
    private CheckBox? _toolRoundsUnlimited;

    /// <summary>SyncToolRoundsInputs 期间置位，避免「程序改文本框」被当成「用户输入」。</summary>
    private bool _syncingToolRounds;

    private Control CreateToolRoundsCard()
    {
        _toolRoundsBox = new TextBox
        {
            Width = 130,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            FontSize = 13,
            Padding = new Thickness(10, 0),
            PlaceholderText = "100",
            Text = _data.MaxToolRounds > 0 ? _data.MaxToolRounds.ToString() : "",
        };
        _toolRoundsBox.TextChanged += (_, _) => OnToolRoundsInput();

        var suffix = new TextBlock
        {
            Text = "轮",
            FontSize = 13,
            Foreground = TextMuted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        _toolRoundsUnlimited = new CheckBox
        {
            Content = "不限制",
            FontSize = 13,
            IsChecked = _data.MaxToolRounds <= 0,
        };
        _toolRoundsUnlimited.IsCheckedChanged += (_, _) => OnToolRoundsInput();

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { _toolRoundsBox, suffix, _toolRoundsUnlimited },
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                row,
                new TextBlock
                {
                    Text = "一整轮回复里最多允许模型调用多少次工具。到上限就会停在那里，" +
                           "模型没机会给出最终答复 —— 多文件改动、多步排查这类任务很容易超过几十轮，" +
                           "所以默认给到 100。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "「不限制」有风险：万一模型陷入反复调用同一个工具的循环，" +
                           "这一轮会一直跑下去，只能靠「停止」按钮中断。",
                    FontSize = 12,
                    Foreground = TextFaint,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SyncToolRoundsInputs();
        return CreateCard("最大工具循环次数", "超出后本轮对话会被强制结束。改完立即生效，不用重开会话。", body);
    }

    /// <summary>数值框与「不限制」的联动；两者任一改动都会走到这里。</summary>
    private void OnToolRoundsInput()
    {
        if (_suppressSave || _syncingToolRounds) return;

        var unlimited = _toolRoundsUnlimited?.IsChecked == true;
        if (unlimited)
        {
            _data.MaxToolRounds = -1;
        }
        else
        {
            // 解析失败/为空都回落到默认值，而不是悄悄写成 0（0 会被当成「不限」）
            var text = _toolRoundsBox?.Text?.Trim() ?? "";
            _data.MaxToolRounds = int.TryParse(text, out var v) && v > 0 ? v : 100;
        }

        SyncToolRoundsInputs();
        Persist();
    }

    /// <summary>
    /// 把「不限制」状态反映到数值框的可编辑性与显示上。
    /// 会改 Text，从而再次触发 TextChanged，所以用 <see cref="_syncingToolRounds"/> 挡住重入。
    /// </summary>
    private void SyncToolRoundsInputs()
    {
        if (_toolRoundsBox is null || _toolRoundsUnlimited is null) return;

        _syncingToolRounds = true;
        try
        {
            var unlimited = _data.MaxToolRounds <= 0;
            _toolRoundsBox.IsEnabled = !unlimited;
            _toolRoundsBox.Opacity = unlimited ? 0.45 : 1;
            _toolRoundsBox.Text = unlimited ? "" : _data.MaxToolRounds.ToString();
        }
        finally
        {
            _syncingToolRounds = false;
        }
    }

    // ---- 卡片 1：会话窗口缓存 ----

    private Control CreateSessionCacheCard()
    {
        // 一档 = 一分钟数；null 表示「一直保留」
        var options = new (int? Minutes, string Title, string Description)[]
        {
            (1, "1 分钟", "切走很快就释放，适合会话很多、在意内存占用时"),
            (5, "5 分钟", "来回对照两个会话时够用"),
            (10, "10 分钟（默认）", "默认值：临时切走再切回来不会重新加载"),
            (30, "30 分钟", "长时间在几个会话之间来回切换"),
            (null, "一直保留", "永不按时间释放。打开过的会话会一直占着内存，会话很多时不建议"),
        };

        var body = new StackPanel { Spacing = 2 };
        foreach (var (minutes, title, description) in options)
        {
            var row = CreateOptionRow(title, description);
            row.Row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _data.SessionCacheKeepForever = minutes is null;
                if (minutes is not null) _data.SessionCacheMinutes = minutes.Value;
                RefreshCacheRows();
                Persist();
            };
            _cacheRows.Add(row with { Value = minutes });
            body.Children.Add(row.Row);
        }

        return CreateCard(
            "会话窗口缓存",
            "切换会话时不再丢弃上一个窗口：保留期内切回来直接复用，不会重新读取历史、重建消息列表。",
            body);
    }

    private void RefreshCacheRows()
    {
        foreach (var row in _cacheRows)
        {
            var selected = _data.SessionCacheKeepForever
                ? row.Value is null
                : row.Value == _data.SessionCacheMinutes;

            row.Dot.IsVisible = selected;
            row.Row.Background = selected ? Surface : Brushes.Transparent;
        }
    }

    // ---- 卡片 2：上下文压缩 ----

    private Control CreateCompressionCard()
    {
        var auto = CreateSwitch(
            "上下文占用超过阈值时自动压缩",
            "发请求前如果上下文用量超过下面的阈值，先让模型把此前的对话总结成一段摘要，再带着摘要发这一轮。" +
            "会多花一次模型请求，但能避免超出窗口。关掉它也不影响手动压缩。",
            _data.AutoCompressContext,
            value =>
            {
                _data.AutoCompressContext = value;
                RefreshThresholdRows();
                Persist();
            });

        var options = new (int Percent, string Title, string Description)[]
        {
            (60, "60%", "压得早，几乎不会碰到窗口上限，代价是摘要生成得更频繁"),
            (70, "70%", "偏保守"),
            (80, "80%（默认）", "默认值：留出约五分之一的余量"),
            (90, "90%", "压得晚，摘要次数最少，但单轮对话很长时可能一次就顶到上限"),
        };

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(auto);
        body.Children.Add(new Border { Height = 6 });

        foreach (var (percent, title, description) in options)
        {
            var row = CreateOptionRow(title, description);
            row.Row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _data.AutoCompressThresholdPercent = percent;
                RefreshThresholdRows();
                Persist();
            };
            _thresholdRows.Add(row with { Value = percent });
            body.Children.Add(row.Row);
        }

        return CreateCard(
            "上下文压缩",
            "把此前的对话压成一段摘要，之后的请求只带摘要 + 摘要之后的新消息（原文仍保留在会话里，不会丢）。" +
            "输入框左下「更多功能」菜单里也有一个手动压缩。",
            body);
    }

    private void RefreshThresholdRows()
    {
        foreach (var row in _thresholdRows)
        {
            // 关了自动压缩，阈值就没有意义了：整组置灰，但仍可点（改完再打开就是新阈值）
            var selected = row.Value == _data.AutoCompressThresholdPercent;
            row.Dot.IsVisible = selected;
            row.Row.Opacity = _data.AutoCompressContext ? 1 : 0.45;
            row.Row.Background = selected ? Surface : Brushes.Transparent;
        }
    }

    // ---- 卡片 3：更新检查 ----

    private Control CreateUpdateCard()
    {
        _updateUrlBox = new TextBox
        {
            Height = 34,
            CornerRadius = new CornerRadius(6),
            FontSize = 13,
            Padding = new Thickness(10, 0),
            PlaceholderText = "https://…/latest.json（留空 = 不检查更新）",
            Text = _data.UpdateManifestUrl ?? "",
        };
        _updateUrlBox.TextChanged += (_, _) => DebounceUrlSave();

        _updateStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var checkBtn = CreateActionButton("立即检查");
        checkBtn.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await CheckNowAsync();
        };

        var actionRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Children = { checkBtn, _updateStatus },
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _updateUrlBox,
                actionRow,
                new TextBlock
                {
                    Text = "清单是一个 JSON：{\"version\":\"1.2.0\",\"url\":\"新版本下载地址\",\"notes\":\"更新说明\"}。" +
                           "程序会拿它的 version 与当前版本比较，有新版时标题栏右上角会出现绿色的「立即更新」。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"当前版本：{AppVersion.Current}",
                    FontSize = 12,
                    Foreground = TextFaint,
                },
            },
        };

        return CreateCard("更新检查", "版本清单的地址。填写后启动时会静默检查一次。", body);
    }

    /// <summary>地址框逐字写盘太浪费，停手一会儿再存。</summary>
    private void DebounceUrlSave()
    {
        if (_suppressSave) return;

        _urlDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _urlDebounce.Stop();

        _urlDebounce.Tick -= OnUrlDebounceTick;
        _urlDebounce.Tick += OnUrlDebounceTick;
        _urlDebounce.Start();
    }

    private void OnUrlDebounceTick(object? sender, EventArgs e)
    {
        _urlDebounce?.Stop();

        var url = _updateUrlBox?.Text?.Trim() ?? "";
        _data.UpdateManifestUrl = string.IsNullOrWhiteSpace(url) ? null : url;
        Persist();
    }

    private async Task CheckNowAsync()
    {
        if (_updateStatus is null) return;

        // 先把框里的内容落盘，免得「检查用的还是旧地址」
        _urlDebounce?.Stop();
        var url = _updateUrlBox?.Text?.Trim() ?? "";
        _data.UpdateManifestUrl = string.IsNullOrWhiteSpace(url) ? null : url;
        Persist();

        _updateStatus.Foreground = TextFaint;
        _updateStatus.Text = "检查中…";

        var result = await UpdateChecker.CheckAsync(url);
        switch (result.Status)
        {
            case UpdateStatus.NotConfigured:
                _updateStatus.Foreground = TextFaint;
                _updateStatus.Text = "还没填地址";
                break;

            case UpdateStatus.UpToDate:
                _updateStatus.Foreground = Ok;
                _updateStatus.Text = $"✓ 已是最新（{result.CurrentVersion}）";
                break;

            case UpdateStatus.UpdateAvailable:
                _updateStatus.Foreground = Ok;
                _updateStatus.Text = $"✓ 发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）";
                break;

            default:
                _updateStatus.Foreground = Danger;
                _updateStatus.Text = "检查失败：" + (result.Error ?? "未知原因");
                break;
        }
    }

    // ---- 持久化 ----

    private void Persist()
    {
        if (_suppressSave) return;
        if (AppSettingsStore.Save(_data)) return;

        FooterHint.Text = $"设置保存失败，请检查数据目录权限：{AppPaths.Settings}";
        FooterHint.Foreground = Danger;
    }

    // ---- 通用小控件（与「默认配置」页保持同一套视觉） ----

    private static Border CreateCard(string title, string? description, Control body)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary
        });

        if (!string.IsNullOrEmpty(description))
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap
            });

        content.Children.Add(new Border { Height = 4 });
        content.Children.Add(body);

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16),
            Child = content
        };
    }

    /// <summary>自绘单选行：左侧圆环 + 选中时点亮的圆点。结构照抄「数据管理」页的存储模式选择。</summary>
    private static OptionRow CreateOptionRow(string title, string description)
    {
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Accent,
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var ring = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Child = dot,
        };

        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontSize = 13, Foreground = TextPrimary },
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    Foreground = TextFaint,
                    TextWrapping = TextWrapping.Wrap
                },
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(ring);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(10, 0, 0, 0);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };

        row.PointerEntered += (_, _) =>
        {
            if (!dot.IsVisible) row.Background = Surface;
        };
        row.PointerExited += (_, _) =>
        {
            if (!dot.IsVisible) row.Background = Brushes.Transparent;
        };

        return new OptionRow(null, row, dot);
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
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        button.PointerEntered += (_, _) => button.Background = Surface;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    /// <summary>
    /// 自绘的开关行，与「默认配置」页同一个写法（复选框 + 缩进的说明文字）。
    /// </summary>
    private static Control CreateSwitch(string label, string description, bool value, Action<bool> onChanged)
    {
        var check = new CheckBox
        {
            Content = label,
            FontSize = 13,
            IsChecked = value,
        };
        check.IsCheckedChanged += (_, _) => onChanged(check.IsChecked == true);

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                check,
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 0, 0, 0),
                },
            },
        };
    }

    /// <summary>
    /// 一行单选。<paramref name="Value"/> 对「会话窗口缓存」是分钟数（null 表示「一直保留」那一档），
    /// 对「上下文压缩」是阈值百分比。
    /// </summary>
    private sealed record OptionRow(int? Value, Border Row, Border Dot);
}
