using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.KnowledgeBase;

/// <summary>
/// 向量库：导入文件 → 切块 → 嵌入 → 检索。
///
/// <b>切块按 Markdown 标题，而不是按固定字数硬切</b>。固定长度切出来的块经常从半句话开始，
/// 单独拿出来语义是断的；按标题切出的块本身就是「一节」的完整内容。
/// 每个块前面会拼上标题链（如「部署 &gt; 反向代理 &gt; Nginx」），因为检索时只看到块正文，
/// 没有标题链它就说不清自己在说什么。
///
/// <b>没有嵌入模型时仍可用</b>：检索退化为关键词匹配（见 <see cref="SearchAsync"/>），
/// 索引本身会直接失败并说明原因。这不是降级策略的补充，而是刻意的分层 ——
/// 用户导入的文件应该始终能被找到，不该因为少配一个模型就完全不可用。
/// </summary>
public static class KnowledgeBaseIndexer
{
    /// <summary>单块字符数上限。中文按字符数算，比 token 估算更好控制、也不用引依赖。</summary>
    private const int MaxChunkChars = 700;

    /// <summary>处理哪些扩展名。「能读成文本」是唯一标准，PDF/docx 需要额外库，先不做。</summary>
    private static readonly string[] SupportedExtensions =
        { ".md", ".markdown", ".txt", ".json", ".yaml", ".yml", ".ini", ".csv", ".log", ".xml" };

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ============================================================
    // 导入
    // ============================================================

    /// <summary>
    /// 把一个外部文件复制进知识库目录并登记。返回登记后的文档行。
    /// 只复制，不索引 —— 索引要花 API 额度，得由用户明确按下「建立索引」。
    /// </summary>
    public static async Task<KbDocument> ImportAsync(string libraryId, string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var dir = AppPaths.KnowledgeBaseFiles(libraryId);
        var target = Path.Combine(dir, fileName);

        // 同名文件不覆盖：序号往上加，避免「导入第二次把第一次的内容顶掉」
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 1;
        while (File.Exists(target))
            target = Path.Combine(dir, $"{stem} ({counter++}){ext}");

        File.Copy(sourcePath, target);

        var info = new FileInfo(target);
        var doc = new KbDocument
        {
            LibraryId = libraryId,
            Path = Path.GetFileName(target),
            Title = Path.GetFileNameWithoutExtension(target),
            Hash = await HashAsync(target),
            Size = info.Length,
            ModifiedAt = ToMillis(info.LastWriteTimeUtc),
            Status = "pending",
        };

        doc.Id = await KnowledgeBaseRepository.UpsertDocumentAsync(doc);
        return doc;
    }

    /// <summary>把一个库的导入目录里已有的文件登记为文档（用于用户直接把文件拷进目录之后）。</summary>
    public static async Task<int> ScanDirectoryAsync(string libraryId)
    {
        var dir = AppPaths.KnowledgeBaseFiles(libraryId);
        if (!Directory.Exists(dir)) return 0;

        var known = (await KnowledgeBaseRepository.GetDocumentsAsync(libraryId))
            .Select(d => d.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var file in Directory.EnumerateFiles(dir).Where(IsSupported))
        {
            var name = Path.GetFileName(file);
            if (known.Contains(name)) continue;

            var info = new FileInfo(file);
            await KnowledgeBaseRepository.UpsertDocumentAsync(new KbDocument
            {
                LibraryId = libraryId,
                Path = name,
                Title = Path.GetFileNameWithoutExtension(name),
                Hash = await HashAsync(file),
                Size = info.Length,
                ModifiedAt = ToMillis(info.LastWriteTimeUtc),
                Status = "pending",
            });
            added++;
        }
        return added;
    }

    // ============================================================
    // 索引
    // ============================================================

    /// <summary>单个文档的索引结果。</summary>
    public sealed record IndexResult(bool Ok, int Chunks, string? Error);

    /// <summary>
    /// 索引一个文档。已索引且文件未变（hash 相同）时直接跳过，不白花额度。
    /// </summary>
    public static async Task<IndexResult> IndexDocumentAsync(
        KbDocument doc, ModelDetails? embedModel, CancellationToken ct)
    {
        if (EmbeddingClient.CheckUsable(embedModel) is { } reason)
            return await FailAsync(doc, reason);

        var file = Path.Combine(AppPaths.KnowledgeBaseFiles(doc.LibraryId), doc.Path);
        if (!File.Exists(file))
            return await FailAsync(doc, $"文件不在了：{doc.Path}");

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, ct);
        }
        catch (Exception ex)
        {
            return await FailAsync(doc, $"读不了这个文件：{ex.Message}");
        }

        var hash = await HashAsync(file);
        if (hash == doc.Hash && doc.IndexedAt > 0 && doc.ChunkCount > 0)
            return new IndexResult(true, doc.ChunkCount, null);

        var chunks = Split(doc.Title, text);
        if (chunks.Count == 0)
            return await FailAsync(doc, "这个文件里没有可索引的正文。");

        List<float[]> vectors;
        try
        {
            vectors = await EmbeddingClient.EmbedBatchAsync(
                embedModel!, chunks.Select(c => c.Text).ToList(), ct);
        }
        catch (EmbeddingException ex)
        {
            return await FailAsync(doc, ex.Message);
        }
        catch (Exception ex)
        {
            return await FailAsync(doc, ex.Message);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = vectors[i];
            chunks[i].Dim = vectors[i].Length;
            chunks[i].ModelId = embedModel!.ModelName;
        }

        await KnowledgeBaseRepository.ReplaceChunksAsync(doc.Id, doc.LibraryId, chunks);

        doc.Hash = hash;
        doc.ChunkCount = chunks.Count;
        doc.IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        doc.Status = "ok";
        doc.Error = null;
        await KnowledgeBaseRepository.UpsertDocumentAsync(doc);

        return new IndexResult(true, chunks.Count, null);
    }

    private static async Task<IndexResult> FailAsync(KbDocument doc, string error)
    {
        doc.Status = "failed";
        doc.Error = error;
        await KnowledgeBaseRepository.UpsertDocumentAsync(doc);
        return new IndexResult(false, 0, error);
    }

    /// <summary>
    /// 切块。按 Markdown 标题分节；一节超过 <see cref="MaxChunkChars"/> 时再按空行切段，
    /// 段还是太长就硬切（宁可切断也不能让块大到把上下文挤满）。
    /// </summary>
    public static List<KbChunk> Split(string title, string text)
    {
        var chunks = new List<KbChunk>();
        var ord = 0;

        foreach (var section in SplitByHeading(text))
        {
            var prefix = section.Heading.Length > 0 ? $"{section.Heading}\n" : "";
            foreach (var body in SplitByLength(section.Body))
            {
                var trimmed = body.Trim();
                if (trimmed.Length < 2) continue;

                chunks.Add(new KbChunk
                {
                    Ord = ord++,
                    Heading = section.Heading,
                    Text = (prefix + trimmed).Trim(),
                });
            }
        }

        return chunks;
    }

    private sealed record Section(string Heading, string Body);

    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    private static List<Section> SplitByHeading(string text)
    {
        var sections = new List<Section>();
        var headingStack = new List<string>();
        var body = new StringBuilder();
        var currentHeading = "";

        void Flush()
        {
            var content = body.ToString().Trim();
            if (content.Length > 0) sections.Add(new Section(currentHeading, content));
            body.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = HeadingPattern.Match(line);
            if (match.Success)
            {
                Flush();

                var level = match.Groups[1].Value.Length;
                var title = match.Groups[2].Value.Trim();

                while (headingStack.Count >= level) headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add(title);
                currentHeading = string.Join(" > ", headingStack);
                continue;
            }

            body.AppendLine(line);
        }

        Flush();
        return sections;
    }

    /// <summary>按空行切段，逐段累加到接近上限为止；单段本身就超长时硬切。</summary>
    private static IEnumerable<string> SplitByLength(string body)
    {
        if (body.Length <= MaxChunkChars)
        {
            yield return body;
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var paragraph in body.Split("\n\n"))
        {
            if (paragraph.Length > MaxChunkChars)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                for (var i = 0; i < paragraph.Length; i += MaxChunkChars)
                    yield return paragraph.Substring(i, Math.Min(MaxChunkChars, paragraph.Length - i));
                continue;
            }

            if (buffer.Length + paragraph.Length + 2 > MaxChunkChars && buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0) buffer.Append("\n\n");
            buffer.Append(paragraph);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }

    // ============================================================
    // 检索
    // ============================================================

    /// <summary>
    /// 在一个库里检索。两条路并行：向量（有嵌入模型时）+ 关键词（笔记与切片正文）。
    /// 结果按「向量得分」与「关键词命中」合并去重 —— 纯向量对错误码、函数名这类查询很差，
    /// 而查资料时最常搜的恰恰是这类东西。
    /// </summary>
    public static async Task<List<KbSearchHit>> SearchAsync(
        string libraryId, string query, int topK, ModelDetails? embedModel, CancellationToken ct)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0 || string.IsNullOrEmpty(libraryId)) return new List<KbSearchHit>();

        var hits = new Dictionary<string, KbSearchHit>(StringComparer.Ordinal);

        // 1) 向量
        if (EmbeddingClient.CheckUsable(embedModel) is null)
        {
            try
            {
                var queryVector = await EmbeddingClient.EmbedAsync(embedModel!, query, ct);
                var chunks = await KnowledgeBaseRepository.GetChunksAsync(libraryId);

                foreach (var chunk in chunks)
                {
                    if (chunk.Embedding is not { Length: > 0 } vector) continue;
                    if (vector.Length != queryVector.Length) continue;   // 换过模型的老切片不参与

                    var score = Cosine(queryVector, vector);
                    if (score <= 0) continue;

                    var key = "d:" + chunk.DocumentId + "#" + chunk.Ord;
                    hits[key] = new KbSearchHit
                    {
                        LibraryId = libraryId,
                        Title = chunk.Title,
                        Heading = chunk.Heading,
                        Text = chunk.Text,
                        Source = chunk.Source,
                        Score = score,
                    };
                }
            }
            catch (Exception)
            {
                // 嵌入失败不该让整次检索失败：关键词那条路还能给出结果
            }
        }

        // 2) 关键词（切片）
        foreach (var chunk in await KnowledgeBaseRepository.SearchChunksByTextAsync(libraryId, query, topK * 4))
        {
            var key = "d:" + chunk.DocumentId + "#" + chunk.Ord;
            if (hits.TryGetValue(key, out var existing))
            {
                existing.MatchedByKeyword = true;
                continue;
            }

            hits[key] = new KbSearchHit
            {
                LibraryId = libraryId,
                Title = chunk.Title,
                Heading = chunk.Heading,
                Text = chunk.Text,
                Source = chunk.Source,
                Score = 0.5,          // 关键词命中给一个中等分，保证能排进结果
                MatchedByKeyword = true,
            };
        }

        // 3) 关键词（笔记）：笔记可能很长，截一段带标题的片段
        foreach (var note in await KnowledgeBaseRepository.SearchNotesByTextAsync(libraryId, query, topK))
        {
            var key = "n:" + note.Id;
            if (hits.ContainsKey(key)) continue;

            hits[key] = new KbSearchHit
            {
                LibraryId = libraryId,
                Title = note.Title,
                Heading = "",
                Text = Excerpt(note.Content, query),
                Source = note.Id,
                Score = 0.5,
                IsNote = true,
                MatchedByKeyword = true,
            };
        }

        return hits.Values
            .OrderByDescending(h => h.MatchedByKeyword)
            .ThenByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>从长正文里取命中词附近的一段，避免把整篇笔记塞进结果。</summary>
    private static string Excerpt(string content, string query, int radius = 200)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (content.Length <= radius * 2) return content;

        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return content[..(radius * 2)] + "…";

        var start = Math.Max(0, index - radius);
        var length = Math.Min(content.Length - start, radius * 2);
        var text = content.Substring(start, length);

        if (start > 0) text = "…" + text;
        if (start + length < content.Length) text += "…";
        return text;
    }

    // ============================================================
    // 工具
    // ============================================================

    public static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static long ToMillis(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
