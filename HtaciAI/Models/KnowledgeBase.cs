using System.Collections.Generic;

namespace HtaciAI.Models;

/// <summary>
/// 知识库：一个命名的资料集合，下辖两部分，彼此独立：
/// <list type="bullet">
///   <item><b>笔记</b>（<see cref="KbNote"/>）：用户手写、随时编辑的文本，存在库里，不需要嵌入模型。</item>
///   <item><b>向量库</b>（<see cref="KbDocument"/>）：导入的文件按块嵌入，供检索；没有配置嵌入模型时不可用。</item>
/// </list>
/// 两者放在同一个库下是因为它们回答的是同一个问题（「这件事我们怎么定的」），
/// 但实现上没有任何耦合：删掉向量索引不影响笔记，反之亦然。
/// </summary>
public sealed class KnowledgeBase
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    // ---- 以下为列表展示用的统计值，不落库 ----

    public int NoteCount { get; set; }

    public int DocumentCount { get; set; }
}

/// <summary>
/// 知识库里的一篇笔记。正文是 Markdown，界面负责渲染与编辑。
///
/// <b>同一库内标题唯一</b>（数据库上有唯一索引）：AI 只用标题寻址，不碰 id ——
/// 32 位十六进制 id 它抄不准，而标题是人本来就会说的东西。约束换来的是
/// 「AI 说得出标题，就一定定位得到唯一一条」。
/// </summary>
public sealed class KbNote
{
    public string Id { get; set; } = "";

    public string LibraryId { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>一句话简介。列表与召回结果里显示它，正文可能很长不适合当摘要。</summary>
    public string Summary { get; set; } = "";

    public string Content { get; set; } = "";

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>
/// 向量库里的一个文件。<see cref="Path"/> 是相对知识库根目录的路径，
/// 只是「哪个文件」的标识；正文存进 <c>kb_chunks</c>，检索不依赖原文件还在。
/// </summary>
public sealed class KbDocument
{
    public string Id { get; set; } = "";

    public string LibraryId { get; set; } = "";

    public string Path { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>
    /// 来源：<c>file</c> = 从磁盘导入的文件，<c>text</c> = AI 或用户直接写进去的一段文字。
    ///
    /// <b>为什么必须区分</b>：文件的条目归文件所有 —— 删掉登记、下次索引又会把它找回来，
    /// 所以删除要走界面并把文件一起处理；文字条目没有磁盘对应物，删了就是删了。
    /// 不区分的话「删除」这个动作要么删不动（立刻复活）、要么删掉用户的文件。
    /// </summary>
    public string Kind { get; set; } = "file";

    public string Hash { get; set; } = "";

    public long Size { get; set; }

    /// <summary>源文件最后修改时间（毫秒）。与 <see cref="Hash"/> 一起用于判断要不要重索引。</summary>
    public long ModifiedAt { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>完成索引的时间；0 = 还没索引成功过（导入失败或待索引）。</summary>
    public long IndexedAt { get; set; }

    /// <summary>索引状态：<c>pending</c> / <c>ok</c> / <c>failed</c>。</summary>
    public string Status { get; set; } = "pending";

    /// <summary>失败原因（<see cref="Status"/> 为 failed 时有值）。</summary>
    public string? Error { get; set; }

    public long CreatedAt { get; set; }
}

/// <summary>
/// 一个切片：切块后的正文 + 它的向量。
/// 正文本身也落库 —— 检索命中后直接把这段文字交给模型，不必回读原文件。
/// </summary>
public sealed class KbChunk
{
    public string DocumentId { get; set; } = "";

    /// <summary>在该文档内的序号，用于按原顺序还原上下文。</summary>
    public int Ord { get; set; }

    /// <summary>标题链（如「安装 &gt; Windows」），切块时按 Markdown 标题生成。</summary>
    public string Heading { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>向量；没有嵌入模型时为空。</summary>
    public float[]? Embedding { get; set; }

    public int Dim { get; set; }

    public string ModelId { get; set; } = "";

    // ---- 以下为检索时联表带出的展示字段，不单独落库 ----

    public string Title { get; set; } = "";

    public string Source { get; set; } = "";
}

/// <summary>一次检索命中的片段。</summary>
public sealed class KbSearchHit
{
    public string LibraryId { get; set; } = "";

    /// <summary>命中的笔记或文档标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>标题链（文档按 Markdown 标题切块时记录，笔记为所属笔记标题）。</summary>
    public string Heading { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>来源标识：笔记的 id 或文档的相对路径。</summary>
    public string Source { get; set; } = "";

    public bool IsNote { get; set; }

    /// <summary>向量余弦相似度；关键词命中的没有这个值。</summary>
    public double Score { get; set; }

    public bool MatchedByKeyword { get; set; }
}

/// <summary>一个库的索引状态汇总，用于界面提示。</summary>
public sealed class KbLibraryStats
{
    public int Notes { get; set; }

    public int Documents { get; set; }

    public int Chunks { get; set; }

    public int Pending { get; set; }

    public int Failed { get; set; }

    /// <summary>最后一次索引用的模型 id 与其维度（换模型后要重建）。</summary>
    public string ModelId { get; set; } = "";

    public int Dim { get; set; }
}
