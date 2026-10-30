using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 端点连通性探测：给「这一行配置到底哪里不对劲」提供直接证据。
///
/// 分两步，是因为配错的位置不同、表现也不同，只测一步会把两种情况混在同一个报错里：
/// <list type="number">
///   <item><b>列模型</b>（<c>GET /models</c>）—— 不发消息、不花 token，验证地址是否可达、
///     密钥是否被接受。这一步挂了，问题一定在地址或密钥上。</item>
///   <item><b>最小补全</b>（发一条真实的对话请求）—— 验证模型调用 id 是否有效、
///     该协议下的请求体是否被接受。这一步挂了而第一步通过，说明地址密钥都对，
///     问题在模型名或协议选错。</item>
/// </list>
///
/// <b>判定标准是「请求正常完成」，不是「回复内容非空」</b>：
/// 思考模型在很小的输出预算下可能只产出思考内容、正文为空；
/// 中转站也可能返回空 content + 正常 usage。这些都是通的，
/// 拿「文字里有『好的』」当判据会把它们误判成失败。
/// </summary>
public static class ConnectionProbe
{
    /// <summary>一条探测结果。<see cref="Detail"/> 已整理成可直接展示的文本。</summary>
    public sealed record StepResult(string Name, bool Ok, string Detail, long ElapsedMs);

    /// <summary>探测用的用户消息。措辞无关紧要，只是让服务端有东西可答。</summary>
    private const string ProbePrompt = "连接测试，请回复好的";

    /// <summary>第一步：列模型（不消耗 token）。</summary>
    public static async Task<StepResult> CheckModelsAsync(
        AiProviderEndpoint endpoint, string? apiKey, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var result = await ModelDiscovery.FetchAsync(endpoint.Endpoint, apiKey, ct);
        watch.Stop();

        if (!result.Ok)
            return new StepResult("列出模型（GET /models）", false, result.Error ?? "未知错误", watch.ElapsedMilliseconds);

        return new StepResult(
            "列出模型（GET /models）",
            true,
            $"地址与密钥可用，服务端返回 {result.ModelIds.Count} 个模型。",
            watch.ElapsedMilliseconds);
    }

    /// <summary>第二步：用指定模型发一条最小对话请求（消耗极少 token）。</summary>
    public static async Task<StepResult> CheckCompletionAsync(
        AiProvider provider,
        AiProviderEndpoint endpoint,
        AiModel model,
        CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();

        var client = ModelCatalog.BuildClient(new ModelDetails
        {
            ModelId = model.Id,
            DisplayName = model.DisplayName,
            ProviderName = provider.Name,
            Protocol = endpoint.Protocol,
            Endpoint = endpoint.Endpoint,
            ApiKey = provider.ApiKey ?? "",
            ModelName = model.CallId,
            ThinkingField = provider.ThinkingField,
        });

        try
        {
            var history = new List<ChatMessage>
            {
                new() { Role = "user", Content = ProbePrompt },
            };

            var options = new ChatRequestOptions
            {
                // Default = 不下发任何思考 / 强度字段，与模型选择器里默认的「none」档一致。
                // 不用 NoThink：显式的 thinking: disabled 有些站不认，会把「配置没错」误测成失败。
                Thinking = ThinkingMode.Default,
                Tools = null,
                SupportsVision = false,
            };

            var result = await client.StreamAsync(
                history, null, options, new ChatRoundSink(), ct);
            watch.Stop();

            // 只看「这一次往返有没有正常完成」。正文为空是合法的（思考型模型 / 极小预算都可能是空）。
            var chars = result.Content?.Length ?? 0;
            var thinkingChars = result.Thinking?.Length ?? 0;

            var detail = chars > 0
                ? $"模型「{model.CallId}」正常回复（{chars} 字）。"
                : thinkingChars > 0
                    ? $"模型「{model.CallId}」正常返回，但只产出了思考内容（{thinkingChars} 字），正文为空。"
                    : $"模型「{model.CallId}」正常返回，但内容为空（部分思考型模型在低输出预算下就是这样）。";

            return new StepResult("发送测试消息", true, detail, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            watch.Stop();
            return new StepResult("发送测试消息", false, "已取消。", watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // HttpClient 自身的超时也走这里，与「用户点了取消」靠上面的分支区分
            watch.Stop();
            return new StepResult("发送测试消息", false, "请求超时，服务端在限定时间内没有回应。", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new StepResult("发送测试消息", false, ex.Message, watch.ElapsedMilliseconds);
        }
    }
}
