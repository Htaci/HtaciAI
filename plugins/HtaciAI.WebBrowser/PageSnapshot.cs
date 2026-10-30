using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 页面快照：把当前页面抽象成「一串带 uid 的可操作元素」。
///
/// <b>为什么要有 uid 这一层</b>：模型没法直接点东西。让它自己写选择器，
/// 它会写 <c>div:nth-child(3) > button</c> 这种一改版就废的东西，而且经常写错。
/// 由我们扫一遍页面、给每个可交互元素编个号，模型只需说「点 uid=7」——
/// 正确率从「猜」变成「查表」。
///
/// <b>uid 存在哪里</b>：写进元素的 <c>data-hai-uid</c> 属性。这样反查是一次
/// <c>querySelector</c>，不需要维护一张可能失同步的映射表。
/// 页面导航后 DOM 重建，属性与编号一起消失，旧 uid 自然失效 —— 这正是我们想要的。
///
/// <b>为什么要截断</b>：一个电商首页的可交互元素轻松上千个，全塞进上下文
/// 会直接吃光预算，模型反而看不到重点。所以限制行数，并在截断时说明怎么办。
/// </summary>
internal static class PageSnapshot
{
    /// <summary>快照最多输出多少行。超出就截断 —— 上下文比完整性重要。</summary>
    public const int MaxLines = 200;

    /// <summary>
    /// 注入到页面的辅助脚本。<b>幂等</b>：用 <c>window.__hai</c> 挂载，
    /// 重复注入只会覆盖自己的命名空间，不会破坏页面。
    /// </summary>
    private const string HelperScript = """
        (() => {
          if (window.__hai) return 'ok';

          const INTERACTIVE = [
            'a[href]', 'button', 'input:not([type=hidden])', 'select', 'textarea',
            'summary', '[role=button]', '[role=link]', '[role=checkbox]', '[role=radio]',
            '[role=menuitem]', '[role=tab]', '[role=switch]', '[role=option]',
            '[contenteditable=true]', '[onclick]', '[tabindex]:not([tabindex="-1"])',
          ].join(',');

          const hai = {
            // 编号从 1 开始并跨多次 snapshot 递增：同一个元素在两次快照里
            // 拿到同一个 uid，模型可以拿上一次的 uid 接着用。
            next: 1,
            assigned: new WeakMap(),

            visible(el) {
              if (el.getClientRects().length === 0) return false;
              const st = getComputedStyle(el);
              if (st.visibility === 'hidden' || st.display === 'none') return false;
              if (parseFloat(st.opacity || '1') === 0) return false;
              return true;
            },

            label(el) {
              const tag = el.tagName.toLowerCase();

              // 可输入控件优先报「用户能填什么」，而不是它的内部结构
              if (tag === 'input') {
                const type = (el.getAttribute('type') || 'text').toLowerCase();
                const ph = el.getAttribute('placeholder');
                const nm = el.getAttribute('name');
                const v = el.value || '';
                let s = `input[${type}]`;
                if (ph) s += ` placeholder="${ph}"`;
                else if (nm) s += ` name="${nm}"`;
                if (v) s += ` value="${v.slice(0, 40)}"`;
                return s;
              }
              if (tag === 'select') {
                const cur = el.selectedOptions && el.selectedOptions[0];
                return `select value="${cur ? cur.textContent.trim().slice(0, 40) : ''}"`;
              }
              if (tag === 'textarea') {
                const ph = el.getAttribute('placeholder');
                return 'textarea' + (ph ? ` placeholder="${ph}"` : '');
              }

              // 其余元素用可见文本
              let text = (el.innerText || el.textContent || '').trim();
              // 只取第一行：多行文本塞进快照会立刻吃掉预算
              text = text.split('\n')[0].slice(0, 80);

              if (!text) {
                const aria = el.getAttribute('aria-label');
                if (aria) text = aria.slice(0, 80);
              }
              if (!text) {
                const title = el.getAttribute('title');
                if (title) text = title.slice(0, 80);
              }

              let desc = tag;
              if (tag === 'a' && el.getAttribute('href')) {
                const href = el.getAttribute('href');
                if (href && !href.startsWith('javascript')) desc += ` href="${href.slice(0, 60)}"`;
              }
              return text ? `${desc} "${text}"` : desc;
            },

            uidOf(el) {
              let uid = hai.assigned.get(el);
              if (uid) return uid;
              uid = el.getAttribute('data-hai-uid');
              if (uid) { hai.assigned.set(el, uid); return uid; }
              uid = String(hai.next++);
              hai.assigned.set(el, uid);
              el.setAttribute('data-hai-uid', uid);
              return uid;
            },

            // 抓快照：返回 [{uid, label, depth}] 的数组
            snapshot(maxNodes) {
              const out = [];
              const seen = new Set();

              const collect = (root) => {
                const nodes = root.querySelectorAll(INTERACTIVE);
                for (const el of nodes) {
                  if (out.length >= maxNodes) return;
                  if (seen.has(el)) continue;
                  if (!hai.visible(el)) continue;
                  seen.add(el);
                  out.push({
                    uid: hai.uidOf(el),
                    label: hai.label(el),
                    depth: hai.depthOf(el),
                  });
                }
              };

              collect(document);
              return out;
            },

            // 用祖先链算缩进层级，让快照能体现嵌套关系（菜单项之类）
            depthOf(el) {
              let depth = 0;
              let p = el.parentElement;
              while (p && depth < 4) {
                if (p.querySelector(INTERACTIVE) === el || p.matches(INTERACTIVE)) depth++;
                p = p.parentElement;
              }
              return depth;
            },

            // 按 uid 找元素；找不到说明页面变了
            byUid(uid) {
              const el = document.querySelector(`[data-hai-uid="${CSS.escape(uid)}"]`);
              return el || null;
            },

            // 元素中心点（视口坐标），用于发真实鼠标事件
            centerOf(el) {
              const r = el.getBoundingClientRect();
              if (r.width === 0 && r.height === 0) return null;
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            },
          };

          window.__hai = hai;
          return 'ok';
        })()
        """;

    /// <summary>确保辅助脚本已注入。每次调用都注入是安全的（幂等），省掉「注入过没」的状态。</summary>
    public static async Task EnsureAsync(CdpClient cdp, CancellationToken ct)
    {
        var r = await cdp.EvaluateAsync(HelperScript, ct);

        if (r.ValueKind != JsonValueKind.String || r.GetString() != "ok")
            throw new InvalidOperationException("无法在页面里初始化元素定位脚本（页面可能禁止了脚本执行）。");
    }

    /// <summary>
    /// 抓一次快照，返回给模型看的文本。
    /// </summary>
    public static async Task<SnapshotResult> CaptureAsync(CdpClient cdp, CancellationToken ct)
    {
        await EnsureAsync(cdp, ct);

        var nodes = await cdp.EvaluateAsync(
            $"(window.__hai.snapshot({MaxLines + 1}))", ct);

        if (nodes.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("快照脚本没有返回预期结果。");

        var builder = new StringBuilder();
        var count = 0;
        var truncated = false;

        foreach (var node in nodes.EnumerateArray())
        {
            if (count >= MaxLines)
            {
                truncated = true;
                break;
            }

            var uid = node.TryGetProperty("uid", out var u) ? u.GetString() : "?";
            var label = node.TryGetProperty("label", out var l) ? l.GetString() : "";
            var depth = node.TryGetProperty("depth", out var d) ? d.GetInt32() : 0;

            // 缩进最多 4 级：再深就不是结构信息，而是噪音
            builder.Append(new string(' ', Math.Min(depth, 4) * 2));
            builder.Append('[').Append(uid).Append("] ").AppendLine(label ?? "");
            count++;
        }

        // 顺便把标题与地址带上：模型据此判断「我在哪一页」
        var title = await cdp.EvaluateAsync("document.title || ''", ct);
        var url = await cdp.EvaluateAsync("location.href || ''", ct);

        return new SnapshotResult(
            Text: builder.ToString(),
            Count: count,
            Truncated: truncated,
            Title: title.ValueKind == JsonValueKind.String ? title.GetString() ?? "" : "",
            Url: url.ValueKind == JsonValueKind.String ? url.GetString() ?? "" : "");
    }

    /// <summary>
    /// 按 uid 找到元素并返回它的中心坐标。
    /// 找不到时抛出的异常文案要能指导模型 —— 它下一步该重新 snapshot，而不是重试同一个 uid。
    /// </summary>
    public static async Task<(double X, double Y)> RequireCenterAsync(
        CdpClient cdp, string uid, CancellationToken ct)
    {
        await EnsureAsync(cdp, ct);

        var result = await cdp.EvaluateAsync($$"""
            (() => {
              const el = window.__hai.byUid({{JsonSerializer.Serialize(uid)}});
              if (!el) return { error: 'notfound' };
              if (!window.__hai.visible(el)) return { error: 'hidden' };
              // 元素可能被别的浮层挡住：先滚进视口再算坐标
              el.scrollIntoView({ block: 'center', inline: 'center' });
              const c = window.__hai.centerOf(el);
              if (!c) return { error: 'nocenter' };
              return { x: c.x, y: c.y };
            })()
            """, ct);

        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("定位元素失败。");

        if (result.TryGetProperty("error", out var err))
        {
            var kind = err.GetString();
            throw kind switch
            {
                "notfound" => new InvalidOperationException(
                    $"页面上找不到 uid={uid} 的元素。页面可能已经变了（导航、局部刷新、弹窗关闭），" +
                    "请重新调用 web_snapshot 拿新的 uid。"),
                "hidden" => new InvalidOperationException(
                    $"uid={uid} 的元素当前不可见（可能在折叠区域或被遮挡）。" +
                    "可以先点开它所在的菜单/标签，或重新 web_snapshot 看当前可见的元素。"),
                _ => new InvalidOperationException(
                    $"uid={uid} 的元素没有可点击区域（尺寸为 0），无法定位。"),
            };
        }

        return (
            result.GetProperty("x").GetDouble(),
            result.GetProperty("y").GetDouble());
    }
}

/// <summary>一次快照的结果。</summary>
/// <param name="Text">给模型看的层级文本，每行形如 <c>[3] button "登录"</c>。</param>
/// <param name="Count">实际输出的行数。</param>
/// <param name="Truncated">是否因为超过上限被截断。</param>
/// <param name="Title">页面标题。</param>
/// <param name="Url">当前地址。</param>
internal sealed record SnapshotResult(
    string Text, int Count, bool Truncated, string Title, string Url);
