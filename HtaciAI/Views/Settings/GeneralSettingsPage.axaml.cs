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
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Network;
using HtaciAI.Services.Plugins;
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

    // 回复总结那张卡：三组单选 + 一条「保留 0 轮」的警告条
    private readonly List<OptionRow> _summaryKeepRows = new();
    private readonly List<OptionRow> _summaryThresholdRows = new();
    private readonly List<OptionRow> _summaryIdleRows = new();
    private Border? _summaryWarning;

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

        // 总开关在「插件」页里也能改，两处必须显示同一个值。
        // 用「先减再加」保证幂等：设置页的小节之间来回切换会反复触发 attach。
        AttachedToVisualTree += (_, _) =>
        {
            PluginManager.Instance.Changed -= OnPluginsChanged;
            PluginManager.Instance.Changed += OnPluginsChanged;
            OnPluginsChanged();
        };
        DetachedFromVisualTree += (_, _) => PluginManager.Instance.Changed -= OnPluginsChanged;
    }

    private void Build()
    {
        ContentPanel.Children.Add(CreateSessionCacheCard());
        ContentPanel.Children.Add(CreateToolRoundsCard());
        ContentPanel.Children.Add(CreateSelfExtensionCard());
        ContentPanel.Children.Add(CreateTaskObedienceCard());
        ContentPanel.Children.Add(CreatePluginsCard());
        ContentPanel.Children.Add(CreateCompressionCard());
        ContentPanel.Children.Add(CreateTurnSummaryCard());
        ContentPanel.Children.Add(CreateApiRequestLogCard());
        ContentPanel.Children.Add(CreateProxyCard());
        ContentPanel.Children.Add(CreateUpdateCard());
        RefreshCacheRows();
        RefreshThresholdRows();
        RefreshTurnSummaryRows();
    }

    // ---- 卡片：直接执行任务 ----

    /// <summary>
    /// 「直接执行任务」。
    ///
    /// 关着时提示词与以前一字不差；打开后在 system 末尾追加一段要求「按用户明确要求执行、
    /// 不用空泛理由推脱、不要在信息齐全时反复确认」的指令（见
    /// <see cref="Services.TaskExecutionDirective"/>）。
    ///
    /// <b>它不是「关掉安全」</b>：真实危害那几条底线仍写在追加段里。
    /// 工具审批与权限档位也不受它影响 —— 那是另一套、在工具调用时生效的机制。
    /// </summary>
    private Control CreateTaskObedienceCard()
    {
        var body = CreateSwitch(
            "直接执行我要求的任务",
            "开启后会在系统提示词里追加一段要求：把你说的事当作主线任务直接做，不要用「我是 AI」「这可能违规」" +
            "这类理由推脱，也不要在信息已经足够时反复征求同意。适合用来测试自动化类任务。" +
            "违法内容、攻击他人系统、窃取凭据这类请求仍然会被拒绝；工具的权限审批也不受影响。",
            _data.ObeyUserTasks,
            value =>
            {
                _data.ObeyUserTasks = value;
                Persist();
            });

        return CreateCard("执行方式", "控制模型是「先问再做」还是「直接做」。下一轮发消息起生效。", body);
    }

    // ---- 卡片：AI 自我扩展 ----

    /// <summary>
    /// 「允许 AI 创建工具与技能」。
    ///
    /// 关掉只是让这两个工具从请求的 tools 数组里消失 —— 权限审批、重名检查、路径约束
    /// 一样都不放松。它控制的是「模型能不能碰到这个能力」，不是「碰到之后有多宽松」。
    /// </summary>
    private Control CreateSelfExtensionCard()
    {
        var toggle = new CheckBox
        {
            Content = "允许 AI 创建工具与技能",
            FontSize = 13,
            IsChecked = _data.AllowAgentSelfExtension,
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_suppressSave) return;
            _data.AllowAgentSelfExtension = toggle.IsChecked == true;
            Persist();
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                toggle,
                new TextBlock
                {
                    Text = "开启后模型可以调用 create_tool / create_skill 自己写脚本工具和技能。" +
                           "新建的工具一律是「危险」等级，每次创建都要你在审批卡片上确认；" +
                           "创建完成的工具和技能不会自动启用，需要你到「工具」「技能」页勾选或加载。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        return CreateCard("AI 自我扩展", "让 AI 为自己新增工具与技能。关掉不影响已经建好的那些。", body);
    }

    // ---- 卡片：插件 ----

    /// <summary>
    /// 「启用插件」。默认关闭，因为加载插件就是执行第三方代码。
    ///
    /// 这里只放总开关与目录入口；逐个插件的启停、能力清单、崩溃记录在 M6 的插件页里 ——
    /// 那是另一块界面，先把它折在设置页会显得拥挤。
    /// </summary>
    private Control CreatePluginsCard()
    {
        var toggle = new CheckBox
        {
            Content = "启用插件",
            FontSize = 13,
            IsChecked = _data.EnablePlugins,
        };
        _pluginsToggle = toggle;
        toggle.IsCheckedChanged += async (_, _) =>
        {
            if (_suppressSave) return;

            _data.EnablePlugins = toggle.IsChecked == true;
            Persist();

            // 开关切换立即生效：开 = 扫描并加载，关 = 全部卸载
            try
            {
                await PluginManager.Instance.ReloadAsync();
            }
            catch (Exception ex)
            {
                if (_pluginsStatus is null) return;
                _pluginsStatus.Foreground = Danger;
                _pluginsStatus.Text = "加载插件失败：" + ex.Message;
                return;
            }

            RefreshPluginStatus();
        };

        _pluginsStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        RefreshPluginStatus();

        var openDir = CreateActionButton("打开插件目录");
        openDir.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            OpenPluginsDirectory();
        };

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Children = { openDir, _pluginsStatus },
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                toggle,
                new TextBlock
                {
                    Text = "插件是第三方代码，运行在本程序进程内 —— 它和主程序享有同样的权限，" +
                           "能读写文件、能建自己的窗口。所以默认关闭，打开之前请确认来源可信。\n" +
                           "疑似导致崩溃的插件会在下次启动时自动跳过加载，并保留崩溃日志。\n" +
                           "逐个插件的启停、能力清单与崩溃记录在「插件」页（新标签页 → 插件）。",
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                row,
            },
        };

        return CreateCard("插件", "扩展界面与对话流程。加载后按插件声明的能力生效。", body);
    }

    private TextBlock? _pluginsStatus;
    private CheckBox? _pluginsToggle;

    /// <summary>
    /// 插件管理器状态变了：把总开关与状态文字拉回当前值。
    /// 同步 <see cref="CheckBox.IsChecked"/> 会触发 <c>IsCheckedChanged</c>，
    /// 必须用 <see cref="_suppressSave"/> 挡住 —— 否则会再存一次盘、再重载一遍插件。
    /// </summary>
    private void OnPluginsChanged()
    {
        if (_pluginsToggle is null) return;

        var wasSuppressed = _suppressSave;
        _suppressSave = true;
        try
        {
            _pluginsToggle.IsChecked = _data.EnablePlugins;
        }
        finally
        {
            _suppressSave = wasSuppressed;
        }

        RefreshPluginStatus();
    }

    private void RefreshPluginStatus()
    {
        if (_pluginsStatus is null) return;

        if (!_data.EnablePlugins)
        {
            _pluginsStatus.Foreground = TextFaint;
            _pluginsStatus.Text = "未启用";
            return;
        }

        var all = PluginManager.Instance.GetAll();
        var loaded = all.Count(p => p.State == PluginRunState.Loaded);
        _pluginsStatus.Foreground = all.Count == 0 ? TextFaint : Ok;
        _pluginsStatus.Text = all.Count == 0
            ? "已启用，但没有找到插件"
            : $"已启用：{loaded}/{all.Count} 个插件运行中";
    }

    private static void OpenPluginsDirectory()
    {
        try
        {
            Directory.CreateDirectory(PluginManager.PluginsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = PluginManager.PluginsDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开资源管理器不是致命问题，用户还能手动过去
        }
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

    // ---- 卡片：API 请求统计 ----

    /// <summary>保留策略的单选行。</summary>
    private readonly List<OptionRow> _logRetentionRows = new();

    /// <summary>
    /// 「API 请求统计」：统计页的数据开关与保留策略。
    ///
    /// <b>默认开启、默认保留 365 天</b>：不记的话「这个月花了多少钱」永远答不出来，
    /// 而一行流水只有几十字节。关掉只是停止写入，已有历史仍然在（要清去统计页手动清）。
    ///
    /// 保留策略改完立即按新值清一次 —— 否则用户得重启才能确认「旧记录真的被删了」。
    /// </summary>
    private Control CreateApiRequestLogCard()
    {
        var body = new StackPanel { Spacing = 2 };

        body.Children.Add(CreateSwitch(
            "记录 API 请求流水",
            "每次请求 API 都记一行：时间、服务商、模型、状态码、耗时、首字延迟、token 用量，" +
            "以及按写入当时的模型价格算出的金额。「统计」页的全部数字都来自这张表。" +
            "不记请求正文、不记响应正文、不记 API Key；地址只存到路径，查询串会被剥掉。",
            _data.ApiRequestLogEnabled,
            value =>
            {
                _data.ApiRequestLogEnabled = value;
                RefreshLogRetentionRows();
                Persist();
            }));

        body.Children.Add(CreateSectionLabel("保留多久"));

        var options = new (int Days, string Title, string Description)[]
        {
            (30, "30 天", "只看最近一个月，库文件最小"),
            (90, "90 天", "适合对一个季度做对照"),
            (180, "180 天", "半年的成本趋势"),
            (365, "365 天（默认）", "默认值：一整年的用量与花销都留着"),
            (0, "永久", "永不自动删除。重度使用一年约几十万行，库文件会一直长"),
        };

        foreach (var (days, title, description) in options)
        {
            var row = CreateOptionRow(title, description);
            row.Row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _data.ApiRequestLogRetentionDays = days;
                RefreshLogRetentionRows();
                Persist();
                _ = ApiRequestRepository.PurgeAsync(days);
            };
            _logRetentionRows.Add(row with { Value = days });
            body.Children.Add(row.Row);
        }

        body.Children.Add(CreateSectionHint(
            "超期的记录在启动时清一次；也可以到「统计」页手动清空。关掉记录不会删掉已有的历史。"));

        RefreshLogRetentionRows();

        return CreateCard(
            "API 请求统计",
            "统计页的数据来源。默认开启：每行只有几十字节，换来的是「这个月花了多少钱」答得出来。",
            body);
    }

    private void RefreshLogRetentionRows()
    {
        foreach (var row in _logRetentionRows)
        {
            var selected = row.Value == _data.ApiRequestLogRetentionDays;
            row.Dot.IsVisible = selected;
            row.Row.Opacity = _data.ApiRequestLogEnabled ? 1 : 0.45;
            row.Row.Background = selected ? Surface : Brushes.Transparent;
        }
    }

    // ---- 卡片 1：会话窗口缓存 ----

    private Control CreateSessionCacheCard()
    {
        // 一档 = 一分钟数；0 = 不缓存；null 表示「一直保留」
        var options = new (int? Minutes, string Title, string Description)[]
        {
            (0, "不缓存", "切走就释放窗口，切回来重新读取历史。会话极多、在意内存时用它"),
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
            "切换会话时不再丢弃上一个窗口：保留期内切回来直接复用，不会重新读取历史、重建消息列表。" +
            "选「不缓存」则每次切回都重新加载，内存占用最低。" +
            "还在跑任务的窗口不会被释放（否则同一个会话会同时存在两份窗口）。",
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

    // ---- 卡片 3：回复总结 ----

    /// <summary>
    /// 「回复总结」：把每一轮 AI 回复压成摘要，构建上下文时用它替代那一轮的原文。
    ///
    /// 与上面的「上下文压缩」是两件事：那个是整体压缩（同时是上下文的分界线），
    /// 这个是轮内替换，不影响分界线，也不会让模型丢掉最近几轮。
    ///
    /// <b>总开关默认关闭</b>：关着时下面几项一律不生效，提示词与上下文都和升级前一致。
    /// </summary>
    private Control CreateTurnSummaryCard()
    {
        _summaryWarning = CreateWarningBar(
            "保留 0 轮意味着模型只看到总结：最近的操作风格、刚确认过的细节都不在上下文里，" +
            "很容易重复已经做过的工作。除非上下文实在放不下，建议至少保留 1 轮。");

        var body = new StackPanel { Spacing = 2 };

        body.Children.Add(CreateSwitch(
            "启用回复总结",
            "把每一轮 AI 回复（含它的全部工具调用）压成一段摘要，之后构建上下文时用摘要替代那一轮原文。" +
            "原文不会删，仍然留在会话里可以直接查看和复制。",
            _data.TurnSummaryEnabled,
            value =>
            {
                _data.TurnSummaryEnabled = value;
                RefreshTurnSummaryRows();
                Persist();
            }));

        body.Children.Add(CreateSwitch(
            "自动总结每条回复",
            "一轮成功结束后自动总结（受上面的总开关约束）。小于下面阈值的小轮不会被总结 —— " +
            "省下来的上下文还不如那次总结本身花的多。",
            _data.AutoSummarizeReply,
            value =>
            {
                _data.AutoSummarizeReply = value;
                Persist();
            }));

        body.Children.Add(CreateSectionLabel("构建上下文时保留最近几轮原文"));
        AddSummaryRows(body, _summaryKeepRows, new (int Value, string Title, string Description)[]
        {
            (0, "0 轮", "只发总结，一条原文都不发（用户消息仍原样保留）"),
            (1, "1 轮", "省到极限：只留最后一轮原文"),
            (2, "2 轮", "偏激进"),
            (3, "3 轮（默认）", "最近的风格与结论都还在原文里"),
            (5, "5 轮", "偏保守，上下文占用更高"),
        }, value => _data.TurnSummaryKeepTurns = value);

        body.Children.Add(_summaryWarning);

        body.Children.Add(CreateSectionLabel("自动总结的体积阈值（一轮至少多大才值得总结）"));
        AddSummaryRows(body, _summaryThresholdRows, new (int Value, string Title, string Description)[]
        {
            (4000, "4000 tokens", "更省上下文，但小轮也会被总结一次"),
            (8000, "8000 tokens（默认）", "默认值：普通问答不会被总结"),
            (16000, "16000 tokens", "只有明显跑过一串工具调用的轮次才总结"),
            (32000, "32000 tokens", "最省模型请求，代价是小轮次的原文继续占着上下文"),
        }, value => _data.TurnSummaryThresholdTokens = value);

        body.Children.Add(CreateSectionLabel("空闲降级（服务端提示词缓存的保鲜时间）"));
        AddSummaryRows(body, _summaryIdleRows, new (int Value, string Title, string Description)[]
        {
            (0, "不降级", "始终按上面保留的轮数发送"),
            (3, "3 分钟", "缓存多半已经过期了"),
            (5, "5 分钟（默认）", "OpenAI 与 Claude 的提示词缓存通常在这个量级"),
            (10, "10 分钟", "缓存时间更长时用"),
        }, value => _data.TurnSummaryIdleMinutes = value,
            "距最后一条消息超过这个时间，就认为缓存已经过期，这时只发总结、不发原文 —— " +
            "反正都要重新写一遍缓存，不如少写一点。缓存还在有效期内时不动窗口，前缀不变才可能继续命中。");

        return CreateCard(
            "回复总结",
            "Agent 的一轮回复常有几十次工具调用、动辄上万 token。总结之后同样的任务只占一行摘要，" +
            "上下文可以撑得更久。会话级的一次性压缩仍在上面那张卡里，两者可以同时用。",
            body);
    }

    /// <summary>一组单选行的公共部分：建行、接点击、登记到刷新用的列表。</summary>
    private void AddSummaryRows(
        StackPanel host,
        List<OptionRow> registry,
        (int Value, string Title, string Description)[] options,
        Action<int> apply,
        string? description = null)
    {
        foreach (var (value, title, optionDescription) in options)
        {
            var row = CreateOptionRow(title, optionDescription);
            row.Row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                apply(value);
                RefreshTurnSummaryRows();
                Persist();
            };
            registry.Add(row with { Value = value });
            host.Children.Add(row.Row);
        }

        if (description is not null) host.Children.Add(CreateSectionHint(description));
    }

    private void RefreshTurnSummaryRows()
    {
        var enabled = _data.TurnSummaryEnabled;

        Mark(_summaryKeepRows, _data.TurnSummaryKeepTurns, enabled);
        Mark(_summaryThresholdRows, _data.TurnSummaryThresholdTokens, enabled);
        Mark(_summaryIdleRows, _data.TurnSummaryIdleMinutes, enabled);

        if (_summaryWarning is not null)
            _summaryWarning.IsVisible = enabled && _data.TurnSummaryKeepTurns <= 0;

        static void Mark(List<OptionRow> rows, int current, bool enabled)
        {
            foreach (var row in rows)
            {
                var selected = row.Value == current;
                row.Dot.IsVisible = selected;
                // 总开关关着时整组置灰，但仍可点：改完再打开就是新值
                row.Row.Opacity = enabled ? 1 : 0.45;
                row.Row.Background = selected ? Surface : Brushes.Transparent;
            }
        }
    }

    private static TextBlock CreateSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = TextPrimary,
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static TextBlock CreateSectionHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = TextFaint,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24, 2, 0, 6),
    };

    /// <summary>
    /// 黄色警示条（与插件页那条同一个视觉）：只在「保留 0 轮」时出现，
    /// 因为那档会让模型彻底失去最近的行为样本，属于真正需要先看一眼的取舍。
    /// </summary>
    private static Border CreateWarningBar(string text)
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

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#92400E")),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon);
        grid.Children.Add(label);
        Grid.SetColumn(label, 1);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFBEB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FDE68A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 6, 0, 6),
            IsVisible = false,
            Child = grid,
        };
    }

    // ---- 卡片：网络代理 ----

    private TextBlock? _proxyStatus;

    /// <summary>
    /// 「使用系统代理」。默认直连 —— 装了 Clash 这类工具的机器上，系统代理往往指向一个本地端口，
    /// 默认跟随会让「第一次启动就连不上」变成很难排查的问题。
    ///
    /// 切换立即生效：<see cref="HttpClientProvider"/> 会把缓存的 HttpClient 全部换掉，
    /// 之后的请求（对话 / 列模型 / 检查更新 / web_fetch / MCP）都按新设置走。
    /// </summary>
    private Control CreateProxyCard()
    {
        _proxyStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };

        var toggle = CreateSwitch(
            "使用系统代理",
            "开启后，对话、列模型、检查更新、web_fetch、MCP 这些出站请求都会经过系统代理 —— 与 Chrome / Edge 读的是同一份设置" +
            "（Windows「设置 → 网络和 Internet → 代理」）。Clash 切换「系统代理」开关后不用重启应用，下一个请求就按新设置走。" +
            "关着时全部直连，不看任何代理配置。",
            _data.UseSystemProxy,
            value =>
            {
                _data.UseSystemProxy = value;
                HttpClientProvider.UseSystemProxy = value;
                RefreshProxyStatus();
                Persist();
            });

        var body = new StackPanel
        {
            Spacing = 6,
            Children = { toggle, _proxyStatus },
        };

        RefreshProxyStatus();
        return CreateCard("网络代理", "切换后立即生效；已经发出的请求可能会被中断，重新发一次即可。", body);
    }

    private void RefreshProxyStatus()
    {
        if (_proxyStatus is null) return;

        if (!_data.UseSystemProxy)
        {
            _proxyStatus.Foreground = TextFaint;
            _proxyStatus.Text = "当前直连，不使用代理。";
            return;
        }

        var proxy = HttpClientProvider.Describe();
        var alive = proxy.Proxy is not null || proxy.FollowDefaultProxy;
        _proxyStatus.Foreground = alive ? Ok : Danger;
        _proxyStatus.Text = alive
            ? $"当前生效：{proxy.Description}（每个请求前重新读取系统设置）"
            : $"已开启，但{proxy.Description} —— 这种情况下请求仍会直连。";
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
