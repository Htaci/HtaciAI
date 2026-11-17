using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Conversations;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services;

/// <summary>
/// 用户在任务进行中「引导插入」的消息：在下一次请求之前并进历史。
///
/// <b>这不是「插进正在进行的请求」</b> —— API 是无状态的，每一轮都是一次完整的新请求，
/// 历史全部重发，没有半边流可以半路塞东西进去。所谓引导，只是在工具循环的轮边界
/// 把这条用户消息追加进历史，再让下一轮请求带着它发出去。
/// 因此模型此前想过什么、做过什么调用都还在（它们本来就在历史里），思考不会丢。
/// </summary>
public sealed record SteerMessage(string Text, IReadOnlyList<ChatAttachment>? Attachments = null);

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
    /// 某次 API 往返即将自动重试时的通知（下一个尝试序号, 总尝试数）。
    /// 重试的粒度是「一次请求」，不是「整轮任务」——见 <see cref="RetryingChatClient"/>。
    /// </summary>
    public Action<int, int>? RetryScheduled { get; set; }

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
    /// <paramref name="store"/>：每条新消息（user / tool / assistant）落库；
    /// <paramref name="beginRound"/>：新一轮 assistant 开始时创建 UI 并返回流式回调。
    ///
    /// <paramref name="userText"/> 为 null 表示<b>重新生成</b>：不新建用户消息，
    /// <paramref name="history"/> 自身就是完整上下文（结尾是那条用户消息），只产生新的 AI 回复。
    /// </summary>
    public async Task<ChatTurnResult> ChatAsync(
        string sessionId,
        string? systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string? userText,
        IReadOnlyList<ChatAttachment>? attachments,
        ChatRequestOptions options,
        IChatClient client,
        IToolExecutor? tools,
        ChatPersistSink store,
        Func<ChatMessage, ChatRoundSink> beginRound,
        CancellationToken ct,
        string? continuedFrom = null,
        Func<IReadOnlyList<SteerMessage>>? takeSteers = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // —— 用户轮次（重新生成时没有这一步） ——
        ChatMessage? userMsg = null;

        // 发出去之前先修好配对：历史里可能留着「assistant 有 tool_calls、却没有 tool 回它」的残缺消息
        // （应用在工具执行途中被杀就会留下这种），服务端遇到它整轮请求直接 400。
        // 放在这里而不是各个调用方：只要是走网关发的请求，就都过这一道。
        var messages = new List<ChatMessage>(ToolCallRepair.ForWire(history));

        if (userText is not null)
        {
            userMsg = new ChatMessage
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
            await store.Insert(userMsg);
            messages.Add(userMsg);
        }

        // —— AI 轮次（多轮工具调用共享同一 turn_id） ——
        var aiTurnId = await ChatRepository.GetNextTurnIdAsync(sessionId);
        ChatMessage? final = null;

        // 自动重试一律套在这一层，而不是让每个调用方自己包：
        // 重试的粒度是「一次 API 往返」，工具循环里的每一次请求各有一份新的 5 次额度。
        var active = new RetryingChatClient(client, RetryScheduled);

        // 统计流水的上下文：这一轮的所有 API 往返都挂在同一个会话 / turn 下，
        // 每次尝试各占一行（重试装饰器会写 attempt）。消息 id 在每轮开始后补上。
        // 覆盖调用方传进来的值 —— 走网关的请求一律是对话请求。
        options.ApiCall = new ApiCallContext
        {
            Kind = ApiRequestKinds.Chat,
            SessionId = sessionId,
            TurnId = aiTurnId,
        };

        // 每轮用户消息重置工具状态（本轮已读取文件等）
        tools?.BeginTurn();

        // 一条 tool 结果行：先插入（内容为占位），执行完再 update 成真实结果。
        async Task<ChatMessage> InsertToolMessageAsync(ChatToolCall call, string content, string status)
        {
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                TurnId = aiTurnId,
                SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
                Role = "tool",
                Content = content,
                ToolCallId = call.Id,
                ToolName = call.Name ?? "",
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await store.Insert(msg);
            messages.Add(msg);
            return msg;
        }

        for (var round = 0; round < MaxToolRounds; round++)
        {
            // 上一轮工具刚跑完、用户这时按了停止：不要再发下一次请求。
            // 不检查的话，停止信号要等这次 HTTP 往返建连后才生效，看起来就是「点了没反应」。
            ct.ThrowIfCancellationRequested();

            // 用户在任务进行中点「引导插入」：在这一轮请求之前把消息并进历史。
            //
            // 边界只能选在这里。上一轮的工具结果已经全部追加进 messages，此刻末尾是
            // tool 消息；Anthropic 要求 tool_use 后面紧跟 tool_result，把用户文本插进
            // 这两者之间，整轮请求直接 400。放在循环开头则永远是「工具结果之后」。
            //
            // 边界不是随时都有的：模型一口气写正文、一次工具都不调，这一轮跑完就结束了，
            // 那时它退回成普通排队消息（下一轮再发）。
            if (takeSteers?.Invoke() is { Count: > 0 } steers)
            {
                foreach (var steer in steers)
                {
                    var steerMsg = new ChatMessage
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        SessionId = sessionId,
                        // 与这一轮的 assistant 消息同一个 turn：它是「这一轮中途插进来的话」，
                        // 不是新的一轮。写成独立 turn 的话，重排历史时它会自成一个用户块落到
                        // 整轮后面（用户看到的是「我明明在工具调用后插的，怎么跑到最后去了」），
                        // 而它其实是在这一轮的第 N 次请求之前进去的。
                        TurnId = aiTurnId,
                        SequenceNumber = await ChatRepository.GetNextSequenceAsync(sessionId),
                        Role = "user",
                        Content = steer.Text,
                        Metadata = steer.Attachments is { Count: > 0 }
                            ? AttachmentJson.Serialize(steer.Attachments)
                            : null,
                        Status = "completed",
                        ModelName = client.ModelName,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    await store.Insert(steerMsg);
                    messages.Add(steerMsg);
                }
            }

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
                // 「继续」接出来的这一轮：只在这一轮第一条消息上留指针，
                // 界面据此把它画成上一轮的后半截（见 BuildTurnBlock）。
                ContinuedFrom = round == 0 ? continuedFrom : null,
            };

            var sink = beginRound(assistantMsg);

            // 统计流水对回具体消息：这一轮的每一次往返都记它的 id
            if (options.ApiCall is { } roundCall) roundCall.MessageId = assistantMsg.Id;

            // 单独攒一份流式正文与思考：被打断时 StreamAsync 不会返回结果，
            // 不在这里攒的话这一轮收到的内容只活在界面控件里，落库的是一条空消息 ——
            // 重开会话看不到，点「继续」时模型也看不到（等于白等一场）。
            var partial = new StringBuilder();
            var partialThinking = new StringBuilder();
            var capturing = new ChatRoundSink
            {
                OnContent = delta => { partial.Append(delta); sink.OnContent(delta); },
                OnThinking = delta => { partialThinking.Append(delta); sink.OnThinking(delta); },
                OnToolCall = sink.OnToolCall,
            };

            ChatStreamResult result;
            try
            {
                result = await active.StreamAsync(messages, systemPrompt, options, capturing, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动停止：不是失败，按 interrupted 落库，不往正文里写错误文案。
                // when 过滤是必须的：超时同样抛 OperationCanceledException，但它不是用户取消的，
                // 要落到下面的通用 catch 里按出错记录 —— 少了它超时会无声无息，那是最难查的一类失败。
                assistantMsg.Status = "interrupted";
                assistantMsg.Content = partial.Length == 0 ? null : partial.ToString();
                assistantMsg.Thinking = partialThinking.Length == 0 ? null : partialThinking.ToString();
                await store.Insert(assistantMsg);
                return new ChatTurnResult(userMsg, assistantMsg, false, null);
            }
            catch (Exception ex)
            {
                assistantMsg.Status = "failed";

                // 失败前已经收到的正文 / 思考要留在库里：那是这一轮真正想出来的东西，
                // 丢掉之后用户点「重试」面对的是一轮空白，等于白等一场。
                // （自动重试只在「一个字都还没吐出来」时发生，所以这里的部分内容一定来自最后一次尝试。）
                var broken = ex as ChatStreamException;
                if (broken is not null)
                {
                    assistantMsg.Content = broken.PartialContent;
                    assistantMsg.Thinking = string.IsNullOrWhiteSpace(broken.PartialThinking)
                        ? null
                        : broken.PartialThinking;
                }

                await store.Insert(assistantMsg);

                // 错误另存一条 role=error 的消息：完整的异常信息（类型 / 消息 / 服务端返回体 / 调用栈）
                // 都在正文里，界面上渲染成红色错误卡。assistant 那条只留状态与上面的部分内容。
                // 记原始异常而不是外面那层包装：错误卡标题要看到的是「HTTP 502」，
                // 而不是包装类的名字。
                await store.Insert(ChatError.Create(
                    sessionId,
                    aiTurnId,
                    await ChatRepository.GetNextSequenceAsync(sessionId),
                    client.ModelName,
                    broken?.Cause ?? ex));

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
            await store.Insert(assistantMsg);
            messages.Add(assistantMsg);
            final = assistantMsg;

            if (result.ToolCalls.Count == 0)
                break;

            // —— 执行工具并追加 tool 结果 ——
            if (tools is null) break;
            var toolCalls = result.ToolCalls.ToList();
            foreach (var call in toolCalls)
            {
                // 一批里有多个调用时，停止要在两个调用之间就生效，
                // 而不是把剩下的每个都启动一遍再各自取消 —— 但<b>每一条都要留一条结果行</b>：
                // 少一条，库里就是「assistant 有 3 个 tool_calls、只有 1 条 tool 回它」，
                // 服务端此后每次发送都 400。所以这里写「未执行」，而不是直接抛出去。
                if (ct.IsCancellationRequested)
                {
                    await InsertToolMessageAsync(call,
                        $"工具「{call.Name ?? ""}」未执行：用户已停止本轮，没有取得结果。", "failed");
                    continue;
                }

                var toolName = call.Name ?? "";

                // 顺序是刻意的：<b>先落一条占位行，再去执行</b>。
                // 带 tool_calls 的 assistant 消息上一轮已经落库了，若等执行完才写结果，
                // 这中间用户关掉软件就会留下「有 tool_calls、没有 tool 回它」的历史 ——
                // 服务端此后每次发送都 400，且那条坏消息留在库里，这个会话再也发不出去。
                // 占位行直接写成中断态：真被杀掉时它已经是最终形态，不需要任何事后收敛。
                var toolMsg = await InsertToolMessageAsync(call, ToolCallRepair.InterruptedResult, "interrupted");

                ToolExecution exec;
                try
                {
                    exec = await tools.ExecuteAsync(call, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 停止发生在工具那一层（审批卡被撤销、工具自己按令牌收了尾）。
                    // 结果照样要写进这条 tool 消息：模型得知道它是被用户停掉的，而不是凭空消失。
                    exec = ToolExecution.Fail("已被用户强制停止：没有取得结果，不要把它当成执行成功");
                }

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
                        await store.Update(assistantMsg);
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

                // 占位行的第二次落库：结果、真实工具名（未解析工具会被改写成 invalid）、失败状态
                toolMsg.Content = toolContent;
                toolMsg.ToolName = toolName;
                // view_image / view_video_frames / view_video 的结果：只存路径，
                // 构造下一次请求时才读文件编码成媒体 part（抽帧可能是多张，存数组）
                toolMsg.Metadata = exec.ImagePaths is { Count: > 0 } images ? ToolImageJson.Serialize(images)
                    : exec.VideoPath is not null ? ToolVideoJson.Serialize(exec.VideoPath)
                    : null;
                toolMsg.Status = exec.Success ? "completed" : "failed";
                await store.Update(toolMsg);
            }
        }

        // 本轮耗时写在最后一条 assistant 消息上。用时间戳推不出来 ——
        // 一轮内所有消息共用同一个 now，单轮消息的 created_at 与 updated_at 相等。
        if (final is not null)
        {
            final.DurationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - now;
            await store.Update(final);
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
    /// 压缩请求也用它落库（见 <c>ConversationRuntime.CompressAsync</c>）：摘要的输出 token
    /// 就是压缩之后上下文的实际占用，两处口径必须一致。
    /// </summary>
    internal static string BuildUsageJson(ChatStreamResult result)
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

    /// <summary>
    /// 工具调用分片。同一轮里的多个调用靠 <see cref="ToolCallDelta.Index"/> 区分，
    /// 界面据此在参数还在传的时候就把工具卡建出来 —— 等到流结束才建的话，
    /// 用户要盯着一个没反应的界面直到工具跑完。
    /// </summary>
    public Action<ToolCallDelta> OnToolCall { get; set; } = _ => { };
}

/// <summary>
/// 一次工具调用的流式分片。
/// id / name 通常只在首片出现，arguments 逐片追加 —— 这里给的是<b>分片原文</b>，
/// 累积成完整调用是客户端自己的事（它返回的 <see cref="ChatStreamResult.ToolCalls"/> 才是权威结果）。
/// </summary>
public readonly record struct ToolCallDelta(int Index, string? Id, string? Name, string ArgumentsDelta);

/// <summary>
/// 网关对外的落库出口。插入与更新分成两个口子，是因为工具结果是<b>先插入、后更新</b>：
/// 只有两个口子分开，界面才能在同一张卡上先显示「执行中」、再把输出填进去。
/// </summary>
public sealed class ChatPersistSink
{
    /// <summary>新消息落库（用户消息、assistant 消息、工具占位行、错误记录）。</summary>
    public required Func<ChatMessage, Task> Insert { get; init; }

    /// <summary>同一条消息的第二次落库（工具跑完、重新解析为 invalid、补本轮耗时）。</summary>
    public required Func<ChatMessage, Task> Update { get; init; }
}

/// <summary>一次 ChatAsync 的结果。<see cref="User"/> 在重新生成时为 null（那一次没有新建用户消息）。</summary>
public sealed record ChatTurnResult(
    ChatMessage? User,
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
    public ModelProtocol Protocol { get; init; } = ModelProtocol.OpenAI;
    public string Endpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    /// <summary>发送给 API 的模型名（模型调用 id）。</summary>
    public string ModelName { get; init; } = "";
    public ThinkingFieldKind ThinkingField { get; init; } = ThinkingFieldKind.Think;
    public bool SupportsThinking { get; init; }
    public IReadOnlyList<string> ThinkingStrengths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是否接受数组格式的 content。已经算好「模型级覆盖 → 服务商设置」的最终值，
    /// 调用方不必再关心两层覆盖关系。
    /// </summary>
    public bool SupportsArrayContent { get; init; } = true;
    public bool SupportsStreaming { get; init; } = true;
    public long ContextWindow { get; init; } = -1;

    /// <summary>最大输出 token。-1 = 未知。目前仅作记录，请求体里还没有下发处。</summary>
    public long MaxOutputTokens { get; init; } = -1;

    /// <summary>
    /// 是否接受 temperature 参数。null = 未知（调用方按「不传」处理）。
    /// 请求体里目前不下发 temperature，这个字段的价值在于界面能把没意义的旋钮置灰：
    /// 部分模型的思考模式下 temperature 收而不生效（DeepSeek 就是），
    /// 参数调了没反应比报错更难查。
    /// </summary>
    public bool? TemperatureSupported { get; init; }

    /// <summary>开发商 id（决定头像）。空 = 没认出来。</summary>
    public string Vendor { get; init; } = "";

    /// <summary>开发商展示名（如「DeepSeek」「智谱」）；没认出来为 null。</summary>
    public string? VendorName { get; init; }

    /// <summary>这行配置的来源，决定界面是否提示「配置需要补充」。见 <see cref="ModelConfigOrigin"/>。</summary>
    public ModelConfigOrigin ConfigOrigin { get; init; } = ModelConfigOrigin.Manual;

    // ---- 价格：单位「每百万 token」，null = 未设置 ----
    // 只给统计页算金额快照用；null 时统计页显示「—」而不是 0（0 会让人以为免费）。

    public double? PriceInput { get; init; }
    public double? PriceCacheHit { get; init; }
    public double? PriceOutput { get; init; }
    public string Currency { get; init; } = "CNY";
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
