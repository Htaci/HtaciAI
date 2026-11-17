using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Network;

namespace HtaciAI.Services.KnowledgeBase;

/// <summary>
/// 嵌入调用：OpenAI 兼容的 <c>POST {base}/embeddings</c>。
///
/// <b>只支持 Chat Completions 形态的服务商</b>（OpenAI / LM Studio 两类协议，以及任何兼容端点）。
/// Anthropic 的 Messages 协议没有 embeddings 接口，选到它时直接报一条能看懂的话，
/// 而不是先发出去再拿到 404。
///
/// 每批最多 <see cref="BatchSize"/> 条：有的服务商对数组长度有限制，一次塞几百条会被整批拒掉，
/// 而报错信息通常不会告诉你是长度问题。
/// </summary>
public static class EmbeddingClient
{
    /// <summary>一次请求带多少条文本。取 32 是保守值，代价只是多几次往返。</summary>
    private const int BatchSize = 32;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 这个模型能不能用来做嵌入。返回 null 表示可以，否则是给用户看的原因。
    /// </summary>
    public static string? CheckUsable(ModelDetails? model)
    {
        if (model is null) return "还没有选嵌入模型。到「设置 → 默认配置」里选一个支持嵌入的模型。";

        if (model.Protocol is not (ModelProtocol.OpenAI or ModelProtocol.LMStudio))
            return $"「{model.DisplayName}」用的是 {model.Protocol} 协议，没有 embeddings 接口。请换一个 OpenAI 兼容的服务商。";

        if (string.IsNullOrWhiteSpace(model.Endpoint))
            return $"「{model.DisplayName}」的服务商没有填地址。";

        return null;
    }

    /// <summary>把一段文本嵌成向量。</summary>
    public static async Task<float[]> EmbedAsync(ModelDetails model, string text, CancellationToken ct)
    {
        var result = await EmbedBatchAsync(model, new[] { text }, ct);
        return result[0];
    }

    /// <summary>批量嵌入。返回顺序与入参一致。</summary>
    public static async Task<List<float[]>> EmbedBatchAsync(
        ModelDetails model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (CheckUsable(model) is { } reason) throw new EmbeddingException(reason);

        var vectors = new List<float[]>(inputs.Count);
        for (var offset = 0; offset < inputs.Count; offset += BatchSize)
        {
            var slice = new List<string>();
            for (var i = offset; i < inputs.Count && i < offset + BatchSize; i++)
                slice.Add(inputs[i]);

            vectors.AddRange(await SendAsync(model, slice, ct));
        }
        return vectors;
    }

    private static async Task<List<float[]>> SendAsync(
        ModelDetails model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var url = OpenAiChatClient.NormalizeBaseUrl(model.Endpoint) + "/embeddings";

        var body = JsonSerializer.Serialize(new
        {
            model = model.ModelName,
            input = inputs,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(model.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);

        HttpResponseMessage response;
        string payload;
        try
        {
            response = await HttpClientProvider.Get(Timeout).SendAsync(request, ct);
            payload = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingException($"嵌入请求超时（{model.DisplayName}）。");
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingException($"连不上嵌入端点：{ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new EmbeddingException(
                    $"嵌入失败（HTTP {(int)response.StatusCode}）：{Shorten(payload)}");
        }

        return ParseVectors(payload, inputs.Count);
    }

    /// <summary>
    /// 解析 <c>{"data":[{"embedding":[…]}, …]}</c>。按 <c>index</c> 排序而不是按返回顺序：
    /// 有的服务商并发处理，回来是乱序的，凭顺序配对会把向量的归属弄错 ——
    /// 那种错误不会报错，只会让检索悄悄变差。
    /// </summary>
    private static List<float[]> ParseVectors(string payload, int expected)
    {
        var vectors = new List<float[]>(expected);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new EmbeddingException($"嵌入响应里没有 data：{Shorten(payload)}");

            var slots = new List<(int Index, float[] Vector)>();
            var fallback = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("embedding", out var array) || array.ValueKind != JsonValueKind.Array)
                    continue;

                var vector = new float[array.GetArrayLength()];
                var i = 0;
                foreach (var number in array.EnumerateArray())
                    vector[i++] = (float)number.GetDouble();

                var index = item.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var parsed)
                    ? parsed
                    : fallback;
                fallback = index + 1;
                slots.Add((index, vector));
            }

            slots.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var slot in slots) vectors.Add(slot.Vector);
        }
        catch (JsonException ex)
        {
            throw new EmbeddingException($"嵌入响应不是合法 JSON：{ex.Message}");
        }

        if (vectors.Count != expected)
            throw new EmbeddingException($"嵌入返回 {vectors.Count} 条向量，请求了 {expected} 条。");

        return vectors;
    }

    private static string Shorten(string text)
        => text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>嵌入失败的原因，消息本身就可以展示给用户。</summary>
public sealed class EmbeddingException : Exception
{
    public EmbeddingException(string message) : base(message) { }
}
