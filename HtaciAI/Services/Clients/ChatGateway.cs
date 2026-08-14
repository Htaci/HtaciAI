using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 统一入口（兼容层/中间层）。对 UI 而言是唯一入口：
/// 负责按模型解析客户端、管理 turn、编排多轮工具调用循环、统一落库。
/// 各客户端返回统一参数，UI 无需关心底层是 OpenAI / Anthropic / Responses。
/// </summary>
public sealed class ChatGateway
{
    private readonly IChatClient _client;

    /// <summary>一次用户消息内，工具调用的最大轮数上限。</summary>
    public int MaxToolRounds { get; init; } = 10;

    public ChatGateway(IChatClient client) => _client = client;

    /// <summary>模型管理系统接入前：一律使用 DeepSeek V4 Flash 走 OpenAiExClient。</summary>
    public ChatGateway() : this(new OpenAiExClient(ChatConfig.Endpoint, ChatConfig.ApiKey, ChatConfig.Model)) { }

    /// <summary>
    /// 按模型 id 解析模型详情。当前模型管理系统尚未接入，恒返回 DeepSeek V4 Flash；
    /// 后续在此接入真实的「模型 id → 详情」解析。
    /// </summary>
    public ModelDetails ResolveModel(string modelId) => ModelDetails.Default;

    /// <summary>
    /// 处理一轮对话：
    /// 用户消息占一个 turn；整个 AI 回复（含多轮工具调用/结果/续写）共享另一个 turn。
    /// 循环直到 AI 不再请求工具或达到轮数上限。
    /// persist：每条新消息（user / tool / assistant）落库；beginRound：新一轮 assistant 开始时创建 UI 并返回流式回调。
    /// </summary>
    public async Task<ChatTurnResult> ChatAsync(
        string sessionId,
        string? systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userText,
        ChatRequestOptions options,
        IToolExecutor? tools,
        Func<ChatMessage, Task> persist,
        Func<ChatMessage, ChatRoundSink> beginRound,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // —— 用户轮次 ——
        var userMsg = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            TurnId = await ChatRepository.GetNextTurnIdAsync(sessionId),
            SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
            Role = "user",
            Content = userText,
            Status = "completed",
            ModelName = _client.ModelName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await persist(userMsg);

        var messages = new List<ChatMessage>(history) { userMsg };

        // —— AI 轮次（多轮工具调用共享同一 turn_id） ——
        var aiTurnId = await ChatRepository.GetNextTurnIdAsync(sessionId);
        ChatMessage? final = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var assistantMsg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                TurnId = aiTurnId,
                SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
                Role = "assistant",
                Status = "completed",
                ModelName = _client.ModelName,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var sink = beginRound(assistantMsg);

            ChatStreamResult result;
            try
            {
                result = await _client.StreamAsync(messages, systemPrompt, options, sink.OnContent, sink.OnThinking, ct);
            }
            catch (Exception ex)
            {
                assistantMsg.Status = "failed";
                sink.OnFailed(ex.Message);
                await persist(assistantMsg);
                return new ChatTurnResult(userMsg, assistantMsg, false, ex.Message);
            }

            assistantMsg.Content = result.Content;
            assistantMsg.Thinking = string.IsNullOrWhiteSpace(result.Thinking) ? null : result.Thinking;
            assistantMsg.Role = result.Role is "user" or "assistant" or "tool" ? result.Role : "assistant";
            assistantMsg.ToolCallId = string.IsNullOrEmpty(result.ToolCallId) ? null : result.ToolCallId;
            if (result.ToolCalls.Count > 0)
            {
                assistantMsg.ToolName = result.ToolCalls[0].Name;
                assistantMsg.Metadata = ToolCallJson.Serialize(result.ToolCalls);
            }
            assistantMsg.UsageJson = BuildUsageJson(result);
            await persist(assistantMsg);
            messages.Add(assistantMsg);
            final = assistantMsg;

            if (result.ToolCalls.Count == 0)
                break;

            // —— 执行工具并追加 tool 结果 ——
            if (tools is null) break;
            foreach (var call in result.ToolCalls)
            {
                string toolContent;
                try
                {
                    toolContent = await tools.ExecuteAsync(call, ct);
                }
                catch (Exception ex)
                {
                    toolContent = "工具执行失败：" + ex.Message;
                }

                var toolMsg = new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SessionId = sessionId,
                    TurnId = aiTurnId,
                    SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
                    Role = "tool",
                    Content = toolContent,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Status = "completed",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await persist(toolMsg);
                messages.Add(toolMsg);
            }
        }

        return new ChatTurnResult(userMsg, final, true, null);
    }

    /// <summary>
    /// 落库格式：只存 input / cache_hit / output + 原始对象；
    /// cache_miss 与 total 由减法/加法现算，不单独存。
    /// </summary>
    private static string BuildUsageJson(ChatStreamResult result)
    {
        object? raw = null;
        if (!string.IsNullOrEmpty(result.RawUsageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.RawUsageJson);
                raw = doc.RootElement.Clone();
            }
            catch
            {
                // 忽略损坏的 usage
            }
        }
        return JsonSerializer.Serialize(new
        {
            input_tokens = result.Usage.InputTokens,
            cache_hit_tokens = result.Usage.CacheHitTokens,
            output_tokens = result.Usage.OutputTokens,
            raw,
        });
    }
}

/// <summary>一个 assistant 轮次的流式回调集合。</summary>
public sealed class ChatRoundSink
{
    public Action<string> OnContent { get; set; } = _ => { };
    public Action<string> OnThinking { get; set; } = _ => { };
    public Action<string> OnFailed { get; set; } = _ => { };
}

/// <summary>一次 ChatAsync 的结果。</summary>
public sealed record ChatTurnResult(
    ChatMessage User,
    ChatMessage? FinalAssistant,
    bool Success,
    string? Error);

/// <summary>
/// 模型详情（「模型 id → 详情」解析的占位结构，模型管理系统接入后填充）。
/// </summary>
public sealed record ModelDetails(
    string ModelId,
    string DisplayName,
    string Provider,
    string Endpoint,
    string ApiKey,
    string ModelName,
    bool SupportsThinking,
    bool SupportsReasoningEffort)
{
    /// <summary>当前唯一的模型：DeepSeek V4 Flash（OpenAI 兼容，走 OpenAiExClient）。</summary>
    public static readonly ModelDetails Default = new(
        "deepseek-v4-flash",
        "DeepSeek V4 Flash",
        "Htaci",
        ChatConfig.Endpoint,
        ChatConfig.ApiKey,
        ChatConfig.Model,
        SupportsThinking: true,
        SupportsReasoningEffort: true);
}

/// <summary>tool_calls 与 chat_message.metadata 之间的 JSON 序列化辅助。</summary>
public static class ToolCallJson
{
    public static string Serialize(IReadOnlyList<ChatToolCall> calls)
        => JsonSerializer.Serialize(new { tool_calls = calls });

    public static List<ChatToolCall> Deserialize(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return new();
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.TryGetProperty("tool_calls", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.Deserialize<List<ChatToolCall>>() ?? new();
            return new();
        }
        catch
        {
            return new();
        }
    }
}
