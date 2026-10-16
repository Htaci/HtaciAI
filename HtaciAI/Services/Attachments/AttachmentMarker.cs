using System;
using System.Collections.Generic;
using System.Text;
using HtaciAI.Models;

namespace HtaciAI.Services.Attachments;

/// <summary>
/// 附件在正文里的规范形态：<c>[agent-file:///C:/Users/图片.png]</c>。
///
/// <para>
/// <b>为什么正文里存这个而不是裸路径。</b>裸路径有两个毛病：它和普通文字混在一起，
/// 渲染时只能靠「拿 metadata 里的路径去正文里找」来定位（附件一旦被删就再也认不出来），
/// 而且复制出来是一串路径、粘回去也变不成附件。改成带标记的形态之后，正文本身就是
/// 自解释的：<b>渲染、复制、粘贴三件事读的都是同一份文本</b>，不再需要额外的元数据去对应。
/// </para>
///
/// <para>
/// <b>为什么加 <c>agent-</c> 前缀而不是直接用 <c>file:///</c>。</b>用户在消息里贴一个
/// 浏览器地址栏复制的 <c>file:///C:/x.html</c> 是很自然的事，那个应当照常显示成网址。
/// 加前缀是第二层保护（第一层是方括号 —— 真 URL 不会自己带上方括号）。
/// </para>
///
/// <para>
/// <b>为什么用连字符不是下划线。</b><c>agent_file</c> 里的下划线不是合法的 URI scheme 字符
/// （RFC 3986 只允许字母、数字、<c>+</c>、<c>-</c>、<c>.</c>），那样的标记过不了
/// <c>Uri.TryCreate</c>，日后有人想用标准工具处理它会静默失败。
/// </para>
///
/// <para>
/// <b>格式是自定义的，不要改成用 <see cref="Uri"/> 解析。</b>见下面「最少转义」的说明。
/// </para>
/// </summary>
public static class AttachmentMarker
{
    /// <summary>标记前缀（含左方括号）。</summary>
    public const string Prefix = "[agent-file:///";

    /// <summary>标记结尾。</summary>
    public const char Suffix = ']';

    // ── 最少转义 ────────────────────────────────────────────────────────────
    //
    // 只转义「会破坏解析」的四个字符，其余一律原样保留：
    //   %  转义引导符本身，不转就没法区分「真百分号」和「转义序列」
    //   ]  标记的结束符 —— 文件名里出现它会提前截断标记
    //   [  不是严格必需，但留着它能让「从正文里扫标记」不必考虑嵌套
    //   控制字符（换行、制表等）——它们会破坏「标记必须在一行内」这个前提
    //
    // 刻意【不】做完整百分号编码：空格不转、中文不转。理由是这个格式是给人看的、
    // 要被复制粘贴的 —— `%E6%B5%8B%E8%AF%95.png` 那样的路径没人愿意面对，
    // 而用户手打一个带空格的路径时也不该被迫写成 %20。
    // 代价是它长得像 URI 但不是 URI（空格位置不合法），所以上面那句「不要用 Uri 解析」是硬约束。
    //
    // 实际影响很小：Windows 文件名禁止 ] 和 % 之外的大部分麻烦字符，macOS/Linux 上 ] 也罕见。

    /// <summary>把路径转义成标记内部的载荷。</summary>
    public static string Escape(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            switch (c)
            {
                case '%': sb.Append("%25"); break;
                case ']': sb.Append("%5D"); break;
                case '[': sb.Append("%5B"); break;
                default:
                    if (char.IsControl(c)) sb.Append('%').Append(((int)c).ToString("X2"));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>把标记内部的载荷还原成路径。无法识别的转义序列原样保留（宁可路径怪一点，也不要丢字符）。</summary>
    public static string Unescape(string payload)
    {
        if (payload.IndexOf('%') < 0) return payload;

        var sb = new StringBuilder(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != '%' || i + 2 >= payload.Length)
            {
                sb.Append(payload[i]);
                continue;
            }

            if (byte.TryParse(payload.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out var code))
            {
                sb.Append((char)code);
                i += 2;
                continue;
            }

            sb.Append(payload[i]);
        }

        return sb.ToString();
    }

    /// <summary>把路径包成标记。</summary>
    public static string Format(string path) => Prefix + Escape(path) + Suffix;

    /// <summary>把一个附件包成标记。</summary>
    public static string Format(ChatAttachment item) => Format(item.Path);

    // ── 扫描 ────────────────────────────────────────────────────────────────
    //
    // 用「从头扫一遍」而不是正则：标记的结束符是第一个未被转义的 `]`，
    // 而文件名里可能合法地出现 `[` `]`（已转义成 %5B/%5D）。手写扫描能把
    // 「转义状态」表达清楚，正则要表达同一件事会难读得多。

    /// <summary>正文里的一处标记。</summary>
    /// <param name="Start">起始下标（指向 <c>[</c>）。</param>
    /// <param name="Length">整段标记的长度（含首尾方括号）。</param>
    /// <param name="Path">还原出的绝对路径。</param>
    public readonly record struct Span(int Start, int Length, string Path)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// 扫描正文里所有标记。<b>不校验文件是否存在</b> —— 那是渲染层的事
    /// （文件可能被删或挪走，那种情况该显示成「文件缺失」而不是当作普通文本）。
    /// </summary>
    public static List<Span> Scan(string? text)
    {
        var found = new List<Span>();
        if (string.IsNullOrEmpty(text)) return found;

        var searchFrom = 0;
        while (true)
        {
            var start = text.IndexOf(Prefix, searchFrom, StringComparison.Ordinal);
            if (start < 0) break;

            var payloadStart = start + Prefix.Length;
            var end = -1;
            var nestedAt = -1;

            for (var i = payloadStart; i < text.Length; i++)
            {
                var c = text[i];

                // 换行说明这个标记没闭合（路径不可能跨行），就此放弃这一处。
                if (c is '\n' or '\r') break;

                if (c == '%')
                {
                    i += 2;   // 跳过转义序列的两个十六进制位，避免把 %5D 里的 D 当成内容
                    continue;
                }

                // 载荷里出现裸 `[` 说明这个标记是残缺的：合法路径里的 `[` 会被转义成 %5B。
                // 典型情形是用户把两个标记粘在一起时前面的那个被截断了 —— 此时必须放弃外层、
                // 从这个 `[` 重新开始，否则它会一路吞掉后面那个完整标记的 `]`，
                // 把两段文本拼成一个错误的路径。
                if (c == '[')
                {
                    nestedAt = i;
                    break;
                }

                if (c == Suffix)
                {
                    end = i;
                    break;
                }
            }

            if (nestedAt >= 0)
            {
                searchFrom = nestedAt;
                continue;
            }

            if (end < 0)
            {
                // 没闭合：从下一个位置继续找，不要把整段当标记。
                searchFrom = payloadStart;
                continue;
            }

            var path = Unescape(text[payloadStart..end]);
            if (path.Length > 0)
                found.Add(new Span(start, end - start + 1, path));

            searchFrom = end + 1;
        }

        return found;
    }

    /// <summary>正文里是否含有标记。</summary>
    public static bool Contains(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 去掉正文里所有标记，并把它们替换成各自的标签（如「图片」）。
    /// 用于标题生成 —— 附件打头的消息，标题不该是那串标记。
    /// </summary>
    /// <param name="separator">标记之间的连接符；默认空格。</param>
    public static string StripMarkers(string? text, Func<string, string>? labelFor = null, string separator = " ")
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!Contains(text)) return text;

        var sb = new StringBuilder();
        var cursor = 0;

        foreach (var span in Scan(text))
        {
            if (span.Start > cursor) sb.Append(text, cursor, span.Start - cursor);

            var label = labelFor?.Invoke(span.Path) ?? AttachmentStore.LabelFor(span.Path);
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && label.Length > 0) sb.Append(separator);
            sb.Append(label);

            cursor = span.End;
        }

        if (cursor < text.Length) sb.Append(text, cursor, text.Length - cursor);

        return sb.ToString();
    }

    /// <summary>由路径构造附件（含类型与标签）。文件不存在也照常返回 —— 见 <see cref="Scan"/> 的说明。</summary>
    public static ChatAttachment? ToAttachment(string path) => AttachmentStore.FromPath(path);
}
