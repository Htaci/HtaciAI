using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Models;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Storage;

namespace HtaciAI.Views;

/// <summary>
/// 插件页：左侧列出插件目录里发现的插件，右侧是总开关与所选插件的详情。
///
/// <b>这里只做「展示 + 转发」</b>：真正的加载、卸载、崩溃策略全在 <see cref="PluginManager"/> 里。
/// 页面不缓存插件状态，每次 <see cref="Refresh"/> 都从管理器取一份新快照 ——
/// 快照是不可变记录，界面不会拿着一个正在被别的流程改的对象。
/// </summary>
public partial class PluginsPage : UserControl
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#5A5A72"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#16A34A"));

    private readonly AppSettingsData _data = AppSettingsStore.Current;

    /// <summary>当前选中的插件 id。刷新时若它已消失，自动回落到第一个。</summary>
    private string? _selectedId;

    /// <summary>程序在改勾选框（还原状态）时置位，避免被当成用户点击。</summary>
    private bool _suppressToggle;

    /// <summary>最近一次启停操作失败的原因，显示在详情顶部直到下次成功操作。</summary>
    private string? _actionError;

    private bool _subscribed;

    public PluginsPage()
    {
        InitializeComponent();

        RefreshButton.Click += (_, _) => Refresh();
        OpenDirectoryButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenPluginsDirectory();
        };

        DevDocLink.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenDevDocs();
        };

        // 标签页切回来时状态可能已经变了（别的页面改了总开关、插件自己崩了）
        AttachedToVisualTree += (_, _) =>
        {
            Subscribe();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        Subscribe();
        Refresh();
    }

    // ============================================================
    // 订阅
    // ============================================================

    private void Subscribe()
    {
        if (_subscribed) return;
        PluginManager.Instance.Changed += OnManagerChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        PluginManager.Instance.Changed -= OnManagerChanged;
        _subscribed = false;
    }

    /// <summary>
    /// 管理器状态变了。<b>延后一拍再重建</b>：事件可能是在它自己的遍历 / 卸载流程中间发出的，
    /// 就地重建控件等于在别人的中午动家具。
    /// </summary>
    private void OnManagerChanged()
        => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);

    // ============================================================
    // 刷新
    // ============================================================

    private void Refresh()
    {
        var all = PluginManager.Instance.GetAll();

        if (_selectedId is null || all.All(p => p.Id != _selectedId))
            _selectedId = all.Count > 0 ? all[0].Id : null;

        var selected = all.FirstOrDefault(p => p.Id == _selectedId);

        UpdateHeader(all);
        RefreshList(all);
        RefreshDetail(selected);
    }

    private void UpdateHeader(IReadOnlyList<PluginInfo> all)
    {
        if (all.Count == 0)
        {
            HeaderDesc.Text = "插件目录里还没有插件";
            return;
        }

        var loaded = all.Count(p => p.State == PluginRunState.Loaded);
        HeaderDesc.Text = PluginManager.IsMasterEnabled
            ? $"共 {all.Count} 个插件，{loaded} 个运行中"
            : $"共 {all.Count} 个插件，总开关已关闭";
    }

    // ---- 左侧列表 ----

    private void RefreshList(IReadOnlyList<PluginInfo> all)
    {
        PluginList.Children.Clear();

        if (all.Count == 0)
        {
            PluginList.Children.Add(new TextBlock
            {
                Text = "暂无插件",
                FontSize = 12,
                Foreground = TextFaint,
                Margin = new Thickness(12, 8, 0, 0),
            });
            return;
        }

        foreach (var info in all)
            PluginList.Children.Add(CreatePluginItem(info));
    }

    private Border CreatePluginItem(PluginInfo info)
    {
        var active = info.Id == _selectedId;

        var title = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                new TextBlock
                {
                    Text = info.Name,
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(active ? "#1A1A2E" : "#4B5563")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(info.Version) ? info.Id : $"{info.Id} · {info.Version}",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.Parse("#B0B7C3")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var statusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(StateColor(info.State)),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = ShortStateText(info.State),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(StateColor(info.State)),
                },
            },
        };
        title.Children.Add(statusRow);

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = active ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = title,
        };

        item.PointerEntered += (_, _) =>
        {
            if (!active) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (_, _) =>
        {
            if (!active) item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (_, e) =>
        {
            _selectedId = info.Id;
            Refresh();
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧详情 ----

    private void RefreshDetail(PluginInfo? info)
    {
        DetailPanel.Children.Clear();

        DetailPanel.Children.Add(CreateMasterSwitchCard());

        if (_actionError is not null)
            DetailPanel.Children.Add(CreateBanner(_actionError, "#FEF2F2", Danger));

        if (info is null)
        {
            DetailPanel.Children.Add(CreateEmptyCard());
            return;
        }

        DetailPanel.Children.Add(CreatePluginCard(info));
    }

    /// <summary>总开关。关掉 = 全部卸载，之后新插件也不会加载。</summary>
    private Control CreateMasterSwitchCard()
    {
        var toggle = new CheckBox
        {
            Content = "启用插件",
            FontSize = 13,
            IsChecked = PluginManager.IsMasterEnabled,
        };
        toggle.IsCheckedChanged += async (_, _) => await OnMasterToggleAsync(toggle);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                toggle,
                new TextBlock
                {
                    Text = "插件运行在本程序进程内，与主程序享有同样的权限 —— 能读写文件、能建自己的窗口。" +
                           "所以这里默认关闭，打开之前请确认插件来源可信。关闭后立即卸载全部插件。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        return CreateCard("插件开关", null, body);
    }

    private async Task OnMasterToggleAsync(CheckBox toggle)
    {
        if (_suppressToggle) return;

        var want = toggle.IsChecked == true;
        if (want == _data.EnablePlugins) return;

        _data.EnablePlugins = want;
        AppSettingsStore.Save(_data);

        _actionError = null;
        toggle.IsEnabled = false;

        try
        {
            await PluginManager.Instance.ReloadAsync();
        }
        catch (Exception ex)
        {
            _actionError = "切换插件开关失败：" + ex.Message;
        }
        finally
        {
            Refresh();
        }
    }

    private Control CreatePluginCard(PluginInfo info)
    {
        var body = new StackPanel { Spacing = 10 };

        // ---- 头部：名称 + 状态 ----
        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = info.Name,
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                    Foreground = TextPrimary,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
                CreateStatusBadge(info),
            },
        };
        body.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(info.Description))
            body.Children.Add(new TextBlock
            {
                Text = info.Description,
                FontSize = 12.5,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
            });

        var meta = string.IsNullOrWhiteSpace(info.Version) ? info.Id : $"{info.Id} · {info.Version}";
        body.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
        });

        // ---- 错误 / 需重启提示 ----
        if (!string.IsNullOrWhiteSpace(info.Error))
            body.Children.Add(CreateBanner(info.Error, "#FEF2F2", Danger));

        if (info.RequiresRestart)
            body.Children.Add(CreateBanner(
                "已停止运行，但插件文件仍被占用、内存没能释放。需要重启程序才能重新加载它。",
                "#FFF7ED", Warning));

        // ---- 能力清单 ----
        if (info.Capabilities.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "声明的能力",
                FontSize = 12,
                Foreground = TextFaint,
                Margin = new Thickness(0, 4, 0, 0),
            });
            body.Children.Add(CreateCapabilityTags(info.Capabilities));
        }

        // ---- 上下文贡献：这个入口能悄悄改变模型行为，字数必须看得见 ----
        if (info.Capabilities.Any(c => string.Equals(c, PluginCapabilities.PromptContribute, StringComparison.OrdinalIgnoreCase)))
        {
            body.Children.Add(new TextBlock
            {
                Text = info.ContributionChars > 0
                    ? $"最近一轮往上下文贡献了 {info.ContributionChars} 字（单插件上限 {PromptComposer.PerPluginLimit} 字）"
                    : "最近一轮没有往上下文贡献内容。",
                FontSize = 11.5,
                Foreground = info.ContributionChars > 0 ? TextMuted : TextFaint,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ---- 操作区 ----
        body.Children.Add(new Border
        {
            Height = 1,
            Background = LineBrush,
            Margin = new Thickness(0, 6, 0, 2),
        });

        var enabled = new CheckBox
        {
            Content = "启用该插件",
            FontSize = 13,
            IsChecked = info.Enabled,
        };
        enabled.IsCheckedChanged += async (_, _) => await OnEnableToggleAsync(info, enabled);

        body.Children.Add(enabled);

        var autoLoad = new CheckBox
        {
            Content = "崩溃后仍自动加载",
            FontSize = 13,
            IsChecked = info.AutoLoadAfterCrash,
            Margin = new Thickness(24, 0, 0, 0),
        };
        autoLoad.IsCheckedChanged += (_, _) =>
        {
            if (_suppressToggle) return;
            PluginManager.Instance.SetAutoLoadAfterCrash(info.Id, autoLoad.IsChecked == true);
        };

        body.Children.Add(autoLoad);
        body.Children.Add(new TextBlock
        {
            Text = "不勾选（默认）：疑似导致崩溃的插件下次启动时会被跳过，避免「一启动就崩、连设置页都进不去」。",
            FontSize = 11.5,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        });

        // ---- 崩溃记录 ----
        if (info.CrashCount > 0 || info.CrashSuspected)
        {
            var crashText = info.CrashSuspected
                ? $"上次退出时疑似由该插件导致崩溃（已记录 {info.CrashCount} 次），本次启动已跳过加载。"
                : $"曾疑似导致崩溃 {info.CrashCount} 次。";

            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 0),
                Children = { CreateBannerText(crashText, Warning) },
            };

            if (!string.IsNullOrWhiteSpace(info.CrashLogPath) && File.Exists(info.CrashLogPath))
            {
                var logBtn = CreateActionButton("查看崩溃日志");
                logBtn.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    OpenFile(info.CrashLogPath!);
                };
                row.Children.Add(logBtn);
            }

            body.Children.Add(row);
        }

        // ---- 目录 ----
        var dirRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
        };
        dirRow.Children.Add(new TextBlock
        {
            Text = info.Directory,
            FontSize = 11,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        var openBtn = CreateActionButton("打开目录");
        openBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenDirectory(info.Directory);
        };
        dirRow.Children.Add(openBtn);
        body.Children.Add(dirRow);

        return CreateCard("插件详情", null, body);
    }

    private Control CreateEmptyCard()
    {
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "插件目录里还没有插件。",
                    FontSize = 13,
                    Foreground = TextPrimary,
                },
                new TextBlock
                {
                    Text = "每个插件是一个独立文件夹，里面要有 plugin.json（清单）和清单里写的入口 dll。" +
                           "放好后点左上角的刷新按钮重新扫描。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = PluginManager.PluginsDirectory,
                    FontSize = 11,
                    Foreground = TextFaint,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var openBtn = CreateActionButton("打开插件目录");
        openBtn.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenPluginsDirectory();
        };
        body.Children.Add(openBtn);

        return CreateCard("还没有插件", null, body);
    }

    // ---- 启用 / 禁用 ----

    private async Task OnEnableToggleAsync(PluginInfo info, CheckBox toggle)
    {
        if (_suppressToggle) return;

        var want = toggle.IsChecked == true;
        if (want == info.Enabled) return;

        // 疑似崩过的插件：用户再启用前必须明确知道后果
        if (want && info.CrashSuspected)
        {
            var (confirmed, autoLoad) = await PluginCrashWarningDialog.ShowAsync(
                TopLevel.GetTopLevel(this) as Window, info);

            if (!confirmed)
            {
                _suppressToggle = true;
                try { toggle.IsChecked = info.Enabled; }
                finally { _suppressToggle = false; }
                return;
            }

            if (autoLoad) PluginManager.Instance.SetAutoLoadAfterCrash(info.Id, true);
        }

        toggle.IsEnabled = false;
        _actionError = null;

        try
        {
            if (want) await PluginManager.Instance.EnableAsync(info.Id);
            else await PluginManager.Instance.DisableAsync(info.Id);
        }
        catch (Exception ex)
        {
            _actionError = (want ? "启用失败：" : "禁用失败：") + ex.Message;
        }
        finally
        {
            toggle.IsEnabled = true;
            Refresh();
        }
    }

    // ============================================================
    // 打开文件 / 目录
    // ============================================================

    /// <summary>
    /// 生成并打开插件开发文档。
    ///
    /// <b>点一次写一次</b>：文档内置在程序里（不发网络请求、不依赖资源文件），
    /// 每次点击都重新写一遍，所以它永远与当前版本的宿主 API 一致；不点就一个文件都不落盘。
    /// </summary>
    private void OpenDevDocs()
    {
        var index = PluginDocs.EnsureWritten();
        if (index is null)
        {
            _actionError = "生成插件开发文档失败，请检查数据目录是否可写：" + AppPaths.Docs;
            Refresh();
            return;
        }

        _actionError = null;
        OpenFile(index);
    }

    private static void OpenPluginsDirectory()
    {
        try
        {
            Directory.CreateDirectory(PluginManager.PluginsDirectory);
        }
        catch
        {
            // 目录建不出来时仍然尝试打开，让用户看到问题所在
        }

        OpenDirectory(PluginManager.PluginsDirectory);
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不是致命问题
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // 同上
        }
    }

    // ============================================================
    // 视觉小件（与「常规设置」页保持同一套）
    // ============================================================

    private static Border CreateCard(string title, string? description, Control body)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
        });

        if (!string.IsNullOrEmpty(description))
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
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
            Child = content,
        };
    }

    private static Border CreateStatusBadge(PluginInfo info)
    {
        var color = StateColor(info.State);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = info.StateText,
                FontSize = 11,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private static Border CreateBanner(string text, string background, IBrush foreground)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static TextBlock CreateBannerText(string text, IBrush foreground) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 520,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private static Control CreateCapabilityTags(IReadOnlyList<string> capabilities)
    {
        var panel = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };

        foreach (var capability in capabilities)
        {
            var text = PluginCapabilities.Describe(capability) ?? capability;

            panel.Children.Add(new Border
            {
                Background = Surface,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    Foreground = TextMuted,
                },
            });
        }

        return panel;
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
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        button.PointerEntered += (_, _) => button.Background = Surface;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static Color StateColor(PluginRunState state) => state switch
    {
        PluginRunState.Loaded => Color.Parse("#16A34A"),
        PluginRunState.Failed or PluginRunState.Blocked => Color.Parse("#DC2626"),
        PluginRunState.SkippedByCrash or PluginRunState.NeedsRestart => Color.Parse("#D97706"),
        PluginRunState.DependencyBlocked => Color.Parse("#D97706"),
        _ => Color.Parse("#9CA3AF"),
    };

    /// <summary>列表里放不下完整状态文案（「已禁用（需重启…）」太长），用短版。</summary>
    private static string ShortStateText(PluginRunState state) => state switch
    {
        PluginRunState.Loaded => "运行中",
        PluginRunState.Disabled => "已禁用",
        PluginRunState.NotLoaded => "未加载",
        PluginRunState.Failed => "加载失败",
        PluginRunState.Blocked => "无法加载",
        PluginRunState.SkippedByCrash => "崩溃后暂停",
        PluginRunState.NeedsRestart => "需重启",
        PluginRunState.DependencyBlocked => "依赖未就绪",
        _ => state.ToString(),
    };
}
