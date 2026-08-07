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
    /// 发送流式请求，增量回调 onDelta；返回 (完整内容, usageJson)。
    /// </summary>
    public static async Task<(string Content, string UsageJson)> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        string? systemPrompt,
        Action<string> onDelta,
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

        var sb = new StringBuilder();
        string? usageJson = null;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);

            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                        onDelta(text);
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                usageJson = usage.GetRawText();
        }

        return (sb.ToString(), usageJson ?? "");
    }

    private sealed record ApiMessage(string role, string content);
}
