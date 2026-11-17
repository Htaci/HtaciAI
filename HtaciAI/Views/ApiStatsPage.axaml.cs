using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Storage;

namespace HtaciAI.Views;

/// <summary>
/// 统计页：把所有出站 API 请求的流水（<c>api_requests</c>）聚合成「花了多少、花在哪、快不快、错在哪」。
///
/// <b>数据来源只有一张表</b>：一行 = 一次 HTTP 往返（含自动重试的每次尝试），
/// 在 <see cref="Services.ApiRecordingChatClient"/> 里写、在这里读。没有预汇总表 ——
/// 几十万行对 SQLite 来说是现算得起的量，省下的复杂度换来「口径随时能改」。
/// </summary>
public partial class ApiStatsPage : UserControl
{
    // ---- 配色：与工具页、MCP 页同一套 ----
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    /// <summary>时间范围。<c>Days &lt; 0</c> = 全部。</summary>
    private static readonly (string Label, int Days)[] Ranges =
    {
        ("今天", 1),
        ("7 天", 7),
        ("30 天", 30),
        ("90 天", 90),
        ("全部", -1),
    };

    /// <summary>默认看 30 天：既看得出趋势，又不至于把「今天刚花了多少」淹掉。</summary>
    private int _rangeIndex = 2;

    private readonly List<(Border Box, TextBlock Label)> _rangeButtons = new();

    private bool _loading;
    private bool _loadedOnce;

    public ApiStatsPage()
    {
        InitializeComponent();
        BuildHeader();
        Subtitle.Text = "正在加载…";

        // 页面是「新标签页 → 统计」就地替换进来的：构造完还没有可视父级，
        // 这时拉数据等于为一次没人看的渲染去扫库。挂上可视树之后再拉。
        AttachedToVisualTree += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _ = RefreshAsync();
        };
    }

    // ============================================================
    // 顶部操作区
    // ============================================================

    private void BuildHeader()
    {
        Actions.Children.Clear();
        _rangeButtons.Clear();

        var groupRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };

        for (var i = 0; i < Ranges.Length; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = Ranges[i].Label,
                FontSize = 12,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var box = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 6),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = label,
            };
            box.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (_rangeIndex == index) return;
                _rangeIndex = index;
                RefreshRangeButtons();
                _ = RefreshAsync();
            };

            _rangeButtons.Add((box, label));
            groupRow.Children.Add(box);
        }

        Actions.Children.Add(new Border
        {
            Background = Surface,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = groupRow,
        });

        var refresh = CreateActionButton("\uF13E", "刷新", TextMuted, LineBrush, Surface);
        refresh.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _ = RefreshAsync();
        };
        Actions.Children.Add(refresh);

        var clear = CreateActionButton(
            "\uF34D", "清空记录", Danger,
            new SolidColorBrush(Color.Parse("#FBD5D5")),
            new SolidColorBrush(Color.Parse("#FEF2F2")));
        clear.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            await ClearAsync();
        };
        Actions.Children.Add(clear);

        RefreshRangeButtons();
    }

    private void RefreshRangeButtons()
    {
        for (var i = 0; i < _rangeButtons.Count; i++)
        {
            var selected = i == _rangeIndex;
            _rangeButtons[i].Box.Background = selected ? Brushes.White : Brushes.Transparent;
            _rangeButtons[i].Label.Foreground = selected ? TextPrimary : TextMuted;
            _rangeButtons[i].Label.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>带图标的小按钮（边框 + 悬停变色），与设置页那个 CreateActionButton 同一套观感。</summary>
    private static Border CreateActionButton(string glyph, string text, IBrush fg, IBrush border, IBrush hover)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = AppFonts.Icons,
            FontSize = 13,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var button = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };

        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    // ============================================================
    // 取数与渲染
    // ============================================================

    private (long From, long? To) ResolveRange()
    {
        var days = Ranges[_rangeIndex].Days;
        if (days < 0) return (0, null);

        var today = DateTime.Today;
        var start = days <= 1 ? today : today.AddDays(-(days - 1));
        return (new DateTimeOffset(start).ToUnixTimeMilliseconds(), null);
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;

        try
        {
            var (from, to) = ResolveRange();

            // 五条聚合互不依赖，并发跑：串行时切一次范围的等待时间是五倍
            var summaryTask = ApiRequestRepository.GetSummaryAsync(from, to);
            var dailyTask = ApiRequestRepository.GetDailyAsync(from, to);
            var modelsTask = ApiRequestRepository.GetByModelAsync(from, to);
            var kindsTask = ApiRequestRepository.GetByKindAsync(from, to);
            var recentTask = ApiRequestRepository.GetRecentAsync(from, to, 100);

            await Task.WhenAll(summaryTask, dailyTask, modelsTask, kindsTask, recentTask);

            Render(
                summaryTask.Result,
                dailyTask.Result,
                modelsTask.Result,
                kindsTask.Result,
                recentTask.Result,
                from);
        }
        catch (Exception ex)
        {
            ContentPanel.Children.Clear();
            ContentPanel.Children.Add(Card("加载失败", null, new TextBlock
            {
                Text = ex.Message,
                FontSize = 12.5,
                Foreground = Danger,
                TextWrapping = TextWrapping.Wrap,
            }));
            Subtitle.Text = "读取统计失败";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render(
        ApiRequestRepository.Summary summary,
        List<ApiRequestRepository.DayBucket> daily,
        List<ApiRequestRepository.ModelBucket> models,
        List<ApiRequestRepository.KindBucket> kinds,
        List<ApiRequest> recent,
        long from)
    {
        var settings = AppSettingsStore.Current;
        var logOn = settings.ApiRequestLogEnabled;
        var retention = settings.ApiRequestLogRetentionDays;
        var retentionText = retention <= 0 ? "永久" : retention + " 天";

        Subtitle.Text = logOn
            ? $"共 {summary.Total:N0} 次请求 · {DescribeRange()} · 记录保留 {retentionText}"
            : $"记录已关闭（设置 → 常规设置 → API 请求统计）· 下面是关闭前的历史 · {DescribeRange()}";

        ContentPanel.Children.Clear();

        if (summary.Total == 0)
        {
            ContentPanel.Children.Add(Card("还没有数据", null, new TextBlock
            {
                Text = logOn
                    ? "这个时间范围里一次 API 请求都没有。发一条消息，或者把范围切到「全部」再看。"
                    : "记录功能关着，所以没有新数据。已有的历史仍然会在范围匹配时显示。",
                FontSize = 12.5,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
            }));
            return;
        }

        ContentPanel.Children.Add(BuildOverview(summary));
        ContentPanel.Children.Add(Card(
            "请求趋势",
            "柱子高度 = 当日请求数。悬停看那天的用量与金额。",
            BuildChart(daily, from, summary.Currency)));

        ContentPanel.Children.Add(BuildModelCard(models));
        ContentPanel.Children.Add(BuildKindCard(kinds, summary));
        ContentPanel.Children.Add(BuildRecentCard(recent));
    }

    private string DescribeRange()
    {
        var days = Ranges[_rangeIndex].Days;
        if (days < 0) return "全部时间";
        if (days <= 1) return "今天";
        return $"最近 {days} 天";
    }

    // ---- 概览 ----

    private static Control BuildOverview(ApiRequestRepository.Summary summary)
    {
        var tiles = new WrapPanel { Orientation = Orientation.Horizontal };

        var okRate = summary.Total > 0 ? (double)summary.OkCount / summary.Total : 0;

        tiles.Children.Add(MetricTile("请求次数", summary.Total.ToString("N0"), $"{summary.OkCount:N0} 次成功"));
        tiles.Children.Add(MetricTile(
            "成功率",
            okRate.ToString("P1"),
            summary.FailedCount > 0 ? $"{summary.FailedCount:N0} 次失败" : "没有失败",
            okRate >= 0.95 ? Ok : Warn));
        tiles.Children.Add(MetricTile(
            "金额",
            FormatCost(summary.Cost, summary.Currency),
            summary.Cost <= 0 ? "模型价格未配置" : "按请求当时的价格累计"));
        tiles.Children.Add(MetricTile(
            "输入 token",
            summary.InputTokens.ToString("N0"),
            summary.CacheHitTokens > 0 ? $"缓存命中 {summary.CacheHitTokens:N0}" : "没有缓存命中"));
        // 缓存命中率单独占一格：它是省钱的主要来源（命中部分按更低的价格计费），
        // 混在「输入 token」的小字里没人会去读
        tiles.Children.Add(MetricTile(
            "缓存命中率",
            summary.CacheHitRate is { } hitRate ? hitRate.ToString("P1") : "—",
            summary.CacheHitRate is null
                ? "没有输入 token"
                : $"命中 {summary.CacheHitTokens:N0} / 输入 {summary.InputTokens:N0}",
            summary.CacheHitRate is { } rate && rate >= 0.5 ? Ok : null));
        tiles.Children.Add(MetricTile("输出 token", summary.OutputTokens.ToString("N0"), null));
        tiles.Children.Add(MetricTile(
            "平均耗时",
            FormatDuration(summary.AvgDurationMs),
            summary.AvgTtftMs is { } ttft ? $"平均首字 {FormatDuration(ttft)}" : "没有首字数据"));

        // 取消的请求单独说一句：它既不算成功也不算失败，混进成功率会让人误解
        if (summary.CancelledCount > 0)
            tiles.Children.Add(MetricTile("已取消", summary.CancelledCount.ToString("N0"), "用户点了「停止」"));

        return Card("概览", null, tiles);
    }

    private static Control MetricTile(string label, string value, string? hint, IBrush? valueBrush = null)
    {
        var stack = new StackPanel { Spacing = 2, Width = 168, Margin = new Thickness(0, 0, 8, 10) };

        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = TextFaint,
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = valueBrush ?? TextPrimary,
        });

        if (!string.IsNullOrEmpty(hint))
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
            });

        return stack;
    }

    // ---- 趋势图 ----

    /// <summary>
    /// 按天柱状图。<b>自己画，不引图表库</b>：一根柱子就是一个定高 Border，
    /// 高度按当天请求数占峰值的比例算。要回答的只有「哪几天忙」，坐标轴与刻度都是多余的。
    /// </summary>
    private static Control BuildChart(List<ApiRequestRepository.DayBucket> daily, long from, string? currency)
    {
        var byDay = new Dictionary<string, ApiRequestRepository.DayBucket>(StringComparer.Ordinal);
        foreach (var bucket in daily) byDay[bucket.Day] = bucket;

        var today = DateTime.Today;

        // 起始日：显式范围按范围起点；「全部」按第一条记录的日期（最多回看 120 天）
        DateTime start;
        if (from > 0)
            start = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime.Date;
        else if (daily.Count > 0 && DateTime.TryParse(daily[0].Day, out var first))
            start = first.Date;
        else
            start = today.AddDays(-29);

        var days = new List<DateTime>();
        for (var d = start; d <= today; d = d.AddDays(1)) days.Add(d);
        if (days.Count == 0) days.Add(today);
        if (days.Count > 120) days = days.Skip(days.Count - 120).ToList();

        var counts = days
            .Select(d => byDay.TryGetValue(d.ToString("yyyy-MM-dd"), out var b) ? b.Count : 0)
            .ToList();
        var max = Math.Max(1, counts.Max());

        const double chartHeight = 132;

        var bars = new Grid { Height = chartHeight, VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < days.Count; i++)
            bars.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var key = day.ToString("yyyy-MM-dd");
            var has = byDay.TryGetValue(key, out var bucket);
            var count = counts[i];

            var bar = new Border
            {
                Height = count <= 0 ? 2 : Math.Max(3, count / (double)max * chartHeight),
                Margin = new Thickness(1.5, 0),
                CornerRadius = new CornerRadius(2),
                Background = count <= 0 ? LineBrush : Accent,
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            if (has && bucket is not null)
                ToolTip.SetTip(bar,
                    $"{day:MM-dd}\n{count:N0} 次请求 · 失败 {bucket.Failed:N0}\n" +
                    $"输入 {bucket.InputTokens:N0} · 输出 {bucket.OutputTokens:N0}\n" +
                    $"金额 {FormatCost(bucket.Cost, currency)}");

            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }

        var axis = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        axis.Children.Add(new TextBlock
        {
            Text = days[0].ToString("MM-dd"),
            FontSize = 11,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        axis.Children.Add(new TextBlock
        {
            Text = $"峰值 {max:N0} 次/天 · {days.Count} 天",
            FontSize = 11,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        axis.Children.Add(new TextBlock
        {
            Text = days[^1].ToString("MM-dd"),
            FontSize = 11,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Right,
        });

        return new StackPanel { Children = { bars, axis } };
    }

    // ---- 按模型 ----

    private static Control BuildModelCard(List<ApiRequestRepository.ModelBucket> models)
    {
        if (models.Count == 0)
            return Card("按模型", null, EmptyHint("这个范围里没有请求。"));

        var table = new Table("*", "72", "92", "72", "92", "92", "92", "56");
        table.AddHeader("模型", "请求", "输入", "命中率", "输出", "金额", "平均耗时", "失败");

        var index = 0;
        foreach (var m in models)
        {
            var nameCell = new StackPanel { Spacing = 1 };
            nameCell.Children.Add(new TextBlock
            {
                Text = m.ModelName ?? "(未知模型)",
                FontSize = 12.5,
                Foreground = TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(m.ProviderName))
                nameCell.Children.Add(new TextBlock
                {
                    Text = m.ProviderName,
                    FontSize = 11,
                    Foreground = TextFaint,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

            table.AddRow(index++ % 2 == 1,
                nameCell,
                RightCell(m.Count.ToString("N0")),
                RightCell(m.InputTokens.ToString("N0")),
                RightCell(
                    m.CacheHitRate is { } rate ? rate.ToString("P1") : "—",
                    m.CacheHitRate is { } r && r >= 0.5 ? Ok : TextFaint),
                RightCell(m.OutputTokens.ToString("N0")),
                RightCell(FormatCost(m.Cost, null)),
                RightCell(FormatDuration(m.AvgDurationMs)),
                RightCell(
                    m.Failed > 0 ? m.Failed.ToString("N0") : "—",
                    m.Failed > 0 ? Danger : TextFaint));
        }

        return Card("按模型", "金额按每次请求写入时的价格累计 —— 之后改价格不会追溯历史。", table.Root);
    }

    // ---- 按用途 ----

    private static Control BuildKindCard(List<ApiRequestRepository.KindBucket> kinds, ApiRequestRepository.Summary summary)
    {
        if (kinds.Count == 0)
            return Card("按用途", null, EmptyHint("这个范围里没有请求。"));

        var table = new Table("*", "78", "78", "96", "96", "96");
        table.AddHeader("用途", "请求", "占比", "输入", "输出", "金额");

        var index = 0;
        foreach (var k in kinds)
        {
            var share = summary.Total > 0 ? (double)k.Count / summary.Total : 0;

            table.AddRow(index++ % 2 == 1,
                new TextBlock
                {
                    Text = ApiRequestKinds.Label(k.Kind),
                    FontSize = 12.5,
                    Foreground = TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                RightCell(k.Count.ToString("N0")),
                RightCell(share.ToString("P0")),
                RightCell(k.InputTokens.ToString("N0")),
                RightCell(k.OutputTokens.ToString("N0")),
                RightCell(FormatCost(k.Cost, null)));
        }

        return Card("按用途", "对话之外也在花钱：话题命名、上下文压缩、连接测试各算一类。", table.Root);
    }

    // ---- 最近请求 ----

    private static Control BuildRecentCard(List<ApiRequest> recent)
    {
        if (recent.Count == 0)
            return Card("最近请求", null, EmptyHint("这个范围里没有请求。"));

        var table = new Table("118", "74", "*", "60", "62", "62", "94", "78");
        table.AddHeader("时间", "用途", "模型", "状态", "耗时", "首字", "输入/输出", "金额");

        var index = 0;
        foreach (var r in recent)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(r.StartedAt).LocalDateTime;

            var kind = r.Attempt > 1
                ? $"{ApiRequestKinds.Label(r.Kind)} #{r.Attempt}"
                : ApiRequestKinds.Label(r.Kind);

            var modelCell = new TextBlock
            {
                Text = r.ModelName ?? "—",
                FontSize = 12.5,
                Foreground = r.Status == ApiRequestStatus.Failed ? Danger : TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (r.ErrorMessage is { Length: > 0 } err) ToolTip.SetTip(modelCell, err);

            var (statusText, statusBrush) = r.Status switch
            {
                ApiRequestStatus.Ok => ("成功", Ok),
                ApiRequestStatus.Cancelled => ("已取消", TextFaint),
                _ => (ApiErrorKinds.Label(r.ErrorKind), Danger),
            };

            var tokens = r.InputTokens is null && r.OutputTokens is null
                ? "—"
                : $"{(r.InputTokens ?? 0):N0}/{(r.OutputTokens ?? 0):N0}";

            table.AddRow(index++ % 2 == 1,
                new TextBlock
                {
                    Text = time.ToString("MM-dd HH:mm:ss"),
                    FontSize = 12,
                    Foreground = TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = kind,
                    FontSize = 12,
                    Foreground = TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                modelCell,
                new TextBlock
                {
                    Text = statusText,
                    FontSize = 12,
                    Foreground = statusBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                RightCell(FormatDuration(r.DurationMs)),
                RightCell(r.TtftMs is { } ttft ? FormatDuration(ttft) : "—"),
                RightCell(tokens),
                RightCell(r.Cost is { } c ? FormatCost(c, r.Currency) : "—"));
        }

        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                table.Root,
                new TextBlock
                {
                    Text = $"只列最近 {recent.Count} 条。更早的请缩小时间范围，或直接查库（表 api_requests）。",
                    FontSize = 11,
                    Foreground = TextFaint,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        return Card("最近请求", "每次往返一行，含自动重试的每次尝试（用途后面带 #2 表示第二次尝试）。", body);
    }

    // ============================================================
    // 清空
    // ============================================================

    private async Task ClearAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "清空全部 API 请求流水？统计页的历史会消失，已经发生过的对话内容不受影响。这个操作不能撤销。",
            "清空统计记录");
        if (!confirmed) return;

        await ApiRequestRepository.ClearAsync();
        await RefreshAsync();
    }

    // ============================================================
    // 表格（项目里没有 DataGrid，统一手绘）
    // ============================================================

    /// <summary>
    /// 一张表：一个 N 列的 Grid，每行由「整行背景条 + 每列一个单元格」组成。
    /// <b>列宽只在构造时声明一次</b>，数据行共用同一组列定义 —— 分两次手写 Grid 迟早会错位。
    /// </summary>
    private sealed class Table
    {
        private readonly int _columnCount;
        private int _row;

        public Grid Root { get; } = new();

        public Table(params string[] widths)
        {
            _columnCount = widths.Length;
            foreach (var width in widths)
                Root.ColumnDefinitions.Add(new ColumnDefinition(
                    width == "*"
                        ? new GridLength(1, GridUnitType.Star)
                        : new GridLength(double.Parse(width, CultureInfo.InvariantCulture))));
        }

        /// <summary>表头：第 0 列左对齐（名称），其余右对齐（数字）。</summary>
        public void AddHeader(params string[] cells)
        {
            var controls = new List<Control>();
            for (var i = 0; i < cells.Length; i++)
                controls.Add(i == 0
                    ? new TextBlock
                    {
                        Text = cells[i],
                        FontSize = 11,
                        Foreground = TextFaint,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : RightCell(cells[i], TextFaint, 11));

            AddRow(false, controls.ToArray());
        }

        public void AddRow(bool stripe, params Control[] cells)
        {
            Root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // 整行底色：单独一条 Border 跨满所有列，单元格本身不设背景 —— 斑马纹只改这一层
            var background = new Border
            {
                Background = stripe ? Surface : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
            };
            Grid.SetRow(background, _row);
            Grid.SetColumn(background, 0);
            Grid.SetColumnSpan(background, _columnCount);
            Root.Children.Add(background);

            for (var i = 0; i < cells.Length && i < _columnCount; i++)
            {
                var host = new Border
                {
                    Padding = new Thickness(8, 6),
                    Child = cells[i],
                };
                Grid.SetRow(host, _row);
                Grid.SetColumn(host, i);
                Root.Children.Add(host);
            }

            _row++;
        }
    }

    private static TextBlock RightCell(string text, IBrush? brush = null, double size = 12.5) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? TextMuted,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Control EmptyHint(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12.5,
        Foreground = TextFaint,
        TextWrapping = TextWrapping.Wrap,
    };

    // ---- 卡片外壳 ----

    private static Border Card(string title, string? hint, Control body)
    {
        var content = new StackPanel { Spacing = 6 };

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
        });

        if (!string.IsNullOrEmpty(hint))
            content.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11.5,
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

    // ---- 数字格式化 ----

    /// <summary>
    /// 金额：小于 0.0001 一律写成「&lt;0.0001」，免得一屏 0.0000 让人以为没花钱。
    /// 价格一个都没配时上游给的是 0，这里显示「—」而不是「¥0」。
    /// </summary>
    private static string FormatCost(double cost, string? currency)
    {
        if (cost <= 0) return "—";

        var symbol = currency switch
        {
            null or "" => "",
            "CNY" => "¥",
            "USD" => "$",
            "EUR" => "€",
            _ => currency + " ",
        };

        return cost < 0.0001
            ? symbol + "<0.0001"
            : symbol + cost.ToString(cost >= 1 ? "N2" : "0.####", CultureInfo.CurrentCulture);
    }

    private static string FormatDuration(double? ms)
    {
        if (ms is null) return "—";
        var value = ms.Value;
        return value < 1000 ? $"{value:0} ms" : $"{value / 1000:0.0} s";
    }
}
