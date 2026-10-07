using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>
/// 统一入口（兼容层/中间层）。对 UI 而言是唯一入口：
/// 负责按模型解析客户端、管理 turn、编排多轮工具调用循环、统一落库。
/// 各客户端返回统一参数，UI 无需关心底层是 OpenAI / Anthropic / Responses。
/// </summary>
public sealed class ChatGateway
{
    /// <summary>
    /// 一次用户消息内，工具调用的最大轮数上限。
    /// 由「常规设置 → 最大工具循环次数」决定，发送前由调用方按当前设置赋值，
    /// 因此是可变属性而不是 init。不限时调用方传 <see cref="int.MaxValue"/>。
    /// </summary>
    public int MaxToolRounds { get; set; } = 100;

    /// <summary>
    /// 按模型 id（UUID 或调用 id）解析客户端，每次发送前按当前选中模型调用。
    ///
    /// <b>解析不到就返回 null，不回退任何内置模型。</b>这里曾经兜底过一个写死地址与密钥的
    /// DeepSeek，后果是「用户一个模型都没配」时不但不报错，还会拿着别人的 key 发请求。
    /// 返回 null 而不是抛异常：调用点都在发送前的判断位置，可空返回值能把「漏处理」变成编译错误。
    /// </summary>
    public async Task<IChatClient?> ResolveClientAsync(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var details = await ModelCatalog.ResolveAsync(modelId);
        return details is null ? null : ModelCatalog.BuildClient(details);
    }

    /// <summary>按模型 id（UUID 或调用 id）解析模型详情，来自服务商+模型两表。</summary>
    public Task<ModelDetails?> ResolveModelAsync(string modelId)
        => ModelCatalog.ResolveAsync(modelId);

    /// <summary>按模型详情构建对应协议的客户端。</summary>
    public IChatClient BuildClient(ModelDetails details)
        => ModelCatalog.BuildClient(details);

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
        IReadOnlyList<ChatAttachment>? attachments,
        ChatRequestOptions options,
        IChatClient client,
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
            // 附件只在 metadata 里记路径与展示信息；正文里保留的路径才是模型读到的
            Metadata = attachments is { Count: > 0 } ? AttachmentJson.Serialize(attachments) : null,
            Status = "completed",
            ModelName = client.ModelName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await persist(userMsg);

        var messages = new List<ChatMessage>(history) { userMsg };

        // —— AI 轮次（多轮工具调用共享同一 turn_id） ——
        var aiTurnId = await ChatRepository.GetNextTurnIdAsync(sessionId);
        ChatMessage? final = null;

        // 每轮用户消息重置工具状态（本轮已读取文件等）
        tools?.BeginTurn();

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
                ModelName = client.ModelName,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var sink = beginRound(assistantMsg);

            ChatStreamResult result;
            try
            {
                result = await client.StreamAsync(messages, systemPrompt, options, sink.OnContent, sink.OnThinking, ct);
            }
            catch (OperationCanceledException)
            {
                // 用户主动停止：不是失败，按 interrupted 落库，不往正文里写错误文案
                assistantMsg.Status = "interrupted";
                await persist(assistantMsg);
                return new ChatTurnResult(userMsg, assistantMsg, false, null);
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
            var toolCalls = result.ToolCalls.ToList();
            foreach (var call in toolCalls)
            {
                var exec = await tools.ExecuteAsync(call, ct);
                var toolName = call.Name ?? "";
                string toolContent;

                if (!exec.Success)
                {
                    if (exec.IsUnknownTool)
                    {
                        // 未解析工具（模型幻觉出不存在的工具名，或调用了被禁用工具）：收敛到哨兵 invalid。
                        // invalid 始终注册、始终可执行，且从不进模型的 tools 数组（内部工具），
                        // 因此只作为服务端兜底被解析执行。保留原 id 以便与 tool 结果配对，
                        // 把原始工具名与错误塞进参数，返回一条可执行的错误 tool-result，循环不断裂。
                        var invalidCall = new ChatToolCall
                        {
                            Id = call.Id,
                            Name = "invalid",
                            Arguments = JsonSerializer.Serialize(new { tool = call.Name ?? "", error = exec.Error }),
                        };
                        ReplaceCall(toolCalls, call.Id, invalidCall);
                        assistantMsg.Metadata = ToolCallJson.Serialize(toolCalls);
                        await ChatRepository.UpdateMessageAsync(assistantMsg);
                        toolName = "invalid";
                        toolContent = BuiltinToolExecutor.FormatInvalidError(invalidCall);
                    }
                    else
                    {
                        // 真实工具运行失败：工具存在且已执行，保持原始 tool_call 不变，
                        // 把错误作为该工具结果反馈，模型据此重试并可继续工具循环。
                        toolContent = $"工具「{toolName}」调用失败：{exec.Error}";
                    }
                }
                else
                {
                    toolContent = exec.Display;
                }

                var toolMsg = new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SessionId = sessionId,
                    TurnId = aiTurnId,
                    SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
                    Role = "tool",
                    Content = toolContent,
                    // view_image 的结果：只存路径，构造下一次请求时才读文件编码成图片 part
                    Metadata = exec.ImagePath is null ? null : ToolImageJson.Serialize(exec.ImagePath),
                    ToolCallId = call.Id,
                    ToolName = toolName,
                    Status = "completed",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await persist(toolMsg);
                messages.Add(toolMsg);
            }
        }

        // 本轮耗时写在最后一条 assistant 消息上。用时间戳推不出来 ——
        // 一轮内所有消息共用同一个 now，单轮消息的 created_at 与 updated_at 相等。
        if (final is not null)
        {
            final.DurationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - now;
            await ChatRepository.UpdateMessageAsync(final);
        }

        return new ChatTurnResult(userMsg, final, true, null);
    }

    /// <summary>在 tool_calls 列表中把指定 id 的调用替换为新调用（未解析工具时替换为 invalid）。</summary>
    private static void ReplaceCall(List<ChatToolCall> calls, string? id, ChatToolCall replacement)
    {
        var idx = calls.FindIndex(c => c.Id == id);
        if (idx >= 0) calls[idx] = replacement;
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
/// 模型详情（由 ModelCatalog 从服务商+模型两表装配，供网关解析、客户端构建与 UI 展示）。
/// </summary>
public sealed class ModelDetails
{
    public string ModelId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public ModelProtocol Protocol { get; init; } = ModelProtocol.OpenAIEx;
    public string Endpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    /// <summary>发送给 API 的模型名（模型调用 id）。</summary>
    public string ModelName { get; init; } = "";
    public ThinkingFieldKind ThinkingField { get; init; } = ThinkingFieldKind.Think;
    public bool SupportsThinking { get; init; }
    public IReadOnlyList<string> ThinkingStrengths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public bool SupportsArrayContent { get; init; } = true;
    public bool SupportsStreaming { get; init; } = true;
    public long ContextWindow { get; init; } = -1;
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
