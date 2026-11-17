using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HtaciAI.Services.KnowledgeBase;

/// <summary>
/// Qwen3 的分词器：GPT-2 风格的 byte-level BPE，语料是模型目录里的
/// <c>vocab.json</c> + <c>merges.txt</c>。
///
/// <b>为什么不解析 tokenizer.json</b>：那份 11MB 的文件里最有用的是同一套 vocab 与 merges，
/// 而它的结构（normalizer / pre_tokenizer / post_processor / decoder 四个可组合层）要完整实现
/// 才不会漏语义。这里只需要「文本 → id」，用这两份小文件就够，且它们的格式二十年没变过。
///
/// <b>预处理正则与 Qwen 官方逐字一致</b>：差别不在「能不能分词」，而在「数字与标点怎么切」。
/// 官方是 <c>\p{N}</c>（一位数字一个 token），GPT-2 是 <c> ?\p{N}+</c>（整串数字一个 token）。
/// 用错了不会报错，只会让 <c>886</c> 与 <c>1234</c> 落到不同的向量空间里 —— 检索时代码与数字最容易出错，
/// 所以这里照官方抄。
/// </summary>
public sealed class QwenTokenizer
{
    /// <summary>
    /// Qwen2/Qwen3 的预切分。逐字抄自官方 <c>tokenizer.py</c>。
    /// <c>(?i:'s|'t|…)</c> 先吃掉英文缩写；<c>[^\r\n\p{L}\p{N}]?\p{L}+</c> 是「可选的前导符 + 字母串」，
    /// 让 <c> word</c> 连空格一起成为一个 BPE 单元；<c>\p{N}</c> 单数字；
    /// 后面三段处理标点与空白（含行尾空白不吞下一行的规则）。
    /// </summary>
    private static readonly Regex PreTokenize = new(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>单个 token 的最长字符数上限（BPE 合并时的保护，正常不会碰到）。</summary>
    private const int MaxTokenLength = 512;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string Left, string Right), int> _ranks;

    /// <summary>
    /// 分词结果缓存。文档切片里重复的短语极多（标题链、代码关键字），
    /// 缓存一层能让整库索引的时间明显下降；上限只是防止极端长文本把内存吃满。
    /// </summary>
    private readonly Dictionary<string, int[]> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public QwenTokenizer(string directory)
    {
        _vocab = LoadVocab(Path.Combine(directory, "vocab.json"));
        _ranks = LoadMerges(Path.Combine(directory, "merges.txt"));

        // 特殊 token 不在 vocab.json 里（它们在 added_tokens.json）。缺了它们，
        // 正文里出现 <|endoftext|> 这类字面量会被拆成奇怪的碎片；补进来成本极低。
        var added = Path.Combine(directory, "added_tokens.json");
        if (File.Exists(added))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(added));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var id))
                        _vocab.TryAdd(prop.Name, id);
                }
            }
            catch (JsonException)
            {
                // 缺这个文件只影响特殊 token，不该让整个分词器起不来
            }
        }
    }

    /// <summary>分词，返回 token id。不添加 BOS/EOS —— Qwen3-Embedding 官方用法也不加。</summary>
    public int[] Encode(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();

        if (maxTokens > 0 && text.Length > maxTokens * 8) text = text[..(maxTokens * 8)];

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;
        }

        var ids = new List<int>(text.Length / 2 + 8);
        foreach (Match match in PreTokenize.Matches(text))
        {
            var piece = match.Value;
            if (piece.Length == 0) continue;

            // 优先整段命中词表：特殊 token 与单字符词都是这么进 vocab 的
            if (_vocab.TryGetValue(piece, out var direct))
            {
                ids.Add(direct);
            }
            else
            {
                var symbols = ToByteLevelSymbols(piece);
                foreach (var symbol in Bpe(symbols))
                {
                    if (_vocab.TryGetValue(symbol, out var id)) ids.Add(id);
                }
            }

            if (maxTokens > 0 && ids.Count >= maxTokens) break;
        }

        if (maxTokens > 0 && ids.Count > maxTokens) ids.RemoveRange(maxTokens, ids.Count - maxTokens);

        var result = ids.ToArray();
        lock (_cacheLock)
        {
            if (_cache.Count < 20000) _cache[text] = result;
        }
        return result;
    }

    // ---- byte-level ----

    /// <summary>
    /// 把一段文本按 UTF-8 字节摊开，再逐字节映射成可打印字符（GPT-2 的 bytes_to_unicode）。
    ///
    /// 为什么必须映射：BPE 是在「字节串」上做的，而词表是 JSON —— 里面只能放合法字符串。
    /// 这套映射把 256 个字节值一一映到 256 个不同的字符上，于是「任意字节序列」都能表示成字符串，
    /// 且不会与可打印 ASCII 冲突（空格变成 <c>Ġ</c> 就是这么来的）。
    /// </summary>
    private static List<string> ToByteLevelSymbols(string piece)
    {
        var bytes = Encoding.UTF8.GetBytes(piece);
        var symbols = new List<string>(bytes.Length);
        foreach (var b in bytes) symbols.Add(ByteToChar[b].ToString());
        return symbols;
    }

    private static readonly char[] ByteToChar = BuildByteToChar();

    private static char[] BuildByteToChar()
    {
        var map = new char[256];
        var used = new List<int>();

        for (var b = '!'; b <= '~'; b++) used.Add(b);
        for (var b = '\u00A1'; b <= '\u00AC'; b++) used.Add(b);
        for (var b = '\u00AE'; b <= '\u00FF'; b++) used.Add(b);

        var assigned = new bool[256];
        var index = 0;
        foreach (var b in used)
        {
            map[b] = (char)b;
            assigned[b] = true;
            index = Math.Max(index, b);
        }

        // 剩下的字节按顺序映到 U+0100 之后，保证与已用的 ASCII/Latin-1 不撞
        var next = 256;
        for (var b = 0; b < 256; b++)
        {
            if (assigned[b]) continue;
            map[b] = (char)next;
            next++;
        }

        return map;
    }

    /// <summary>标准 BPE：反复把「rank 最小的一对相邻符号」合并成一个，直到无可合并。</summary>
    private List<string> Bpe(List<string> symbols)
    {
        if (symbols.Count < 2) return symbols;

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestAt = -1;

            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (!_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank)) continue;
                if (rank >= bestRank) continue;
                bestRank = rank;
                bestAt = i;
            }

            if (bestAt < 0) break;

            var merged = symbols[bestAt] + symbols[bestAt + 1];
            if (merged.Length > MaxTokenLength) break;

            symbols[bestAt] = merged;
            symbols.RemoveAt(bestAt + 1);
        }

        return symbols;
    }

    // ---- 文件 ----

    private static Dictionary<string, int> LoadVocab(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到分词器词表：{path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var vocab = new Dictionary<string, int>(doc.RootElement.GetPropertyCount(), StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var id))
                vocab[prop.Name] = id;
        }
        return vocab;
    }

    private static Dictionary<(string, string), int> LoadMerges(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到分词器合并表：{path}", path);

        var ranks = new Dictionary<(string, string), int>();
        using var reader = new StreamReader(path, Encoding.UTF8);

        var rank = 0;
        var first = true;
        while (reader.ReadLine() is { } line)
        {
            // 首行是 "#version: 0.2"，不是合并规则
            if (first)
            {
                first = false;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            }

            if (line.Length == 0) continue;

            var space = line.IndexOf(' ');
            if (space <= 0) continue;

            var left = line[..space];
            var right = line[(space + 1)..];
            ranks[(left, right)] = rank++;
        }
        return ranks;
    }
}
