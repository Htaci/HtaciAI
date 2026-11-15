using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Network;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>
/// Anthropic Messages 协议客户端（<c>POST /v1/messages</c>）。
///
/// 与 OpenAI 两套协议的差异全在 wire 层，网关侧无感：
/// <list type="bullet">
///   <item>角色只有 user / assistant，system 走顶层 <c>system</c>，不在 messages 里；</item>
///   <item>内容一律是 block 数组（text / image / tool_use / tool_result），没有「一个字符串的 content」；</item>
///   <item>工具调用是 assistant 消息里的 <c>tool_use</c> block，结果必须由<b>紧接着的下一条 user 消息</b>
///     里的 <c>tool_result</c> 回答；</item>
///   <item>相邻同角色消息会被合并 —— 服务端要求角色交替，「连续两条 user」直接 400。</item>
/// </list>
///
/// <b>思考：</b>统一走 adaptive（<c>thinking.type = "adaptive"</c>），强度用 <c>output_config.effort</c>。
/// 不用 legacy 的 <c>enabled + budget_tokens</c>：那一套在 4.7 及以后的模型上恒 400，
/// 而 adaptive 是 4.6 起的现行写法。NoThink 与 Default 都不下发 thinking ——
/// 多数 5.x 模型对 <c>disabled</c> 直接 400（见 models.ini 各条注释），
/// 协议层又看不出当前是哪个模型，宁可不传。
/// <c>display: "summarized"</c> 是刻意写的：5.x 默认 omitted，不写就在界面上看不到任何思考内容。
///
/// <b>历史里的思考块不回放。</b>Anthropic 的 thinking block 带签名，且签名与「生成它时的
/// 前缀（system / tools / 之前的全部消息）」「生成它的账号」绑定。本应用支持编辑消息、
/// 删除、重新生成、上下文压缩 —— 这些都会改动前缀，回放旧签名在 5.1 及以后的模型上
/// 会被判为 <c>Invalid signature in thinking block</c>（400）。官方文档明确：
/// 不回放思考块不会报错，只是模型用不到那部分旧推理。所以这里只回放正文与工具调用。
/// </summary>
public sealed class AnthropicChatClient : IChatClient
{
    /// <summary>共享客户端：走不走代理由设置页的「使用系统代理」决定，见 <see cref="HttpClientProvider"/>。</summary>
    private static HttpClient Http => HttpClientProvider.Get(TimeSpan.FromMinutes(5));

    /// <summary>必带的协议版本头，缺了服务端直接 400。</summary>
    private const string ApiVersion = "2023-06-01";

    /// <summary>
    /// <c>max_tokens</c> 在 Messages 协议里是<b>必填</b>，且没有「不限制」的写法。
    /// 模型表里没记最大输出时用这个保守值（Claude 系列普遍 ≥ 8192）。
    /// </summary>
    private const int DefaultMaxTokens = 8192;

    /// <summary>Anthropic 只接受这四种图片格式；其余格式（bmp / tiff / svg）会 400，只能降级成文本。</summary>
    private static readonly string[] AllowedImageMimes =
        { "image/jpeg", "image/png", "image/gif", "image/webp" };

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;

    public string ModelName => _model;

    /// <param name="maxOutputTokens">模型的最大输出 token（来自模型表）；≤ 0 表示未知。</param>
    public AnthropicChatClient(string endpoint, string apiKey, string model, long maxOutputTokens = -1)
    {
        _endpoint = NormalizeMessagesEndpoint(endpoint);
        _apiKey = apiKey;
        _model = model;
        _maxTokens = maxOutputTokens > 0 && maxOutputTokens <= int.MaxValue
            ? (int)maxOutputTokens
            : DefaultMaxTokens;
    }

    /// <summary>
    /// 规范化到 Messages 端点：<c>https://api.anthropic.com</c> → <c>.../v1/messages</c>；
    /// 已含 <c>/messages</c> 的只剥掉重拼，避免 <c>.../v1/messages/v1/messages</c>。
    ///
    /// 与 <see cref="OpenAiChatClient.NormalizeBaseUrl"/> 共用「末尾是不是版本号」这一个判断：
    /// DeepSeek 的 Anthropic 端点是 <c>https://api.deepseek.com/anthropic</c>，
    /// 补出来必须是 <c>.../anthropic/v1/messages</c>。
    /// </summary>
    public static string NormalizeMessagesEndpoint(string endpoint)
    {
        var url = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (url.Length == 0) return url;

        if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/messages".Length].TrimEnd('/');

        return OpenAiChatClient.EndsWithVersion(url) ? url + "/messages" : url + "/v1/messages";
    }

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        ChatRoundSink sink,
        CancellationToken ct)
    {
        var body = BuildBody(systemPrompt, history, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("anthropic-version", ApiVersion);

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            // 两个鉴权头都带：官方站认 x-api-key，兼容网关与 DeepSeek 的 Anthropic 端点
            // 认 Authorization: Bearer（它们就是把 Claude Code 的 ANTHROPIC_AUTH_TOKEN 当 bearer 用）。
            // 多带的那个会被忽略 —— 少带一个则一定鉴权失败。
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 与另两个客户端同一套错误整理：状态码 + 服务端返回体，一并能贴进错误卡
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                await OpenAiChatClient.DescribeHttpFailureAsync(response, ct), null, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var thinkingSb = new StringBuilder();

        // 工具调用：event 里的 index 是「content block 下标」，会跳过 text / thinking 块，
        // 而网关按下标遍历 ToolCalls 逐个执行 —— 中间留空位会被当成一次空调用，所以自己转成稠密下标。
        var toolCalls = new List<ChatToolCall>();
        var denseIndex = new Dictionary<int, int>();

        var usage = new Dictionary<string, long>(StringComparer.Ordinal);

        while (true)
        {
            // ct 要交给读取本身：SendAsync 的 ct 只管到响应头，输出途中点「停止」靠这里中断。
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // event: / 空行一律跳过

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = GetString(root, "type");

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("usage", out var startUsage))
                        AccumulateUsage(usage, startUsage);
                    break;

                case "content_block_start":
                {
                    var index = GetInt(root, "index");
                    if (!root.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object)
                        break;
                    if (GetString(block, "type") != "tool_use") break;

                    var callId = GetString(block, "id");
                    var name = GetString(block, "name");

                    var dense = toolCalls.Count;
                    denseIndex[index] = dense;
                    toolCalls.Add(new ChatToolCall { Id = callId, Name = name, Arguments = "" });

                    // 先把卡片建出来（参数还在传），与另两个客户端一致
                    sink.OnToolCall(new ToolCallDelta(dense, callId, name, ""));
                    break;
                }

                case "content_block_delta":
                {
                    if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                        break;

                    switch (GetString(delta, "type"))
                    {
                        case "text_delta":
                            if (GetString(delta, "text") is { Length: > 0 } text)
                            {
                                contentSb.Append(text);
                                sink.OnContent(text);
                            }
                            break;

                        case "thinking_delta":
                            if (GetString(delta, "thinking") is { Length: > 0 } think)
                            {
                                thinkingSb.Append(think);
                                sink.OnThinking(think);
                            }
                            break;

                        // 工具入参是「一段一段的 JSON 文本」，拼起来才是完整对象（与 OpenAI 的
                        // function.arguments 分片同理）。signature_delta 只是签名，没有内容。
                        case "input_json_delta":
                        {
                            if (!denseIndex.TryGetValue(GetInt(root, "index"), out var dense)) break;
                            var partial = GetString(delta, "partial_json") ?? "";
                            if (partial.Length == 0) break;

                            toolCalls[dense].Arguments += partial;
                            sink.OnToolCall(new ToolCallDelta(dense, null, null, partial));
                            break;
                        }
                    }
                    break;
                }

                // 收尾事件：output_tokens 只在这里给，input_tokens 只在 message_start 给
                case "message_delta":
                    if (root.TryGetProperty("usage", out var deltaUsage) && deltaUsage.ValueKind == JsonValueKind.Object)
                        AccumulateUsage(usage, deltaUsage);
                    break;

                case "message_stop":
                    // break 只能跳出 switch，用返回值退出循环
                    return Result();

                case "error":
                    // 内容审计、额度用尽等错误在流中间单独下发，不接住就只剩一条空回复
                    throw new InvalidOperationException("流式响应中返回错误：" + root.GetRawText());
            }
        }

        return Result();

        ChatStreamResult Result() => new(
            contentSb.ToString(),
            thinkingSb.ToString(),
            "assistant",
            "",
            toolCalls,
            new TokenUsage(
                usage.GetValueOrDefault("input_tokens"),
                // 缓存命中 = 读到的缓存；cache_creation 是「这次写进去的」，不算命中。
                usage.GetValueOrDefault("cache_read_input_tokens"),
                usage.GetValueOrDefault("output_tokens")),
            JsonSerializer.Serialize(usage));
    }

    /// <summary>把 usage 对象里的数值字段并进 <paramref name="target"/>（两次事件各给一部分）。</summary>
    private static void AccumulateUsage(Dictionary<string, long> target, JsonElement usage)
    {
        foreach (var prop in usage.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var value))
                target[prop.Name] = value;
    }

    private Dictionary<string, object?> BuildBody(
        string? systemPrompt, IReadOnlyList<ChatMessage> history, ChatRequestOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            // max_tokens 在 Messages 协议里必填：默认用模型声明的最大输出，
            // 调用方明确要求时以它为准（见 ChatRequestOptions.MaxOutputTokens）。
            ["max_tokens"] = options.MaxOutputTokens is > 0 and var maxTokens ? maxTokens : _maxTokens,
            ["messages"] = BuildMessages(history, options.SupportsVision),
            ["stream"] = true,
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            body["system"] = systemPrompt;

        switch (options.Thinking)
        {
            case ThinkingMode.Default:
            case ThinkingMode.NoThink:
                // 都不下发：Default 是「走模型默认」，NoThink 想要 disabled ——
                // 但多数 5.x 模型对 disabled 返回 400，而协议层看不出是哪个模型。
                // 宁可和 Default 同义，也不要把一轮对话直接打成错误。
                break;

            default:
                body["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "adaptive",
                    // 不写 display 时 5.x 默认 omitted：思考块照样计费，但 thinking 字段是空串，
                    // 界面上就是「有思考卡、没内容」。
                    ["display"] = "summarized",
                };

                if (EffortFor(options.Thinking) is { } effort)
                    body["output_config"] = new Dictionary<string, object?> { ["effort"] = effort };
                break;
        }

        if (options.Tools is { Count: > 0 })
        {
            // Anthropic 的工具定义是扁平结构：name / description / input_schema，不套 function。
            body["tools"] = options.Tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = OpenAiChatClient.ParseToolParameters(t.InputSchemaJson),
            }).ToList();
        }

        return body;
    }

    /// <summary>
    /// 思考档位 → <c>output_config.effort</c>。Anthropic 只认 low/medium/high/xhigh/max，
    /// 所以最弱的 Minimal 收敛到 low。
    /// </summary>
    private static string? EffortFor(ThinkingMode mode) => mode switch
    {
        ThinkingMode.Minimal => "low",
        ThinkingMode.Low => "low",
        ThinkingMode.Medium => "medium",
        ThinkingMode.High => "high",
        ThinkingMode.XHigh => "xhigh",
        ThinkingMode.Max => "max",
        _ => null,
    };

    // ============================================================
    // 历史 → messages
    // ============================================================

    /// <summary>一个回合。<see cref="Blocks"/> 至少一个，空的回合在收尾时被丢掉。</summary>
    private sealed class Turn
    {
        public required string Role { get; init; }
        public List<Block> Blocks { get; } = new();
    }

    /// <summary>一个 content block。<see cref="ToolId"/> 只对 tool_use / tool_result 有意义。</summary>
    private sealed class Block
    {
        public string? ToolId { get; init; }
        public required Dictionary<string, object?> Body { get; init; }
    }

    private static List<object> BuildMessages(IReadOnlyList<ChatMessage> history, bool supportsVision)
    {
        var turns = new List<Turn>();

        // 连续同角色的消息并进同一个回合：tool 结果本来就是若干条独立记录（一条工具一行），
        // 而 Anthropic 要求紧接着 assistant 的下一条 user 消息里带齐全部 tool_result。
        void Append(string role, Block block)
        {
            var last = turns.Count > 0 ? turns[^1] : null;
            if (last is null || last.Role != role)
            {
                last = new Turn { Role = role };
                turns.Add(last);
            }
            last.Blocks.Add(block);
        }

        foreach (var m in history)
        {
            if (m.IsDeleted) continue;

            switch (m.Role)
            {
                case "user":
                    if (string.IsNullOrEmpty(m.Content)) continue;
                    Append("user", new Block
                    {
                        Body = new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content },
                    });
                    break;

                case "assistant":
                {
                    var calls = ToolCallJson.Deserialize(m.Metadata);
                    var hasText = !string.IsNullOrEmpty(m.Content);
                    if (!hasText && calls.Count == 0) continue;

                    // 思考内容刻意不回放（见类注释：签名与账号 / 前缀绑定，回放会 400）。
                    if (hasText)
                        Append("assistant", new Block
                        {
                            Body = new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content! },
                        });

                    foreach (var call in calls)
                    {
                        if (string.IsNullOrEmpty(call.Id)) continue;
                        Append("assistant", new Block
                        {
                            ToolId = call.Id,
                            Body = new Dictionary<string, object?>
                            {
                                ["type"] = "tool_use",
                                ["id"] = call.Id,
                                ["name"] = call.Name ?? "",
                                ["input"] = ParseToolInput(call.Arguments),
                            },
                        });
                    }
                    break;
                }

                case "tool":
                    if (string.IsNullOrEmpty(m.ToolCallId)) continue;
                    Append("user", new Block
                    {
                        ToolId = m.ToolCallId,
                        Body = new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m.ToolCallId,
                            ["content"] = BuildToolResultContent(m, supportsVision),
                            // 失败的工具结果标成 is_error，模型才知道那是错的、不是数据
                            ["is_error"] = m.Status == "failed",
                        },
                    });
                    break;
            }
        }

        PruneUnpairedToolBlocks(turns);

        return turns
            .Select(t => (object)new Dictionary<string, object?>
            {
                ["role"] = t.Role,
                ["content"] = t.Blocks.Select(b => (object)b.Body).ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 把工具调用与结果配不上的块剔掉，两侧都要处理：
    /// <list type="bullet">
    ///   <item>tool_use 后面没有对应的 tool_result —— 服务端的措辞是「must be followed by」，
    ///     一个不对就是整轮 400。出现场景：工具跑之前软件被杀掉（网关会补占位行，但历史也可能被手工删）。</item>
    ///   <item>tool_result 前面没有对应的 tool_use —— 用户删掉了那条 assistant 消息之后常见，
    ///     服务端会报「unexpected tool_use_id」。</item>
    /// </list>
    /// 首条不能是 assistant：Anthropic 不接受以助手开头的对话（首条用户消息被删时会出现）。
    /// </summary>
    private static void PruneUnpairedToolBlocks(List<Turn> turns)
    {
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Role != "assistant") continue;

            var answered = i + 1 < turns.Count && turns[i + 1].Role == "user"
                ? IdsOf(turns[i + 1], "tool_result")
                : new HashSet<string>(StringComparer.Ordinal);

            turns[i].Blocks.RemoveAll(b => b.ToolId is not null && IsToolUse(b) && !answered.Contains(b.ToolId));
        }

        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Role != "user") continue;

            var declared = i > 0 && turns[i - 1].Role == "assistant"
                ? IdsOf(turns[i - 1], "tool_use")
                : new HashSet<string>(StringComparer.Ordinal);

            turns[i].Blocks.RemoveAll(b => b.ToolId is not null && IsToolResult(b) && !declared.Contains(b.ToolId));
        }

        while (turns.Count > 0 && turns[0].Role != "user") turns.RemoveAt(0);

        // 块被剔空的回合留着会让角色序列变成「user, user」这种不合法的组合
        turns.RemoveAll(t => t.Blocks.Count == 0);
        while (turns.Count > 0 && turns[0].Role != "user") turns.RemoveAt(0);
    }

    private static bool IsToolUse(Block b) => (b.Body.TryGetValue("type", out var t) ? t as string : null) == "tool_use";

    private static bool IsToolResult(Block b)
        => (b.Body.TryGetValue("type", out var t) ? t as string : null) == "tool_result";

    private static HashSet<string> IdsOf(Turn turn, string type)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in turn.Blocks)
        {
            var kind = b.Body.TryGetValue("type", out var t) ? t as string : null;
            if (kind != type || b.ToolId is null) continue;
            ids.Add(b.ToolId);
        }
        return ids;
    }

    /// <summary>
    /// 工具入参：模型给的是 JSON 文本，而 <c>input</c> 必须是对象。
    /// 空串、坏 JSON、甚至给了个字符串，一律退成空对象 —— 服务端要的是结构，不是原文。
    /// </summary>
    private static object ParseToolInput(string? arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 半截 JSON（模型没写完就被停掉）走空对象
            }
        }

        return new Dictionary<string, object?>();
    }

    /// <summary>
    /// 工具结果的 content：支持视觉时把 view_image 的结果展开成 text + image 两个 block
    /// （路径落库、这里才读文件编码）；不支持视觉、文件被挪走、或格式不在 Anthropic
    /// 允许的四种之内时退回纯文本 —— 保证换模型 / 删文件之后历史仍然发得出去。
    ///
    /// Messages API 没有视频内容块，所以 view_video 的结果在这里只会是那段纯文本
    /// （metadata 里的 video_path 没人读）。ConversationRuntime 也不会在有 video 能力
    /// 的 Anthropic 模型上放行这个工具，见它那里的协议判断。
    /// </summary>
    private static object BuildToolResultContent(ChatMessage m, bool supportsVision)
    {
        var text = m.Content ?? "";
        if (!supportsVision) return text;

        var parts = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
        };

        // 一个结果可能带多张图（view_video_frames 抽出来的帧）：逐张校验格式后各成一个 image block
        foreach (var imagePath in ToolImageJson.DeserializeMany(m.Metadata))
        {
            if (!File.Exists(imagePath)) continue;

            var mime = OpenAiChatClient.MimeFor(imagePath);
            if (!AllowedImageMimes.Contains(mime, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                parts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = mime,
                        ["data"] = Convert.ToBase64String(File.ReadAllBytes(imagePath)),
                    },
                });
            }
            catch (IOException)
            {
                // 读文件失败（被占用 / 权限）：跳过这一张，别让历史发不出去
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return parts.Count > 1 ? parts : text;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : -1;
}
