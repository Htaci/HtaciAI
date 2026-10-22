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
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Storage;

namespace HtaciAI.Views;

/// <summary>
/// 插件页：左侧是插件列表，右侧是<b>主页</b>（安全提示 + 插件卡片）或所选插件的详情。
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

    /// <summary>
    /// 当前选中的插件 id。<c>null</c> = 停在主页（插件卡片总览）。
    /// </summary>
    private string? _selectedId;

    /// <summary>程序在改勾选框（还原状态）时置位，避免被当成用户点击。</summary>
    private bool _suppressToggle;

    /// <summary>最近一次启停操作失败的原因，显示在详情顶部直到下次成功操作。</summary>
    private string? _actionError;

    private bool _subscribed;

    public PluginsPage()
    {
        InitializeComponent();

        // 刷新 = 重新扫描目录：刚放进去的插件立刻能识别，不必重启程序
        RefreshButton.Click += async (_, _) => await RescanAsync();
        HomeButton.Click += (_, _) => ShowHome();
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

            // 顺手重新扫一次目录：用户常见的动作就是「先把插件文件夹拷进去，再切到这页看」。
            // 不自动扫的话，他得自己找到右上角的刷新按钮才会看见，很容易以为「插件没被识别」。
            _ = RescanAsync();
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

    /// <summary>
    /// 重建整页。<b>默认停在主页</b>（用户要求的「打开就是主页」）：
    /// <see cref="_selectedId"/> 为 null 就是这个状态；点了左侧某个插件才进详情。
    /// </summary>
    private void Refresh()
    {
        var all = PluginManager.Instance.GetAll();

        // 选中的插件没了（被删、被移走）就退回主页，而不是随便挑一个顶上
        if (_selectedId is not null && all.All(p => p.Id != _selectedId))
            _selectedId = null;

        UpdateHeader(all);
        RefreshList(all);
        RefreshContent(all);
    }

    private void ShowHome()
    {
        _selectedId = null;
        Refresh();
    }

    /// <summary>
    /// 刷新按钮：重新扫描插件目录并加载新插件。
    ///
    /// <b>为什么不只是重画界面</b>：插件是「刚放进目录一个文件夹」这种形态，不做扫描就永远看不见它 ——
    /// 之前正是这样，用户只能重启程序。这里扫描不动正在运行的插件，所以不会打断桌宠。
    /// </summary>
    private async Task RescanAsync()
    {
        _actionError = null;
        RefreshButton.IsEnabled = false;

        try
        {
            await PluginManager.Instance.RescanAsync();
        }
        catch (Exception ex)
        {
            _actionError = "重新扫描插件目录失败：" + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            Refresh();
        }
    }

    private void UpdateHeader(IReadOnlyList<PluginInfo> all)
    {
        var selected = _selectedId is null ? null : all.FirstOrDefault(p => p.Id == _selectedId);

        // ---- 主页：页面级标题、插件数、开发文档入口 ----
        if (selected is null)
        {
            HeaderTitle.Text = "插件";
            OpenDirectoryButton.IsVisible = true;
            DevDocLink.IsVisible = true;
            HeaderCaps.IsVisible = false;
            HeaderCaps.Children.Clear();
            DetailActions.IsVisible = false;
            DetailActions.Children.Clear();

            HeaderDesc.Text = all.Count == 0
                ? "插件目录里还没有插件"
                : PluginManager.IsMasterEnabled
                    ? $"共 {all.Count} 个插件，{all.Count(p => p.State == PluginRunState.Loaded)} 个运行中"
                    : $"共 {all.Count} 个插件，总开关已关闭（可在设置里打开）";
            return;
        }

        // ---- 详情：标题就是这个插件的名字，副标题是版本号 ----
        HeaderTitle.Text = selected.Name;
        HeaderDesc.Text = string.IsNullOrWhiteSpace(selected.Version)
            ? selected.Id
            : $"版本 {selected.Version}";

        OpenDirectoryButton.IsVisible = false;
        DevDocLink.IsVisible = false;

        // 能力标签放在原来「开发文档」那行的位置：这个插件能做什么，比插件有几行代码更要紧
        HeaderCaps.Children.Clear();
        foreach (var capability in selected.Capabilities)
            HeaderCaps.Children.Add(CreateTag(capability));
        HeaderCaps.IsVisible = HeaderCaps.Children.Count > 0;

        BuildDetailActions(selected);
    }

    /// <summary>
    /// 详情页右上角的两个开关：崩溃后自动加载、启用。
    /// 手动开关（而不是勾选框）更符合「点一下就生效」的语义，也不会跟下面的内容抢注意力。
    /// </summary>
    private void BuildDetailActions(PluginInfo info)
    {
        DetailActions.Children.Clear();

        var autoLoad = new ToggleSwitch
        {
            IsChecked = info.AutoLoadAfterCrash,
            OnContent = null,
            OffContent = null,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(autoLoad, info.CrashSuspected
            ? "崩溃后仍自动加载。该插件疑似导致过崩溃，打开前请确认"
            : "崩溃后仍自动加载：不打开时，疑似导致崩溃的插件下次启动会被跳过");
        autoLoad.IsCheckedChanged += (_, _) =>
        {
            if (_suppressToggle) return;
            PluginManager.Instance.SetAutoLoadAfterCrash(info.Id, autoLoad.IsChecked == true);
        };

        var enable = new ToggleSwitch
        {
            IsChecked = info.Enabled,
            OnContent = null,
            OffContent = null,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(enable, info.Enabled
            ? "已启用，关掉立即停用它"
            : "启用会立即加载并执行该插件的代码");
        enable.IsCheckedChanged += async (_, _) => await OnEnableToggleAsync(info, enable);

        DetailActions.Children.Add(CreateSwitchGroup("崩溃后自动加载", autoLoad));
        DetailActions.Children.Add(CreateSwitchGroup("启用", enable));
        DetailActions.IsVisible = true;
    }

    private static Control CreateSwitchGroup(string label, Control toggle) => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 8,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Children =
        {
            new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
            toggle,
        },
    };

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

        for (var i = 0; i < all.Count; i++)
            PluginList.Children.Add(CreatePluginItem(all[i], canMoveUp: i > 0, canMoveDown: i < all.Count - 1));
    }

    private Border CreatePluginItem(PluginInfo info, bool canMoveUp, bool canMoveDown)
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

        // 顺序箭头：鼠标移上来才出现（平时不抢注意力，也不需要额外一行说明）
        var up = CreateOrderButton("\uF2B7", "上移（更早加载）");
        var down = CreateOrderButton("\uF2A4", "下移（更晚加载）");
        up.Click += (_, e) =>
        {
            e.Handled = true;
            PluginManager.Instance.Move(info.Id, -1);
            Refresh();
        };
        down.Click += (_, e) =>
        {
            e.Handled = true;
            PluginManager.Instance.Move(info.Id, +1);
            Refresh();
        };

        var arrows = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 0,
            IsVisible = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        // 只有真能移动的方向才给箭头：到顶了还显示一个点了没反应的按钮，只会让人以为卡住了
        if (canMoveUp) arrows.Children.Add(up);
        if (canMoveDown) arrows.Children.Add(down);

        title.Margin = new Thickness(0);

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        layout.Children.Add(title);
        layout.Children.Add(arrows);
        Grid.SetColumn(arrows, 1);

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = active ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = layout,
        };

        item.PointerEntered += (_, _) =>
        {
            if (arrows.Children.Count > 0) arrows.IsVisible = true;
            if (!active) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (_, _) =>
        {
            arrows.IsVisible = false;
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

    /// <summary>一个很小的顺序箭头按钮（列表项右侧，悬停时出现）。</summary>
    private static Button CreateOrderButton(string glyph, string tip)
    {
        var button = new Button
        {
            Width = 18,
            Height = 14,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = AppFonts.Icons,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#8892A4")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };

        ToolTip.SetTip(button, tip);
        return button;
    }

    // ---- 右侧：主页 / 详情 ----

    /// <summary>
    /// 右侧内容。没选插件 = <b>主页</b>：安全提示 + 插件卡片（直接排在页面上）；
    /// 选了插件 = 那个插件的详情（README 为主）。
    /// </summary>
    private void RefreshContent(IReadOnlyList<PluginInfo> all)
    {
        DetailPanel.Children.Clear();

        if (_actionError is not null)
            DetailPanel.Children.Add(CreateBanner(_actionError, "#FEF2F2", Danger));

        if (_selectedId is null)
        {
            DetailPanel.Children.Add(CreateSafetyNotice());
            DetailPanel.Children.Add(CreatePluginGrid(all));
            return;
        }

        if (all.FirstOrDefault(p => p.Id == _selectedId) is { } info)
        {
            DetailPanel.Children.Add(CreateDetailCard(info));
            return;
        }

        DetailPanel.Children.Add(CreatePluginGrid(all));
    }

    /// <summary>
    /// 黄色警示条：插件与主程序同进程同权限。
    ///
    /// <b>只留这一条</b>（不再包在「插件开关」卡片里）：开关挪走后，这句话是页面上唯一
    /// 需要用户先读一眼的内容，所以给它单独的位置，而不是塞进某张卡的说明文字里。
    /// </summary>
    private static Control CreateSafetyNotice()
    {
        var icon = new TextBlock
        {
            Text = "\uF86A", // Fluent System Icons：warning
            FontFamily = AppFonts.Icons,
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.Parse("#D97706")),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };

        var text = new TextBlock
        {
            Text = "插件运行在主程序进程，拥有主程序同等权限，在安装/启用插件前，应确认插件来源可信与插件内容安全。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#92400E")),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFBEB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FDE68A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = grid,
        };
    }

    /// <summary>
    /// 主页：插件卡片<b>直接排在页面上</b>（不再套一层带标题的卡片 —— 那层壳只是多了一圈边框）。
    /// **运行中的排在前面**，组内按加载顺序；点了进详情。
    /// </summary>
    private Control CreatePluginGrid(IReadOnlyList<PluginInfo> all)
    {
        if (all.Count == 0) return CreateEmptyCard();

        var panel = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 0, -12, 0),
        };

        foreach (var info in all.OrderByDescending(p => p.State == PluginRunState.Loaded))
            panel.Children.Add(CreatePluginTile(info));

        return panel;
    }

    /// <summary>
    /// 主页上的一张插件卡片：启用的正常样式，未启用的浅灰半透明（一眼分得出谁在跑）。
    /// </summary>
    private Control CreatePluginTile(PluginInfo info)
    {
        var color = StateColor(info.State);
        var enabled = info.Enabled;

        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = info.Name,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = enabled ? TextPrimary : TextMuted,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        new Border
                        {
                            Width = 6, Height = 6,
                            CornerRadius = new CornerRadius(3),
                            Background = new SolidColorBrush(color),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = ShortStateText(info.State),
                            FontSize = 10.5,
                            Foreground = new SolidColorBrush(color),
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(info.Version) ? info.Id : $"{info.Id} · {info.Version}",
                            FontSize = 10.5,
                            Foreground = TextFaint,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(info.Description) ? "（没有写说明）" : info.Description,
                    FontSize = 11.5,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 34,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var normalBackground = Brushes.White;
        var normalBorder = LineBrush;

        var tile = new Border
        {
            Width = 230,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(12),
            Background = enabled ? normalBackground : new SolidColorBrush(Color.Parse("#F6F7F9")),
            BorderBrush = enabled ? normalBorder : new SolidColorBrush(Color.Parse("#EDEFF2")),
            BorderThickness = new Thickness(1),
            Opacity = enabled ? 1 : 0.72,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = body,
        };

        tile.PointerEntered += (_, _) =>
        {
            if (enabled)
            {
                tile.Background = Surface;
                tile.BorderBrush = new SolidColorBrush(Color.Parse("#CFD8E6"));
            }
            else
            {
                tile.Opacity = 0.88;
            }
        };
        tile.PointerExited += (_, _) =>
        {
            tile.Background = enabled ? normalBackground : new SolidColorBrush(Color.Parse("#F6F7F9"));
            tile.BorderBrush = enabled ? normalBorder : new SolidColorBrush(Color.Parse("#EDEFF2"));
            tile.Opacity = enabled ? 1 : 0.72;
        };
        tile.PointerPressed += (_, e) =>
        {
            _selectedId = info.Id;
            Refresh();
            e.Handled = true;
        };

        return tile;
    }

    /// <summary>
    /// 单个插件的详情页。
    ///
    /// <b>主体就是这个插件的 README.md</b>（用 Markdown 渲染，与对话里的渲染器同一个控件）：
    /// 插件作者写文档时不用再学一套格式；没写 README 才退回清单里的简介。
    /// 能力、启停开关这些「宿主关心的事」都在页头和右上角，不跟正文挤在一起。
    /// </summary>
    private Control CreateDetailCard(PluginInfo info)
    {
        var body = new StackPanel { Spacing = 12 };

        // ---- 错误 / 需重启提示 ----
        if (!string.IsNullOrWhiteSpace(info.Error))
            body.Children.Add(CreateBanner(info.Error, "#FEF2F2", Danger));

        if (info.RequiresRestart)
            body.Children.Add(CreateBanner(
                "已停止运行，但插件文件仍被占用、内存没能释放。需要重启程序才能重新加载它。" +
                "常见原因：插件没有在 ShutdownAsync 里停掉自己的定时器 / 动画 / 后台任务，" +
                "或者用了泛型 Storage.Load<T>。详情见插件页的「插件开发文档」。",
                "#FFF7ED", Warning));

        // ---- 正文：README.md / 简介 ----
        body.Children.Add(CreateReadmeBlock(info));

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
        body.Children.Add(new Border
        {
            Height = 1,
            Background = LineBrush,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var dirRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
        };
        dirRow.Children.Add(new TextBlock
        {
            Text = info.Directory,
            FontSize = 11,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
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

        return CreateCard(null, null, body);
    }

    /// <summary>
    /// 插件说明：优先 README.md（Markdown 渲染），没有就用清单里的简介。
    /// </summary>
    private static Control CreateReadmeBlock(PluginInfo info)
    {
        var readme = TryReadReadme(info.Directory);
        if (readme is not null)
        {
            var view = new Controls.Markdown();
            view.SetMarkdown(StripLeadingTitle(readme, info));
            return view;
        }

        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Description) ? "（这个插件没有写说明）" : info.Description,
            FontSize = 12.5,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>
    /// 去掉 README 开头那行与插件同名的 <c># 标题</c>：页头已经写着插件名了，
    /// 正文再来一个巨大的同名标题，看起来像页面标题重复了。
    /// <b>只删同名的那行</b>，标题写的是别的内容就保留。
    /// </summary>
    private static string StripLeadingTitle(string markdown, PluginInfo info)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();

        var i = 0;
        while (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Count) return markdown;

        var line = lines[i].TrimStart();
        if (!line.StartsWith("# ", StringComparison.Ordinal)) return markdown;

        var heading = line[2..].Trim();
        if (!string.Equals(heading, info.Name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(heading, info.Id, StringComparison.OrdinalIgnoreCase))
            return markdown;

        lines.RemoveAt(i);
        if (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) lines.RemoveAt(i);

        return string.Join("\n", lines);
    }

    /// <summary>读插件目录里的 README（.md / .markdown，大小写都认）。读不到返回 null。</summary>
    private static string? TryReadReadme(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            var file = Directory.EnumerateFiles(directory, "README*")
                .FirstOrDefault(f =>
                    f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

            if (file is null) return null;

            var text = File.ReadAllText(file);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // 读不了 README 不该让整个详情页打不开
            return null;
        }
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

    private async Task OnEnableToggleAsync(PluginInfo info, ToggleSwitch toggle)
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

    /// <summary>白底圆角卡。<paramref name="title"/> 为 null 时不画标题（详情页正文就是一整张卡）。</summary>
    private static Border CreateCard(string? title, string? description, Control body)
    {
        var content = new StackPanel { Spacing = 6 };

        if (!string.IsNullOrEmpty(title))
        {
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
        }

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

    private static Control CreateTag(string capability)
    {
        var text = PluginCapabilities.Describe(capability) ?? capability;

        return new Border
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
        };
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
