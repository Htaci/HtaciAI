using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;
using HtaciAI.Services.KnowledgeBase;
using HtaciAI.Services.Network;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 内置工具运行的宿主上下文：文件基准目录 + 会话技能读写回调（供 load_skill / unload_skill）。
/// </summary>
public sealed class BuiltinToolContext
{
    /// <summary>相对路径解析的基准目录（默认应用当前目录）。</summary>
    public string BaseDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// 当前会话 id。插件工具会带上它，用来判断「这个资源是否被授权给了这个会话」
    /// （内置浏览器把窗口授权给会话就是这个用法）。空串表示没有会话上下文。
    /// </summary>
    public string SessionId { get; set; } = "";

    /// <summary>读取当前会话已启用技能（可能为 null）。</summary>
    public Func<Task<List<SessionSkill>?>>? GetSessionSkills { get; set; }

    /// <summary>保存当前会话已启用技能。</summary>
    public Func<List<SessionSkill>, Task>? SaveSessionSkills { get; set; }

    /// <summary>
    /// 向用户提问并等待作答（由会话视图在当前轮次里插一张提问卡片实现）。为 null 时 ask_user_question 工具不可用。
    /// 返回 null 表示用户关掉了这次询问，未作答。
    /// 取消令牌同样要传下去：点了「停止」之后不该再继续等一张没人会答的卡。
    /// </summary>
    public Func<List<AskQuestion>, CancellationToken, Task<List<AskAnswer>?>>? AskUser { get; set; }

    /// <summary>
    /// 请用户去做一件模型做不了的事（扫码、输验证码），等他说做完再继续。
    /// 返回 true = 已完成，false = 用户取消，null = 没等到人（会话窗口没开）。
    /// 为 null 时 ask_user_continue 工具不可用。
    /// </summary>
    public Func<string, string, CancellationToken, Task<bool?>>? WaitForUser { get; set; }

    /// <summary>
    /// 当前会话选定的知识库 id。空 = 没选，<c>kb_search</c> 直接返回提示。
    /// 由 <c>ConversationRuntime</c> 在每次发送前从会话状态里刷进来 ——
    /// 与工具上下文里其它「每轮现刷」的字段同一套做法。
    /// </summary>
    public string KnowledgeBaseId { get; set; } = "";

    /// <summary>
    /// 当前模型是否支持视觉。由会话视图在每次发送前按所选模型刷新；
    /// 不支持时 view_image 直接返回错误，让模型自己知道看不了图并继续。
    /// </summary>
    public bool SupportsVision { get; set; }

    /// <summary>
    /// 当前模型是否支持视频输入。与 <see cref="SupportsVision"/> 同一套刷新时机：
    /// 不支持时 view_video 直接报错，历史里遗留的视频结果由客户端降级成文本。
    /// </summary>
    public bool SupportsVideo { get; set; }

    /// <summary>
    /// 派发子 Agent。为 null 时 task 工具直接报不可用 ——
    /// 只有拿到「父会话的配置快照与交互宿主」的那一侧（<c>ConversationRuntime</c>）才能提供它，
    /// 插件在别处调工具时本来也不该凭空派发出一个会话。
    /// </summary>
    public Func<Subagents.SubagentTaskRequest, CancellationToken, Task<Subagents.SubagentTaskResult>>? RunSubagent { get; set; }

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
    private static HttpClient Http => HttpClientProvider.Get(TimeSpan.FromSeconds(30));
    private const double MaxFileBytes = 1024 * 1024; // 单文件搜索上限 1MB

    public async Task<ToolExecution> ExecuteAsync(ToolDefinition tool, ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        // 已经停止就不要再启动任何东西（尤其是 bash：这里放过去就会真的起一个进程）
        ct.ThrowIfCancellationRequested();

        try
        {
            return tool.Name switch
            {
                "bash" => await RunBashAsync(tool, call, ctx, ct),
                "read" => Read(call, ctx),
                "edit" => await EditAsync(call, ctx),
                "write" => await WriteAsync(call, ctx),
                "glob" => Glob(call, ctx),
                "grep" => await GrepAsync(call, ctx, ct),
                "webfetch" => await WebfetchAsync(call, ct),
                "load_skill" => await LoadSkillAsync(call, ctx),
                "unload_skill" => await UnloadSkillAsync(call, ctx),
                "ask_user_question" => await AskUserQuestionAsync(call, ctx, ct),
        "ask_user_continue" => await AskUserContinueAsync(call, ctx, ct),
                "view_image" => ViewImage(call, ctx),
                BuiltinTools.KbSearchName => await KbSearchAsync(call, ctx, ct),
                "view_video" => ViewVideo(call, ctx),
                "view_video_frames" => await ViewVideoFramesAsync(call, ctx, ct),
                BuiltinTools.TaskToolName => await RunTaskAsync(call, ctx, ct),
                BuiltinTools.CreateToolName => await CreateToolAsync(call),
                BuiltinTools.CreateSkillName => await CreateSkillAsync(call),
                "invalid" => Invalid(call),
                _ => ToolExecution.Fail($"暂不支持的内置工具：{tool.Name}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点了停止：必须往外抛。在这里 catch 成一条「执行异常」，网关就会把它当成
            // 普通工具结果继续往下跑（后面的工具、下一轮请求都会照发）。
            throw;
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"{tool.Name} 执行异常：{ex.Message}");
        }
    }

    // ---- task（派发子 Agent） ----

    /// <summary>
    /// 解析 task 的入参并交给宿主去跑。<b>这里不碰会话</b>：
    /// 子会话的创建、配置收紧、runtime 借出全在 <see cref="Subagents.SubagentRunner"/> 里，
    /// 工具这一层只管参数与结果形状 —— 否则「哪些工具算安全」和「怎么继承权限」会混进同一个文件。
    /// </summary>
    private static async Task<ToolExecution> RunTaskAsync(ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        if (ctx.RunSubagent is null)
            return ToolExecution.Fail("当前环境不支持派发子 Agent（没有会话上下文）");

        var agent = BuiltinToolContext.GetArg(call, "agent");
        if (string.IsNullOrWhiteSpace(agent))
            return ToolExecution.Fail("缺少 agent 参数：要派给哪个子 Agent");

        var prompt = BuiltinToolContext.GetArg(call, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolExecution.Fail("缺少 prompt 参数：子 Agent 看不到本会话历史，必须把任务写全");

        var request = new Subagents.SubagentTaskRequest(
            agent.Trim(),
            prompt,
            BuiltinToolContext.GetArg(call, "session"),
            BuiltinToolContext.GetIntArg(call, "max_turns"));

        var result = await ctx.RunSubagent(request, ct);

        // 失败也走 Ok/失败两种形状：失败时主 Agent 需要读到原因才能换做法，
        // 所以把文本原样交给它（ToolExecution.Fail 会把 Error 作为工具结果回给模型）。
        return result.Success ? ToolExecution.Ok(result.Text) : ToolExecution.Fail(result.Text);
    }

    // ---- bash ----

    private static async Task<ToolExecution> RunBashAsync(
        ToolDefinition tool, ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        var command = BuiltinToolContext.GetArg(call, "command") ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return ToolExecution.Fail("缺少 command 参数");

        var workdir = BuiltinToolContext.GetArg(call, "workdir");

        // 命令自己等多久：入参（模型为这一次申请）> 工具页配的值（0 = 一直等）> 60 秒。
        // 分派器那层还有一道更外层的守卫，两处取同一个值，先触发的一定是这条更具体的。
        var timeout = BuiltinToolContext.GetIntArg(call, "timeout")
                      ?? tool.TimeoutSeconds
                      ?? 60;

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 会弹系统窗口的命令直接拒掉：这类窗口是模态的，ShellExecute 不返回、cmd 不返回，
        // 工具就一直卡着（超时也救不了，框还在桌面上等用户点确定）。见 ShellEnvironment.RejectGuiCommand。
        if (ShellEnvironment.RejectGuiCommand(command) is { } rejection)
            return ToolExecution.Fail(rejection);

        // 用 ArgumentList 传递参数，避免引号/空格被二次解析。
        //
        // 执行器由「环境配置 → 命令行工具」决定（默认 cmd.exe）。命令开头自己点名了
        // powershell / pwsh 的仍然按它走：模型知道在 cmd 里要显式调用别的 shell 时不会套两层。
        var trimmed = command.TrimStart();
        if (OperatingSystem.IsWindows() &&
            (trimmed.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = trimmed.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh" : "powershell";
            psi.ArgumentList.Add("-NoProfile");
            // 需要确认 / 读输入时立即失败，不留在提示上等人（子进程没有窗口，人看不见它在等）
            psi.ArgumentList.Add("-NonInteractive");
            // 走 base64：模型写的命令里可能带着引号，-Command 那条路会被命令行转义吃掉
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                ShellEnvironment.PowerShellPrefix + command)));
        }
        else
        {
            // 各 shell 的拼法不同（cmd 不能走 ArgumentList），交给 EffectiveShell 自己装
            ShellEnvironment.Resolve().Configure(psi, command);
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

        // stdin 立刻关掉：子进程既没有窗口也没有人守着，任何「等你敲一下」的东西
        // （pause / choice / Read-Host / git 要账号）都拿到 EOF 立刻失败，而不是把这一轮吊死。
        if (psi.RedirectStandardInput)
        {
            try { p.StandardInput.Close(); } catch { /* 已退出则忽略 */ }
        }

        var stdoutTask = ReadTextAsync(p.StandardOutput);
        var stderrTask = ReadTextAsync(p.StandardError);

        // timeoutSec <= 0 表示「一直等」：不建计时器，只跟随用户停止。
        using var cts = timeoutSec > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
            : null;
        using var linked = cts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户点「停止」：进程树杀掉后把取消抛出去。
            // 不能在这里返回一条「超时」——那会让工具循环继续执行剩下的调用。
            try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            await TryDrainAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            await TryDrainAsync(stdoutTask, stderrTask);
            return ToolExecution.Fail(
                $"命令执行超时（>{timeoutSec}s）。常见原因：在等输入、或弹出了系统窗口等人点确定、" +
                "或本身就是常驻进程（那样请显式设更大的 timeout）");
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

    /// <summary>
    /// 读子进程的输出并解成文本。**不能直接用 StreamReader 的默认编码**：.NET 默认按 UTF-8 解，
    /// 而 cmd 的内建命令（<c>dir</c> / <c>echo</c> / <c>findstr</c>）在中文系统上按 GBK 输出 ——
    /// 含中文的文件名与提示会整片变成 U+FFFD，模型看到的就是一串乱码路径，接着按乱码去拼命令。
    /// 这里先按<b>严格</b> UTF-8 试，不合法再按系统 OEM 代码页解，两头的输出都能读对。
    /// </summary>
    private static async Task<string> ReadTextAsync(StreamReader reader)
    {
        using var buffer = new MemoryStream();
        await reader.BaseStream.CopyToAsync(buffer).ConfigureAwait(false);
        return DecodeOutput(buffer.ToArray());
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static string DecodeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8：当作系统本地编码（中文 Windows 上是 GBK / 936）
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var page = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                return Encoding.GetEncoding(page > 0 ? page : 936).GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>
    /// 进程杀掉之后把两条流收尾，但<b>不能无限等</b>：孙进程还攥着管道时
    /// <c>ReadToEndAsync</c> 永远不完成，那「超时」就变成「还是卡着」——只等 2 秒，拿不到就放弃。
    /// </summary>
    private static async Task TryDrainAsync(Task<string> a, Task<string> b)
    {
        var both = Task.WhenAll(a, b);
        try
        {
            await both.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 超时 / 流已断：结果反正不要了
        }
        finally
        {
            // 丢掉的那个 WhenAll 之后可能异常，观察掉，别留成未观察异常
            _ = both.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停止不能被这个兜底 catch 吞成一条「grep 失败」—— 那会让工具循环继续跑
            throw;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户点了停止，不是超时
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

    // ---- create_tool / create_skill（AI 自举） ----
    //
    // 这两个工具让模型自己扩展能力。它们的安全模型是「**用户批准的是一段具体内容**」：
    // 危险等级由程序固定（不接受模型传参），审批卡片上把要写入的脚本 / 正文摊开给用户看，
    // 用户点「允许执行」才落盘。所有校验都在本文件内做，因为这是唯一的执行入口 ——
    // 放到 UI 或注册表里做，别的调用路径就绕过去了。

    /// <summary>脚本源码体积上限。脚本是给一次性任务写的小工具，几十 KB 已经远超合理范围。</summary>
    private const int MaxScriptBytes = 64 * 1024;

    /// <summary>入参 schema 上限。真有这么大的 schema，说明该拆工具了。</summary>
    private const int MaxSchemaBytes = 16 * 1024;

    /// <summary>
    /// 技能正文上限。技能是<b>全文注入 system</b> 的，一个超大技能会直接吃掉上下文 ——
    /// 这是自举里唯一能「一次写坏、长期生效」的地方，必须设上限。
    /// </summary>
    private const int MaxSkillBodyBytes = 24 * 1024;

    /// <summary>工具名 = 模型调用名：小写字母开头，后接小写字母 / 数字 / 下划线。</summary>
    private static readonly Regex ToolNamePattern = new(@"^[a-z][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    /// <summary>技能名 = 目录名 = load_skill 的调用名，规则与「创建技能」窗口一致。</summary>
    private static readonly Regex SkillNamePattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static async Task<ToolExecution> CreateToolAsync(ChatToolCall call)
    {
        // 总开关：关掉之后即使这个工具还挂在某个会话的工具列表里也不许执行
        // （工具列表只在组装请求时过滤，会话存档里可能留着旧 id）
        if (!AppSettingsStore.Current.AllowAgentSelfExtension)
            return ToolExecution.Fail("用户已关闭「允许 AI 创建工具与技能」，无法创建工具。");

        var name = (BuiltinToolContext.GetArg(call, "name") ?? "").Trim();
        if (!ToolNamePattern.IsMatch(name))
            return ToolExecution.Fail(
                "工具名不合法：必须小写字母开头，只含小写字母、数字、下划线，长度 2-64（如 fetch_weather）。");

        var description = (BuiltinToolContext.GetArg(call, "description") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(description))
            return ToolExecution.Fail("缺少 description：模型靠它判断什么时候该调用这个工具。");

        var runtimeText = (BuiltinToolContext.GetArg(call, "runtime") ?? "").Trim().ToLowerInvariant();
        var runtime = runtimeText switch
        {
            "python" => ScriptRuntimeKind.Python,
            "node" => ScriptRuntimeKind.Node,
            _ => (ScriptRuntimeKind?)null,
        };
        if (runtime is null)
            return ToolExecution.Fail("runtime 只能是 python 或 node。");

        var script = BuiltinToolContext.GetArg(call, "script") ?? "";
        if (string.IsNullOrWhiteSpace(script))
            return ToolExecution.Fail("缺少 script：请把脚本源码全文放在 script 里。");
        if (Encoding.UTF8.GetByteCount(script) > MaxScriptBytes)
            return ToolExecution.Fail($"脚本过大（上限 {MaxScriptBytes / 1024} KB），请精简后重试。");

        var schema = (BuiltinToolContext.GetArg(call, "input_schema") ?? "").Trim();
        if (schema.Length == 0) schema = "{}";
        if (Encoding.UTF8.GetByteCount(schema) > MaxSchemaBytes)
            return ToolExecution.Fail($"input_schema 过大（上限 {MaxSchemaBytes / 1024} KB）。");
        if (ValidateSchema(schema) is { } schemaError)
            return ToolExecution.Fail("input_schema 不合法：" + schemaError);

        // 重名必须挡：tool_tools.name 没有唯一约束，只会在内存注册表里被静默覆盖 ——
        // 那样模型写一个叫 bash 的工具就能把真正的 bash 顶掉。
        if (ToolRegistry.Instance.ResolveByName(name) is { } existing)
            return ToolExecution.Fail(
                $"工具名已存在：{name}（来源：{existing.Source}）。换一个名字，或让用户直接修改原工具。");

        // 运行时没装就别落盘：留一个跑不起来的工具比不建更糟
        var probe = await RuntimeDetector.ProbeAsync(runtime.Value);
        if (!probe.Found)
            return ToolExecution.Fail(
                $"未检测到 {RuntimeDetector.DisplayName(runtime.Value)} 运行时，无法创建该工具。" +
                "请让用户到「设置 → 环境配置」检查，或改用另一种运行时。");

        var dir = AppPaths.Tools;
        var path = Path.Combine(dir, name + (runtime == ScriptRuntimeKind.Python ? ".py" : ".js"));

        var tool = new ToolDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            Source = ToolSource.Script,
            Runtime = runtime.Value,
            Target = path,
            InputSchemaJson = schema,
            Enabled = true,
            // 固定「危险」：等级若能让模型自评，它可以把自己写的工具标成「安全」从而绕开审批。
            // 用户可在工具页改成别的等级，那是知情之后的选择。
            DangerLevel = ToolDangerLevel.Danger,
            CreatedBy = ToolDefinition.CreatedByAgent,
        };

        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(false));
            await ToolRepository.CreateAsync(tool);
        }
        catch (Exception ex)
        {
            // 落库失败就把文件也删掉，不留一个「有脚本、没登记」的孤儿文件
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉也没别的办法 */ }
            return ToolExecution.Fail($"创建工具失败：{ex.Message}");
        }

        ToolRegistry.Instance.Register(tool);

        // 说清楚「还没生效」：本轮请求的 tools 数组早就发出去了，会话的启用列表也不含它，
        // 不写明的话模型会紧接着调用一个它以为已经能用的工具，然后收到「未知工具」。
        return ToolExecution.Ok($"""
            已创建脚本工具「{name}」（{RuntimeDetector.DisplayName(runtime.Value)}）。
            脚本路径：{path}
            危险等级：危险（用户可在「工具」页调整）。

            注意：该工具**尚未启用**，本轮不可调用。请告知用户到「工具」页勾选它，
            之后在同一会话里即可使用。
            """);
    }

    private static async Task<ToolExecution> CreateSkillAsync(ChatToolCall call)
    {
        if (!AppSettingsStore.Current.AllowAgentSelfExtension)
            return ToolExecution.Fail("用户已关闭「允许 AI 创建工具与技能」，无法创建技能。");

        var name = (BuiltinToolContext.GetArg(call, "name") ?? "").Trim();
        if (!SkillNamePattern.IsMatch(name))
            return ToolExecution.Fail(
                "技能名不合法：必须小写字母或数字开头，只含小写字母、数字、. _ -，长度 2-64（如 review-sql）。");

        var description = (BuiltinToolContext.GetArg(call, "description") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(description))
            return ToolExecution.Fail("缺少 description：模型靠它判断什么时候该加载这个技能。");

        var body = BuiltinToolContext.GetArg(call, "body") ?? "";
        if (string.IsNullOrWhiteSpace(body))
            return ToolExecution.Fail("缺少 body：SKILL.md 正文不能为空。");
        if (Encoding.UTF8.GetByteCount(body) > MaxSkillBodyBytes)
            return ToolExecution.Fail(
                $"技能正文过大（上限 {MaxSkillBodyBytes / 1024} KB）。技能会全文注入 system 提示词，" +
                "过大会挤掉对话本身；请删掉通用常识，只留这个任务真正需要的步骤与约定。");

        var alias = (BuiltinToolContext.GetArg(call, "alias") ?? "").Trim();

        // 重名（含内置技能）必须挡：覆盖掉内置技能等于用户失去官方指引，而 CreateOnDisk 抛的是
        // 「已存在」这种模糊说法，这里先给出更具体的提示。
        if (SkillRegistry.Instance.ResolveById(name) is not null
            || SkillRegistry.Instance.GetAll().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ToolExecution.Fail($"技能已存在：{name}。换一个名字，或让用户直接改原技能。");

        SkillDefinition created;
        try
        {
            created = SkillRegistry.Instance.CreateOnDisk(
                name, name, alias, description, body, SkillDefinition.OriginAgent);
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"创建技能失败：{ex.Message}");
        }

        return ToolExecution.Ok($"""
            已创建技能「{created.DisplayName}」（{created.Id}），文件：{Path.Combine(SkillRegistry.SkillsRoot, name, "SKILL.md")}

            注意：该技能**尚未加载**。要让它在当前会话生效，请调用 load_skill（name="{name}"）；
            用户也可以在「技能」页查看、修改或删除它。加载前请先确认正文写的是用户想要的东西。
            """);
    }

    /// <summary>
    /// 校验入参 schema：能解析、根是 object、属性名都是合法标识符。
    /// 返回 null 表示通过。这里只做「能不能安全下发给模型」的检查，不做完整 JSON Schema 校验 ——
    /// 各家服务商对 schema 的支持本来就不一致，校验太严反而会挡掉合理写法。
    /// </summary>
    private static string? ValidateSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "根必须是 JSON 对象";

            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "object")
                return "根节点的 type 必须是 \"object\"";

            if (root.TryGetProperty("properties", out var props))
            {
                if (props.ValueKind != JsonValueKind.Object) return "properties 必须是对象";
                foreach (var prop in props.EnumerateObject())
                    if (!Regex.IsMatch(prop.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                        return $"属性名不合法：{prop.Name}（只能用字母、数字、下划线，且不以数字开头）";
            }

            return null;
        }
        catch (JsonException ex)
        {
            return "不是有效的 JSON（" + ex.Message + "）";
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
        if (AttachmentStore.IsVideo(path))
            return ToolExecution.Fail($"这是视频文件，请用 view_video 查看：{path}");
        if (!AttachmentStore.IsImage(path)) return ToolExecution.Fail($"不是可识别的图片格式：{path}");

        var bytes = new FileInfo(path).Length;
        if (bytes > MaxImageBytes)
            return ToolExecution.Fail($"图片过大（{bytes / 1024 / 1024} MB），上限 {MaxImageBytes / 1024 / 1024} MB");

        return ToolExecution.OkImage($"已读取图片：{path}", path);
    }

    // ---- view_video ----

    /// <summary>
    /// 单段视频的大小上限。视频不做解码或抽帧，整段 base64 进请求体，
    /// 所以这里守的是「一次性内联」这条路的上限：base64 会把体积撑大约 4/3，
    /// 20 MB 的源文件编码后约 27 MB，已接近多数端点（Gemini / Qwen / GLM）的内联额度。
    /// </summary>
    private const long MaxVideoBytes = 20 * 1024 * 1024;

    private static ToolExecution ViewVideo(ChatToolCall call, BuiltinToolContext ctx)
    {
        var path = BuiltinToolContext.ResolvePath(ctx, BuiltinToolContext.GetArg(call, "path"));
        if (string.IsNullOrEmpty(path)) return ToolExecution.Fail("缺少 path 参数");

        // 不支持视频的模型：明确告诉它看不了，而不是让整轮请求被 API 拒掉
        if (!ctx.SupportsVideo)
            return ToolExecution.Fail("当前模型不支持视频输入，无法查看视频内容。请基于文件名与用户描述继续，或请用户直接说明视频内容。");

        if (!File.Exists(path)) return ToolExecution.Fail($"文件不存在：{path}");
        if (AttachmentStore.IsImage(path))
            return ToolExecution.Fail($"这是图片，请用 view_image 查看：{path}");
        if (!AttachmentStore.IsVideo(path)) return ToolExecution.Fail($"不是可识别的视频格式：{path}");

        var bytes = new FileInfo(path).Length;
        if (bytes > MaxVideoBytes)
            return ToolExecution.Fail(
                $"视频过大（{bytes / 1024 / 1024} MB），上限 {MaxVideoBytes / 1024 / 1024} MB。" +
                "请让用户换更短或更低码率的片段（本地不做抽帧压缩）。");

        return ToolExecution.OkVideo($"已读取视频：{path}", path);
    }

    // ---- view_video_frames（本地抽帧） ----

    /// <summary>默认抽多少帧。</summary>
    private const int DefaultFrameCount = 12;

    /// <summary>一次最多抽多少帧。再多就是拿上下文换细节，模型反而看不过来。</summary>
    private const int MaxFrameCount = 24;

    /// <summary>抽出来的帧的长边上限（像素）。4K 原样送一张就能吃掉几千 token。</summary>
    private const int FrameLongSide = 768;

    /// <summary>ffmpeg / ffprobe 单次运行的上限。抽 24 帧通常几秒完成，两分钟是给慢盘和长视频的余量。</summary>
    private const int FrameProcessTimeoutSeconds = 120;

    /// <summary>
    /// 抽帧：把「模型看不了整段视频」这件事在本地解决 —— 用 ffmpeg 按间隔解码出若干帧，
    /// 压到长边 768 后作为图片交上去（与 view_image 走同一条图片通道，不碰视频通道）。
    ///
    /// 为什么要压：一帧 1080p 原图约 1.5 MB、编码后仍是几十万 token 量级；
    /// 768 英寸长边的 JPEG 每帧约 200~400 token，12 帧才 3k~5k，才有资格和正文比。
    /// </summary>
    private static async Task<ToolExecution> ViewVideoFramesAsync(
        ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        var path = BuiltinToolContext.ResolvePath(ctx, BuiltinToolContext.GetArg(call, "path"));
        if (string.IsNullOrEmpty(path)) return ToolExecution.Fail("缺少 path 参数");

        // 交出去的是图片而不是视频：要的是「视觉」能力，不是「视频输入」能力
        if (!ctx.SupportsVision)
            return ToolExecution.Fail("当前模型不支持视觉图像，抽出来的帧它也看不了。请基于文件名与用户描述继续，或请用户直接说明视频内容。");

        if (!File.Exists(path)) return ToolExecution.Fail($"文件不存在：{path}");
        if (AttachmentStore.IsImage(path))
            return ToolExecution.Fail($"这是图片，请用 view_image 查看：{path}");
        if (!AttachmentStore.IsVideo(path)) return ToolExecution.Fail($"不是可识别的视频格式：{path}");

        var ffmpeg = RuntimeDetector.ResolveExecutable(ScriptRuntimeKind.Ffmpeg);
        if (ffmpeg is null)
            return ToolExecution.Fail(
                "没找到 ffmpeg，无法抽帧。请让用户到「设置 → 环境配置」的 FFmpeg 卡片里点「重新检测」，" +
                "装好之后还没找到就点「手动选择…」指定 ffmpeg.exe。");

        // ffprobe 一般和 ffmpeg 同目录；先找兄弟文件，再退回 PATH
        var ffprobe = FindSibling(ffmpeg, "ffprobe") ?? RuntimeDetector.FindInPath("ffprobe");
        if (ffprobe is null)
            return ToolExecution.Fail(
                "没找到 ffprobe（读视频时长要用它）。它和 ffmpeg 在同一个目录里，" +
                "请让用户在环境配置的 FFmpeg 卡片里指定那个目录下的 ffprobe.exe。");

        // 时长决定抽帧范围与间隔，先问出来
        var probe = await RunProcessAsync(NewPsi(ffprobe, new[]
        {
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        }), FrameProcessTimeoutSeconds, ct);
        if (!probe.Success)
            return ToolExecution.Fail($"读取视频信息失败：{probe.Display}");
        if (!double.TryParse(probe.Content.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var total) || total <= 0)
            return ToolExecution.Fail($"读不出视频时长：{probe.Content.Trim()}");

        var start = Math.Clamp(ReadDoubleArg(call, "start") ?? 0, 0, total);
        var remaining = total - start;
        if (remaining <= 0.05)
            return ToolExecution.Fail($"起点 {start:0.#} 秒已经在视频末尾（总长 {total:0.#} 秒）。");

        var span = ReadDoubleArg(call, "duration") is { } wanted and > 0
            ? Math.Min(wanted, remaining)
            : remaining;

        var maxFrames = Math.Clamp(ReadIntArg(call, "max_frames") ?? DefaultFrameCount, 1, MaxFrameCount);

        // 间隔：模型给了就按它；但帧数会超上限时自动放宽 —— 少看几帧还能答，直接报错就什么都没得看
        var interval = ReadDoubleArg(call, "interval") is { } every and > 0 ? every : span / maxFrames;
        if (span / interval > maxFrames) interval = span / maxFrames;
        interval = Math.Max(interval, 0.05);

        var dir = Path.Combine(
            AppPaths.Attachments, "frames",
            $"{SafeFileName(Path.GetFileNameWithoutExtension(path))}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        Directory.CreateDirectory(dir);

        // fps 滤镜要的是有理数：写成 1000/毫秒数，别让 1/3.47 这种小数被解析成别的东西
        var rate = $"1000/{Math.Max(1, (int)Math.Round(interval * 1000))}";
        var run = await RunProcessAsync(NewPsi(ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error",
            // -nostdin：别让它去读继承来的 stdin（从管道启动的应用会把 stdin 传下去，ffmpeg 会一直等）
            "-nostdin",
            // -ss 放在 -i 前面：输入侧定位，长视频不用从头解；默认 accurate_seek 保证时间点准
            "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", span.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path,
            // 长边压到 768（filter 里 min() 的逗号按 ffmpeg 语法反斜杠转义）
            "-vf", $"fps={rate},scale=min({FrameLongSide}\\,iw):-2",
            "-an", "-q:v", "3", "-y",
            Path.Combine(dir, "frame_%04d.jpg"),
        }), FrameProcessTimeoutSeconds, ct);
        if (!run.Success) return ToolExecution.Fail($"ffmpeg 抽帧失败：{run.Display}");

        var files = Directory.GetFiles(dir, "frame_*.jpg");
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
            return ToolExecution.Fail("ffmpeg 没有产出任何帧（片段太短，或整段没有可解码的画面）。");

        // 序号换算回时间点写进文件名：模型读文件名就知道每帧在第几秒，不必再对照一份时间轴
        var frames = new List<string>();
        long bytes = 0;
        var take = Math.Min(files.Length, maxFrames);
        for (var n = 0; n < take; n++)
        {
            var at = start + (n + 0.5) * interval;
            var target = Path.Combine(dir, $"frame_{n + 1:0000}_{FormatClock(at)}.jpg");
            try
            {
                if (!string.Equals(files[n], target, StringComparison.Ordinal)) File.Move(files[n], target);
            }
            catch
            {
                target = files[n]; // 改名失败不影响内容，继续用原文件名
            }

            frames.Add(target);
            try { bytes += new FileInfo(target).Length; } catch { /* 取不到大小就不统计 */ }
        }

        var sb = new StringBuilder();
        sb.Append($"已抽取 {frames.Count} 帧（每 {interval:0.##} 秒一帧，覆盖第 {start:0.#}–{start + span:0.#} 秒；");
        sb.Append($"原视频 {total:0.#} 秒，图像合计 {bytes / 1024.0 / 1024.0:0.##} MB）。这些帧随本条工具结果一起发出：");
        for (var n = 0; n < frames.Count; n++)
            sb.Append('\n').Append($"{n + 1}. 第 {start + (n + 0.5) * interval:0.#} 秒 → {frames[n]}");

        return ToolExecution.OkImages(sb.ToString(), frames);
    }

    /// <summary>同目录下的兄弟可执行文件（ffprobe 就在 ffmpeg 旁边）。</summary>
    private static string? FindSibling(string exe, string name)
    {
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return null;

        var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(sibling) ? sibling : null;
    }

    private static ProcessStartInfo NewPsi(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>文件名里去掉各平台的非法字符：视频名要拿来当抽帧目录名。</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var safe = sb.ToString().Trim();
        return safe.Length == 0 ? "video" : (safe.Length > 60 ? safe[..60] : safe);
    }

    /// <summary>秒数写成 00m14s / 01h02m03s，只为了让文件名里的时间点一眼能读。</summary>
    private static string FormatClock(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return total >= 3600
            ? $"{total / 3600:00}h{total % 3600 / 60:00}m{total % 60:00}s"
            : $"{total / 60:00}m{total % 60:00}s";
    }

    /// <summary>读一个小数参数；缺失或不是数字时返回 null（调用方自己决定默认值）。</summary>
    private static double? ReadDoubleArg(ChatToolCall call, string name)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments)) return null;

        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(
                    value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- ask_user_question ----

    private static async Task<ToolExecution> AskUserQuestionAsync(ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        if (ctx.AskUser is null)
            return ToolExecution.Fail("当前会话不支持向用户提问");

        var questions = AskUserQuestionTool.Parse(call.Arguments, out var error);
        if (questions is null)
            return ToolExecution.Fail(error ?? "问题参数无效");

        var answers = await ctx.AskUser(questions, ct);
        return ToolExecution.Ok(answers is null
            ? AskUserQuestionTool.CancelledText
            : AskUserQuestionTool.FormatAnswers(questions, answers));
    }

    // ---- ask_user_continue ----

    /// <summary>默认等多久。用户做现实操作常要好几分钟，别给太小；上限一小时。</summary>
    private const int DefaultContinueSeconds = 900;
    private const int MaxContinueSeconds = 3600;

    private static async Task<ToolExecution> AskUserContinueAsync(
        ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        if (ctx.WaitForUser is null)
            return ToolExecution.Fail("当前会话不支持「等待用户操作」");

        var title = BuiltinToolContext.GetArg(call, "title") ?? "";
        var instructions = BuiltinToolContext.GetArg(call, "instructions") ?? "";

        if (string.IsNullOrWhiteSpace(instructions))
            return ToolExecution.Fail("缺少 instructions：要写清用户该怎么做。");

        var seconds = ReadIntArg(call, "timeout_seconds") ?? DefaultContinueSeconds;
        seconds = Math.Clamp(seconds, 30, MaxContinueSeconds);

        // 这里刻意不套工具统一的 5 分钟超时：那是给「跑得快」的工具用的，
        // 而这个工具的耗时取决于人在现实里做多快。所以自己管时间，并且
        // 超时后不是「被取消」，而是如实告诉模型还没等到 —— 让他自己决定要不要再等。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            var answered = await ctx.WaitForUser(title, instructions, timeout.Token);

            return answered switch
            {
                true => ToolExecution.Ok(
                    "用户已完成操作，可以继续。" +
                    "（如果下一步依赖刚才的变化，先重新观察页面，不要假设已经生效。）"),
                false => ToolExecution.Ok(
                    "用户取消了这次操作，没有完成。" +
                    "不要再重复同一个请求；问清他的意图，或者换一条不需要他动手的路。"),
                null => ToolExecution.Ok(
                    "没有等到用户（会话窗口没打开，或他离开了）。不要假设操作已经完成。"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 自己的超时：如实说「还在等」，把决定权交回给模型
            return ToolExecution.Fail(
                $"等满了 {seconds} 秒，用户还没点「继续」。可以再调一次继续等，或者先问他还需要什么。");
        }
    }

    /// <summary>读一个整数参数；缺失或不是数字时返回 null（调用方自己决定默认值）。</summary>
    private static int? ReadIntArg(ChatToolCall call, string name)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments)) return null;

        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- invalid ----

    // ---- kb_search ----

    /// <summary>
    /// 查知识库。会话没选库时直接说清楚 —— 不偷偷去查某个默认库，
    /// 否则「答得头头是道但引用的是别的项目」比答不出来更糟。
    /// </summary>
    private static async Task<ToolExecution> KbSearchAsync(
        ChatToolCall call, BuiltinToolContext ctx, CancellationToken ct)
    {
        var query = BuiltinToolContext.GetArg(call, "query") ?? "";
        if (string.IsNullOrWhiteSpace(query))
            return ToolExecution.Fail("缺少 query 参数");

        if (string.IsNullOrWhiteSpace(ctx.KnowledgeBaseId))
            return ToolExecution.Fail(
                "这个会话没有选定知识库。请让用户在输入框左侧的知识库选择器里选一个，"
                + "或者告诉用户「当前没有可用知识库」。");

        var topK = 6;
        if (int.TryParse(BuiltinToolContext.GetArg(call, "top_k"), out var parsed) && parsed > 0)
            topK = Math.Min(parsed, 20);

        var library = await KnowledgeBaseRepository.GetAsync(ctx.KnowledgeBaseId);
        if (library is null)
            return ToolExecution.Fail("这个会话选定的知识库已经不存在了，请让用户重新选一个。");

        var embedModel = await AppDefaults.ResolveEmbeddingModelAsync();

        List<Models.KbSearchHit> hits;
        try
        {
            hits = await KnowledgeBaseIndexer.SearchAsync(
                library.Id, query, topK, embedModel, ct);
        }
        catch (Exception ex)
        {
            return ToolExecution.Fail($"知识库检索失败：{ex.Message}");
        }

        if (hits.Count == 0)
        {
            var reason = embedModel is null
                ? "（向量检索未启用：没有配置嵌入模型，这次只用了关键词匹配）"
                : "";
            return ToolExecution.Ok($"「{library.Name}」里没有找到相关内容。{reason}");
        }

        var sb = new StringBuilder();
        sb.Append("知识库「").Append(library.Name).Append("」里找到 ").Append(hits.Count).AppendLine(" 条：");
        if (embedModel is null)
            sb.AppendLine("（向量检索未启用：没有配置嵌入模型，结果来自关键词匹配）");
        sb.AppendLine();

        var index = 1;
        foreach (var hit in hits)
        {
            var where = hit.IsNote
                ? $"笔记「{hit.Title}」"
                : hit.Heading.Length > 0
                    ? $"{hit.Title} › {hit.Heading}"
                    : hit.Title;

            sb.Append('[').Append(index++).Append("] ").AppendLine(where);
            sb.AppendLine(hit.Text.Trim());
            sb.AppendLine();
        }

        return ToolExecution.Ok(sb.ToString().TrimEnd());
    }

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
