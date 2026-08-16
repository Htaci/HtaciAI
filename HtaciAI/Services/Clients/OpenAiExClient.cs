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

namespace HtaciAI.Services;

/// <summary>
/// OpenAI API 协议兼容的扩展客户端。
/// 以 OpenAI /chat/completions 协议为主，额外支持：
///  - 思考开关字段（默认 "thinking": "enabled"/"disabled"）与推理强度字段（默认 "reasoning_effort"），
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
    private readonly ThinkingFieldKind _thinkingField;   // think / enable_thinking / none
    private readonly string _effortField;                // "reasoning_effort"（OpenAI 官方字段）

    public string ModelName => _model;

    public OpenAiExClient(
        string endpoint,
        string apiKey,
        string model,
        ThinkingFieldKind thinkingField = ThinkingFieldKind.Think,
        string effortField = "reasoning_effort")
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _model = model;
        _thinkingField = thinkingField;
        _effortField = effortField;
    }

    public async Task<ChatStreamResult> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        ChatRequestOptions options,
        Action<string> onContent,
        Action<string>? onThinking,
        CancellationToken ct)
    {
        var messages = BuildMessages(systemPrompt, history);
        var body = BuildBody(messages, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var response = await Http.SendAsync(request, ct);
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

            // reasoning_content：思考内容
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var text = rc.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    thinkingSb.Append(text);
                    onThinking?.Invoke(text);
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
                body[_effortField] = "low";
                break;
            case ThinkingMode.High:
                WriteThinkingToggle(body, enabled: true);
                body[_effortField] = "high";
                break;
            case ThinkingMode.Max:
                WriteThinkingToggle(body, enabled: true);
                body[_effortField] = "max";
                break;
            default:
                // ThinkingMode.Default（none）：不填，走模型默认
                break;
        }

        return body;

        // 思考开关按服务商声明的字段写法下发；None 时无法下发开关，仅靠强度字段
        void WriteThinkingToggle(Dictionary<string, object?> body, bool enabled)
        {
            switch (_thinkingField)
            {
                case ThinkingFieldKind.Think:
                    body["thinking"] = enabled ? "enabled" : "disabled";
                    break;
                case ThinkingFieldKind.EnableThinking:
                    body["enable_thinking"] = enabled;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>从历史消息重建 OpenAI 格式消息数组（system 独立前置，不进 history）。</summary>
    private static List<object> BuildMessages(string? systemPrompt, IReadOnlyList<ChatMessage> history)
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
                        ["content"] = m.Content ?? "",
                    });
                    break;
            }
        }

        return list;
    }
}
