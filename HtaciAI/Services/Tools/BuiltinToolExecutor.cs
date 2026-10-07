using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.Skills;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 内置工具运行的宿主上下文：文件基准目录 + 会话技能读写回调（供 load_skill / unload_skill）。
/// </summary>
public sealed class BuiltinToolContext
{
    /// <summary>相对路径解析的基准目录（默认应用当前目录）。</summary>
    public string BaseDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>读取当前会话已启用技能（可能为 null）。</summary>
    public Func<Task<List<SessionSkill>?>>? GetSessionSkills { get; set; }

    /// <summary>保存当前会话已启用技能。</summary>
    public Func<List<SessionSkill>, Task>? SaveSessionSkills { get; set; }

    /// <summary>
    /// 向用户提问并等待作答（由会话视图弹窗实现）。为 null 时 ask_user_question 工具不可用。
    /// 返回 null 表示用户取消/关闭了对话框，未作答。
    /// </summary>
    public Func<List<AskQuestion>, Task<List<AskAnswer>?>>? AskUser { get; set; }

    /// <summary>
    /// 当前模型是否支持视觉。由会话视图在每次发送前按所选模型刷新；
    /// 不支持时 view_image 直接返回错误，让模型自己知道看不了图并继续。
    /// </summary>
    public bool SupportsVision { get; set; }

    /// <summary>把入参（可为相对路径）解析为绝对路径；空入参返回空串。</summary>
    public static string ResolvePath(BuiltinToolContext ctx, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim();
        return Path.IsPathRooted(p)
            ? Path.GetFullPath(p)
            : Path.GetFullPath(Path.Combine(ctx.BaseDirectory, p));
    }

    /// <summary>从工具调用参数 JSON 中读取某个属性（字符串）。</summary>
    public static string? GetArg(ChatToolCall call, string name)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments)) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从工具调用参数 JSON 中读取某个整型属性（缺省返回 null）。</summary>
    public static int? GetIntArg(ChatToolCall call, string name)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments)) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从工具调用参数 JSON 中读取某个布尔属性（缺省返回 null）。
    /// 兼容 true/false、字符串 "true"/"false"、"1"/"0" 与数字 1/0 —— 模型不一定严格按 schema 的 boolean 传参。
    /// </summary>
    public static bool? GetBoolArg(ChatToolCall call, string name)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments)) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(name, out var v)) return null;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.GetDouble() != 0,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : v.GetString() == "1",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 内置工具执行器：按 <see cref="ToolDefinition.Name"/> 分派实现（bash / read / edit / write / glob / grep / webfetch / load_skill / unload_skill / invalid）。
/// </summary>
public sealed class BuiltinToolExecutor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const double MaxFileBytes = 1024 * 1024; // 单文件搜索上限 1MB

    public async Task<ToolExecution> ExecuteAsync(ToolDefinition tool, ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        try
        {
            return tool.Name switch
            {
                "bash" => await RunBashAsync(call, ctx, ct),
                "read" => Read(call, ctx),
                "edit" => await EditAsync(call, ctx),
                "write" => await WriteAsync(call, ctx),
                "glob" => Glob(call, ctx),
                "grep" => await GrepAsync(call, ctx, ct),
                "webfetch" => await WebfetchAsync(call, ct),
                "load_skill" => await LoadSkillAsync(call, ctx),
                "unload_skill" => await UnloadSkillAsync(call, ctx),
                "ask_user_question" => await AskUserQuestionAsync(call, ctx),
                "view_image" => ViewImage(call, ctx),
                "invalid" => Invalid(call),
                _ => ToolExecution.Fail($"暂不支持的内置工具：{tool.Name}"),
            };
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"{tool.Name} 执行异常：{ex.Message}");
        }
    }

    // ---- bash ----

    private static async Task<ToolExecution> RunBashAsync(ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        var command = BuiltinToolContext.GetArg(call, "command") ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return ToolExecution.Fail("缺少 command 参数");

        var workdir = BuiltinToolContext.GetArg(call, "workdir");
        var timeout = BuiltinToolContext.GetIntArg(call, "timeout") ?? 60;

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 用 ArgumentList 传递参数，避免引号/空格被二次解析。
        var trimmed = command.TrimStart();
        if (OperatingSystem.IsWindows())
        {
            if (trimmed.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "powershell";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
        }
        else
        {
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        if (!string.IsNullOrWhiteSpace(workdir))
            psi.WorkingDirectory = BuiltinToolContext.ResolvePath(ctx, workdir);
        else
            psi.WorkingDirectory = ctx.BaseDirectory;

        return await RunProcessAsync(psi, timeout, ct);
    }

    private static async Task<ToolExecution> RunProcessAsync(ProcessStartInfo psi, int timeoutSec, CancellationToken ct)
    {
        using var p = new Process { StartInfo = psi };
        if (!p.Start())
            return ToolExecution.Fail($"无法启动进程：{psi.FileName}");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSec)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            await TryDrainAsync(stdoutTask, stderrTask);
            return ToolExecution.Fail($"命令执行超时（>{timeoutSec}s）");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (p.ExitCode != 0)
        {
            var err = BuildError(stdout, stderr);
            return ToolExecution.Fail($"命令退出码 {p.ExitCode}：{err}");
        }
        var output = stdout.Trim();
        return ToolExecution.Ok(string.IsNullOrWhiteSpace(output) ? "（命令无输出）" : output);
    }

    private static string BuildError(string stdout, string stderr)
    {
        var err = stderr.Trim();
        var outl = stdout.Trim();
        return err.Length > 0 ? err : (outl.Length > 0 ? outl : "无输出");
    }

    private static async Task TryDrainAsync(Task<string> a, Task<string> b)
    {
        try { await a; } catch { }
        try { await b; } catch { }
    }

    // ---- read ----

    private static ToolExecution Read(ChatToolCall call, BuiltinToolContext ctx)
    {
        var filePath = BuiltinToolContext.GetArg(call, "filePath") ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
            return ToolExecution.Fail("缺少 filePath 参数");

        var full = BuiltinToolContext.ResolvePath(ctx, filePath);
        if (Directory.Exists(full))
            return ToolExecution.Ok(ListDirectory(full));

        if (!File.Exists(full))
            return ToolExecution.Fail($"文件不存在：{full}");

        var offset = BuiltinToolContext.GetIntArg(call, "offset") ?? 0;
        var limit = BuiltinToolContext.GetIntArg(call, "limit");
        try
        {
            var lines = File.ReadAllLines(full);
            var start = Math.Clamp(offset, 0, Math.Max(0, lines.Length - 1));
            var count = limit.HasValue ? Math.Max(0, limit.Value) : lines.Length - start;
            var slice = lines.Skip(start).Take(count).ToList();
            var text = string.Join("\n", slice);
            return ToolExecution.Ok($"（{full}，行 {start + 1}-{start + slice.Count} / 共 {lines.Length} 行）\n\n" + text);
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"读取失败：{ex.Message}");
        }
    }

    private static string ListDirectory(string dir)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(dir)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .Select(e => Directory.Exists(e) ? $"[目录] {Path.GetFileName(e)}" : $"[文件] {Path.GetFileName(e)}")
                .ToList();
            return $"（目录 {dir} 共 {entries.Count} 项）\n{string.Join("\n", entries)}";
        }
        catch (Exception ex)
        {
            return $"读取目录失败：{ex.Message}";
        }
    }

    // ---- edit ----

    private static async Task<ToolExecution> EditAsync(ChatToolCall call, BuiltinToolContext ctx)
    {
        var filePath = BuiltinToolContext.GetArg(call, "filePath") ?? "";
        var oldString = BuiltinToolContext.GetArg(call, "oldString") ?? "";
        var newString = BuiltinToolContext.GetArg(call, "newString") ?? "";
        // schema 声明为 boolean，必须按布尔读取（此前用 GetIntArg 只认数字，模型传 true 会被忽略→"替换全部"失效）
        var replaceAll = BuiltinToolContext.GetBoolArg(call, "replaceAll") ?? false;
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrEmpty(oldString))
            return ToolExecution.Fail("edit 需要 filePath 与 oldString");

        var full = BuiltinToolContext.ResolvePath(ctx, filePath);
        if (!File.Exists(full))
            return ToolExecution.Fail($"文件不存在：{full}");

        var text = await File.ReadAllTextAsync(full);
        var count = replaceAll
            ? CountOccurrences(text, oldString)
            : (text.Contains(oldString) ? 1 : 0);
        var updated = replaceAll ? text.Replace(oldString, newString) : ReplaceFirst(text, oldString, newString);

        if (count == 0)
            return ToolExecution.Fail($"未找到要替换的文本：{oldString}");

        await File.WriteAllTextAsync(full, updated);
        return ToolExecution.Ok($"已替换 {count} 处。文件：{full}");
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? text : text[..idx] + replacement + text[(idx + needle.Length)..];
    }

    // ---- write ----

    private static async Task<ToolExecution> WriteAsync(ChatToolCall call, BuiltinToolContext ctx)
    {
        var filePath = BuiltinToolContext.GetArg(call, "filePath") ?? "";
        var content = BuiltinToolContext.GetArg(call, "content") ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
            return ToolExecution.Fail("缺少 filePath 参数");

        var full = BuiltinToolContext.ResolvePath(ctx, filePath);
        var existed = File.Exists(full);
        await File.WriteAllTextAsync(full, content);
        return ToolExecution.Ok($"已{(existed ? "覆盖" : "创建")}文件：{full}");
    }

    // ---- glob ----

    private static ToolExecution Glob(ChatToolCall call, BuiltinToolContext ctx)
    {
        var pattern = BuiltinToolContext.GetArg(call, "pattern") ?? "";
        var root = BuiltinToolContext.ResolvePath(ctx, BuiltinToolContext.GetArg(call, "path"));
        if (string.IsNullOrEmpty(pattern))
            return ToolExecution.Fail("缺少 pattern 参数");
        if (string.IsNullOrEmpty(root))
            root = ctx.BaseDirectory;

        try
        {
            if (!Directory.Exists(root))
                return ToolExecution.Fail($"目录不存在：{root}");

            var regex = GlobToRegex(pattern);
            var hits = new List<string>();
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (regex.IsMatch(rel))
                {
                    hits.Add(rel);
                    if (hits.Count >= 500) break;
                }
            }
            return ToolExecution.Ok($"（{hits.Count} 个匹配）\n" + string.Join("\n", hits));
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"glob 失败：{ex.Message}");
        }
    }

    // ---- grep ----

    private static async Task<ToolExecution> GrepAsync(ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        const int MaxMatches = 200;

        var pattern = BuiltinToolContext.GetArg(call, "pattern") ?? "";
        var root = BuiltinToolContext.ResolvePath(ctx, BuiltinToolContext.GetArg(call, "path"));
        var include = BuiltinToolContext.GetArg(call, "include");
        var contextLines = Math.Clamp(BuiltinToolContext.GetIntArg(call, "context") ?? 0, 0, 10);
        if (string.IsNullOrEmpty(pattern))
            return ToolExecution.Fail("缺少 pattern 参数");
        if (string.IsNullOrEmpty(root))
            root = ctx.BaseDirectory;

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (Exception ex) { return ToolExecution.Fail($"正则无效：{ex.Message}"); }

        // include 同时按「相对路径」和「文件名」匹配：既支持 src/**/*.cs 这类路径模式，
        // 也支持 *.cs 这种只写文件名后缀的常见写法（含分隔符时按路径匹配，否则按文件名匹配）。
        Regex? includePathRegex = null;
        Regex? includeNameRegex = null;
        if (!string.IsNullOrWhiteSpace(include))
        {
            try
            {
                includePathRegex = GlobToPathRegex(include!);
                includeNameRegex = GlobToNameRegex(include!);
            }
            catch (Exception ex)
            {
                return ToolExecution.Fail($"include 模式无效：{ex.Message}");
            }
        }

        try
        {
            if (!Directory.Exists(root))
                return ToolExecution.Fail($"目录不存在：{root}");

            var output = new List<string>();
            var matchCount = 0;
            var truncated = false;
            var scanned = 0;

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                var name = Path.GetFileName(file);
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (includePathRegex is not null &&
                    !includePathRegex.IsMatch(rel) && !includeNameRegex!.IsMatch(name))
                    continue;

                // 跳过超大文件与二进制文件（含 NUL 字节，逐字符正则匹配没有意义且会有乱码输出）
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;

                var text = await File.ReadAllTextAsync(file);
                if (text.Contains('\0')) continue;

                var lines = text.Replace("\r\n", "\n").Split('\n');
                var hitIndices = new List<int>();
                for (var i = 0; i < lines.Length; i++)
                    if (regex.IsMatch(lines[i])) hitIndices.Add(i);

                if (hitIndices.Count == 0) continue;

                // 上下文区间合并去重后输出；匹配行用 ':' 分隔，上下文行用 '-' 分隔（对齐 ripgrep 习惯）
                var hitSet = hitIndices.ToHashSet();
                var emitted = new SortedSet<int>();
                foreach (var hit in hitIndices)
                {
                    if (matchCount >= MaxMatches) { truncated = true; break; }
                    matchCount++;
                    for (var j = Math.Max(0, hit - contextLines); j <= Math.Min(lines.Length - 1, hit + contextLines); j++)
                        emitted.Add(j);
                }

                foreach (var j in emitted)
                    output.Add($"{rel}:{j + 1}{(hitSet.Contains(j) ? ":" : "-")} {lines[j].Trim()}");

                if (truncated) break;
            }

            if (output.Count == 0)
                return ToolExecution.Ok($"（无匹配，已扫描 {scanned} 个文件）");

            var header = truncated
                ? $"（已达到 {MaxMatches} 处匹配上限，结果被截断；请缩小 path 或收窄 pattern）"
                : $"（{matchCount} 处匹配）";
            return ToolExecution.Ok(header + "\n" + string.Join("\n", output));
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"grep 失败：{ex.Message}");
        }
    }

    // ---- webfetch ----

    private static async Task<ToolExecution> WebfetchAsync(ChatToolCall call, CancellationToken ct)
    {
        var url = BuiltinToolContext.GetArg(call, "url") ?? "";
        if (string.IsNullOrWhiteSpace(url))
            return ToolExecution.Fail("缺少 url 参数");

        var timeout = BuiltinToolContext.GetIntArg(call, "timeout") ?? 30;
        var format = (BuiltinToolContext.GetArg(call, "format") ?? "text").Trim().ToLowerInvariant();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeout)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        try
        {
            using var resp = await Http.GetAsync(url, linked.Token);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(linked.Token);
            if (string.IsNullOrWhiteSpace(raw))
                return ToolExecution.Ok($"（{url}，无内容）");

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                         || (mediaType.Length == 0 && raw.TrimStart().StartsWith("<", StringComparison.Ordinal));

            // 非 HTML（JSON / 纯文本等）不做转换，原样返回，避免误伤
            string body, kind;
            if (!isHtml || format == "html")
            {
                body = raw;
                kind = isHtml ? "原始 HTML" : mediaType is { Length: > 0 } ? mediaType : "原始内容";
            }
            else if (format == "markdown")
            {
                body = HtmlConverter.ToMarkdown(raw);
                kind = "Markdown";
            }
            else
            {
                body = HtmlConverter.ToPlainText(raw);
                kind = "纯文本";
            }

            return ToolExecution.Ok($"（{url}，{kind}，{body.Length} 字符）\n" + Truncate(body, 20000));
        }
        catch (OperationCanceledException)
        {
            return ToolExecution.Fail($"请求超时（>{timeout}s）：{url}");
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"请求失败：{ex.Message}");
        }
    }

    // ---- load_skill / unload_skill ----

    /// <summary>
    /// 把模型给的技能名解析为注册表中的真实技能：先按 id（目录名）匹配，再按 Name 匹配。
    /// 解析失败返回 null —— 模型可能凭印象编造技能名，不能直接写进会话技能列表。
    /// </summary>
    private static SkillDefinition? ResolveSkill(string name)
        => SkillRegistry.Instance.ResolveById(name)
           ?? SkillRegistry.Instance.GetAll().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static async Task<ToolExecution> LoadSkillAsync(ChatToolCall call, BuiltinToolContext ctx)
    {
        var name = BuiltinToolContext.GetArg(call, "name") ?? "";
        if (string.IsNullOrWhiteSpace(name)) return ToolExecution.Fail("缺少 name 参数");
        if (ctx.GetSessionSkills is null || ctx.SaveSessionSkills is null)
            return ToolExecution.Fail("当前会话不支持技能加载");

        var skill = ResolveSkill(name);
        if (skill is null)
            return ToolExecution.Fail($"技能不存在：{name}。可加载的技能见系统提示词中的技能列表。");

        // 统一用 id 记录（name 是展示名，可能与目录名不同）
        var skills = await ctx.GetSessionSkills() ?? new List<SessionSkill>();
        var existing = skills.FirstOrDefault(s => s.Id == skill.Id);
        if (existing is null)
            skills.Add(new SessionSkill { Id = skill.Id, Status = "loaded" });
        else
            existing.Status = "loaded";
        await ctx.SaveSessionSkills(skills);
        return ToolExecution.Ok($"已加载技能：{skill.Name}（{skill.Id}）");
    }

    private static async Task<ToolExecution> UnloadSkillAsync(ChatToolCall call, BuiltinToolContext ctx)
    {
        var name = BuiltinToolContext.GetArg(call, "name") ?? "";
        if (string.IsNullOrWhiteSpace(name)) return ToolExecution.Fail("缺少 name 参数");
        if (ctx.GetSessionSkills is null || ctx.SaveSessionSkills is null)
            return ToolExecution.Fail("当前会话不支持技能卸载");

        // 解析失败时回退用原始入参：技能可能已被禁用/删除，但会话里仍留着它的加载记录
        var id = ResolveSkill(name)?.Id ?? name;

        var skills = await ctx.GetSessionSkills() ?? new List<SessionSkill>();
        var before = skills.Count;
        skills.RemoveAll(s => s.Id == id);
        if (skills.Count == before)
            return ToolExecution.Fail($"技能未加载：{name}");
        await ctx.SaveSessionSkills(skills);
        return ToolExecution.Ok($"已卸载技能：{name}");
    }

    // ---- view_image ----

    /// <summary>单张图片的大小上限（超过就不往上下文里塞了）。</summary>
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private static ToolExecution ViewImage(ChatToolCall call, BuiltinToolContext ctx)
    {
        var path = BuiltinToolContext.ResolvePath(ctx, BuiltinToolContext.GetArg(call, "path"));
        if (string.IsNullOrEmpty(path)) return ToolExecution.Fail("缺少 path 参数");

        // 不支持视觉的模型：明确告诉它看不了，让它据此继续，而不是让整轮请求失败
        if (!ctx.SupportsVision)
            return ToolExecution.Fail("当前模型不支持视觉图像，无法查看图片内容。请基于文件名与用户描述继续，或请用户直接说明图片内容。");

        if (!File.Exists(path)) return ToolExecution.Fail($"文件不存在：{path}");
        if (!AttachmentStore.IsImage(path)) return ToolExecution.Fail($"不是可识别的图片格式：{path}");

        var bytes = new FileInfo(path).Length;
        if (bytes > MaxImageBytes)
            return ToolExecution.Fail($"图片过大（{bytes / 1024 / 1024} MB），上限 {MaxImageBytes / 1024 / 1024} MB");

        return ToolExecution.OkImage($"已读取图片：{path}", path);
    }

    // ---- ask_user_question ----

    private static async Task<ToolExecution> AskUserQuestionAsync(ChatToolCall call, BuiltinToolContext ctx)
    {
        if (ctx.AskUser is null)
            return ToolExecution.Fail("当前会话不支持向用户提问");

        var questions = AskUserQuestionTool.Parse(call.Arguments, out var error);
        if (questions is null)
            return ToolExecution.Fail(error ?? "问题参数无效");

        var answers = await ctx.AskUser(questions);
        return ToolExecution.Ok(answers is null
            ? AskUserQuestionTool.CancelledText
            : AskUserQuestionTool.FormatAnswers(questions, answers));
    }

    // ---- invalid ----

    private static ToolExecution Invalid(ChatToolCall call)
        => ToolExecution.Ok(FormatInvalidError(call));

    /// <summary>按 invalid 的契约（tool / error）拼装错误反馈文本，供内置 invalid 工具使用。</summary>
    public static string FormatInvalidError(ChatToolCall call) =>
        $"工具「{BuiltinToolContext.GetArg(call, "tool") ?? "未知"}」调用失败：{BuiltinToolContext.GetArg(call, "error") ?? "未知错误"}";

    // ---- 辅助 ----

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n…（已截断）";

    private static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("."); break;
                case '/': sb.Append("/"); break;
                case '.': sb.Append("\\."); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }

    /// <summary>
    /// 把 glob 转成「相对路径」正则：* 不跨目录、** 跨目录、且 **/ 允许零级目录
    /// （因此 **/*.cs 也能匹配根目录下的 a.cs）。
    /// </summary>
    private static Regex GlobToPathRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '/': sb.Append('/'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }

    private static Regex GlobToNameRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
            sb.Append(c == '*' ? ".*" : c == '?' ? "." : Regex.Escape(c.ToString()));
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
