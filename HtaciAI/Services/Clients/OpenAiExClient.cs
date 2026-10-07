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
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>
/// OpenAI API 协议兼容的扩展客户端。
/// 以 OpenAI /chat/completions 协议为主，额外支持：
///  - 思考开关字段（默认 "thinking": {"type":"enabled"/"disabled"}）与推理强度字段（默认 "reasoning_effort"），
///    仅在明确设置时才写入请求体，未设置时走模型默认行为；
///  - 解析 reasoning_content（思考内容）、role、tool_call_id、tool_calls；
///  - 多轮工具调用所需的完整消息重建（assistant.tool_calls / tool.tool_call_id）。
/// 字段名做成可替换常量，便于对具体厂商端点微调。
/// </summary>
public sealed class OpenAiExClient : IChatClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ThinkingFieldKind _thinkingField;   // think / enable_thinking / reasoning_effort / none
    private readonly string _effortField;                // "reasoning_effort"（OpenAI 官方字段）

    public string ModelName => _model;

    public OpenAiExClient(
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
    /// 规范化 OpenAI 兼容 Chat Completions 端点：
    /// base URL（如 https://api.moonshot.cn）自动补全为 /v1/chat/completions；
    /// 已含完整路径则原样返回，避免重复追加成 .../v1/chat/completions/v1/chat/completions。
    /// </summary>
    public static string NormalizeOpenAiEndpoint(string endpoint)
    {
        var url = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        if (url.Length == 0) return url;

        // 已含完整路径：直接返回
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;

        // 已到版本目录（如 .../v1）：只需补 /chat/completions
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return url + "/chat/completions";

        // 其余一律视为 base URL：补 /v1/chat/completions
        return url + "/v1/chat/completions";
    }

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        Action<string> onContent,
        Action<string>? onThinking,
        CancellationToken ct)
    {
        var messages = BuildMessages(systemPrompt, history, options.SupportsVision);
        var body = BuildBody(messages, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        //using var response = await Http.SendAsync(request, ct);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

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
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
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
                        onThinking?.Invoke(text);
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
                    onContent(text);
                }
            }

            // tool_calls：数组，按 index 累积 id / function.name / function.arguments
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
                    if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        call.Id = idProp.GetString();
                    if (item.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            call.Name = nameProp.GetString();
                        if (fn.TryGetProperty("arguments", out var argProp) && argProp.ValueKind == JsonValueKind.String)
                            call.Arguments += argProp.GetString();
                    }
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

        switch (options.Thinking)
        {
            case ThinkingMode.NoThink:
                WriteThinkingToggle(body, enabled: false);
                break;
            case ThinkingMode.Low:
                WriteThinkingToggle(body, enabled: true);
                body[_effortField] = MapEffort("low");
                break;
            case ThinkingMode.High:
                WriteThinkingToggle(body, enabled: true);
                body[_effortField] = MapEffort("high");
                break;
            case ThinkingMode.Max:
                WriteThinkingToggle(body, enabled: true);
                body[_effortField] = MapEffort("max");
                break;
            default:
                // ThinkingMode.Default（none）：不填，走模型默认
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
                    ["parameters"] = ParseParameters(t.InputSchemaJson),
                },
            }).ToList();
        }

        return body;

        // 把工具定义的入参 JSON Schema 解析为请求体对象；空/损坏时回退为开放 object。
        static object ParseParameters(string schemaJson)
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
    private static object BuildToolContent(ChatMessage m, bool supportsVision)
    {
        var text = m.Content ?? "";
        if (!supportsVision) return text;

        var imagePath = ToolImageJson.Deserialize(m.Metadata);
        if (imagePath is null || !File.Exists(imagePath)) return text;

        try
        {
            var uri = $"data:{MimeFor(imagePath)};base64,{Convert.ToBase64String(File.ReadAllBytes(imagePath))}";
            return new List<object>
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
                new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?> { ["url"] = uri },
                },
            };
        }
        catch
        {
            return text;
        }
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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

    /// <summary>从历史消息重建 OpenAI 格式消息数组（system 独立前置，不进 history）。</summary>
    private static List<object> BuildMessages(string? systemPrompt, IReadOnlyList<ChatMessage> history, bool supportsVision)
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
                        ["content"] = BuildToolContent(m, supportsVision),
                    });
                    break;
            }
        }

        return list;
    }
}
