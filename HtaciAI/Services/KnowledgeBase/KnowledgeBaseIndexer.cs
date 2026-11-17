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

    /// <summary>删除一个条目的结果。<see cref="FileRemoved"/> 表示知识库目录里那份副本也被删掉了。</summary>
    public sealed record RemoveResult(bool Ok, bool FileRemoved, string? Error);

    /// <summary>
    /// 删掉一个条目：登记、切片，以及（文件导入的那种）知识库目录里那份副本。
    ///
    /// <b>只碰副本</b>：导入时把文件复制进 data 的那一刻就和源文件断了关系，这里删的从来
    /// 不是用户自己的那一份。
    ///
    /// <b>副本不在了照删</b>：登记与切片各占一片数据，缺了文件不该让另外两片都删不掉 ——
    /// 文件没了正是它再也嵌不进去的状态。但若副本还在、只是删不掉（被别的程序占着），
    /// 就整个放弃：删掉登记之后下次扫描会把这个文件重新登记回来，变成「删了又复活」。
    /// </summary>
    public static async Task<RemoveResult> RemoveDocumentAsync(KbDocument doc)
    {
        var removed = false;

        if (doc.Kind != "text" && doc.Path.Length > 0)
        {
            var dir = Path.GetFullPath(AppPaths.KnowledgeBaseFiles(doc.LibraryId));
            var file = Path.GetFullPath(Path.Combine(dir, doc.Path));

            // 登记里的 path 是写进库的字符串，歪成 ../.. 删的就是 data 目录外面了。
            // 只认目录内的路径，别的一律按下落不明处理。
            if (!file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new RemoveResult(false, false, $"这条记录的路径不在知识库目录里：{doc.Path}");

            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                    removed = true;
                }
                catch (Exception ex)
                {
                    return new RemoveResult(false, false,
                        $"副本文件删不掉：{Path.GetFileName(file)}（{ex.Message}）。"
                        + "它可能正被别的程序占用，关掉之后再试。");
                }
            }
        }

        await KnowledgeBaseRepository.DeleteDocumentAsync(doc.Id);
        return new RemoveResult(true, removed, null);
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
    /// 把一段文字当成一篇文档写进向量库：切块 → 嵌入 → 落库。
    ///
    /// <b>为什么要有这条</b>：导入只认磁盘上的文件，而很多时候要存下来的东西
    /// 就是一席对话里的结论、一段需求、一条命令 —— 没有文件，也不该为此先建一个文件。
    ///
    /// <b>没有磁盘文件，所以 <see cref="KbDocument.Path"/> 是 <c>text:&lt;id&gt;</c> 这样的占位</b>：
    /// 它同时是「这条不是文件」的标记，删除时据此区分（文件的条目删了会被下次索引找回来）。
    /// </summary>
    public static async Task<IndexResult> AddTextAsync(
        string libraryId, string title, string text, ModelDetails? embedModel, CancellationToken ct)
    {
        if (EmbeddingClient.CheckUsable(embedModel) is { } reason)
            return new IndexResult(false, 0, reason);

        var chunks = Split(title, text);
        if (chunks.Count == 0)
            return new IndexResult(false, 0, "这段文字里没有可索引的内容。");

        var doc = new KbDocument
        {
            LibraryId = libraryId,
            Path = "text:" + KnowledgeBaseRepository.NewId(),
            Title = title,
            Kind = "text",
            Size = Encoding.UTF8.GetByteCount(text),
            ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hash = HashText(text),
            Status = "pending",
        };
        doc.Id = await KnowledgeBaseRepository.UpsertDocumentAsync(doc);

        List<float[]> vectors;
        try
        {
            vectors = await EmbeddingClient.EmbedBatchAsync(
                embedModel!, chunks.Select(c => c.Text).ToList(), ct);
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

        doc.ChunkCount = chunks.Count;
        doc.IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        doc.Status = "ok";
        doc.Error = null;
        await KnowledgeBaseRepository.UpsertDocumentAsync(doc);

        return new IndexResult(true, chunks.Count, null);
    }

    /// <summary>纯文本的 hash（正文比较用，不涉及文件）。</summary>
    public static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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
    ///
    /// <b>低于 <paramref name="minScore"/> 的向量命中直接丢掉，即使这样召回条数不够</b>：
    /// 召回最坏的形态不是「没找到」，而是「找到一条不相干的」—— 模型会拿它当事实。
    /// 余弦相似度在 -1~1，同语言同主题的句子通常落在 0.4 以上，0.35 是能滤掉噪声又不至于
    /// 把弱相关但有用的内容一起扔掉的位置。
    ///
    /// <b>关键词命中不吃这个阈值</b>：它不是相似度，是字面出现了。把它也卡阈值，
    /// 等于把「搜错误码」这条最可靠的路堵死。
    /// </summary>
    public static async Task<List<KbSearchHit>> SearchAsync(
        string libraryId, string query, int topK, ModelDetails? embedModel, CancellationToken ct,
        double minScore = 0.35)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0 || string.IsNullOrEmpty(libraryId)) return new List<KbSearchHit>();

        var hits = new Dictionary<string, KbSearchHit>(StringComparer.Ordinal);
        // 两路各自的排名，最终按 RRF 融合。不用「关键词固定给 0.5」那种假分数：
        // 那样一来它跟向量分不可比，二来加了阈值以后 0.5 会被当成真的相似度。
        var vectorRank = new Dictionary<string, int>(StringComparer.Ordinal);
        var keywordRank = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1) 向量
        if (EmbeddingClient.CheckUsable(embedModel) is null)
        {
            try
            {
                var queryVector = await EmbeddingClient.EmbedAsync(embedModel!, query, ct);
                var chunks = await KnowledgeBaseRepository.GetChunksAsync(libraryId);

                var scored = new List<(string Key, double Score, KbChunk Chunk)>();
                foreach (var chunk in chunks)
                {
                    if (chunk.Embedding is not { Length: > 0 } vector) continue;
                    if (vector.Length != queryVector.Length) continue;   // 换过模型的老切片不参与

                    var score = Cosine(queryVector, vector);
                    if (score < minScore) continue;

                    scored.Add(("d:" + chunk.DocumentId + "#" + chunk.Ord, score, chunk));
                }

                var index = 0;
                foreach (var item in scored.OrderByDescending(s => s.Score).Take(topK * 2))
                {
                    vectorRank[item.Key] = index++;
                    hits[item.Key] = new KbSearchHit
                    {
                        LibraryId = libraryId,
                        Title = item.Chunk.Title,
                        Heading = item.Chunk.Heading,
                        Text = item.Chunk.Text,
                        Source = item.Chunk.Source,
                        Score = item.Score,
                    };
                }
            }
            catch (Exception)
            {
                // 嵌入失败不该让整次检索失败：关键词那条路还能给出结果
            }
        }

        // 2) 关键词（切片）
        var index2 = 0;
        foreach (var chunk in await KnowledgeBaseRepository.SearchChunksByTextAsync(libraryId, query, topK * 4))
        {
            var key = "d:" + chunk.DocumentId + "#" + chunk.Ord;
            keywordRank[key] = index2++;

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
                Score = 0,
                MatchedByKeyword = true,
            };
        }

        // 3) 关键词（笔记）：笔记可能很长，截一段带标题的片段
        var index3 = 0;
        foreach (var note in await KnowledgeBaseRepository.SearchNotesByTextAsync(libraryId, query, topK))
        {
            var key = "n:" + note.Id;
            keywordRank[key] = index3++;

            if (hits.ContainsKey(key)) continue;

            hits[key] = new KbSearchHit
            {
                LibraryId = libraryId,
                Title = note.Title,
                Heading = "",
                Text = Excerpt(note.Content, query),
                Source = note.Id,
                Score = 0,
                IsNote = true,
                MatchedByKeyword = true,
            };
        }

        // RRF：两路都命中的自然排在最前，只有一路的按各自名次往后排。
        // 常数 60 是 RRF 的惯例值 —— 它让名次之间的差距在头部更明显、尾部更平缓。
        double Rrf(string key)
        {
            var sum = 0.0;
            if (vectorRank.TryGetValue(key, out var vr)) sum += 1.0 / (60 + vr + 1);
            if (keywordRank.TryGetValue(key, out var kr)) sum += 1.0 / (60 + kr + 1);
            return sum;
        }

        return hits
            .OrderByDescending(kv => Rrf(kv.Key))
            .ThenByDescending(kv => kv.Value.Score)
            .Take(topK)
            .Select(kv => kv.Value)
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
