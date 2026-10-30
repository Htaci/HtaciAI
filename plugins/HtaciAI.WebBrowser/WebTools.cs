using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 页面自动化工具集。所有工具都带 <c>browser_id</c>（哪个浏览器窗口）与可选的
/// <c>tab_id</c>（窗口里的哪个标签）。
///
/// <b>危险等级的划分依据</b>不是「影响力大小」，而是「会不会改动用户看见的东西」：
/// <list type="bullet">
///   <item><b>Safe</b>：只读。snapshot / screenshot / console / 等待 / 列表；</item>
///   <item><b>Risk</b>：会改页面状态。点击、填表、按键、导航 —— 这些可能提交订单、删数据，需要用户点头；</item>
///   <item><b>Danger</b>：<c>web_evaluate</c> 能在用户已登录的站点里执行任意 JS，
///         等于拿到那个身份的完整权限（读 cookie、发转账请求），每次都要审批。</item>
/// </list>
/// </summary>
internal static class WebTools
{
    /// <summary>注册除 web_open / web_screenshot / web_list_browsers 之外的全部工具。</summary>
    public static void Register(
        IPluginContext context,
        BrowserManager manager,
        List<IDisposable> registrations)
    {
        var tools = context.Tools!;
        var automation = new BrowserAutomation(manager);

        // ------------------------------------------------------------
        // 读取类
        // ------------------------------------------------------------

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_snapshot",
                Description =
                    "抓当前页面的可操作元素清单，每个元素带一个 uid。" +
                    "点击/填表前必须先调它拿到 uid；页面变了（导航、刷新、弹窗）uid 会失效，要重新抓。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id（web_open 返回的）"},
                    "tab_id": {"type": "string", "description": "可选。窗口里的哪个标签，省略则用当前标签"}
                    """, required: new[] { "browser_id" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, ct) => await GuardAsync(context, "web_snapshot", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId, async (cdp, _) =>
                {
                    var snap = await PageSnapshot.CaptureAsync(cdp, ct);

                    var head = $"页面：{snap.Title}（{snap.Url}）\n" +
                               $"可操作元素：{snap.Count} 个\n\n";

                    var body = snap.Count == 0
                        ? "（这个页面没有可识别的可操作元素。可能是空白页，或内容都在 canvas/iframe 里。）"
                        : snap.Text;

                    var tail = snap.Truncated
                        ? $"\n\n（只显示了前 {PageSnapshot.MaxLines} 个。目标不在这里时，" +
                          "用 web_evaluate 按文本精确查找，或先滚动/展开再看。）"
                        : "";

                    return PluginToolResult.Ok(head + body + tail);
                }, ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_console",
                Description = "列出页面的控制台消息（报错优先）。排查「点了没反应」时先看它。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"},
                    "limit": {"type": "integer", "description": "最多返回几条，默认 30"}
                    """, required: new[] { "browser_id" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, ct) => await GuardAsync(context, "web_console", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var limit = (int)Math.Clamp(ToolArgs.Num(call.ArgumentsJson, "limit") ?? 30, 1, 200);

                return await automation.RunAsync(browserId, call.SessionId, tabId, async (cdp, surface) =>
                {
                    var messages = surface.DrainConsole(limit);

                    if (messages.Count == 0)
                        return PluginToolResult.Ok("控制台没有消息（页面可能还没产生任何日志）。");

                    var lines = new List<string> { $"最近 {messages.Count} 条控制台消息：" };
                    lines.AddRange(messages);
                    return PluginToolResult.Ok(string.Join("\n", lines));
                }, ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_list_tabs",
                Description = "列出某个浏览器窗口里开着的标签页（id、标题、地址）。不确定 tab_id 时用它。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id；省略则列出所有浏览器"}
                    """),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, ct) => await GuardAsync(context, "web_list_tabs", async () =>
            {
                var browserId = ToolArgs.Str(call.ArgumentsJson, "browser_id");

                return await automation.RunAsync(async () =>
                {
                    if (string.IsNullOrWhiteSpace(browserId))
                        return PluginToolResult.Ok(DescribeAllBrowsers(manager));

                    manager.RequireAccess(browserId, call.SessionId);
                    var window = manager.Require(browserId);

                    var lines = new List<string> { $"浏览器 {browserId} 的标签页：" };
                    foreach (var (id, title, url) in window.GetPageInfo())
                        lines.Add($"- {id}（{title}）：{url}");

                    return PluginToolResult.Ok(string.Join("\n", lines));
                }, ct);
            })));

        // ------------------------------------------------------------
        // 操作类（会改页面状态 → Risk）
        // ------------------------------------------------------------

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_click",
                Description =
                    "点击页面上的元素。uid 来自 web_snapshot。" +
                    "点击会发真实鼠标事件，能触发页面自己的交互逻辑。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "uid": {"type": "string", "description": "元素 uid（web_snapshot 里的编号）"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"},
                    "double_click": {"type": "boolean", "description": "是否双击，默认 false"}
                    """, required: new[] { "browser_id", "uid" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_click", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var uid = RequireArg(call.ArgumentsJson, "uid");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var dbl = ToolArgs.Bool(call.ArgumentsJson, "double_click", false);

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.ClickAsync(cdp, uid, dbl, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_fill",
                Description =
                    "给输入框/下拉框/勾选框赋值。uid 来自 web_snapshot。" +
                    "勾选框传 \"true\"/\"false\"，下拉框传选项的 value 或可见文本。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "uid": {"type": "string", "description": "元素 uid"},
                    "value": {"type": "string", "description": "要填的值"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "uid", "value" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_fill", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var uid = RequireArg(call.ArgumentsJson, "uid");
                var value = ToolArgs.Str(call.ArgumentsJson, "value") ?? "";
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.FillAsync(cdp, uid, value, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_fill_form",
                Description =
                    "一次填多个字段。<b>填表单优先用它</b>，不要循环调 web_fill —— " +
                    "少一半轮次，也不会因为中间某次失败而填一半。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "elements": {"type": "array", "description": "要填的字段列表", "items": {"type": "object", "properties": {"uid": {"type": "string", "description": "元素 uid"}, "value": {"type": "string", "description": "要填的值"}}, "required": ["uid", "value"]}},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "elements" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_fill_form", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var fields = ToolArgs.Fields(call.ArgumentsJson, "elements");

                if (fields.Count == 0)
                    return PluginToolResult.Fail("缺少 elements（要填的字段列表）。");

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.FillFormAsync(cdp, fields, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_type_text",
                Description =
                    "往当前聚焦的输入框逐字打字，会触发每一次按键事件 —— " +
                    "自动补全、搜索建议这类逻辑需要它。需要先点击聚焦。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "text": {"type": "string", "description": "要输入的文本"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"},
                    "submit_key": {"type": "string", "description": "可选。输入完按什么键，如 Enter"}
                    """, required: new[] { "browser_id", "text" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_type_text", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var text = ToolArgs.Str(call.ArgumentsJson, "text") ?? "";
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var submitKey = ToolArgs.Str(call.ArgumentsJson, "submit_key");

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.TypeTextAsync(cdp, text, submitKey, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_press_key",
                Description =
                    "按一个键或组合键，如 Enter、Control+A、Shift+Tab、Escape。" +
                    "提交表单、关闭弹层、快捷键常用它。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "key": {"type": "string", "description": "键名或组合，修饰键用 + 连接（Control/Shift/Alt/Meta）"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "key" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_press_key", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var key = RequireArg(call.ArgumentsJson, "key");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.PressKeyAsync(cdp, key, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_hover",
                Description = "把鼠标移到元素上。展开下拉菜单、显示 tooltip 时用。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "uid": {"type": "string", "description": "元素 uid"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "uid" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_hover", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var uid = RequireArg(call.ArgumentsJson, "uid");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, _) => PluginToolResult.Ok(
                        await PageActions.HoverAsync(cdp, uid, ct)), ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_handle_dialog",
                Description =
                    "处理页面弹出的 alert / confirm / prompt。" +
                    "点完按钮页面卡住不动时，先看看是不是有对话框在等应答。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "action": {"type": "string", "enum": ["accept", "dismiss"], "description": "接受还是取消"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"},
                    "prompt_text": {"type": "string", "description": "prompt 对话框的输入内容"}
                    """, required: new[] { "browser_id", "action" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_handle_dialog", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var action = RequireArg(call.ArgumentsJson, "action");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var promptText = ToolArgs.Str(call.ArgumentsJson, "prompt_text");

                var accept = string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase);

                return await automation.RunAsync(browserId, call.SessionId, tabId,
                    async (cdp, surface) =>
                    {
                        var handled = await cdp.HandleDialogAsync(accept, promptText, ct);
                        return handled
                            ? PluginToolResult.Ok($"已{(accept ? "接受" : "取消")}页面对话框。")
                            : PluginToolResult.Fail(
                                "当前没有等待处理的对话框。" +
                                "如果页面看起来卡住了，可能是别的原因（加载中、脚本报错），试试 web_console。");
                    }, ct);
            })));

        // ------------------------------------------------------------
        // 导航类
        // ------------------------------------------------------------

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_navigate",
                Description = "在指定标签里跳转、后退、前进或刷新。只改地址，不开新标签（开新页面用 web_open）。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "type": {"type": "string", "enum": ["url", "back", "forward", "reload"], "description": "动作类型"},
                    "url": {"type": "string", "description": "type=url 时的目标地址"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "type" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Risk,
            },
            async (call, ct) => await GuardAsync(context, "web_navigate", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var type = RequireArg(call.ArgumentsJson, "type").ToLowerInvariant();
                var url = ToolArgs.Str(call.ArgumentsJson, "url");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId, async (cdp, surface) =>
                {
                    switch (type)
                    {
                        case "url":
                            if (string.IsNullOrWhiteSpace(url))
                                return PluginToolResult.Fail("type=url 时必须给 url。");

                            var loaded = await surface.NavigateAsync(BrowserManager.NormalizeUrl(url), ct);
                            return PluginToolResult.Ok($"已跳转到 {loaded}");

                        case "back":
                            await surface.GoBackAsync(ct);
                            await Task.Delay(200, ct);
                            return PluginToolResult.Ok($"已后退，当前地址：{surface.CurrentUrl}");

                        case "forward":
                            await surface.GoForwardAsync(ct);
                            await Task.Delay(200, ct);
                            return PluginToolResult.Ok($"已前进，当前地址：{surface.CurrentUrl}");

                        case "reload":
                            await surface.ReloadAsync(ct);
                            await Task.Delay(500, ct);
                            return PluginToolResult.Ok($"已刷新：{surface.CurrentUrl}");

                        default:
                            return PluginToolResult.Fail(
                                $"不认识的 type：{type}（可用 url / back / forward / reload）");
                    }
                }, ct);
            })));

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_wait_for",
                Description =
                    "<b>等页面出现指定文字</b>，等着它出现而不是靠固定 sleep。" +
                    "点完按钮要等加载、等结果时用它，比盲等可靠得多。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "texts": {"type": "array", "description": "要等哪些文字（任一出现就算命中）", "items": {"type": "string"}},
                    "tab_id": {"type": "string", "description": "可选。标签 id"},
                    "timeout_seconds": {"type": "integer", "description": "最多等几秒，默认 15"},
                    "visible_only": {"type": "boolean", "description": "是否只算可见文字，默认 true"}
                    """, required: new[] { "browser_id", "texts" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, ct) => await GuardAsync(context, "web_wait_for", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");
                var texts = ToolArgs.Strings(call.ArgumentsJson, "texts");
                var seconds = Math.Clamp(ToolArgs.Num(call.ArgumentsJson, "timeout_seconds") ?? 15, 1, 300);
                var visibleOnly = ToolArgs.Bool(call.ArgumentsJson, "visible_only", true);

                if (texts.Count == 0)
                    return PluginToolResult.Fail("缺少 texts（要等哪些文字）。");

                return await automation.RunAsync(browserId, call.SessionId, tabId, async (cdp, _) =>
                {
                    var hit = await cdp.WaitForTextAsync(
                        texts, visibleOnly, TimeSpan.FromSeconds(seconds), ct);

                    if (hit is not null)
                        return PluginToolResult.Ok($"等到了：「{hit}」");

                    return PluginToolResult.Fail(
                        $"等了 {seconds} 秒，页面上没出现这些文字中的任何一个：" +
                        string.Join("、", texts) +
                        "。可以用 web_snapshot 看看现在页面上有什么，或者先看 web_console 有没有报错。");
                }, ct);
            })));

        // ------------------------------------------------------------
        // 执行类（Danger）
        // ------------------------------------------------------------

        registrations.Add(tools.Register(
            new PluginToolDefinition
            {
                Name = "web_evaluate",
                Description =
                    "在当前页面里执行一段 JavaScript 并取回结果（必须可 JSON 序列化）。" +
                    "适合精确查数据、算数值、读页面状态；snapshot 找不到的元素也可以用它按选择器定位。" +
                    "<b>这会以用户登录身份执行任意代码</b>，只在确实必要时用。",
                InputSchemaJson = Schema("""
                    "browser_id": {"type": "string", "description": "浏览器 id"},
                    "expression": {"type": "string", "description": "JS 表达式或立即执行的函数，返回值会被 JSON 序列化"},
                    "tab_id": {"type": "string", "description": "可选。标签 id"}
                    """, required: new[] { "browser_id", "expression" }),
                Toolset = WebBrowserPlugin.Tools,
                DangerLevel = PluginToolDangerLevel.Danger,
            },
            async (call, ct) => await GuardAsync(context, "web_evaluate", async () =>
            {
                var browserId = RequireArg(call.ArgumentsJson, "browser_id");
                var expression = RequireArg(call.ArgumentsJson, "expression");
                var tabId = ToolArgs.Str(call.ArgumentsJson, "tab_id");

                return await automation.RunAsync(browserId, call.SessionId, tabId, async (cdp, _) =>
                {
                    var result = await cdp.EvaluateAsync(expression, ct);

                    var json = result.ValueKind switch
                    {
                        JsonValueKind.Undefined => "null",
                        JsonValueKind.Null => "null",
                        JsonValueKind.String => JsonSerializer.Serialize(result.GetString()),
                        _ => result.GetRawText(),
                    };

                    // 长结果会吃掉上下文：截断并在末尾说明
                    const int maxChars = 4000;
                    if (json.Length > maxChars)
                        json = json[..maxChars] + "\n…（结果过长已截断，请在 JS 里先筛选/汇总）";

                    return PluginToolResult.Ok(json);
                }, ct);
            })));
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>
    /// 把所有异常翻成给模型看的一句话。
    /// <b>不吞异常</b>：失败原因必须让模型能据此调整，否则它会原样重试。
    /// </summary>
    private static async Task<PluginToolResult> GuardAsync(
        IPluginContext context, string toolName, Func<Task<PluginToolResult>> work)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException)
        {
            return PluginToolResult.Fail($"{toolName} 已取消。");
        }
        catch (UnauthorizedAccessException ex)
        {
            // 权限问题不是故障，是「需要用户点头」—— 要给模型可执行的下一步
            return PluginToolResult.Fail(
                ex.Message + "\n可以请用户点工具栏上的「共享给智能体」把这个窗口交给当前会话，" +
                "或者直接不带 browser_id 调 web_open 新开一个（新窗口自动归你）。");
        }
        catch (InvalidOperationException ex)
        {
            return PluginToolResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            context.Log.Error($"{toolName} 失败", ex);
            return PluginToolResult.Fail($"{toolName} 失败：{ex.Message}");
        }
    }

    /// <summary>取一个必填字符串参数，缺失时抛出可读的异常。</summary>
    private static string RequireArg(string? json, string name)
    {
        var value = ToolArgs.Str(json, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"缺少 {name} 参数。");

        return value;
    }

    /// <summary>
    /// 拼 JSON Schema。
    ///
    /// <b>为什么用这个而不是每处手写整串 JSON</b>：手写时那些反斜杠要转义两层，
    /// 改起来极易出错（写错一个位置，schema 非法，工具直接登记失败还不易察觉）。
    /// </summary>
    private static string Schema(string properties, string[]? required = null)
    {
        var req = required is { Length: > 0 }
            ? ",\"required\":[" + string.Join(",", Array.ConvertAll(required, r => $"\"{r}\"")) + "]"
            : "";

        return "{\"type\":\"object\",\"properties\":{" + properties + "}" + req + "}";
    }

    /// <summary>列出所有开着的浏览器，供「不确定有哪些」时使用。</summary>
    private static string DescribeAllBrowsers(BrowserManager manager)
    {
        var ids = manager.OpenIds;
        if (ids.Count == 0) return "现在没有开着的浏览器。用 web_open 打开一个。";

        var lines = new List<string> { $"共 {ids.Count} 个浏览器：" };
        foreach (var id in ids)
        {
            try
            {
                var window = manager.Require(id);
                lines.Add($"- {id}（{window.PageCount} 个标签，当前 {window.CurrentUrl}）");
                foreach (var (tabId, title, url) in window.GetPageInfo())
                    lines.Add($"    {tabId}（{title}）：{url}");
            }
            catch (Exception ex)
            {
                lines.Add($"- {id}（读取失败：{ex.Message}）");
            }
        }

        return string.Join("\n", lines);
    }
}
