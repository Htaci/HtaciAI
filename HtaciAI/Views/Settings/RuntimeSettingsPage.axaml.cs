using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 环境配置页：管理脚本工具依赖的运行时（Python / Node.js）。
/// 自动检测系统 PATH，检测失败可手动指定解释器；数据驱动结构便于未来扩展其他运行时。
/// </summary>
public partial class RuntimeSettingsPage : UserControl
{
    private readonly Dictionary<ScriptRuntimeKind, RuntimeCard> _cards = new();

    public RuntimeSettingsPage()
    {
        InitializeComponent();

        foreach (var kind in new[] { ScriptRuntimeKind.Python, ScriptRuntimeKind.Node })
            RuntimeList.Children.Add(CreateCard(kind));

        _ = RefreshAllAsync();
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
        foreach (var kind in _cards.Keys)
            await RefreshAsync(kind);
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
}
