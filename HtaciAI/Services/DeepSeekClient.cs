using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 流式工具调用（OpenAI 兼容）。流式场景下工具调用按 index 分片下发，
/// id / function.name 通常只在首块出现，function.arguments 逐块累积。
/// </summary>
public sealed class ChatToolCall
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

/// <summary>一次流式对话的完整结果。</summary>
public sealed record StreamResult(
    string Content,
    string Thinking,
    string Role,
    string ToolCallId,
    IReadOnlyList<ChatToolCall> ToolCalls,
    string UsageJson);

/// <summary>
/// DeepSeek 流式聊天客户端（OpenAI 兼容 /chat/completions 接口）。
/// 注意：方法内部全程 await 且不加 ConfigureAwait(false)，保证 UI 线程回调安全。
/// </summary>
public static class DeepSeekClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ChatConfig.ApiKey}");
        return client;
    }

    /// <summary>
    /// 发送流式请求。
    /// onContent：正文增量（delta.content）；onThinking：思考增量（delta.reasoning_content，可空）。
    /// 返回 <see cref="StreamResult"/>，含正文、思考、role、工具调用与用量。
    /// </summary>
    public static async Task<StreamResult> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        Action<string> onContent,
        Action<string>? onThinking,
        CancellationToken ct)
    {
        var messages = new List<ApiMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ApiMessage("system", systemPrompt));

        foreach (var m in history)
        {
            if (m.IsDeleted || string.IsNullOrEmpty(m.Content))
                continue;
            if (m.Role is not ("user" or "assistant"))
                continue;
            messages.Add(new ApiMessage(m.Role, m.Content));
        }

        var payload = new
        {
            model = ChatConfig.Model,
            messages,
            stream = true,
            stream_options = new { include_usage = true },
        };

        using var response = await Http.PostAsJsonAsync(ChatConfig.Endpoint, payload, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var thinkingSb = new StringBuilder();
        string? role = null;
        string? toolCallId = null;
        var toolCalls = new List<ChatToolCall>();
        string? usageJson = null;

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

            // reasoning_content：DeepSeek 思考内容（V4 Flash 默认开启）
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
            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                usageJson = usage.GetRawText();
        }

        return new StreamResult(
            contentSb.ToString(),
            thinkingSb.ToString(),
            role ?? "",
            toolCallId ?? "",
            toolCalls,
            usageJson ?? "");
    }

    private sealed record ApiMessage(string role, string content);
}
