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
using Avalonia.Threading;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Tasks;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 定时任务页：左侧任务列表，右侧编辑并查看运行历史。
///
/// <b>触发规则按闹钟模型填</b>（种类 + 时间 + 哪几天），不是 cron 表达式 ——
/// 见 <see cref="TaskSchedule"/>。填完保存时算好 <see cref="TaskDefinition.NextRunAt"/>，
/// 调度器只认这一列。
///
/// <b>运行历史里的每一行都是一次真实会话</b>（<see cref="TaskRun"/>），点开由宿主页
/// （<c>MainWindow</c>）把会话视图打开 —— 与点系统提醒跳会话走同一条路。
/// </summary>
public partial class TasksPage : UserControl
{
    private List<TaskDefinition> _tasks = new();
    private TaskDefinition? _selected;
    private readonly List<ModelDetails> _models = new();

    /// <summary>「下次触发还有多久」要一直走，所以靠一个很轻的定时器刷新文案。</summary>
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>请求宿主打开某个会话（运行历史里点一行）。</summary>
    public event Action<string>? OpenSessionRequested;

    public TasksPage()
    {
        InitializeComponent();
        SetupButtons();
        BuildKindBox();
        BuildWeekdayChecks();
        BuildPermissionBox();

        _ticker.Tick += (_, _) => UpdateStatusLine();

        AttachedToVisualTree += (_, _) =>
        {
            _ticker.Start();
            TaskClock.Shared.Changed += OnClockChanged;
            _ = ReloadAsync();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _ticker.Stop();
            TaskClock.Shared.Changed -= OnClockChanged;
        };
    }

    private void OnClockChanged()
    {
        // 后台跑完一次后刷新列表里的「上次结果」；调度器在 UI 线程上发这个事件
        if (Dispatcher.UIThread.CheckAccess()) _ = ReloadAsync(keepSelection: true);
        else Dispatcher.UIThread.Post(() => _ = ReloadAsync(keepSelection: true));
    }

    // ============================================================
    // 静态选项
    // ============================================================

    private void BuildKindBox()
    {
        KindBox.Items.Clear();
        foreach (var item in new[] { "每天", "每周", "每月", "一次" })
            KindBox.Items.Add(item);

        KindBox.SelectedIndex = 0;
        KindBox.SelectionChanged += (_, _) => ApplyKindToFields();
    }

    private void BuildPermissionBox()
    {
        PermissionBox.Items.Clear();
        foreach (var mode in new[]
                 {
                     PermissionMode.Strict, PermissionMode.Normal, PermissionMode.Loose, PermissionMode.Free,
                 })
            PermissionBox.Items.Add(PermissionName(mode));

        PermissionBox.SelectedIndex = 1;
    }

    private static string PermissionName(PermissionMode mode) => mode switch
    {
        PermissionMode.Strict => "严格（全部确认）",
        PermissionMode.Normal => "普通（推荐）",
        PermissionMode.Loose => "宽松",
        PermissionMode.Free => "自由（不确认）",
        _ => "普通（推荐）",
    };

    /// <summary>周一为 1、周日为 7，与 <see cref="TaskSchedule"/> 的约定一致。</summary>
    private void BuildWeekdayChecks()
    {
        WeekdayPanel.Children.Clear();
        for (var day = 1; day <= 7; day++)
        {
            WeekdayPanel.Children.Add(new CheckBox
            {
                Content = "周" + TaskSchedule.WeekdayName(day),
                Tag = day,
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }
    }

    private static string KindOfIndex(int index) => index switch
    {
        1 => TaskTriggerKind.Weekly,
        2 => TaskTriggerKind.Monthly,
        3 => TaskTriggerKind.Once,
        _ => TaskTriggerKind.Daily,
    };

    private static int IndexOfKind(string kind) => kind switch
    {
        TaskTriggerKind.Weekly => 1,
        TaskTriggerKind.Monthly => 2,
        TaskTriggerKind.Once => 3,
        _ => 0,
    };

    // ============================================================
    // 按钮
    // ============================================================

    private void SetupButtons()
    {
        SetupHover(AddTaskBtn, OnAddClick);
        SetupHover(SaveBtn, () => _ = OnSaveClickAsync(), "#2F2F3C");
        SetupHover(RunNowBtn, () => _ = OnRunNowClickAsync());
        SetupHover(DeleteBtn, () => _ = OnDeleteClickAsync(), "#FECACA");
    }

    /// <summary>统一的悬停反馈：hover 底色 + 手型光标，移出恢复原背景。</summary>
    private void SetupHover(Border btn, Action onClick, string hoverBg = "#F1F3F5")
    {
        var normalBg = btn.Background;
        btn.PointerEntered += (_, _) =>
        {
            btn.Background = new SolidColorBrush(Color.Parse(hoverBg));
            btn.Cursor = new Cursor(StandardCursorType.Hand);
        };
        btn.PointerExited += (_, _) =>
        {
            btn.Background = normalBg;
            btn.Cursor = new Cursor(StandardCursorType.Arrow);
        };
        btn.PointerPressed += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void OnAddClick()
    {
        // 还没落库的新任务：保存时才 INSERT，取消就什么也不留
        _selected = new TaskDefinition
        {
            Title = "新任务",
            TriggerKind = TaskTriggerKind.Daily,
            TriggerTime = "09:00:00",
            MaxTurns = TaskRunner.DefaultMaxTurns,
            IsEnabled = true,
        };

        ShowDetail();
        TitleBox.Focus();
    }

    private async Task OnDeleteClickAsync()
    {
        if (_selected is null) return;

        // 还没保存过的新任务：直接从界面上撤掉即可
        if (_tasks.All(t => t.Id != _selected.Id))
        {
            _selected = _tasks.FirstOrDefault();
            ShowDetail();
            return;
        }

        if (!await ConfirmDialog.ShowAsync(Owner,
                $"确定删除定时任务「{_selected.Title}」吗？运行历史会一起隐藏，跑过的会话仍可在对话里回看。",
                "删除定时任务"))
            return;

        await TaskRepository.SoftDeleteDefinitionAsync(_selected.Id);
        _selected = null;
        await ReloadAsync();
    }

    private async Task OnSaveClickAsync()
    {
        if (_selected is null) return;

        if (!TryReadForm(out var error))
        {
            StatusLine.Text = "⚠ " + error;
            return;
        }

        var def = _selected;
        def.Title = TitleBox.Text?.Trim() ?? "";
        def.Prompt = PromptBox.Text ?? "";
        def.TriggerKind = KindOfIndex(KindBox.SelectedIndex);
        def.TriggerTime = TaskSchedule.NormalizeTime(TimeBox.Text);
        def.TriggerDays = ReadDays();
        def.OnceAt = ReadOnceAt();
        def.Model = SelectedModelId() ?? "";
        def.ToolPermissionMode = (PermissionMode)Math.Max(0, PermissionBox.SelectedIndex);
        def.MaxTurns = ParseMaxTurns();
        def.BaseDirectory = string.IsNullOrWhiteSpace(BaseDirBox.Text) ? null : BaseDirBox.Text.Trim();
        def.IsEnabled = EnabledCheck.IsChecked != false;

        // 用户亲手改过这条任务：连续失败计数清掉。否则「自动停用」之后重新启用，
        // 下一次再失败一次就又立刻被停用，等于永远起不来。
        if (def.IsEnabled) def.FailureCount = 0;

        // 保存时就把下一次触发算好：调度器只认 next_run_at，不现算规则
        def.NextRunAt = def.IsEnabled ? TaskSchedule.NextOccurrence(def, DateTimeOffset.Now) : null;

        await TaskRepository.SaveDefinitionAsync(def);
        TaskClock.Shared.Wake();

        await ReloadAsync(keepSelection: true);
        StatusLine.Text = def.IsEnabled
            ? "已保存。" + (def.NextRunAt is null ? "当前规则算不出下一次触发时间，请检查。" : "下次触发：" + TaskSchedule.DescribeNext(def.NextRunAt, DateTimeOffset.Now))
            : "已保存（当前是停用状态，不会触发）。";
    }

    private async Task OnRunNowClickAsync()
    {
        if (_selected is null) return;

        if (_tasks.All(t => t.Id != _selected.Id))
        {
            StatusLine.Text = "⚠ 先保存，再立即运行。";
            return;
        }

        if (!TryReadForm(out var error))
        {
            StatusLine.Text = "⚠ " + error;
            return;
        }

        StatusLine.Text = "正在运行…";
        var run = await TaskClock.Shared.RunNowAsync(_selected);

        await ReloadAsync(keepSelection: true);

        StatusLine.Text = run is null
            ? "⚠ 没能开始：别的任务正在运行，稍后再试。"
            : run.Status == TaskRunStatus.Ok
                ? "运行完成：" + (string.IsNullOrWhiteSpace(run.Summary) ? "（没有正文结果）" : run.Summary)
                : "运行失败：" + (run.Error ?? "未知原因");
    }

    // ============================================================
    // 表单 ↔ 模型
    // ============================================================

    private void ApplyKindToFields()
    {
        var kind = KindOfIndex(KindBox.SelectedIndex);

        var isOnce = kind == TaskTriggerKind.Once;
        var isWeekly = kind == TaskTriggerKind.Weekly;
        var isMonthly = kind == TaskTriggerKind.Monthly;

        TimeBox.IsVisible = !isOnce;
        OnceBox.IsVisible = isOnce;
        DaysBox.IsVisible = isMonthly;
        WeekdayPanel.IsVisible = isWeekly;

        RuleHint.Text = kind switch
        {
            TaskTriggerKind.Daily => "每天到点跑一次。",
            TaskTriggerKind.Weekly => "勾上要跑的星期（周一为 1，也按这里的勾选顺序无关）。",
            TaskTriggerKind.Monthly => "填每月几号，用逗号分隔；当月没有那一天（比如 31 号的 2 月）会跳过。",
            _ => "只跑一次，跑完自动停用。",
        };
    }

    private void ShowDetail()
    {
        var def = _selected;
        if (def is null)
        {
            EditorScroll.IsVisible = false;
            return;
        }

        EditorScroll.IsVisible = true;
        EditorTitle.Text = string.IsNullOrWhiteSpace(def.Title) ? "定时任务" : def.Title;
        EditorSub.Text = _tasks.Any(t => t.Id == def.Id)
            ? "到点自动跑一轮 Agent 对话，结果留在下面的运行历史里"
            : "新任务：填好点保存才会加入列表";

        TitleBox.Text = def.Title;
        PromptBox.Text = def.Prompt;
        KindBox.SelectedIndex = IndexOfKind(def.TriggerKind);
        TimeBox.Text = TaskSchedule.NormalizeTime(def.TriggerTime);
        DaysBox.Text = string.Join(",", def.TriggerDays);
        OnceBox.Text = def.OnceAt is { } at
            ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";
        EnabledCheck.IsChecked = def.IsEnabled;
        MaxTurnsBox.Text = def.MaxTurns.ToString(CultureInfo.InvariantCulture);
        BaseDirBox.Text = def.BaseDirectory ?? "";
        PermissionBox.SelectedIndex = (int)def.ToolPermissionMode;

        ApplyKindToFields();
        ApplyWeekdayChecks(def);
        ApplyModelSelection(def);

        UpdateStatusLine();
        _ = LoadRunsAsync(def);
    }

    private void ApplyWeekdayChecks(TaskDefinition def)
    {
        foreach (var child in WeekdayPanel.Children)
        {
            if (child is CheckBox box && box.Tag is int day)
                box.IsChecked = def.TriggerDays.Contains(day);
        }
    }

    private void ApplyModelSelection(TaskDefinition def)
    {
        ModelBox.Items.Clear();
        ModelBox.Items.Add("跟随默认模型");

        foreach (var model in _models)
            ModelBox.Items.Add(model.DisplayName + "（" + model.ProviderName + "）");

        var index = _models.FindIndex(m => m.ModelId == def.Model || m.ModelName == def.Model);
        ModelBox.SelectedIndex = index < 0 ? 0 : index + 1;
    }

    private string? SelectedModelId()
        => ModelBox.SelectedIndex <= 0 || ModelBox.SelectedIndex - 1 >= _models.Count
            ? null
            : _models[ModelBox.SelectedIndex - 1].ModelId;

    private bool TryReadForm(out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            error = "标题不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            error = "提示词不能为空 —— 定时任务没有人在旁边补充说明。";
            return false;
        }

        var kind = KindOfIndex(KindBox.SelectedIndex);

        if (kind == TaskTriggerKind.Once)
        {
            if (ReadOnceAt() is null)
            {
                error = "一次性的时间没填对，格式应是 yyyy-MM-dd HH:mm:ss。";
                return false;
            }
        }
        else if (TaskSchedule.ParseTime(TimeBox.Text) is null)
        {
            error = "时间没填对，格式应是 HH:mm:ss。";
            return false;
        }

        if (kind == TaskTriggerKind.Weekly && ReadDays().Count == 0)
        {
            error = "至少要勾一个星期。";
            return false;
        }

        if (kind == TaskTriggerKind.Monthly && ReadDays().Count == 0)
        {
            error = "至少填一个月内的日期（1-31）。";
            return false;
        }

        return true;
    }

    private List<int> ReadDays()
    {
        var kind = KindOfIndex(KindBox.SelectedIndex);

        if (kind == TaskTriggerKind.Weekly)
            return WeekdayPanel.Children.OfType<CheckBox>()
                .Where(b => b.IsChecked == true && b.Tag is int)
                .Select(b => (int)b.Tag!)
                .ToList();

        var list = new List<int>();
        foreach (var part in (DaysBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var day) && day is >= 1 and <= 31)
                list.Add(day);

        return list.Distinct().OrderBy(d => d).ToList();
    }

    private long? ReadOnceAt()
    {
        var text = OnceBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)) return null;

        var local = DateTime.SpecifyKind(date, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    private int ParseMaxTurns()
        => int.TryParse(MaxTurnsBox.Text?.Trim(), out var turns)
            ? Math.Clamp(turns, 1, TaskRunner.HardMaxTurns)
            : TaskRunner.DefaultMaxTurns;

    // ============================================================
    // 列表
    // ============================================================

    private async Task ReloadAsync(bool keepSelection = false)
    {
        var keepId = keepSelection ? _selected?.Id : null;

        _tasks = await TaskRepository.GetDefinitionsAsync();

        if (keepId is not null)
            _selected = _tasks.FirstOrDefault(t => t.Id == keepId) ?? _selected;
        else
            _selected ??= _tasks.FirstOrDefault();

        if (_selected is not null && _tasks.All(t => t.Id != _selected.Id) && !keepSelection)
            _selected = _tasks.FirstOrDefault();

        await LoadModelsAsync();
        RefreshList();
        ShowDetail();
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var list = await ModelCatalog.ListEnabledAsync();
            _models.Clear();
            _models.AddRange(list);
        }
        catch
        {
            _models.Clear();
        }
    }

    private void RefreshList()
    {
        TaskList.Children.Clear();

        if (_tasks.Count == 0)
        {
            TaskList.Children.Add(new TextBlock
            {
                Text = "还没有定时任务，点左上角新建",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(12, 8, 0, 0),
            });
            return;
        }

        foreach (var def in _tasks)
            TaskList.Children.Add(CreateTaskItem(def));
    }

    private Border CreateTaskItem(TaskDefinition def)
    {
        var isActive = _selected is not null && _selected.Id == def.Id;

        var title = new TextBlock
        {
            Text = (def.IsEnabled ? "" : "（已停用）") + def.Title,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var rule = new TextBlock
        {
            Text = TaskSchedule.Describe(def),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var next = new TextBlock
        {
            Text = def.IsEnabled
                ? "下次：" + TaskSchedule.DescribeNext(def.NextRunAt, DateTimeOffset.Now)
                : "不会触发",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(def.IsEnabled ? "#9CA3AF" : "#DC2626")),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Background = new SolidColorBrush(Color.Parse(isActive ? "#FFFFFF" : "Transparent")),
            Child = new StackPanel { Spacing = 0, Children = { title, rule, next } },
        };

        item.PointerEntered += (_, _) => item.Cursor = new Cursor(StandardCursorType.Hand);
        item.PointerPressed += (_, e) =>
        {
            _selected = def;
            RefreshList();
            ShowDetail();
            e.Handled = true;
        };

        return item;
    }

    /// <summary>状态行：下次触发、上次结果、失败与停用原因。</summary>
    private void UpdateStatusLine()
    {
        if (_selected is null)
        {
            StatusLine.Text = "";
            return;
        }

        var def = _selected;
        var parts = new List<string>();

        if (!def.IsEnabled)
        {
            parts.Add(def.FailureCount >= TaskClock.DisableAfterFailures
                ? $"已自动停用（连续失败 {def.FailureCount} 次）。"
                : "已停用，不会触发。");
        }
        else if (def.NextRunAt is { } next)
        {
            parts.Add("下次触发：" + DateTimeOffset.FromUnixTimeMilliseconds(next).ToLocalTime()
                          .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                      + "（" + TaskSchedule.DescribeNext(next, DateTimeOffset.Now) + "）");
        }
        else
        {
            parts.Add("当前规则算不出下一次触发时间。");
        }

        if (def.LastRunAt is { } last)
            parts.Add("上次触发：" + DateTimeOffset.FromUnixTimeMilliseconds(last).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        if (def.FailureCount > 0 && def.IsEnabled)
            parts.Add($"连续失败 {def.FailureCount} 次（{TaskClock.DisableAfterFailures} 次会自动停用）。");

        StatusLine.Text = string.Join("　", parts);

        // 列表里的相对时间也要跟着走
        if (_tasks.Count > 0) RefreshList();
    }

    // ============================================================
    // 运行历史
    // ============================================================

    private async Task LoadRunsAsync(TaskDefinition def)
    {
        RunList.Children.Clear();

        if (_tasks.All(t => t.Id != def.Id))
        {
            RunList.Children.Add(Hint("保存后才会开始记录运行历史。"));
            return;
        }

        List<TaskRun> runs;
        try
        {
            runs = await TaskRepository.GetRunsAsync(def.Id);
        }
        catch
        {
            RunList.Children.Add(Hint("读不到运行历史。"));
            return;
        }

        if (runs.Count == 0)
        {
            RunList.Children.Add(Hint("还没有跑过。"));
            return;
        }

        foreach (var run in runs)
            RunList.Children.Add(CreateRunItem(run));
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
    };

    private Border CreateRunItem(TaskRun run)
    {
        var (label, color) = run.Status switch
        {
            TaskRunStatus.Ok => ("完成", "#059669"),
            TaskRunStatus.Failed => ("失败", "#DC2626"),
            TaskRunStatus.Skipped => ("跳过", "#9CA3AF"),
            _ => ("运行中", "#2563EB"),
        };

        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = DateTimeOffset.FromUnixTimeMilliseconds(run.CreatedAt).ToLocalTime()
                        .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                },
                new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(color)),
                },
            },
        };

        var body = new TextBlock
        {
            Text = run.Status == TaskRunStatus.Failed
                ? run.Error ?? "（没有失败原因）"
                : string.IsNullOrWhiteSpace(run.Summary) ? "（没有正文结果）" : run.Summary,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Background = new SolidColorBrush(Color.Parse("#F9FAFB")),
            Child = new StackPanel { Children = { head, body } },
        };

        item.PointerEntered += (_, _) => item.Cursor = new Cursor(StandardCursorType.Hand);
        item.PointerPressed += (_, e) =>
        {
            // 一次运行就是一个会话：交给宿主页打开完整对话（工具调用、参数、结果都在里面）
            OpenSessionRequested?.Invoke(run.Id);
            e.Handled = true;
        };

        return item;
    }
}
