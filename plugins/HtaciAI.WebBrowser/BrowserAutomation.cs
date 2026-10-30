using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 把「一次页面操作」的所有前置条件收在一处：权限校验 → 找到窗口 → 找到标签 →
/// 切到 UI 线程 → 建 CDP 客户端 → 执行 → 回到调用线程。
///
/// <b>为什么必须集中</b>：这批工具有十几个，每个都要做这五件事。
/// 分散写的话，漏掉权限校验就是越权操作、漏掉线程切换就是跨线程访问内核、
/// 漏掉标签切换就是「模型在后台标签上操作，用户看着另一页」——
/// 三种都是偶发且难复现的问题。
/// </summary>
internal sealed class BrowserAutomation
{
    private readonly BrowserManager _manager;

    public BrowserAutomation(BrowserManager manager) => _manager = manager;

    /// <summary>
    /// 在指定浏览器/标签上执行一段自动化动作。
    /// <paramref name="tabId"/> 为空时用当前活动标签。
    /// </summary>
    public Task<T> RunAsync<T>(
        string browserId,
        string? sessionId,
        string? tabId,
        Func<CdpClient, WebViewSurface, Task<T>> work,
        CancellationToken ct)
        => BrowserManager.UiAsync(async () =>
        {
            _manager.RequireAccess(browserId, sessionId);

            var window = _manager.Require(browserId);

            // 指定了标签就先切过去：用户在窗口里能看见模型在动哪一页，
            // 而且 WebView 只有在可见状态下才会正确计算元素坐标。
            if (!string.IsNullOrWhiteSpace(tabId))
            {
                if (!window.ActivatePage(tabId))
                    throw new InvalidOperationException(
                        $"浏览器 {browserId} 里没有 {tabId} 这个标签。现有标签：{window.DescribeTabs()}");
            }

            var surface = window.FindPage(tabId)
                ?? throw new InvalidOperationException(
                    $"浏览器 {browserId} 里没有可操作的标签页。现有标签：{window.DescribeTabs()}");

            // 等内核就绪：标签可能刚建出来，控制器还没连上
            await surface.ReadyTask.WaitAsync(ct);

            return await work(new CdpClient(surface), surface);
        }, ct);

    /// <summary>不带页面的动作（列表、开关窗口之类）。</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
        => BrowserManager.UiAsync(work, ct);
}

/// <summary>
/// 工具层的公共辅助：解析参数、统一异常翻译。
/// </summary>
internal static class ToolArgs
{
    /// <summary>取一个字符串参数；缺失返回 null。</summary>
    public static string? Str(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            return doc.RootElement.TryGetProperty(name, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>取一个布尔参数；缺失用默认值。</summary>
    public static bool Bool(string? json, string name, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return fallback;

            if (!doc.RootElement.TryGetProperty(name, out var v)) return fallback;

            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => fallback,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback;
        }
    }

    /// <summary>取一个数字参数；缺失返回 null。</summary>
    public static double? Num(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            if (!doc.RootElement.TryGetProperty(name, out var v)) return null;

            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => v.GetDouble(),
                System.Text.Json.JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读一个「uid → 值」的数组参数（fill_form 用）。
    /// 格式：<c>[{"uid":"3","value":"张三"}, ...]</c>
    /// </summary>
    public static List<(string Uid, string Value)> Fields(string? json, string name)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
            if (!doc.RootElement.TryGetProperty(name, out var arr)) return result;
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                var uid = item.TryGetProperty("uid", out var u) &&
                          u.ValueKind == System.Text.Json.JsonValueKind.String
                    ? u.GetString()
                    : null;

                // value 可能是字符串、数字、布尔：统一按文本读，免得模型被类型卡住
                var value = item.TryGetProperty("value", out var v)
                    ? v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => v.GetString() ?? "",
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        System.Text.Json.JsonValueKind.Null => "",
                        _ => v.ToString(),
                    }
                    : "";

                if (!string.IsNullOrWhiteSpace(uid))
                    result.Add((uid!, value));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 参数坏了就当没给，由调用方报「缺少 elements」
        }

        return result;
    }

    /// <summary>读一个字符串数组参数（wait_for 的 texts 用）。</summary>
    public static List<string> Strings(string? json, string name)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return result;
            if (!doc.RootElement.TryGetProperty(name, out var arr)) return result;

            if (arr.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var single = arr.GetString();
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single!);
                return result;
            }

            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return result;

            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String &&
                    item.GetString() is { Length: > 0 } s)
                    result.Add(s);
        }
        catch (System.Text.Json.JsonException)
        {
            // 同上
        }

        return result;
    }
}
