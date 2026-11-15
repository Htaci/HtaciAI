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
/// OpenAI /chat/completions 协议客户端（含各家「OpenAI 兼容但有出入」的扩展）。
/// 以 OpenAI /chat/completions 协议为主，额外支持：
///  - 思考开关字段（默认 "thinking": {"type":"enabled"/"disabled"}）与推理强度字段（默认 "reasoning_effort"），
///    仅在明确设置时才写入请求体，未设置时走模型默认行为；
///  - 解析 reasoning_content（思考内容）、role、tool_call_id、tool_calls；
///  - 多轮工具调用所需的完整消息重建（assistant.tool_calls / tool.tool_call_id）。
/// 字段名做成可替换常量，便于对具体厂商端点微调。
/// </summary>
public sealed class OpenAiChatClient : IChatClient
{
    /// <summary>共享客户端：走不走代理由设置页的「使用系统代理」决定，见 <see cref="HttpClientProvider"/>。</summary>
    private static HttpClient Http => HttpClientProvider.Get(TimeSpan.FromMinutes(5));

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ThinkingFieldKind _thinkingField;   // think / enable_thinking / reasoning_effort / none
    private readonly string _effortField;                // "reasoning_effort"（OpenAI 官方字段）

    public string ModelName => _model;

    public OpenAiChatClient(
        string endpoint,
        string apiKey,
        string model,
        ThinkingFieldKind thinkingField = ThinkingFieldKind.Think,
        string effortField = "reasoning_effort")
    {
        _endpoint = NormalizeOpenAiEndpoint(endpoint);
        _apiKey = apiKey;
        _model = model;
        _thinkingField = thinkingField;
        _effortField = effortField;
    }

    /// <summary>
    /// 把一次失败的响应整理成给人看的文本：首行是状态码，其后是服务端返回体。
    ///
    /// 首行格式固定（<c>HTTP 401 Unauthorized</c>）是有用的：错误卡的标题取的就是异常消息的首行
    /// （见 <c>ChatError.Summarize</c>），状态码摆在标题上，一眼能看出是鉴权、模型名不存在还是额度用尽。
    /// </summary>
    internal static async Task<string> DescribeHttpFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var head = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (body.Length == 0) return head;

            // 服务端偶尔回一整页 HTML 错误页，无上限地收进去只会把记录撑爆
            const int bodyLimit = 8000;
            if (body.Length > bodyLimit) body = body[..bodyLimit] + "\n…（返回体过长，已截断）";

            return head + "\n服务端返回：" + body;
        }
        catch
        {
            // 读返回体失败（连接已断等）不影响「这次请求失败」这件事本身
            return head;
        }
    }

    /// <summary>
    /// 规范化到「API 版本根」，如 <c>https://api.moonshot.cn</c> → <c>https://api.moonshot.cn/v1</c>；
    /// 已经以版本号结尾的（<c>/v1</c>、<c>/api/paas/v4</c>、<c>/api/v3</c>）原样返回。
    ///
    /// 与 <see cref="NormalizeOpenAiEndpoint"/>、<see cref="ModelsUrl"/> 共用同一套判断，
    /// 三者拼出的路径必然同源 —— 否则「对话能通、拉模型列表却 404」这种问题会很难查。
    /// </summary>
    public static string NormalizeBaseUrl(string endpoint)
    {
        var url = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (url.Length == 0) return url;

        // 已含完整接口路径：剥掉，回到版本根，避免重复追加。
        // /responses 也要认：用户常把整条接口 URL 粘进来，不剥的话会拼成 .../v1/responses/v1/responses。
        foreach (var suffix in new[] { "/chat/completions", "/responses" })
        {
            if (!url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            url = url[..^suffix.Length].TrimEnd('/');
            break;
        }

        // 末尾已经是版本号就原样用。不能只认 /v1：智谱是 /api/paas/v4、火山方舟是 /api/v3，
        // 一律补 /v1 会拼出 .../v4/v1/chat/completions 这种必然 404 的地址。
        return EndsWithVersion(url) ? url : url + "/v1";
    }

    /// <summary>
    /// 路径最后一段是不是版本号（v1 / v3 / v4 …）。
    /// 由 Anthropic 客户端复用：那边同样需要「已经填了版本根就不再补 /v1」。
    /// </summary>
    internal static bool EndsWithVersion(string url)
    {
        var slash = url.LastIndexOf('/');
        if (slash < 0 || slash == url.Length - 1) return false;

        var segment = url[(slash + 1)..];
        if (segment.Length < 2 || (segment[0] != 'v' && segment[0] != 'V')) return false;

        for (var i = 1; i < segment.Length; i++)
            if (!char.IsAsciiDigit(segment[i])) return false;

        return true;
    }

    /// <summary>
    /// 规范化 OpenAI 兼容 Chat Completions 端点：
    /// base URL（如 https://api.moonshot.cn）自动补全为 /v1/chat/completions；
    /// 已含完整路径则原样返回，避免重复追加成 .../v1/chat/completions/v1/chat/completions。
    /// </summary>
    public static string NormalizeOpenAiEndpoint(string endpoint)
    {
        var baseUrl = NormalizeBaseUrl(endpoint);
        return baseUrl.Length == 0 ? baseUrl : baseUrl + "/chat/completions";
    }

    /// <summary>模型列表端点（OpenAI 兼容 GET /models，用于「获取模型」）。</summary>
    public static string ModelsUrl(string endpoint)
    {
        var baseUrl = NormalizeBaseUrl(endpoint);
        return baseUrl.Length == 0 ? baseUrl : baseUrl + "/models";
    }

    /// <summary>
    /// 把工具定义的入参 JSON Schema 解析为请求体对象；空/损坏时回退为开放 object。
    /// Chat Completions 与 Responses 两个客户端的参数形态不同，schema 本体是同一份。
    /// </summary>
    internal static object ParseToolParameters(string schemaJson)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                return JsonDocument.Parse(schemaJson).RootElement.Clone();
            }
            catch (JsonException)
            {
                // 忽略损坏的 schema，走回退
            }
        }
        return new { type = "object", properties = new Dictionary<string, object>() };
    }

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        ChatRoundSink sink,
        CancellationToken ct)
    {
        var messages = BuildMessages(systemPrompt, history, options.SupportsVision, options.SupportsVideo);
        var body = BuildBody(messages, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        //using var response = await Http.SendAsync(request, ct);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 不用 EnsureSuccessStatusCode：它抛出的消息里只有状态码，
        // 而服务端的错误原因（鉴权失败、模型不存在、额度用尽…）全在返回体里 —— 那才是排查要看的。
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await DescribeHttpFailureAsync(response, ct), null, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var thinkingSb = new StringBuilder();
        string? role = null;
        string? toolCallId = null;
        var toolCalls = new List<ChatToolCall>();
        var usage = TokenUsage.Empty;
        string? rawUsage = null;

        while (true)
        {
            // 必须把 ct 交给读取本身：SendAsync 的 ct 只管到响应头，
            // 流式阶段（输出中）点「停止」就靠这里把读操作中断掉。
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);

            // 有些服务商在流中间用 error 对象报错（内容审计、额度用尽…）。
            // 不处理会连原因一起丢掉 —— 那一轮静默地变成「空回复」。
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
                throw new InvalidOperationException("流式响应中返回错误：" + errorEl.GetRawText());

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var delta = choices[0].GetProperty("delta");

            // role：assistant 段通常由首块携带
            if (delta.TryGetProperty("role", out var roleProp) && roleProp.ValueKind == JsonValueKind.String)
                role = roleProp.GetString();

            // tool_call_id：工具结果段顶层字段（部分厂商实现，多为 tool 角色块）
            if (delta.TryGetProperty("tool_call_id", out var tci) && tci.ValueKind == JsonValueKind.String)
                toolCallId = tci.GetString();

            // 思考内容：LM Studio（gpt-oss）从 delta.reasoning 输出，DeepSeek R1 从 reasoning_content 输出。
            // 两字段都检查，哪个有内容用哪个（多数实现只下发其一，安全性不受影响；对象形态会被忽略）。
            foreach (var key in new[] { "reasoning_content", "reasoning" })
            {
                if (delta.TryGetProperty(key, out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    var text = rc.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        thinkingSb.Append(text);
                        sink.OnThinking(text);
                    }
                }
            }

            // content：正文
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contentSb.Append(text);
                    sink.OnContent(text);
                }
            }

            // tool_calls：数组，按 index 累积 id / function.name / function.arguments。
            // 每片都往外抛一次：界面靠它「参数还在传的时候就先把工具卡建出来」，
            // 攒到最后一次性返回的话，用户要等到工具都跑完了才看得到任何动静。
            if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tc.EnumerateArray())
                {
                    var index = 0;
                    if (item.TryGetProperty("index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Number)
                        index = idxProp.GetInt32();

                    while (toolCalls.Count <= index)
                        toolCalls.Add(new ChatToolCall());

                    var call = toolCalls[index];
                    string? idDelta = null;
                    string? nameDelta = null;
                    var argumentDelta = "";

                    if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        idDelta = idProp.GetString();
                        call.Id = idDelta;
                    }
                    if (item.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        {
                            nameDelta = nameProp.GetString();
                            call.Name = nameDelta;
                        }
                        if (fn.TryGetProperty("arguments", out var argProp) && argProp.ValueKind == JsonValueKind.String)
                        {
                            argumentDelta = argProp.GetString() ?? "";
                            call.Arguments += argumentDelta;
                        }
                    }

                    sink.OnToolCall(new ToolCallDelta(index, idDelta, nameDelta, argumentDelta));
                }
            }

            // usage：仅最后一个块携带
            if (doc.RootElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                rawUsage = usageEl.GetRawText();
                usage = TokenUsage.FromJson(usageEl);
            }
        }

        return new ChatStreamResult(
            contentSb.ToString(),
            thinkingSb.ToString(),
            role ?? "",
            toolCallId ?? "",
            toolCalls,
            usage,
            rawUsage ?? "");
    }

    /// <summary>
    /// 构造请求体。thinking / 强度字段仅在设置了对应模式时才写入，
    /// 未设置时请求体不含这些字段，走模型默认行为。
    /// </summary>
    private Dictionary<string, object?> BuildBody(List<object> messages, ChatRequestOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };

        // 输出上限：只在调用方明确要求时下发（见 ChatRequestOptions.MaxOutputTokens）。
        // 不下发时由服务端给默认值 —— 普通对话一直是这么跑的，不要在这里替它决定。
        if (options.MaxOutputTokens is > 0 and var maxTokens)
            body["max_tokens"] = maxTokens;

        switch (options.Thinking)
        {
            case ThinkingMode.Default:
                // 不填任何思考字段，走模型默认。这是唯一「无论模型支持什么都不会错」的选项，
                // 所以档位未知时的默认值必须是它，而不是 NoThink。
                break;
            case ThinkingMode.NoThink:
                WriteThinkingToggle(body, enabled: false);
                break;
            default:
                // 其余都是「开启思考 + 指定强度」。档位名统一取自 ThinkingModes，
                // 这里不再逐个列举 —— 新增档位只改那一处。
                WriteThinkingToggle(body, enabled: true);
                if (ThinkingModes.WireValue(options.Thinking) is { } strength)
                    body[_effortField] = MapEffort(strength);
                break;
        }

        // 工具：激活的工具非空时下发 tools 数组（OpenAI 兼容 function calling）。
        if (options.Tools is { Count: > 0 })
        {
            body["tools"] = options.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = ParseToolParameters(t.InputSchemaJson),
                },
            }).ToList();
        }

        return body;

        // 思考开关按服务商声明的字段写法下发；None/ReasoningEffort 不写开关对象。
        void WriteThinkingToggle(Dictionary<string, object?> body, bool enabled)
        {
            switch (_thinkingField)
            {
                case ThinkingFieldKind.Think:
                    // DeepSeek OpenAI 兼容格式：{"thinking": {"type": "enabled"/"disabled"}}。
                    // 开关字段必须是对象而非字符串，否则服务端返回 400（NoThink/Low/High/Max 均走此分支）。
                    body["thinking"] = new Dictionary<string, object?> { ["type"] = enabled ? "enabled" : "disabled" };
                    break;
                case ThinkingFieldKind.EnableThinking:
                    body["enable_thinking"] = enabled;
                    break;
                case ThinkingFieldKind.ReasoningEffort:
                    // LM Studio：OpenAI 兼容 /v1/chat/completions 无 thinking 开关对象，仅靠顶层 reasoning_effort 控制强度。
                    break;
                default:
                    break;
            }
        }

        // 强度值收敛：LM Studio 的 reasoning_effort 只认 low/medium/high，没有 max；
        // 应用侧最高档 max 收敛到 high 避免服务端 400，其余协议原样透传。
        string MapEffort(string requested)
            => _thinkingField == ThinkingFieldKind.ReasoningEffort && requested == "max"
                ? "high"
                : requested;
    }

    /// <summary>
    /// tool 消息的 content。带图片的 view_image 结果在模型支持视觉时展开成 content-part 数组
    /// （已实测该端点接受 tool 消息里带 image_url）；不支持视觉、或文件已被挪走时退回纯文本，
    /// 保证换模型 / 删文件之后历史仍然能发出去。
    ///
    /// 落库的是路径，这里才读文件编码 —— 同一份字节每次编码结果一致，
    /// 上下文缓存因此仍然能命中。
    /// </summary>
    private static object BuildToolContent(ChatMessage m, bool supportsVision, bool supportsVideo)
    {
        var text = m.Content ?? "";

        // 视频：模型与协议都支持时原样内联成 video_url 块（各家兼容端点都是这个字段），
        // 本地不解码、不抽帧 —— 时序与音轨交给服务端自己处理
        if (supportsVideo)
        {
            var videoPath = ToolVideoJson.Deserialize(m.Metadata);
            if (videoPath is not null && File.Exists(videoPath))
            {
                try
                {
                    var videoUri = $"data:{VideoMimeFor(videoPath)};base64," +
                                   Convert.ToBase64String(File.ReadAllBytes(videoPath));
                    return new List<object>
                    {
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "video_url",
                            ["video_url"] = new Dictionary<string, object?> { ["url"] = videoUri },
                        },
                    };
                }
                catch
                {
                    return text;
                }
            }
        }

        if (!supportsVision) return text;

        var parts = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
        };

        // 一个结果可能带多张图（view_video_frames 抽出来的帧）：按抽帧顺序各成一个 image_url 块
        foreach (var imagePath in ToolImageJson.DeserializeMany(m.Metadata))
        {
            if (!File.Exists(imagePath)) continue;
            try
            {
                var uri = $"data:{MimeFor(imagePath)};base64,{Convert.ToBase64String(File.ReadAllBytes(imagePath))}";
                parts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?> { ["url"] = uri },
                });
            }
            catch
            {
                // 这一张读不出来（被挪走 / 被占用）：跳过它，别让整轮请求发不出去
            }
        }

        // 一张都没发出去就退回纯文本，模型至少还能看到工具写的那段说明
        return parts.Count > 1 ? parts : text;
    }

    internal static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        _ => "image/jpeg",
    };

    /// <summary>视频扩展名 → MIME，只用于内联编码（服务端按它决定用什么解码器）。</summary>
    internal static string VideoMimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mpeg" or ".mpg" => "video/mpeg",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".3gp" => "video/3gpp",
        _ => "video/mp4",
    };

    /// <summary>从历史消息重建 OpenAI 格式消息数组（system 独立前置，不进 history）。</summary>
    private static List<object> BuildMessages(string? systemPrompt, IReadOnlyList<ChatMessage> history, bool supportsVision, bool supportsVideo)
    {
        var list = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            list.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var m in history)
        {
            if (m.IsDeleted) continue;

            switch (m.Role)
            {
                case "user":
                    if (string.IsNullOrEmpty(m.Content)) continue;
                    list.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = m.Content });
                    break;

                case "assistant":
                {
                    var calls = ToolCallJson.Deserialize(m.Metadata);
                    if (string.IsNullOrEmpty(m.Content) && calls.Count == 0) continue;

                    var msg = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = m.Content ?? "" };
                    if (calls.Count > 0)
                    {
                        msg["tool_calls"] = calls.Select(c => new Dictionary<string, object?>
                        {
                            ["id"] = c.Id ?? "",
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = c.Name ?? "",
                                ["arguments"] = c.Arguments ?? "{}",
                            },
                        }).ToList();
                    }
                    list.Add(msg);
                    break;
                }

                case "tool":
                    if (string.IsNullOrEmpty(m.ToolCallId)) continue;
                    list.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId,
                        ["content"] = BuildToolContent(m, supportsVision, supportsVideo),
                    });
                    break;
            }
        }

        return list;
    }
}
