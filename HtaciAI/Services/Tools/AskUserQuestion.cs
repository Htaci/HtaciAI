using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HtaciAI.Services.Tools;

/// <summary>ask_user_question 工具里的一个问题。</summary>
public sealed class AskQuestion
{
    public string Question { get; set; } = "";

    /// <summary>短标签，显示在问题上方（可为空）。</summary>
    public string Header { get; set; } = "";

    /// <summary>true = 多选，false = 单选（默认）。</summary>
    public bool MultiSelect { get; set; }

    public List<AskOption> Options { get; set; } = new();
}

/// <summary>问题的一个可选项。</summary>
public sealed class AskOption
{
    public string Label { get; set; } = "";

    /// <summary>补充说明这一项意味着什么（可为空）。</summary>
    public string Description { get; set; } = "";
}

/// <summary>用户对一条问题的回答。选项与自定义文本可以并存（多选时是叠加，单选时互斥由对话框保证）。</summary>
public sealed class AskAnswer
{
    public string Question { get; set; } = "";

    /// <summary>选中的选项 label，按选项原始顺序排列（而非点击顺序）。</summary>
    public List<string> Selected { get; set; } = new();

    /// <summary>用户在自定义输入框里填的内容（未填为 null）。</summary>
    public string? Custom { get; set; }

    /// <summary>既没选选项也没填自定义文本。</summary>
    public bool IsEmpty => Selected.Count == 0 && string.IsNullOrWhiteSpace(Custom);
}

/// <summary>
/// ask_user_question 的入参解析与结果文本组装。
/// 解析刻意宽松：模型不一定会严格按 schema 传参（选项写成纯字符串、多选标志写成 "true"/1 都可能），
/// 这里能救则救，救不了才报错。
/// </summary>
public static class AskUserQuestionTool
{
    /// <summary>单次询问的问题数上限。</summary>
    public const int MaxQuestions = 4;

    /// <summary>单个问题的选项数上限。</summary>
    public const int MaxOptions = 6;

    /// <summary>用户取消询问时反馈给模型的文本。</summary>
    public const string CancelledText =
        "用户取消了这次询问，没有作答。不要重复发起同样的问题，基于已有信息继续；" +
        "若确实缺关键信息，直接在回答里说明你需要什么。";

    /// <summary>
    /// 解析工具入参。成功返回问题列表；失败返回 null 并把原因写入 <paramref name="error"/>。
    /// </summary>
    public static List<AskQuestion>? Parse(string? argumentsJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            error = "缺少参数";
            return null;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            error = $"参数不是合法 JSON：{ex.Message}";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            error = "缺少 questions 数组";
            return null;
        }

        var list = new List<AskQuestion>();
        foreach (var item in arr.EnumerateArray())
        {
            if (list.Count >= MaxQuestions) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var q = new AskQuestion
            {
                Question = String(item, "question") ?? "",
                Header = String(item, "header") ?? "",
                MultiSelect = Bool(item, "multiSelect") ?? Bool(item, "multi_select") ?? false,
            };

            if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    if (q.Options.Count >= MaxOptions) break;
                    // 选项可能被写成对象或纯字符串，两种都收
                    var label = o.ValueKind == JsonValueKind.String
                        ? o.GetString()
                        : String(o, "label");
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    q.Options.Add(new AskOption
                    {
                        Label = label.Trim(),
                        Description = o.ValueKind == JsonValueKind.Object ? String(o, "description") ?? "" : "",
                    });
                }
            }

            if (q.Options.Count == 0) continue;   // 没有选项的问题没有意义，跳过
            if (string.IsNullOrWhiteSpace(q.Question)) q.Question = q.Options[0].Label;
            list.Add(q);
        }

        if (list.Count == 0)
        {
            error = "questions 里没有有效的问题（每个问题至少需要一个选项）";
            return null;
        }
        return list;
    }

    /// <summary>把用户作答整理成给模型看的结果文本。</summary>
    public static string FormatAnswers(List<AskQuestion> questions, List<AskAnswer> answers)
    {
        var sb = new StringBuilder("用户已回答：");
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var a = answers.FirstOrDefault(x => x.Question == q.Question);
            sb.AppendLine();
            sb.Append(i + 1).Append(". ").Append(q.Question).Append(" → ");

            if (a is null || a.IsEmpty)
            {
                sb.Append("（未作答）");
                continue;
            }

            var parts = new List<string>();
            if (a.Selected.Count > 0) parts.Add(string.Join("、", a.Selected));
            if (!string.IsNullOrWhiteSpace(a.Custom)) parts.Add($"(自定义) {a.Custom.Trim()}");
            sb.Append(string.Join("；", parts));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把 <see cref="FormatAnswers"/> 的输出反解析回作答 —— 历史里只剩这段文本，
    /// 想把它渲染成问答卡片就得先读回来。结果对不上格式（用户取消、工具报错、旧数据）时返回 null。
    /// </summary>
    public static List<AskAnswer>? ParseAnswers(List<AskQuestion> questions, string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output.Replace("\r\n", "\n").TrimEnd().Split('\n');
        if (lines.Length < questions.Count + 1 || lines[0].Trim() != "用户已回答：") return null;

        var answers = new List<AskAnswer>();
        for (var i = 0; i < questions.Count; i++)
        {
            var line = lines[i + 1].Trim();
            var prefix = (i + 1) + ". ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;

            var arrow = line.IndexOf(" → ", prefix.Length, StringComparison.Ordinal);
            if (arrow < 0) return null;
            var body = line[(arrow + 3)..].Trim();

            var answer = new AskAnswer { Question = questions[i].Question };
            if (body != "（未作答）")
            {
                // 自定义回答固定排在最后且带 "(自定义) " 前缀，先把它摘出来，剩下的按选项原顺序匹配
                var customAt = body.IndexOf("(自定义) ", StringComparison.Ordinal);
                var selectedPart = body;
                if (customAt >= 0)
                {
                    selectedPart = body[..customAt].TrimEnd('；', ' ');
                    answer.Custom = body[(customAt + 5)..].Trim();
                }
                if (selectedPart.Length > 0)
                    answer.Selected = questions[i].Options
                        .Where(o => selectedPart.Contains(o.Label, StringComparison.Ordinal))
                        .Select(o => o.Label)
                        .ToList();
            }
            answers.Add(answer);
        }
        return answers;
    }

    // ---- 宽松取值 ----

    private static string? String(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : v.GetString() == "1",
            _ => null,
        };
    }
}
