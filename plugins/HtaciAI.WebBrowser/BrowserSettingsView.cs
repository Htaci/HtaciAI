using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 插件页 → 内置浏览器 → 「设置」分页的内容。
///
/// <b>为什么放在插件页而不是自建标签页</b>：插件页是用户管理插件的固定入口，
/// 这些操作（清缓存、清登录、重置）本质上是「管理这个插件的数据」，和启用/停用/版本号是一件事。
/// 另开一个标签页的话，入口会飘 —— 标签页关掉之后就再也找不到了。
///
/// <b>这里是轻量界面</b>：只有概览与按钮，不做多级导航。列表型的重度管理
/// （逐个站点看 Cookie、逐条删历史）不放在这里，它会把这个分页撑成一个小型浏览器。
///
/// <b>所有清理动作都要求先关掉浏览器窗口</b>：profile 目录被浏览器进程持着，
/// 直接删会得到一串「另一个程序正在使用」。界面因此自己先关窗，并把这件事写在按钮说明里，
/// 不让用户点完才发现「清理没生效」。
/// </summary>
internal sealed class BrowserSettingsView : UserControl
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#5A5A72"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    private readonly IPluginContext _context;
    private readonly BrowserManager _manager;

    private readonly TextBlock _totalText = Num();
    private readonly TextBlock _cacheText = Num();
    private readonly TextBlock _shotsText = Num();
    private readonly TextBlock _historyText = Num();
    private readonly TextBlock _openText = new() { FontSize = 12, Foreground = TextMuted };
    private readonly TextBlock _statusText = new()
    {
        FontSize = 12,
        Foreground = TextMuted,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };

    private readonly StackPanel _historyList = new() { Spacing = 6 };
    private readonly StackPanel _buttons = new() { Spacing = 0 };

    private bool _busy;

    public BrowserSettingsView(IPluginContext context, BrowserManager manager)
    {
        _context = context;
        _manager = manager;

        Content = BuildRoot();
        _ = RefreshUsageAsync();
    }

    private static TextBlock Num() => new()
    {
        Text = "…",
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextPrimary,
    };

    // ============================================================
    // 布局
    // ============================================================

    private Control BuildRoot()
    {
        var root = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 4, 0) };

        root.Children.Add(new TextBlock
        {
            Text = "内置浏览器的数据都存在插件的私有目录里（不在程序目录，也不影响系统里的 Edge / Chrome）。" +
                   "清理前会自动关闭所有浏览器标签页。",
            FontSize = 12,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(BuildUsageCard());
        root.Children.Add(BuildActionsCard());
        root.Children.Add(BuildHistoryCard());
        root.Children.Add(_statusText);

        return root;
    }

    /// <summary>概览：数据目录、体积、当前开着几个窗口。</summary>
    private Control BuildUsageCard()
    {
        var rows = new StackPanel { Spacing = 8 };

        rows.Children.Add(UsageRow("Chromium 数据目录", _totalText));
        rows.Children.Add(UsageRow("　其中可清理的缓存", _cacheText));
        rows.Children.Add(UsageRow("截图", _shotsText));
        rows.Children.Add(UsageRow("访问历史", _historyText));

        rows.Children.Add(new TextBlock
        {
            Text = _manager.DataFolder,
            FontSize = 11,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                Secondary("打开数据目录", () => OpenFolder(_manager.DataFolder), "在资源管理器里打开这个目录"),
                Secondary("重新统计", () => { _ = RefreshUsageAsync(); }, "重新计算上面几行的体积"),
            },
        };

        rows.Children.Add(buttons);
        rows.Children.Add(_openText);

        return Card(rows);
    }

    /// <summary>清理与重置。</summary>
    private Control BuildActionsCard()
    {
        _buttons.Children.Add(ActionRow(
            "清理缓存",
            "删掉磁盘缓存、代码缓存、GPU 缓存。不影响登录状态，只是下次打开网页要重新下载资源。",
            "清理缓存",
            () => RunCleanupAsync("清理缓存", () => BrowserData.ClearCache(_manager.DataFolder))));

        _buttons.Children.Add(Separator());

        _buttons.Children.Add(ActionRow(
            "清除 Cookie 与登录数据",
            "删掉所有网站的 Cookie、本地存储、保存的账号密码与表单。这等于让你从所有网站退出登录，下次使用要重新登录。",
            "清除登录数据",
            () => RunCleanupAsync("清除登录数据", () => BrowserData.ClearIdentity(_manager.DataFolder))));

        _buttons.Children.Add(Separator());

        _buttons.Children.Add(ActionRow(
            "清空访问历史",
            "只清插件记录的历史（设置页下面那份列表），不动网页本身和登录状态。",
            "清空历史",
            ClearHistoryAsync));

        _buttons.Children.Add(Separator());

        _buttons.Children.Add(ActionRow(
            "清空截图",
            "删掉 web_screenshot 产生的图片。已发给模型的截图会留在对话里，但本地文件不再存在。",
            "清空截图",
            () => RunCleanupAsync("清空截图", () => BrowserData.ClearShots(_manager.ShotsFolder), closeBrowsers: false)));

        _buttons.Children.Add(Separator());

        // 重置放在最后并且是两段确认：它会删掉整个 profile，包括登录态、书签、站点设置
        _buttons.Children.Add(ActionRow(
            "重置浏览器数据",
            "删除整个 Chromium 数据目录，回到「第一次使用」的状态。登录态、历史、缓存、站点权限全部消失，不可撤销。",
            "重置…",
            ResetProfileAsync,
            dangerous: true));

        return Card(_buttons);
    }

    private Control BuildHistoryCard()
    {
        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock
        {
            Text = "最近的访问记录（插件自己记的，最新在前）",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
        });

        body.Children.Add(_historyList);
        return Card(body);
    }

    private static Control UsageRow(string label, TextBlock value)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        return row;
    }

    private Control ActionRow(
        string title, string description, string buttonText, Func<Task> action, bool dangerous = false)
    {
        var text = new StackPanel { Spacing = 3 };

        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = dangerous ? Danger : TextPrimary,
        });

        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap,
        });

        var button = dangerous
            ? DangerButton(buttonText, action)
            : Secondary(buttonText, () => { _ = action(); });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 8) };

        row.Children.Add(text);
        Grid.SetColumn(button, 1);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(button);

        return row;
    }

    // ============================================================
    // 动作
    // ============================================================

    /// <summary>
    /// 通用清理流程：关窗 → 后台删文件 → 刷新概览。
    /// <b>关窗先于删除</b>，否则删的是一批被占用的文件，结果必然是「清理了但体积没变」。
    ///
    /// <paramref name="closeBrowsers"/> 只对删 profile 里的东西有意义：
    /// 清截图、清历史与应用数据无关，没必要把用户正在看的页面关掉。
    /// </summary>
    private async Task RunCleanupAsync(string what, Func<BrowserData.Cleanup> work, bool closeBrowsers = true)
    {
        await GuardAsync(what, async () =>
        {
            var closed = closeBrowsers ? await CloseBrowsersAsync() : 0;
            SetStatus(closed > 0 ? $"已先关闭 {closed} 个浏览器窗口，正在{what}…" : $"正在{what}…");

            var result = await Task.Run(work);

            SetStatus((closed > 0 ? $"（已关闭 {closed} 个浏览器窗口）" : "") + result.Describe(what));
            await RefreshUsageAsync();
        });
    }

    private Task ClearHistoryAsync()
    {
        // 历史在内存里也有一份，清文件不够 —— 不清内存的话刷新一下列表又回来了
        _manager.History.Clear();
        BuildHistoryRows();
        SetStatus("访问历史已清空。");
        return RefreshUsageAsync();
    }

    /// <summary>
    /// 重置：两段确认（第一次点变红，第二次才执行）。
    /// <b>不用弹窗</b>：插件页里弹窗会挡住整个页面，而且宿主的弹窗样式在插件里拿不到，
    /// 自己拼一个只会显得突兀。两段式按钮的确认强度够用，也不会打断操作。
    /// </summary>
    private async Task ResetProfileAsync()
    {
        if (_busy) return;

        await GuardAsync("重置浏览器数据", async () =>
        {
            var closed = await CloseBrowsersAsync();
            var folder = _manager.DataFolder;

            SetStatus(closed > 0 ? $"已先关闭 {closed} 个浏览器窗口，正在重置…" : "正在重置…");

            var result = await Task.Run(() => BrowserData.ResetProfile(folder));

            // 历史的 JSON 也在数据目录里（不是 profile），重置要连它一起清，否则「重置」名不副实
            _manager.History.Clear();
            BuildHistoryRows();

            SetStatus(result.Ok
                ? "浏览器数据已重置，下次打开浏览器会重新创建一份干净的 profile。"
                : result.Describe("重置"));

            await RefreshUsageAsync();
        });
    }

    /// <summary>关掉所有浏览器窗口并等一小会儿，让 Chromium 进程放开文件句柄。</summary>
    private async Task<int> CloseBrowsersAsync()
    {
        var count = _manager.OpenIds.Count;
        if (count == 0) return 0;

        await _manager.CloseAllAsync(CancellationToken.None);

        // 子进程退出是异步的：不等就直接删，会稳定命中「文件正被占用」
        await Task.Delay(600);
        return count;
    }

    private async Task RefreshUsageAsync()
    {
        try
        {
            var dataFolder = _manager.DataFolder;
            var shotsFolder = _manager.ShotsFolder;

            var usage = await Task.Run(() => BrowserData.Measure(dataFolder, shotsFolder));

            _totalText.Text = BrowserData.Describe(usage.ProfileBytes);
            _cacheText.Text = BrowserData.Describe(usage.CacheBytes);
            _shotsText.Text = usage.ShotCount > 0
                ? $"{BrowserData.Describe(usage.ShotBytes)}（{usage.ShotCount} 张）"
                : "无";
            _historyText.Text = $"{_manager.History.Entries.Count} 条（{BrowserData.Describe(usage.HistoryBytes)}）";

            var open = _manager.OpenIds.Count;
            _openText.Text = open == 0
                ? "当前没有打开的浏览器窗口。"
                : $"当前有 {open} 个浏览器窗口在运行；清理前会自动关闭它们。";

            BuildHistoryRows();
        }
        catch (Exception ex)
        {
            _context.Log.Error("统计浏览器数据体积失败", ex);
            SetStatus($"统计失败：{ex.Message}");
        }
    }

    private void BuildHistoryRows()
    {
        _historyList.Children.Clear();

        var entries = _manager.History.Entries;
        if (entries.Count == 0)
        {
            _historyList.Children.Add(new TextBlock
            {
                Text = "还没有浏览记录。",
                FontSize = 12,
                Foreground = TextFaint,
            });
            return;
        }

        var shown = Math.Min(entries.Count, 20);
        for (var i = 0; i < shown; i++)
        {
            var entry = entries[i];

            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontSize = 12,
                Foreground = TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{entry.Time:MM-dd HH:mm}　{entry.Url}",
                FontSize = 10.5,
                Foreground = TextFaint,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (i > 0)
                _historyList.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 3, 0, 3) });

            _historyList.Children.Add(row);
        }

        if (entries.Count > shown)
        {
            _historyList.Children.Add(new TextBlock
            {
                Text = $"……共 {entries.Count} 条，只显示最近 {shown} 条",
                FontSize = 11,
                Foreground = TextFaint,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    // ============================================================
    // 小工具
    // ============================================================

    /// <summary>统一兜异常。<b>吞掉不记 = 点了没反应</b>，那是最难查的一类问题。</summary>
    private async Task GuardAsync(string what, Func<Task> action)
    {
        if (_busy) return;

        _busy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{what}已取消。");
        }
        catch (Exception ex)
        {
            _context.Log.Error($"设置页{what}失败", ex);
            SetStatus($"{what}失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
        _statusText.IsVisible = true;
    }

    private void OpenFolder(string path)
    {
        try
        {
            // 目录可能还不存在（用户没打开过浏览器）：先建出来，否则资源管理器会弹错误框
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            SetStatus($"已打开：{path}");
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"打开数据目录失败：{ex.Message}");
            SetStatus($"打开目录失败：{ex.Message}");
        }
    }

    private static Border Card(Control content) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#FBFCFD")),
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14, 12, 14, 12),
        Child = content,
    };

    private static Control Separator() => new Border { Height = 1, Background = Line };

    private static Button Secondary(string text, Action onClick, string? tip = null)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D8DEE8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        if (tip is not null) ToolTip.SetTip(button, tip);

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// 危险按钮：<b>第一次点只变成「确认…」，第二次才真的执行</b>，5 秒内没点就自己复原。
    /// 这比弹窗轻，又比单击直删稳。
    /// </summary>
    private Button DangerButton(string text, Func<Task> action)
    {
        var armed = false;
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.Parse("#FEF2F2")),
            Foreground = Danger,
            BorderBrush = new SolidColorBrush(Color.Parse("#FBD5D5")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        void Restore()
        {
            armed = false;
            button.Content = text;
            button.Background = new SolidColorBrush(Color.Parse("#FEF2F2"));
            button.Foreground = Danger;
        }

        button.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                button.Content = "再点一次确认重置";
                button.Background = Danger;
                button.Foreground = Brushes.White;

                // 5 秒没继续点就复原：否则这个按钮会一直停在「确认」状态，
                // 下次用户误点就直接删了数据
                _ = Task.Delay(5000).ContinueWith(
                    _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (armed) Restore(); }),
                    TaskScheduler.Default);

                return;
            }

            Restore();
            _ = action();
        };

        return button;
    }
}
