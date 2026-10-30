using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 页面上「能用 uid 做的动作」。每个动作都先定位元素再发真实事件。
///
/// <b>统一约定</b>：所有对 uid 的操作失败时，异常文案都要指向「重新 snapshot」。
/// 模型最常见的错误是拿着过期 uid 反复重试，而我们能做的最有价值的事
/// 不是让它成功，而是让它知道该换一条路。
/// </summary>
internal static class PageActions
{
    /// <summary>点击（可选双击）。</summary>
    public static async Task<string> ClickAsync(
        CdpClient cdp, string uid, bool doubleClick, CancellationToken ct)
    {
        var (x, y) = await PageSnapshot.RequireCenterAsync(cdp, uid, ct);
        await cdp.ClickAtAsync(x, y, doubleClick, ct);

        return await DescribeAfterActionAsync(cdp, ct,
            doubleClick ? $"已双击 uid={uid}" : $"已点击 uid={uid}");
    }

    /// <summary>悬停。用于展开下拉菜单、触发 tooltip。</summary>
    public static async Task<string> HoverAsync(CdpClient cdp, string uid, CancellationToken ct)
    {
        var (x, y) = await PageSnapshot.RequireCenterAsync(cdp, uid, ct);
        await cdp.MouseMoveAsync(x, y, ct);

        // 菜单展开通常有过渡动画，等一小会儿再读状态
        await Task.Delay(120, ct);
        return await DescribeAfterActionAsync(cdp, ct, $"已悬停 uid={uid}");
    }

    /// <summary>
    /// 给输入控件赋值。
    ///
    /// <b>为什么不用 <c>el.value = x</c></b>：React/Vue 受控组件把 value 的 setter
    /// 劫持了，直接赋值不会触发它们的 onChange，界面上输入框看着有值、实际状态没变，
    /// 提交时是空的。这里走原生 setter + 手动派发 input/change，
    /// 是框架生态里公认的「让框架知道你改了值」的做法。
    /// </summary>
    public static async Task<string> FillAsync(
        CdpClient cdp, string uid, string value, CancellationToken ct)
    {
        await PageSnapshot.EnsureAsync(cdp, ct);

        var result = await cdp.EvaluateAsync($$"""
            (() => {
              const el = window.__hai.byUid({{JsonSerializer.Serialize(uid)}});
              if (!el) return { error: 'notfound' };
              if (!window.__hai.visible(el)) return { error: 'hidden' };

              const tag = el.tagName.toLowerCase();

              // checkbox / radio：按「值是不是 true」决定勾选
              if (tag === 'input') {
                const type = (el.getAttribute('type') || 'text').toLowerCase();
                if (type === 'checkbox' || type === 'radio') {
                  const want = !({{JsonSerializer.Serialize(value)}} === 'false' ||
                                 {{JsonSerializer.Serialize(value)}} === '0');
                  if (el.checked !== want) el.click();
                  return { ok: true, kind: type };
                }
              }

              // <select>：按 value 或可见文本匹配选项
              if (tag === 'select') {
                const want = {{JsonSerializer.Serialize(value)}};
                const opt = Array.from(el.options).find(
                  o => o.value === want || (o.textContent || '').trim() === want);
                if (!opt) {
                  const names = Array.from(el.options).map(o => (o.textContent || '').trim());
                  return { error: 'nooption', options: names.slice(0, 20) };
                }
                el.value = opt.value;

                // 用原生 setter 再设一次：受控组件需要这个
                const desc = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value');
                desc.set.call(el, opt.value);

                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                return { ok: true, kind: 'select', picked: (opt.textContent || '').trim() };
              }

              // 普通输入
              el.scrollIntoView({ block: 'center', inline: 'center' });
              el.focus();

              const proto = tag === 'textarea'
                ? HTMLTextAreaElement.prototype
                : HTMLInputElement.prototype;
              const desc = Object.getOwnPropertyDescriptor(proto, 'value');

              const next = {{JsonSerializer.Serialize(value)}};
              if (desc && desc.set) desc.set.call(el, next);
              else el.value = next;

              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              el.dispatchEvent(new Event('blur', { bubbles: true }));

              return { ok: true, kind: 'text', length: next.length };
            })()
            """, ct);

        return InterpretFillResult(result, uid);
    }

    /// <summary>把 fill 的脚本结果翻成一句给模型看的话。</summary>
    private static string InterpretFillResult(JsonElement result, string uid)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("填写失败：页面脚本没有返回预期结果。");

        if (result.TryGetProperty("error", out var err))
        {
            switch (err.GetString())
            {
                case "notfound":
                    throw new InvalidOperationException(
                        $"页面上找不到 uid={uid}。页面可能已经变了，请重新 web_snapshot。");

                case "hidden":
                    throw new InvalidOperationException(
                        $"uid={uid} 当前不可见，无法填写。请先让它在屏幕上出现，或重新 web_snapshot。");

                case "nooption":
                    var options = result.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array
                        ? string.Join("、", EnumerateStrings(arr))
                        : "";
                    throw new InvalidOperationException(
                        $"下拉框里没有这个选项。现有选项：{options}");

                default:
                    throw new InvalidOperationException($"填写失败：{err.GetString()}");
            }
        }

        var kind = result.TryGetProperty("kind", out var k) ? k.GetString() : "text";

        return kind switch
        {
            "checkbox" or "radio" => $"已设置 uid={uid} 的勾选状态。",
            "select" => $"已选择 uid={uid} 的选项" +
                        (result.TryGetProperty("picked", out var p) ? $"「{p.GetString()}」" : "") + "。",
            _ => $"已填写 uid={uid}。",
        };
    }

    /// <summary>
    /// 往当前聚焦的元素里逐字输入。
    /// <b>与 fill 的区别</b>：这会触发每一次 keydown/keypress/input，
    /// 所以能激发自动补全、搜索建议这类逻辑；代价是慢。
    /// </summary>
    public static async Task<string> TypeTextAsync(
        CdpClient cdp, string text, string? submitKey, CancellationToken ct)
    {
        // 一次性插入：走输入法路径，中文与 emoji 也正确
        await cdp.InsertTextAsync(text, ct);

        if (!string.IsNullOrWhiteSpace(submitKey))
        {
            await cdp.PressKeyAsync(submitKey, ct);
            await Task.Delay(150, ct);
            return await DescribeAfterActionAsync(cdp, ct, $"已输入 {text.Length} 个字符并按了 {submitKey}");
        }

        return $"已输入 {text.Length} 个字符。";
    }

    /// <summary>按一个组合键。</summary>
    public static async Task<string> PressKeyAsync(
        CdpClient cdp, string keySpec, CancellationToken ct)
    {
        await cdp.PressKeyAsync(keySpec, ct);
        await Task.Delay(120, ct);
        return await DescribeAfterActionAsync(cdp, ct, $"已按键 {keySpec}");
    }

    /// <summary>一次填多个字段，减少轮次。</summary>
    public static async Task<string> FillFormAsync(
        CdpClient cdp, IReadOnlyList<(string Uid, string Value)> fields, CancellationToken ct)
    {
        var lines = new List<string>();
        var failed = 0;

        foreach (var (uid, value) in fields)
        {
            try
            {
                lines.Add(await FillAsync(cdp, uid, value, ct));
            }
            catch (InvalidOperationException ex)
            {
                // 单个字段失败不该中断整张表单：如实报出来，其余继续填
                failed++;
                lines.Add($"uid={uid} 失败：{ex.Message}");
            }
        }

        if (failed > 0)
            lines.Add($"（{fields.Count} 个字段中 {failed} 个失败）");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 动作之后回报「现在发生了什么」。
    /// 只回一句「已点击」信息量太低 —— 模型不知道点完该等还是该换路。
    /// </summary>
    private static async Task<string> DescribeAfterActionAsync(
        CdpClient cdp, CancellationToken ct, string action)
    {
        var url = await SafeEvaluateStringAsync(cdp, "location.href || ''", ct);
        var dialog = await SafeEvaluateStringAsync(cdp,
            "(() => { const d = window.__hai && window.__hai.dialog; return d || ''; })()", ct);

        var extra = "";
        if (!string.IsNullOrEmpty(dialog))
            extra = $"\n注意：页面上有一个 {dialog} 对话框在等你处理（用 web_handle_dialog）。";

        return $"{action}。当前地址：{url}{extra}";
    }

    private static async Task<string> SafeEvaluateStringAsync(
        CdpClient cdp, string expression, CancellationToken ct)
    {
        try
        {
            var r = await cdp.EvaluateAsync(expression, ct);
            return r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        }
        catch
        {
            // 动作已经完成了，读状态失败不该让它变成一次失败
            return "";
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                yield return item.GetString() ?? "";
    }

    /// <summary>
    /// 把坐标点击（实验性）实现掉：有些元素没有 uid（canvas、自定义控件），
    /// 只能按坐标点。参数是相对视口左上角的像素。
    /// </summary>
    public static async Task<string> ClickAtAsync(
        CdpClient cdp, double x, double y, bool doubleClick, CancellationToken ct)
    {
        await cdp.ClickAtAsync(x, y, doubleClick, ct);
        return await DescribeAfterActionAsync(cdp, ct, $"已在坐标 ({x}, {y}) 点击");
    }
}
