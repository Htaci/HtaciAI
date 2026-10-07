using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Models;
using HtaciAI.Services.Storage;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 数据管理页：展示当前数据根目录，并提供存储模式（默认 / 便携 / APPDATA）切换。
///
/// 交互约定：<b>点中某个模式即立刻保存</b>，结果直接写在下方的状态行里——
/// 数据根要等重启才变，所以按钮只负责「重启」，不承担「保存」。
/// </summary>
public partial class DataManagementPage : UserControl
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush TextMono = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#546E7A"));
    private static readonly IBrush RingBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#EA580C"));

    private readonly List<ModeRow> _modeRows = new();
    private StorageMode _selected;
    private TextBlock? _modeBadge;
    private Border? _restartPanel;
    private Border? _importCard;
    private CheckBox? _migrateCheck;
    private TextBlock? _statusText;

    public DataManagementPage()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        _selected = AppPaths.Mode;

        var banner = CreateBanner();
        if (banner is not null) ContentPanel.Children.Add(banner);

        ContentPanel.Children.Add(CreateRootCard());
        ContentPanel.Children.Add(CreateModeCard());

        _importCard = CreateImportCard();
        if (_importCard is not null) ContentPanel.Children.Add(_importCard);

        RefreshBadge();
        RefreshImportCard();

        FooterHint.Text = "数据迁移只拷贝不删除：旧位置的数据会原样保留，确认新位置可用后可自行清理。";
    }

    // ---- 提示条（仅在数据目录解析异常时出现） ----

    private static Control? CreateBanner()
    {
        if (AppPaths.InitError is not { } error) return null;

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FEF3C7")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FDE68A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = new TextBlock
            {
                Text = error,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#92400E")),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    // ---- 卡片一：当前数据目录 ----

    private Control CreateRootCard()
    {
        _modeBadge = new TextBlock
        {
            FontSize = 12,
            Foreground = TextMono
        };

        var badge = new Border
        {
            Background = Surface,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _modeBadge
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "当前数据目录",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(badge);
        Grid.SetColumn(badge, 1);

        var rootText = new TextBlock
        {
            Text = AppPaths.Root,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = TextMono,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 10, 0, 0)
        };
        ToolTip.SetTip(rootText, AppPaths.Root);

        var body = new StackPanel { Children = { header, rootText } };
        body.Children.Add(CreateSubPathRow("数据库", AppPaths.DatabaseFile));
        body.Children.Add(CreateSubPathRow("附件", AppPaths.Attachments));
        body.Children.Add(CreateSubPathRow("技能", AppPaths.Skills));
        body.Children.Add(CreateSubPathRow("脚本工具", AppPaths.Tools));

        var openButton = CreateActionButton("打开目录");
        openButton.PointerPressed += (_, e) =>
        {
            OpenDirectory(AppPaths.Root);
            e.Handled = true;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(openButton);
        body.Children.Add(actions);

        return CreateCard(body);
    }

    private static Control CreateSubPathRow(string label, string path)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = TextFaint,
            Width = 64,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var value = new TextBlock
        {
            Text = path,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        };
        ToolTip.SetTip(value, path);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(name);
        grid.Children.Add(value);
        Grid.SetColumn(value, 1);
        return grid;
    }

    // ---- 卡片二：存储模式 ----

    private Control CreateModeCard()
    {
        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = "存储模式",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary
        });
        body.Children.Add(new TextBlock
        {
            Text = "点选即保存。数据位置要重启应用后才会切换。",
            FontSize = 12,
            Foreground = TextFaint,
            Margin = new Thickness(0, 6, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var mode in new[] { StorageMode.Default, StorageMode.Portable, StorageMode.AppData })
            body.Children.Add(CreateModeRow(mode));

        body.Children.Add(CreateRestartPanel());

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            IsVisible = false
        };
        body.Children.Add(_statusText);

        RefreshModeRows();
        return CreateCard(body);
    }

    private Control CreateModeRow(StorageMode mode)
    {
        var dot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            BorderBrush = RingBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            }
        };
        var dotInner = (Border)dot.Child!;

        var title = new TextBlock
        {
            Text = ModeName(mode),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary
        };
        var description = new TextBlock
        {
            FontSize = 12,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            Children = { title, description }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(dot);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };

        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (mode == _selected) return;

            _selected = mode;
            RefreshModeRows();
            ApplySelectedMode();
        };
        row.PointerEntered += (_, _) =>
        {
            if (mode != _selected) row.Background = Surface;
        };
        row.PointerExited += (_, _) =>
        {
            if (mode != _selected) row.Background = Brushes.Transparent;
        };

        _modeRows.Add(new ModeRow(mode, row, dotInner, description));
        return row;
    }

    /// <summary>需要重启才切换数据位置时，才露出「迁移现有数据」与重启按钮。</summary>
    private Border CreateRestartPanel()
    {
        _migrateCheck = new CheckBox
        {
            Content = "把现有数据迁移到新位置（只拷贝，不删除旧数据）",
            FontSize = 12,
            IsChecked = true,
            Foreground = TextMuted
        };
        // 保存过意图之后再改勾选，要把最新意图重新写一遍，否则磁盘上的记录和界面不一致。
        _migrateCheck.IsCheckedChanged += (_, _) =>
        {
            if (_restartPanel?.IsVisible == true) ApplySelectedMode();
        };

        var restart = CreatePrimaryButton("立即重启");
        restart.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!AppRestart.TryRestart())
                ShowStatus("无法自动重启，请手动关闭并重新打开应用。", Danger);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(restart);

        _restartPanel = new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            IsVisible = false,
            Child = new StackPanel { Children = { _migrateCheck, actions } }
        };
        return _restartPanel;
    }

    /// <summary>点中模式即保存，并立刻给出反馈。</summary>
    private void ApplySelectedMode()
    {
        var result = StorageModeService.Apply(_selected, _migrateCheck?.IsChecked != false);

        ShowStatus(result.Message, result.Saved ? Accent : Danger);

        if (_restartPanel is not null)
            _restartPanel.IsVisible = result.Saved && result.RestartRequired;

        RefreshBadge();
        RefreshImportCard();
    }

    private void RefreshModeRows()
    {
        foreach (var row in _modeRows)
        {
            var selected = row.Mode == _selected;
            row.Row.Background = selected ? Surface : Brushes.Transparent;
            row.Dot.IsVisible = selected;

            row.Description.Text = selected
                ? $"{ModeDescription(row.Mode)}\n→ {AppPaths.PreviewRoot(row.Mode)}"
                : ModeDescription(row.Mode);
        }
    }

    /// <summary>未重启前，当前路径仍是旧模式的；把「已保存、待重启」的差异显式写在徽标上。</summary>
    private void RefreshBadge()
    {
        if (_modeBadge is null) return;

        var configured = StorageBootstrap.Read().Mode;
        if (configured == AppPaths.Mode)
        {
            _modeBadge.Text = ModeName(AppPaths.Mode);
            _modeBadge.Foreground = TextMono;
            return;
        }

        _modeBadge.Text = $"{ModeName(AppPaths.Mode)} → {ModeName(configured)}（重启后）";
        _modeBadge.Foreground = BadgeBrush;
    }

    /// <summary>重启面板和导入卡片都会写同一份迁移意图，同时露出会让用户以为能都点。</summary>
    private void RefreshImportCard()
    {
        if (_importCard is not null)
            _importCard.IsVisible = _restartPanel?.IsVisible != true;
    }

    private void ShowStatus(string text, IBrush color)
    {
        if (_statusText is null) return;
        _statusText.Text = text;
        _statusText.Foreground = color;
        _statusText.IsVisible = true;
    }

    // ---- 卡片三：导入旧数据 ----

    /// <summary>
    /// 当前根与旧的 <c>%APPDATA%\HtaciAI</c> 不是同一处、且那边还有数据库时，提供一次性导入入口。
    /// 对应「一直在用 APPDATA，现在想把数据挪到程序目录」这个最常见的迁移场景——
    /// 此时模式切换帮不上忙（目标根早就是当前位置了），只能显式从旧位置拉。
    /// </summary>
    private Border? CreateImportCard()
    {
        var legacy = AppPaths.AppDataRoot;
        if (AppPaths.PathEquals(legacy, AppPaths.Root)) return null;
        if (!DataMigrator.HasDatabase(legacy)) return null;

        var alreadyHasData = DataMigrator.HasDatabase(AppPaths.Root);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "导入旧数据",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary
        });
        body.Children.Add(new TextBlock
        {
            Text = alreadyHasData
                ? $"旧位置 {legacy} 还留有一份数据，但当前位置已经有数据库了，未自动导入。"
                : $"检测到旧位置 {legacy} 存在数据，而当前位置还没有。可以把数据库、附件、技能和脚本工具一并搬过来。",
            FontSize = 12,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        if (alreadyHasData) return CreateCard(body);

        var status = new TextBlock
        {
            FontSize = 12,
            Foreground = Danger,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false
        };

        var button = CreatePrimaryButton("导入并重启");
        button.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            if (!StorageModeService.RequestImport(legacy))
            {
                status.Text = "无法写入配置，请检查配置目录权限。";
                status.IsVisible = true;
                return;
            }

            if (!AppRestart.TryRestart())
            {
                status.Text = "已登记导入，请手动重启应用以执行。";
                status.IsVisible = true;
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(button);

        body.Children.Add(actions);
        body.Children.Add(status);
        return CreateCard(body);
    }

    // ---- 通用小控件 ----

    private static Border CreateCard(Control body) => new()
    {
        Background = Brushes.White,
        CornerRadius = new CornerRadius(12),
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(18, 16),
        Child = body
    };

    /// <summary>幽灵样式的小按钮（透明底 + 边框 + 悬停浅灰），与「环境配置」页保持一致。</summary>
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
                Foreground = TextMono,
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

    private static void OpenDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不值得弹错
        }
    }

    private static string ModeName(StorageMode mode) => mode switch
    {
        StorageMode.Portable => "便携",
        StorageMode.AppData => "APPDATA",
        _ => "默认"
    };

    private static string ModeDescription(StorageMode mode) => mode switch
    {
        StorageMode.Portable => "始终使用程序目录下的 data 文件夹，数据跟着程序走。",
        StorageMode.AppData => @"始终使用 %APPDATA%\HtaciAI，即使程序目录下有 data 文件夹。",
        _ => @"程序目录下存在 data 文件夹时使用它，否则使用 %APPDATA%\HtaciAI。"
    };

    /// <summary>一行模式选项里需要随选中态联动的控件。</summary>
    private sealed class ModeRow(StorageMode mode, Border row, Border dot, TextBlock description)
    {
        public StorageMode Mode { get; } = mode;
        public Border Row { get; } = row;
        public Border Dot { get; } = dot;
        public TextBlock Description { get; } = description;
    }
}
