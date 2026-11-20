using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HtaciAI.Models;

namespace HtaciAI.Services.Tasks;

/// <summary>
/// 把「闹钟式」触发规则算成下一次触发的绝对时刻（以及翻回人能读懂的一行描述）。
///
/// <b>一律按本地时区算</b>：用户填的「08:30」是墙上的钟，不是 UTC。
/// 存库统一为 epoch ms（UTC），与项目其它时间列一致。
///
/// <b>算的是「严格晚于 after 的那一次」</b>：调度器每跑完一次就从「现在」重算，
/// 于是程序关掉期间错过的那几轮自然塌成一次，不会醒来连补十次。
///
/// <b>每月 31 号遇到 2 月：跳过，不顺延到 28/29 号</b>。顺延会让「每月最后一天交报表」
/// 这类任务变成「每月 30 号左右」，而用户填的是 31 就是 31。
/// </summary>
public static class TaskSchedule
{
    /// <summary>算 <paramref name="def"/> 在 <paramref name="after"/> 之后的下一次触发时刻；不会再有则 null。</summary>
    public static long? NextOccurrence(TaskDefinition def, DateTimeOffset after)
    {
        var afterMs = after.ToUnixTimeMilliseconds();

        switch (def.TriggerKind)
        {
            case TaskTriggerKind.Once:
                // 一次性任务：定好的那一刻还没到就是它，过了就再也没有
                return def.OnceAt is { } at && at > afterMs ? at : null;

            case TaskTriggerKind.Daily:
            {
                if (ParseTime(def.TriggerTime) is not { } time) return null;

                var local = after.ToLocalTime().DateTime.Date;
                // 两天足够：今天的时刻若已过，下一次就是明天的
                for (var i = 0; i <= 1; i++)
                {
                    var candidate = ToEpoch(local.AddDays(i) + time);
                    if (candidate > afterMs) return candidate;
                }
                return null;
            }

            case TaskTriggerKind.Weekly:
            {
                if (ParseTime(def.TriggerTime) is not { } time) return null;

                var days = NormalizeWeekly(def.TriggerDays);
                if (days.Count == 0) return null;

                var local = after.ToLocalTime().DateTime.Date;
                for (var i = 0; i <= 7; i++)
                {
                    var date = local.AddDays(i);
                    if (!days.Contains(IsoWeekday(date))) continue;

                    var candidate = ToEpoch(date + time);
                    if (candidate > afterMs) return candidate;
                }
                return null;
            }

            case TaskTriggerKind.Monthly:
            {
                if (ParseTime(def.TriggerTime) is not { } time) return null;

                var days = NormalizeMonthly(def.TriggerDays);
                if (days.Count == 0) return null;

                // 往后找 13 个月：够跨过任何一个「本月没有这一天」的月份
                var start = after.ToLocalTime().DateTime.Date;
                for (var month = 0; month <= 12; month++)
                {
                    var cursor = new DateTime(start.Year, start.Month, 1).AddMonths(month);
                    var lastDay = DateTime.DaysInMonth(cursor.Year, cursor.Month);

                    foreach (var day in days)
                    {
                        // 当月没有这一天：跳过（见类注释）
                        if (day > lastDay) continue;

                        var candidate = ToEpoch(new DateTime(cursor.Year, cursor.Month, day) + time);
                        if (candidate > afterMs) return candidate;
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>一行描述，界面上直接显示，例如「每周一、三、五 08:30:00」。</summary>
    public static string Describe(TaskDefinition def)
    {
        var time = ParseTime(def.TriggerTime) is { } t
            ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : "未设定时间";

        switch (def.TriggerKind)
        {
            case TaskTriggerKind.Once:
                return def.OnceAt is { } at
                    ? "一次 · " + DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "一次 · 未设定时间";

            case TaskTriggerKind.Daily:
                return "每天 " + time;

            case TaskTriggerKind.Weekly:
            {
                var names = NormalizeWeekly(def.TriggerDays).Select(WeekdayName).ToList();
                return (names.Count == 0 ? "未选星期" : "每周" + string.Join("、", names)) + " " + time;
            }

            case TaskTriggerKind.Monthly:
            {
                var days = NormalizeMonthly(def.TriggerDays);
                return (days.Count == 0 ? "未选日期" : "每月 " + string.Join("、", days) + " 号") + " " + time;
            }

            default:
                return "未设定";
        }
    }

    /// <summary>下次触发的相对描述（「3 小时 12 分后」）。null 表示不会再触发。</summary>
    public static string DescribeNext(long? nextRunAt, DateTimeOffset now)
    {
        if (nextRunAt is not { } at) return "不会再触发";

        var span = TimeSpan.FromMilliseconds(at - now.ToUnixTimeMilliseconds());
        if (span <= TimeSpan.Zero) return "即将触发";

        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时后";
        if (span.TotalHours >= 1) return $"{span.Hours} 小时 {span.Minutes} 分后";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} 分 {span.Seconds} 秒后";
        return $"{span.Seconds} 秒后";
    }

    public static string WeekdayName(int iso) => iso switch
    {
        1 => "一",
        2 => "二",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        7 => "日",
        _ => "?",
    };

    /// <summary>周一为 1、周日为 7。用 <see cref="DayOfWeek.Sunday"/> 当 0 的默认约定差一天，很容易写错。</summary>
    public static int IsoWeekday(DateTime date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    /// <summary>解析 <c>HH:mm:ss</c>（也接受 <c>HH:mm</c>）。解析不出来返回 null —— 调用方当「这条规则无效」处理。</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;

        if (!int.TryParse(parts[0], out var h) || h is < 0 or > 23) return null;
        if (!int.TryParse(parts[1], out var m) || m is < 0 or > 59) return null;

        var s = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out s) || s is < 0 or > 59)) return null;

        return new TimeSpan(h, m, s);
    }

    /// <summary>规范化成 <c>HH:mm:ss</c>，界面回显用。</summary>
    public static string NormalizeTime(string? text)
        => ParseTime(text) is { } t ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "09:00:00";

    private static HashSet<int> NormalizeWeekly(IEnumerable<int> days)
        => days.Where(d => d is >= 1 and <= 7).ToHashSet();

    private static List<int> NormalizeMonthly(IEnumerable<int> days)
        => days.Where(d => d is >= 1 and <= 31).Distinct().OrderBy(d => d).ToList();

    /// <summary>本地时间 → epoch ms（UTC）。按当天实际时区偏移换算，跨夏令时的日子不会差一小时。</summary>
    private static long ToEpoch(DateTime localUnspecified)
        => new DateTimeOffset(DateTime.SpecifyKind(localUnspecified, DateTimeKind.Local)).ToUnixTimeMilliseconds();
}
