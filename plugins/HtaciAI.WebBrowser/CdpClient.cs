using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 页面自动化用的 CDP 门面。
///
/// <b>为什么走 CDP 而不是全用 <c>Runtime.evaluate</c></b>：
/// <list type="bullet">
///   <item>点击、悬停要发<b>真实鼠标事件</b>（<c>Input.dispatchMouseEvent</c>）。
///         纯 JS 的 <c>el.click()</c> 绕过命中测试与 pointer 事件，
///         对 React/Vue 那类监听 pointerdown 的组件是无效的 —— 表现成「点了没反应」；</item>
///   <item>键盘输入同理：<c>Input.dispatchKeyEvent</c> 会触发 keydown/keypress/keyup，
///         而直接改 <c>value</c> 只触发一次 input；</item>
///   <item>但「找到元素」这件事用 JS 最方便，所以两者混着用：
///         JS 负责定位与观察，CDP 负责真正的事件注入。</item>
/// </list>
///
/// <b>uid 的寿命</b>：uid 由注入到页面里的计数器分配，并写回元素的
/// <c>data-hai-uid</c> 属性。页面导航后 JS 环境重建、计数器归零，
/// 旧 uid 自然失效 —— 调用方会收到「找不到元素，请重新 snapshot」，
/// 这正是我们要的行为（不能让模型拿着过期 uid 去点新页面上的东西）。
/// </summary>
internal sealed class CdpClient
{
    private readonly WebViewSurface _surface;

    public CdpClient(WebViewSurface surface) => _surface = surface;

    /// <summary>执行任意 CDP 方法，返回原始 JSON 结果。</summary>
    private async Task<JsonElement> CallAsync(string method, string? parameters, CancellationToken ct)
    {
        var core = await _surface.RequireCoreAsync(ct);

        // 空对象而不是空串：某些方法对 null 参数更挑剔，统一传 {} 最省事
        var json = await core
            .CallDevToolsProtocolMethodAsync(method, parameters ?? "{}")
            .WaitAsync(TimeSpan.FromSeconds(30), ct);

        // 空返回（如 Input.*）没有内容可解析
        if (string.IsNullOrWhiteSpace(json)) return default;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> SendAsync(string method, string? parameters, CancellationToken ct)
        => CallAsync(method, parameters, ct);

    // ============================================================
    // JS 求值
    // ============================================================

    /// <summary>
    /// 在页面主框架里跑一段 JS 并取回结果。
    /// <paramref name="expression"/> 必须是<b>返回 JSON 可序列化值</b>的表达式或 IIFE。
    /// </summary>
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,

            // 用户手势：某些页面（如需要点击激活的音频、剪贴板）会用这个标志判断
            userGesture = true,
        });

        var response = await CallAsync("Runtime.evaluate", parameters, ct);

        // 异常要显式检查：CDP 不会因为它失败就抛，而是放在 exceptionDetails 里
        if (response.TryGetProperty("exceptionDetails", out var ex))
        {
            var text = ex.TryGetProperty("text", out var t) ? t.GetString() : "脚本执行出错";
            var detail = ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d)
                ? d.GetString()
                : null;

            throw new InvalidOperationException(detail is { Length: > 0 } ? detail : text);
        }

        return response.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
            ? value.Clone()
            : default;
    }

    // ============================================================
    // 鼠标
    // ============================================================

    /// <summary>
    /// 在视口坐标发一次完整的鼠标点击（move → press → release）。
    /// 分三步而不是只发 pressed：很多组件靠 mouseover/mousemove 才展开或改变命中区域。
    /// </summary>
    public async Task ClickAtAsync(double x, double y, bool doubleClick, CancellationToken ct)
    {
        await MouseMoveAsync(x, y, ct);

        var press = JsonSerializer.Serialize(new
        {
            type = "mousePressed", x, y, button = "left", clickCount = 1,
        });
        await CallAsync("Input.dispatchMouseEvent", press, ct);

        var release = JsonSerializer.Serialize(new
        {
            type = "mouseReleased", x, y, button = "left", clickCount = 1,
        });
        await CallAsync("Input.dispatchMouseEvent", release, ct);

        if (!doubleClick) return;

        // 双击：第二次 pressed/released 带 clickCount=2，浏览器据此派生 dblclick
        var press2 = JsonSerializer.Serialize(new
        {
            type = "mousePressed", x, y, button = "left", clickCount = 2,
        });
        await CallAsync("Input.dispatchMouseEvent", press2, ct);

        var release2 = JsonSerializer.Serialize(new
        {
            type = "mouseReleased", x, y, button = "left", clickCount = 2,
        });
        await CallAsync("Input.dispatchMouseEvent", release2, ct);
    }

    /// <summary>把指针移到某点。悬停菜单、tooltip 靠这个触发。</summary>
    public async Task MouseMoveAsync(double x, double y, CancellationToken ct)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            type = "mouseMoved", x, y, button = "none",
        });
        await CallAsync("Input.dispatchMouseEvent", parameters, ct);
    }

    // ============================================================
    // 键盘
    // ============================================================

    /// <summary>
    /// 插入文本。用 <c>Input.insertText</c> 而不是逐个 keyDown：
    /// 它会走输入法路径、触发 beforeinput/input，对中文与 emoji 也正确，
    /// 而逐个发 keyDown 只能表达 ASCII。
    /// </summary>
    public Task InsertTextAsync(string text, CancellationToken ct)
        => CallAsync("Input.insertText", JsonSerializer.Serialize(new { text }), ct);

    /// <summary>
    /// 按一个组合键。写法如 <c>"Enter"</c>、<c>"Control+A"</c>、<c>"Shift+Tab"</c>。
    ///
    /// <b>必须分 keyDown / keyUp 两次发</b>，并且 modifiers 用位掩码：
    /// Alt=1、Control=2、Meta=4、Shift=8。少发 keyUp 会让页面以为键一直按着，
    /// 后续输入全部带上修饰键。
    /// </summary>
    public async Task PressKeyAsync(string keySpec, CancellationToken ct)
    {
        var (key, code, keyCode, modifiers) = KeyParser.Parse(keySpec);

        var down = JsonSerializer.Serialize(new
        {
            type = "keyDown", key, code, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers,
        });
        await CallAsync("Input.dispatchKeyEvent", down, ct);

        var up = JsonSerializer.Serialize(new
        {
            type = "keyUp", key, code, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode, modifiers,
        });
        await CallAsync("Input.dispatchKeyEvent", up, ct);
    }

    // ============================================================
    // 对话框 / 导航 / 等待
    // ============================================================

    /// <summary>处理 alert / confirm / prompt。返回是否真的处理了一个对话框。</summary>
    public Task<bool> HandleDialogAsync(bool accept, string? promptText, CancellationToken ct)
        => _surface.HandleDialogAsync(accept, promptText, ct);

    /// <summary>等页面出现指定文本。返回命中的那一条，超时返回 null。</summary>
    public async Task<string?> WaitForTextAsync(
        IReadOnlyList<string> texts, bool mustBeVisible, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = BuildVisibleTextCheck(texts, mustBeVisible);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hit = await EvaluateAsync(condition, ct);
            if (hit.ValueKind == JsonValueKind.String) return hit.GetString();

            await Task.Delay(200, ct);
        }

        return null;
    }

    /// <summary>在页面里找这些文字里哪一个先出现；都没有就返回 null。</summary>
    private static string BuildVisibleTextCheck(IReadOnlyList<string> texts, bool mustBeVisible)
    {
        var list = JsonSerializer.Serialize(texts);

        // 可见性判定：offsetParent 为 null 通常意味着 display:none 或脱离布局；
        // 但 position:fixed 的元素 offsetParent 也是 null，所以用 getClientRects 兜一道。
        var visibleCheck = mustBeVisible
            ? "if (!el.getClientRects().length) return false;"
            : "";

        return $$"""
            (() => {
              const targets = {{list}};
              const walker = document.createTreeWalker(document.body || document.documentElement,
                                                      NodeFilter.SHOW_ELEMENT);
              // 直接把整页可见文本拼起来找子串。比逐个元素比对更省事，
              // 也不会因为文字被拆进多个 <span> 而漏掉。
              let node = walker.currentNode;
              while (node) {
                const el = node;
                if (el.childElementCount === 0) {
                  const t = (el.textContent || '').trim();
                  if (t) {
                    for (const target of targets) {
                      if (t.includes(target)) {
                        {{visibleCheck}}
                        return target;
                      }
                    }
                  }
                }
                node = walker.nextNode();
              }
              return null;
            })()
            """;
    }
}
