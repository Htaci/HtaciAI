using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 环境配置页：管理脚本工具依赖的运行时（Python / Node.js）、版本控制 Git、
/// 视频抽帧用的 FFmpeg，以及 <c>bash</c> 工具背后执行命令的 shell。
/// 自动检测系统 PATH，检测失败可手动指定解释器；数据驱动结构便于未来扩展其他运行时。
/// </summary>
public partial class RuntimeSettingsPage : UserControl
{
    private readonly Dictionary<ScriptRuntimeKind, RuntimeCard> _cards = new();
    private readonly List<ShellRow> _shellRows = new();
    private TextBlock? _shellSummary;

    public RuntimeSettingsPage()
    {
        InitializeComponent();

        // 命令行工具排在最前：它决定 AI 用什么语法写命令，比「装没装 Python」更常影响使用
        RuntimeList.Children.Add(CreateShellCard());

        foreach (var kind in new[] { ScriptRuntimeKind.Python, ScriptRuntimeKind.Node, ScriptRuntimeKind.Git, ScriptRuntimeKind.Ffmpeg })
            RuntimeList.Children.Add(CreateCard(kind));

        _ = RefreshAllAsync();
    }

    // ---- 命令行工具卡片 ----

    /// <summary>
    /// bash 工具执行器卡片：一行一个候选 shell，点哪行就用哪个执行命令。
    ///
    /// 「自动」也在列表里：它不是一个程序，而是「Windows → cmd、其他平台 → /bin/bash」这条规则，
    /// 点它等于把选择权交回程序。
    /// </summary>
    private Border CreateShellCard()
    {
        var summary = new TextBlock
        {
            Text = "检测中…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _shellSummary = summary;

        var body = new StackPanel { Spacing = 6 };
        foreach (var kind in ShellEnvironment.AllKinds)
            body.Children.Add(CreateShellRow(kind));

        var refreshBtn = CreateActionButton("重新检测");
        refreshBtn.Margin = new Thickness(0, 8, 0, 0);
        refreshBtn.HorizontalAlignment = HorizontalAlignment.Left;
        refreshBtn.PointerPressed += (s, e) =>
        {
            _ = RefreshShellRowsAsync();
            e.Handled = true;
        };
        body.Children.Add(refreshBtn);

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = "bash 工具",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E"))
        });
        content.Children.Add(new TextBlock
        {
            Text = "shell · AI 实际通过哪个命令行工具执行命令，选中项会写进该工具的介绍里",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280"))
        });
        content.Children.Add(summary);
        content.Children.Add(body);

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16),
            Child = content
        };
    }

    private Border CreateShellRow(ShellKind kind)
    {
        var dot = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };

        var name = new TextBlock
        {
            Text = ShellEnvironment.DisplayName(kind),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E"))
        };
        var desc = new TextBlock
        {
            Text = ShellEnvironment.Description(kind),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            TextWrapping = TextWrapping.Wrap
        };
        var path = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            FontFamily = new FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };
        var status = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(desc);
        text.Children.Add(path);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(status);
        Grid.SetColumn(status, 2);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        row.PointerEntered += (s, e) =>
        {
            if (!IsSelected(kind)) row.Background = new SolidColorBrush(Color.Parse("#F8FAFC"));
        };
        row.PointerExited += (s, e) =>
        {
            if (!IsSelected(kind)) row.Background = Brushes.Transparent;
        };
        row.PointerPressed += (s, e) =>
        {
            SelectShell(kind);
            e.Handled = true;
        };

        _shellRows.Add(new ShellRow(kind, row, dot, status, path));
        return row;
    }

    private bool IsSelected(ShellKind kind) => ShellEnvironment.Preference == kind;

    /// <summary>
    /// 切换执行器并立即生效。
    /// <b>必须重注册内置工具</b>：bash 的描述里写着当前执行器（模型据它决定命令语法），
    /// 描述是注册时生成的，不刷新的话这一轮对话仍会拿到旧的那句。
    /// </summary>
    private void SelectShell(ShellKind kind)
    {
        ShellEnvironment.SetPreference(kind);
        ToolRegistry.Instance.RegisterBuiltins();
        RefreshShellRows();
    }

    private async Task RefreshShellRowsAsync()
    {
        foreach (var row in _shellRows)
        {
            row.Status.Text = "检测中…";
            row.Status.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
            row.Path.IsVisible = false;
        }

        var results = await ShellEnvironment.DetectAllAsync();
        var byKind = results.ToDictionary(r => r.Kind);

        foreach (var row in _shellRows)
        {
            if (!byKind.TryGetValue(row.Kind, out var info))
                continue;

            if (info.Found)
            {
                row.Status.Text = string.IsNullOrWhiteSpace(info.Version) ? "可用" : info.Version;
                row.Status.Foreground = new SolidColorBrush(Color.Parse("#16A34A"));
            }
            else
            {
                row.Status.Text = info.Error ?? "未检测到";
                row.Status.Foreground = new SolidColorBrush(
                    Color.Parse(row.Kind == ShellKind.Auto ? "#9CA3AF" : "#DC2626"));
            }

            if (!string.IsNullOrWhiteSpace(info.FileName))
            {
                row.Path.Text = info.FileName;
                row.Path.IsVisible = true;
            }
        }

        RefreshShellRows();
    }

    /// <summary>只重画选中状态（不碰检测结果）。</summary>
    private void RefreshShellRows()
    {
        var selected = ShellEnvironment.Preference;
        foreach (var row in _shellRows)
        {
            var isSelected = row.Kind == selected;
            row.Dot.IsVisible = isSelected;
            row.Dot.Background = isSelected
                ? new SolidColorBrush(Color.Parse("#4A90D9"))
                : Brushes.Transparent;
            row.Row.Background = isSelected
                ? new SolidColorBrush(Color.Parse("#F1F3F5"))
                : Brushes.Transparent;
        }

        var shell = ShellEnvironment.Resolve();
        _shellSummary!.Text = $"当前执行器：{ShellEnvironment.DisplayName(shell.Kind)}（{shell.FileName}）";
        _shellSummary.Foreground = new SolidColorBrush(Color.Parse("#4B5563"));
    }

    // ---- 卡片构建（数据驱动，新增运行时只需在此枚举中加入） ----

    private Border CreateCard(ScriptRuntimeKind kind)
    {
        var nameText = new TextBlock
        {
            Text = RuntimeDetector.DisplayName(kind),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusText = new TextBlock
        {
            Text = "检测中…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(nameText);
        header.Children.Add(statusText);
        Grid.SetColumn(statusText, 1);

        var descText = new TextBlock
        {
            Text = $"{RuntimeDetector.FileExtension(kind)} · {RuntimeDetector.Description(kind)}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var pathText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            FontFamily = new FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var detectBtn = CreateActionButton("重新检测");
        var pickBtn = CreateActionButton("手动选择…");
        var resetBtn = CreateActionButton("恢复自动检测");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(detectBtn);
        actions.Children.Add(pickBtn);
        actions.Children.Add(resetBtn);

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16),
            Child = new StackPanel
            {
                Children = { header, descText, pathText, actions }
            }
        };

        var state = new RuntimeCard(statusText, pathText, resetBtn);

        detectBtn.PointerPressed += (s, e) =>
        {
            _ = RefreshAsync(kind);
            e.Handled = true;
        };
        pickBtn.PointerPressed += async (s, e) =>
        {
            await PickAndSetAsync(kind);
            e.Handled = true;
        };
        resetBtn.PointerPressed += (s, e) =>
        {
            RuntimeDetector.SetManualPath(kind, null);
            _ = RefreshAsync(kind);
            e.Handled = true;
        };

        _cards[kind] = state;
        return card;
    }

    /// <summary>幽灵样式的小按钮（透明底 + 边框 + 悬停浅灰）。</summary>
    private Border CreateActionButton(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = label
        };
        btn.PointerEntered += (s, e) => btn.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        btn.PointerExited += (s, e) => btn.Background = Brushes.Transparent;
        return btn;
    }

    // ---- 探测与刷新 ----

    private async Task RefreshAllAsync()
    {
        // 两组互不依赖：shell 检测与运行时检测同时跑，页面不用等两轮
        var shellTask = RefreshShellRowsAsync();
        foreach (var kind in _cards.Keys)
            await RefreshAsync(kind);
        await shellTask;
    }

    private async Task RefreshAsync(ScriptRuntimeKind kind)
    {
        var card = _cards[kind];
        card.Status.Text = "检测中…";
        card.Status.Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"));
        card.Path.Text = "";
        card.Reset.IsVisible = false;

        var result = await RuntimeDetector.ProbeAsync(kind);

        card.Path.Text = result.ResolvedPath ?? (result.IsManual ? "（指定路径不可用）" : "（未检测到）");

        if (result.Found)
        {
            var src = result.IsManual ? "手动指定" : "自动检测";
            card.Status.Text = $"✓ {result.Version} · {src}";
            card.Status.Foreground = new SolidColorBrush(Color.Parse("#16A34A"));
        }
        else
        {
            card.Status.Text = result.IsManual ? "✗ 指定路径不可用" : "未检测到";
            card.Status.Foreground = new SolidColorBrush(Color.Parse(result.IsManual ? "#DC2626" : "#9CA3AF"));
        }

        card.Reset.IsVisible = result.IsManual;
    }

    /// <summary>弹出文件选择框，选择解释器可执行文件并保存。</summary>
    private async Task PickAndSetAsync(ScriptRuntimeKind kind)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择 {RuntimeDetector.DisplayName(kind)} 可执行文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe", "*" } }
            }
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        RuntimeDetector.SetManualPath(kind, path);
        await RefreshAsync(kind);
    }

    /// <summary>每张运行时卡片的可更新控件引用。</summary>
    private sealed class RuntimeCard(TextBlock status, TextBlock path, Border reset)
    {
        public TextBlock Status { get; } = status;
        public TextBlock Path { get; } = path;
        public Border Reset { get; } = reset;
    }

    /// <summary>一行 shell 候选的控件引用。</summary>
    private sealed class ShellRow(ShellKind kind, Border row, Border dot, TextBlock status, TextBlock path)
    {
        public ShellKind Kind { get; } = kind;
        public Border Row { get; } = row;
        public Border Dot { get; } = dot;
        public TextBlock Status { get; } = status;
        public TextBlock Path { get; } = path;
    }
}
