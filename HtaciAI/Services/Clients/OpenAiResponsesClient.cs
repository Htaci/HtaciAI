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
/// OpenAI Responses 协议客户端（<c>POST /v1/responses</c>）。
///
/// 与 <see cref="OpenAiChatClient"/> 的差异全在 wire 层，网关侧无感：
///  - 请求是 <c>instructions</c> + 扁平 <c>input</c> item 数组，不是 messages；
///  - 工具调用是顶层 <c>function_call</c> item，结果用 <c>function_call_output</c> 回填；
///  - 流式是强类型事件（<c>response.output_text.delta</c> 等），不是统一的 chunk。
///
/// <b>固定 store:false</b>：上下文由本地库管理，每轮重发完整 input。
/// 改用 previous_response_id 会让服务端那份状态和本地历史（编辑/删除/重新生成）对不上。
/// </summary>
public sealed class OpenAiResponsesClient : IChatClient
{
    /// <summary>共享客户端：走不走代理由设置页的「使用系统代理」决定，见 <see cref="HttpClientProvider"/>。</summary>
    private static HttpClient Http => HttpClientProvider.Get(TimeSpan.FromMinutes(5));

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;

    public string ModelName => _model;

    public OpenAiResponsesClient(string endpoint, string apiKey, string model)
    {
        _endpoint = NormalizeResponsesEndpoint(endpoint);
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>
    /// base URL（如 https://api.openai.com）补全为 /v1/responses；
    /// 已含完整路径时先剥掉再拼，避免 .../v1/responses/v1/responses。
    /// </summary>
    public static string NormalizeResponsesEndpoint(string endpoint)
    {
        var baseUrl = OpenAiChatClient.NormalizeBaseUrl(endpoint);
        return baseUrl.Length == 0 ? baseUrl : baseUrl + "/responses";
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
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                await OpenAiChatClient.DescribeHttpFailureAsync(response, ct), null, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var thinkingSb = new StringBuilder();

        // 稠密下标：网关按下标遍历 ToolCalls 逐个执行，中间留空位会被当成一次空工具调用。
        // 事件里的 output_index 会跳过 reasoning / message 项（0 是思考、1 是正文、2 才是函数调用），
        // 所以这里自己维护一张 output_index → 稠密下标的映射。
        var toolCalls = new List<ChatToolCall>();
        var denseIndex = new Dictionary<int, int>();

        var usage = TokenUsage.Empty;
        string? rawUsage = null;

        while (true)
        {
            // ct 要交给读取本身：SendAsync 的 ct 只管到响应头，输出途中点「停止」靠这里中断。
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                continue;

            switch (typeProp.GetString())
            {
                case "response.output_text.delta":
                    if (GetString(root, "delta") is { Length: > 0 } text)
                    {
                        contentSb.Append(text);
                        sink.OnContent(text);
                    }
                    break;

                // 思考内容只以摘要形式下发（请求里 reasoning.summary = auto）；
                // reasoning_text 是少数实现给的原文，一并收着。
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (GetString(root, "delta") is { Length: > 0 } think)
                    {
                        thinkingSb.Append(think);
                        sink.OnThinking(think);
                    }
                    break;

                case "response.output_item.added":
                {
                    var outputIndex = GetInt(root, "output_index");
                    if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                        break;
                    if (GetString(item, "type") != "function_call") break;

                    // call_id 才是与 function_call_output 配对的那个 id（item.id 是 fc_ 前缀的服务端 id）。
                    var callId = GetString(item, "call_id") ?? GetString(item, "id");
                    var name = GetString(item, "name");

                    var index = toolCalls.Count;
                    denseIndex[outputIndex] = index;
                    toolCalls.Add(new ChatToolCall { Id = callId, Name = name, Arguments = "" });

                    sink.OnToolCall(new ToolCallDelta(index, callId, name, ""));
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    if (!denseIndex.TryGetValue(GetInt(root, "output_index"), out var index)) break;
                    var delta = GetString(root, "delta") ?? "";
                    if (delta.Length == 0) break;

                    toolCalls[index].Arguments += delta;
                    sink.OnToolCall(new ToolCallDelta(index, null, null, delta));
                    break;
                }

                // 参数接收完成：以事件里的完整 arguments 为准，防止分片丢了任何一段。
                case "response.function_call_arguments.done":
                {
                    if (!denseIndex.TryGetValue(GetInt(root, "output_index"), out var index)) break;
                    if (GetString(root, "arguments") is { } full) toolCalls[index].Arguments = full;
                    break;
                }

                case "response.completed":
                case "response.incomplete":
                {
                    if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object)
                        break;

                    if (resp.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        rawUsage = usageEl.GetRawText();
                        usage = ParseUsage(usageEl);
                    }

                    // 完成事件里的 output 是权威结果：name / arguments 在这补齐，
                    // 流式分片缺一段也不会让工具拿着半截参数去执行。
                    ReconcileToolCalls(resp, toolCalls);
                    break;
                }

                case "error":
                    // 内容审计、额度用尽等错误在流中间单独下发，不接住就只剩一条空回复。
                    throw new InvalidOperationException("流式响应中返回错误：" + root.GetRawText());

                case "response.failed":
                {
                    var detail = root.TryGetProperty("response", out var failed)
                                 && failed.ValueKind == JsonValueKind.Object
                                 && failed.TryGetProperty("error", out var err)
                        ? err.GetRawText()
                        : root.GetRawText();
                    throw new InvalidOperationException("Responses 请求失败：" + detail);
                }
            }
        }

        return new ChatStreamResult(
            contentSb.ToString(),
            thinkingSb.ToString(),
            "assistant",
            "",
            toolCalls,
            usage,
            rawUsage ?? "");
    }

    /// <summary>用完成事件里的 output 数组校正流式累积结果。</summary>
    private static void ReconcileToolCalls(JsonElement resp, List<ChatToolCall> toolCalls)
    {
        if (!resp.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return;

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (GetString(item, "type") != "function_call") continue;

            var callId = GetString(item, "call_id") ?? GetString(item, "id");
            var name = GetString(item, "name");
            var arguments = GetString(item, "arguments");

            // 先按 call_id 找已累积的那条；找不到（事件里没发 added）就补一条。
            var index = toolCalls.FindIndex(c => callId is not null && c.Id == callId);
            if (index < 0)
            {
                index = toolCalls.Count;
                toolCalls.Add(new ChatToolCall { Id = callId, Name = name, Arguments = "" });
            }

            var call = toolCalls[index];
            call.Id = callId ?? call.Id;
            call.Name = name ?? call.Name;
            if (!string.IsNullOrEmpty(arguments)) call.Arguments = arguments;
        }
    }

    /// <summary>
    /// Responses 的 usage：input_tokens / output_tokens / input_tokens_details.cached_tokens。
    /// 字段名与 Chat Completions 不同，所以不能复用 <see cref="TokenUsage.FromJson"/>。
    /// </summary>
    private static TokenUsage ParseUsage(JsonElement usage)
    {
        long cacheHit = 0;
        if (usage.TryGetProperty("input_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cacheHit = GetLong(details, "cached_tokens");
        }

        return new TokenUsage(GetLong(usage, "input_tokens"), cacheHit, GetLong(usage, "output_tokens"));
    }

    private Dictionary<string, object?> BuildBody(
        string? systemPrompt, IReadOnlyList<ChatMessage> history, ChatRequestOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["input"] = BuildInput(history, options.SupportsVision),
            ["stream"] = true,
            // 上下文由本地库管理，不用服务端会话（见类注释）。
            ["store"] = false,
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            body["instructions"] = systemPrompt;

        // 输出上限：只在调用方明确要求时下发（见 ChatRequestOptions.MaxOutputTokens）
        if (options.MaxOutputTokens is > 0 and var maxTokens)
            body["max_output_tokens"] = maxTokens;

        switch (options.Thinking)
        {
            case ThinkingMode.Default:
                // 不传 reasoning，走模型默认。
                break;
            case ThinkingMode.NoThink:
                // Responses 没有独立的思考开关对象，只能靠 reasoning.effort="none" 关掉，
                // 但「none 不被该模型接受」是真的会 400（见 models.ini 里 OpenAI 各条目的 note），
                // 而协议层看不出当前模型是哪一档。这里宁可不传 —— 与 Default 同义，绝不会错。
                break;
            default:
                if (ThinkingModes.WireValue(options.Thinking) is { } strength)
                {
                    body["reasoning"] = new Dictionary<string, object?>
                    {
                        ["effort"] = strength,
                        // 要摘要才收得到思考内容（response.reasoning_summary_text.delta）
                        ["summary"] = "auto",
                    };
                }
                break;
        }

        if (options.Tools is { Count: > 0 })
        {
            // Responses 的工具定义是扁平结构：name / description / parameters 直接在 tool 上，不套 function。
            body["tools"] = options.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = OpenAiChatClient.ParseToolParameters(t.InputSchemaJson),
            }).ToList();
        }

        return body;
    }

    /// <summary>
    /// 从历史消息重建 input item 数组。system 走 instructions，不进 input。
    /// </summary>
    private static List<object> BuildInput(IReadOnlyList<ChatMessage> history, bool supportsVision)
    {
        var items = new List<object>();

        foreach (var m in history)
        {
            if (m.IsDeleted) continue;

            switch (m.Role)
            {
                case "user":
                    if (string.IsNullOrEmpty(m.Content)) continue;
                    items.Add(Message("user", "input_text", m.Content));
                    break;

                case "assistant":
                {
                    var calls = ToolCallJson.Deserialize(m.Metadata);
                    if (string.IsNullOrEmpty(m.Content) && calls.Count == 0) continue;

                    if (!string.IsNullOrEmpty(m.Content))
                        items.Add(Message("assistant", "output_text", m.Content));

                    // 工具调用是与 message 平级的独立 item，不是 message 里的字段。
                    // store:false 下这些 function_call 必须原样带回，否则服务端配不上后面的结果。
                    foreach (var c in calls)
                    {
                        if (string.IsNullOrEmpty(c.Id)) continue;
                        items.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = c.Id,
                            ["name"] = c.Name ?? "",
                            ["arguments"] = c.Arguments ?? "{}",
                        });
                    }
                    break;
                }

                case "tool":
                    if (string.IsNullOrEmpty(m.ToolCallId)) continue;
                    items.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = m.ToolCallId,
                        ["output"] = BuildToolOutput(m, supportsVision),
                    });
                    break;
            }
        }

        return items;

        static Dictionary<string, object?> Message(string role, string textType, string text) => new()
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = new List<object>
            {
                new Dictionary<string, object?> { ["type"] = textType, ["text"] = text },
            },
        };
    }

    /// <summary>
    /// 工具结果的 output。带图片的 view_image 结果在模型支持视觉时展开成 part 数组，
    /// 不支持视觉、或文件已被挪走时退回纯文本 —— 与 Chat Completions 客户端的降级策略一致，
    /// 保证换模型 / 删文件之后历史仍然能发出去。
    ///
    /// Responses 协议目前没有视频输入类型，view_video 的结果在这里同样只剩纯文本。
    /// </summary>
    private static object BuildToolOutput(ChatMessage m, bool supportsVision)
    {
        var text = m.Content ?? "";
        if (!supportsVision) return text;

        var parts = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text },
        };

        // 一个结果可能带多张图（view_video_frames 抽出来的帧）：按抽帧顺序各成一个 input_image
        foreach (var imagePath in ToolImageJson.DeserializeMany(m.Metadata))
        {
            if (!File.Exists(imagePath)) continue;
            try
            {
                var uri = $"data:{OpenAiChatClient.MimeFor(imagePath)};base64," +
                          Convert.ToBase64String(File.ReadAllBytes(imagePath));
                parts.Add(new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = uri });
            }
            catch
            {
                // 这一张读不出来：跳过它，别让整轮请求发不出去
            }
        }

        return parts.Count > 1 ? parts : text;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : -1;

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}
