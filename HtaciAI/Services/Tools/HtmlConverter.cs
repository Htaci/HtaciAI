using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 极简 HTML → Markdown / 纯文本转换器（无外部依赖，不引 NuGet）。
///
/// 面向「把网页正文喂给模型」这一用途，覆盖常见语义标签：
/// 标题、段落、换行、分隔线、粗体/斜体/删除线、行内代码、代码块、
/// 有序/无序列表（含嵌套）、引用块、链接、图片、表格。
///
/// 设计取向：遇到不认识的标签一律当「透明容器」处理（只保留内部文本），
/// 因此任何输入都能降级出可读结果，不会因为标签不认识就丢内容或抛异常。
/// </summary>
public static class HtmlConverter
{
    private static readonly Regex TagRegex = new(
        @"<(?<close>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*)>",
        RegexOptions.Compiled);

    /// <summary>引用块的占位哨兵：收尾时据此把内部各行统一加上 "> " 前缀。正文里不会出现的控制字符。</summary>
    private const char QuoteSentinel = '\u0001';

    /// <summary>把 HTML 转成 Markdown。</summary>
    public static string ToMarkdown(string html)
    {
        var src = Preprocess(html);

        var sb = new StringBuilder();
        var listStack = new Stack<string>();   // 打开中的列表：ul / ol
        var counters = new Stack<int>();       // 有序列表当前序号
        var hrefStack = new Stack<string?>();  // 打开中的 <a> 的 href

        var inPre = false;
        var tableRowIndex = 0;
        var tableCellsInRow = 0;
        var i = 0;

        foreach (Match m in TagRegex.Matches(src))
        {
            if (m.Index > i)
                AppendText(sb, src[i..m.Index], inPre);
            i = m.Index + m.Length;

            var closing = m.Groups["close"].Value.Length > 0;
            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var attrs = m.Groups["attrs"].Value;

            // 代码块内部除 </pre> 外的标签一律忽略，保证代码原样保留
            if (inPre && tag != "pre") continue;

            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    if (closing) EnsureBlankLine(sb);
                    else
                    {
                        EnsureBlankLine(sb);
                        sb.Append('#', tag[1] - '0').Append(' ');
                    }
                    break;

                // 块级容器：前后留空行。div/section 之类即使语义不精确，也能把内容切开
                case "p": case "div": case "section": case "article": case "aside":
                case "header": case "footer": case "main": case "nav":
                case "figure": case "figcaption": case "address":
                    EnsureBlankLine(sb);
                    break;

                case "br":
                    sb.Append('\n');
                    break;

                case "hr":
                    EnsureBlankLine(sb);
                    sb.Append("---");
                    EnsureBlankLine(sb);
                    break;

                case "strong": case "b":
                    sb.Append("**");
                    break;

                case "em": case "i": case "cite": case "var":
                    sb.Append('*');
                    break;

                case "del": case "s": case "strike":
                    sb.Append("~~");
                    break;

                case "code":
                    sb.Append('`');
                    break;

                case "pre":
                    if (!closing)
                    {
                        EnsureBlankLine(sb);
                        sb.Append("```\n");
                        inPre = true;
                    }
                    else
                    {
                        inPre = false;
                        sb.Append("\n```");
                        EnsureBlankLine(sb);
                    }
                    break;

                case "blockquote":
                    if (!closing) sb.Append(QuoteSentinel);
                    else
                    {
                        // 用哨兵字符而非记录 sb.Length：块级元素收尾时可能裁剪掉缓冲区尾部的空白，
                        // 裸下标会因此错位（曾导致「注意」的「注」被留在引用符之外）。
                        var idx = -1;
                        for (var k = sb.Length - 1; k >= 0; k--)
                            if (sb[k] == QuoteSentinel) { idx = k; break; }

                        if (idx >= 0)
                        {
                            var inner = sb.ToString(idx + 1, sb.Length - idx - 1).Trim('\n');
                            sb.Length = idx;
                            foreach (var line in inner.Split('\n'))
                            {
                                var t = line.TrimEnd();
                                sb.Append(t.Length == 0 ? ">" : "> " + t).Append('\n');
                            }
                            EnsureBlankLine(sb);
                        }
                    }
                    break;

                case "ul": case "ol":
                    if (closing)
                    {
                        if (listStack.Count > 0) { listStack.Pop(); counters.Pop(); }
                        if (listStack.Count == 0) EnsureNewline(sb);
                    }
                    else
                    {
                        if (listStack.Count == 0) EnsureBlankLine(sb);
                        else EnsureNewline(sb);
                        listStack.Push(tag);
                        counters.Push(1);
                    }
                    break;

                case "li":
                    if (!closing)
                    {
                        EnsureNewline(sb);
                        var depth = Math.Max(0, listStack.Count - 1);
                        sb.Append(' ', depth * 2);
                        if (listStack.Count > 0 && listStack.Peek() == "ol")
                        {
                            var n = counters.Pop();
                            counters.Push(n + 1);
                            sb.Append(n).Append(". ");
                        }
                        else sb.Append("- ");
                    }
                    break;

                case "a":
                    if (closing)
                    {
                        var href = hrefStack.Count > 0 ? hrefStack.Pop() : null;
                        sb.Append(']');
                        if (!string.IsNullOrWhiteSpace(href)) sb.Append('(').Append(href).Append(')');
                    }
                    else
                    {
                        hrefStack.Push(ExtractAttr(attrs, "href"));
                        sb.Append('[');
                    }
                    break;

                case "img":
                    if (!closing)
                    {
                        var srcAttr = ExtractAttr(attrs, "src");
                        if (!string.IsNullOrWhiteSpace(srcAttr))
                            sb.Append($"![{ExtractAttr(attrs, "alt") ?? ""}]({srcAttr})");
                    }
                    break;

                case "table":
                    if (!closing) { tableRowIndex = 0; EnsureBlankLine(sb); }
                    else EnsureBlankLine(sb);
                    break;

                case "tr":
                    if (!closing)
                    {
                        EnsureNewline(sb);
                        tableCellsInRow = 0;
                    }
                    else
                    {
                        sb.Append('|').Append('\n');
                        // 首行作为表头，补一行 markdown 分隔
                        if (tableRowIndex == 0)
                        {
                            sb.Append('|');
                            for (var c = 0; c < tableCellsInRow; c++) sb.Append(" --- |");
                            sb.Append('\n');
                        }
                        tableRowIndex++;
                    }
                    break;

                case "td": case "th":
                    if (!closing) { sb.Append("| "); tableCellsInRow++; }
                    else sb.Append(' ');
                    break;

                default:
                    // 未知标签：透明处理，仅保留内部文本
                    break;
            }
        }

        if (i < src.Length)
            AppendText(sb, src[i..], inPre);

        return Cleanup(sb.ToString());
    }

    /// <summary>把 HTML 转成纯文本（保留段落换行，去掉全部标记）。</summary>
    public static string ToPlainText(string html)
    {
        var s = Preprocess(html);
        s = Regex.Replace(s,
            @"<(br|/p|/div|/li|/tr|/h[1-6]|/blockquote|/pre|/ul|/ol|/table)\s*/?>",
            "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]\s*>", "\t", RegexOptions.IgnoreCase); // 表格单元格用制表符分隔，否则文字会粘连
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[^\S\n]+", " ");      // 折叠行内空白，保留换行
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"[ \t]+\n", "\n");     // 去掉行尾多余制表符
        s = Regex.Replace(s, @"\n+(?=- )", "\n");    // 列表项之间不空行
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    // ---- 辅助 ----

    /// <summary>去掉注释与整段无正文价值的标签（脚本 / 样式 / 头部等）。</summary>
    private static string Preprocess(string html)
    {
        var s = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        s = Regex.Replace(s,
            @"<(script|style|noscript|template|svg|iframe|head)\b[^>]*>.*?</\1\s*>",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return s;
    }

    private static void AppendText(StringBuilder sb, string text, bool inPre)
    {
        if (text.Length == 0) return;
        var decoded = WebUtility.HtmlDecode(text);

        // pre 内原样保留
        if (inPre) { sb.Append(decoded); return; }

        // HTML 中连续空白等价于一个空格
        var collapsed = Regex.Replace(decoded, @"\s+", " ");

        // 块与块之间的源码缩进/换行只产出孤独的空格，既污染输出又会让后续块级元素的
        // 起始下标被 EnsureBlankLine 的裁剪打乱，直接丢弃
        if (collapsed == " " && (sb.Length == 0 || sb[^1] == '\n')) return;

        sb.Append(collapsed);
    }

    /// <summary>确保缓冲区以空行（两个换行）结尾；空缓冲区不做处理。</summary>
    private static void EnsureBlankLine(StringBuilder sb)
    {
        TrimTrailingSpaces(sb);
        if (sb.Length == 0) return;

        var newlines = 0;
        for (var k = sb.Length - 1; k >= 0 && sb[k] == '\n'; k--) newlines++;

        if (newlines == 0) sb.Append("\n\n");
        else if (newlines == 1) sb.Append('\n');
    }

    /// <summary>确保缓冲区以换行结尾（至少要另起一行）。</summary>
    private static void EnsureNewline(StringBuilder sb)
    {
        TrimTrailingSpaces(sb);
        if (sb.Length == 0) return;
        if (sb[^1] != '\n') sb.Append('\n');
    }

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t')) sb.Length--;
    }

    private static string Cleanup(string s)
    {
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>从标签属性串里取出某个属性值（支持双引号 / 单引号 / 无引号）。</summary>
    private static string? ExtractAttr(string attrs, string name)
    {
        var m = Regex.Match(attrs,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }
}
