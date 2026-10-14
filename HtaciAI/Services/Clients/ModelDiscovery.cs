using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 从服务商拉取可用模型列表（OpenAI 兼容 <c>GET /models</c>，见 <see cref="OpenAiExClient.ModelsUrl"/>）。
///
/// 只负责「把 id 拿回来」，不写库：批量添加的决策（跳过已有的、用什么默认配置）
/// 留给调用方，这样这里保持成纯粹的只读探测，测试时也好替换。
/// </summary>
public static class ModelDiscovery
{
    /// <summary>
    /// 独立的 HttpClient：超时比对话短得多 —— 列个模型列表卡上五分钟毫无意义，
    /// 早点失败让用户看到原因（地址写错 / Key 无效）比一直转圈强。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>拉取结果。失败时 <see cref="ModelIds"/> 为空，原因在 <see cref="Error"/>。</summary>
    /// <param name="ModelIds">解析出的模型调用 id（已去重并排序）。</param>
    /// <param name="Error">失败原因（已转成可直接展示给用户的中文描述）。</param>
    public sealed record FetchResult(bool Ok, List<string> ModelIds, string? Error);

    public static async Task<FetchResult> FetchAsync(AiProvider provider, CancellationToken ct = default)
    {
        var url = OpenAiExClient.ModelsUrl(provider.Endpoint);
        if (string.IsNullOrWhiteSpace(url))
            return new FetchResult(false, new(), "该服务商没有填写目标 URL 地址。");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                request.Headers.Add("Authorization", $"Bearer {provider.ApiKey}");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                    ? ""
                    : $" {response.ReasonPhrase}";
                return new FetchResult(false, new(),
                    $"服务端返回 {(int)response.StatusCode}{reason}。" +
                    (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                        or System.Net.HttpStatusCode.Forbidden
                        ? "请检查 API Key。"
                        : ""));
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var ids = ParseModelIds(json);
            return ids.Count == 0
                ? new FetchResult(false, ids, "服务端返回的列表里没有解析到任何模型 id。")
                : new FetchResult(true, ids, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 的超时也走 OperationCanceledException，靠「外部没取消」把它和用户取消区分开
            return new FetchResult(false, new(), "请求超时，请检查网络或目标地址。");
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(false, new(), "已取消。");
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(false, new(), $"无法连接到服务端：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new FetchResult(false, new(), $"获取模型列表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析 OpenAI 兼容的模型列表响应：<c>{"data":[{"id":"deepseek-chat",...},...]}</c>。
    ///
    /// 只认 <c>data[].id</c> —— 这是该协议里唯一被普遍实现的字段，其余（owned_by /
    /// created 等）各家差异极大，拿来用反而会引入不一致。解析不出来就返回空，由调用方报错。
    /// </summary>
    private static List<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var item in data.EnumerateArray())
            {
                // id 可能是对象里唯一的字符串；非对象条目直接跳过
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idProp)) continue;
                if (idProp.ValueKind != JsonValueKind.String) continue;

                var id = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
        }
        catch (JsonException)
        {
            // 不是合法 JSON（常见于把 HTML 错误页当响应返回）：当作没解析到
        }

        return ids
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
