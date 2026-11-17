using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 知识库仓储（kb_libraries / kb_notes / kb_documents / kb_chunks 四表）。
///
/// 四个表之间<b>不加外键</b>：删库时由仓储按顺序自己清（chunks → documents → notes → library）。
/// 与 chat_message 的处理方式一致 —— 应用内所有删除都走这里，加外键只会让
/// 「先删父再删子」的顺序要求渗进每一个调用点。
/// </summary>
public static class KnowledgeBaseRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string NewId() => Guid.NewGuid().ToString("N");

    // ---- 库 ----

    /// <summary>全部知识库，带笔记数与文档数（列表直接显示，不再逐库查一次）。</summary>
    public static async Task<List<KnowledgeBase>> GetAllAsync()
    {
        var list = new List<KnowledgeBase>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.id, l.name, COALESCE(l.description, ''), l.created_at, l.updated_at,
                   (SELECT COUNT(1) FROM kb_notes     n WHERE n.library_id = l.id),
                   (SELECT COUNT(1) FROM kb_documents d WHERE d.library_id = l.id)
            FROM kb_libraries l
            ORDER BY l.updated_at DESC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KnowledgeBase
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Description = r.GetString(2),
                CreatedAt = r.GetInt64(3),
                UpdatedAt = r.GetInt64(4),
                NoteCount = r.GetInt32(5),
                DocumentCount = r.GetInt32(6),
            });
        return list;
    }

    public static async Task<KnowledgeBase?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, COALESCE(description, ''), created_at, updated_at
            FROM kb_libraries WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new KnowledgeBase
        {
            Id = r.GetString(0),
            Name = r.GetString(1),
            Description = r.GetString(2),
            CreatedAt = r.GetInt64(3),
            UpdatedAt = r.GetInt64(4),
        };
    }

    public static async Task CreateAsync(KnowledgeBase kb)
    {
        var now = Now();
        if (kb.CreatedAt == 0) kb.CreatedAt = now;
        kb.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kb_libraries (id, name, description, created_at, updated_at)
            VALUES ($id, $name, $description, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", kb.Id);
        cmd.Parameters.AddWithValue("$name", kb.Name);
        cmd.Parameters.AddWithValue("$description", kb.Description);
        cmd.Parameters.AddWithValue("$created_at", kb.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", kb.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task RenameAsync(string id, string name, string description)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE kb_libraries SET name = $name, description = $description, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>删库：连同它的笔记、文档与切片一起清掉。</summary>
    public static async Task DeleteAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var sql in new[]
                 {
                     "DELETE FROM kb_chunks    WHERE library_id = $id;",
                     "DELETE FROM kb_documents WHERE library_id = $id;",
                     "DELETE FROM kb_notes     WHERE library_id = $id;",
                     "DELETE FROM kb_libraries WHERE id = $id;",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>把「某会话引用了这个库」的引用清空，避免删库后会话指向一个不存在的 id。</summary>
    public static async Task ClearReferencesAsync(string libraryId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_sessions      SET kb_id = NULL         WHERE kb_id = $id;
            UPDATE workspace_sessions SET kb_id = NULL         WHERE kb_id = $id;
            UPDATE workspaces         SET default_kb_id = NULL WHERE default_kb_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", libraryId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 笔记 ----

    public static async Task<List<KbNote>> GetNotesAsync(string libraryId)
    {
        var list = new List<KbNote>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, library_id, title, COALESCE(content, ''), created_at, updated_at
            FROM kb_notes WHERE library_id = $lib ORDER BY updated_at DESC;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KbNote
            {
                Id = r.GetString(0),
                LibraryId = r.GetString(1),
                Title = r.GetString(2),
                Content = r.GetString(3),
                CreatedAt = r.GetInt64(4),
                UpdatedAt = r.GetInt64(5),
            });
        return list;
    }

    public static async Task<KbNote?> GetNoteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, library_id, title, COALESCE(content, ''), created_at, updated_at
            FROM kb_notes WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new KbNote
        {
            Id = r.GetString(0),
            LibraryId = r.GetString(1),
            Title = r.GetString(2),
            Content = r.GetString(3),
            CreatedAt = r.GetInt64(4),
            UpdatedAt = r.GetInt64(5),
        };
    }

    public static async Task UpsertNoteAsync(KbNote note)
    {
        var now = Now();
        if (note.CreatedAt == 0) note.CreatedAt = now;
        note.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kb_notes (id, library_id, title, content, created_at, updated_at)
            VALUES ($id, $lib, $title, $content, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                title = $title, content = $content, updated_at = $updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$lib", note.LibraryId);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$content", note.Content);
        cmd.Parameters.AddWithValue("$created_at", note.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", note.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();

        await TouchLibraryAsync(conn, note.LibraryId);
    }

    public static async Task DeleteNoteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kb_notes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 文档（向量库） ----

    public static async Task<List<KbDocument>> GetDocumentsAsync(string libraryId)
    {
        var list = new List<KbDocument>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, library_id, path, title, COALESCE(hash, ''), size, modified_at,
                   chunk_count, indexed_at, status, error
            FROM kb_documents WHERE library_id = $lib ORDER BY path ASC;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapDocument(r));
        return list;
    }

    public static async Task<KbDocument?> GetDocumentAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, library_id, path, title, COALESCE(hash, ''), size, modified_at,
                   chunk_count, indexed_at, status, error
            FROM kb_documents WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapDocument(r) : null;
    }

    /// <summary>导入或更新一个文档行（按「库 + 路径」去重）。返回最终落库的 id。</summary>
    public static async Task<string> UpsertDocumentAsync(KbDocument doc)
    {
        var now = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();

        if (string.IsNullOrEmpty(doc.Id))
        {
            using var find = conn.CreateCommand();
            find.CommandText = "SELECT id FROM kb_documents WHERE library_id = $lib AND path = $path;";
            find.Parameters.AddWithValue("$lib", doc.LibraryId);
            find.Parameters.AddWithValue("$path", doc.Path);
            if (await find.ExecuteScalarAsync() is string existing) doc.Id = existing;
            else doc.Id = NewId();
        }

        if (doc.CreatedAt == 0) doc.CreatedAt = now;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kb_documents
                (id, library_id, path, title, hash, size, modified_at, chunk_count, indexed_at, status, error, created_at, updated_at)
            VALUES
                ($id, $lib, $path, $title, $hash, $size, $modified_at, $chunk_count, $indexed_at, $status, $error, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                path = $path, title = $title, hash = $hash, size = $size, modified_at = $modified_at,
                chunk_count = $chunk_count, indexed_at = $indexed_at, status = $status, error = $error,
                updated_at = $updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", doc.Id);
        cmd.Parameters.AddWithValue("$lib", doc.LibraryId);
        cmd.Parameters.AddWithValue("$path", doc.Path);
        cmd.Parameters.AddWithValue("$title", doc.Title);
        cmd.Parameters.AddWithValue("$hash", doc.Hash);
        cmd.Parameters.AddWithValue("$size", doc.Size);
        cmd.Parameters.AddWithValue("$modified_at", doc.ModifiedAt);
        cmd.Parameters.AddWithValue("$chunk_count", doc.ChunkCount);
        cmd.Parameters.AddWithValue("$indexed_at", doc.IndexedAt);
        cmd.Parameters.AddWithValue("$status", doc.Status);
        cmd.Parameters.AddWithValue("$error", (object?)doc.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", doc.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", now);
        await cmd.ExecuteNonQueryAsync();

        await TouchLibraryAsync(conn, doc.LibraryId);
        return doc.Id;
    }

    public static async Task DeleteDocumentAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM kb_chunks WHERE document_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM kb_documents WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---- 切片 ----

    /// <summary>整体替换一个文档的切片（重新索引时先清后写，同一事务内完成）。</summary>
    public static async Task ReplaceChunksAsync(string documentId, string libraryId, IReadOnlyList<KbChunk> chunks)
    {
        var now = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        await using var tx = await conn.BeginTransactionAsync();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM kb_chunks WHERE document_id = $id;";
            del.Parameters.AddWithValue("$id", documentId);
            await del.ExecuteNonQueryAsync();
        }

        foreach (var chunk in chunks)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO kb_chunks (id, library_id, document_id, ord, heading, text, embedding, dim, model_id, created_at)
                VALUES ($id, $lib, $doc, $ord, $heading, $text, $embedding, $dim, $model_id, $created_at);
                """;
            cmd.Parameters.AddWithValue("$id", NewId());
            cmd.Parameters.AddWithValue("$lib", libraryId);
            cmd.Parameters.AddWithValue("$doc", documentId);
            cmd.Parameters.AddWithValue("$ord", chunk.Ord);
            cmd.Parameters.AddWithValue("$heading", (object?)chunk.Heading ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", chunk.Text);
            cmd.Parameters.AddWithValue("$embedding",
                chunk.Embedding is { Length: > 0 } v ? ToBytes(v) : DBNull.Value);
            cmd.Parameters.AddWithValue("$dim", chunk.Dim);
            cmd.Parameters.AddWithValue("$model_id", chunk.ModelId);
            cmd.Parameters.AddWithValue("$created_at", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    /// <summary>
    /// 取出一个库的全部切片（含向量）。检索时在内存里算余弦 ——
    /// 万级切片是毫秒量级，换来的是不必分发任何原生扩展。
    /// </summary>
    public static async Task<List<KbChunk>> GetChunksAsync(string libraryId)
    {
        var list = new List<KbChunk>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.document_id, c.ord, COALESCE(c.heading, ''), c.text, c.embedding, c.dim,
                   COALESCE(c.model_id, ''), COALESCE(d.title, ''), COALESCE(d.path, '')
            FROM kb_chunks c
            LEFT JOIN kb_documents d ON d.id = c.document_id
            WHERE c.library_id = $lib
            ORDER BY c.document_id, c.ord;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KbChunk
            {
                DocumentId = r.GetString(0),
                Ord = r.GetInt32(1),
                Heading = r.GetString(2),
                Text = r.GetString(3),
                Embedding = r.IsDBNull(4) ? null : FromBytes((byte[])r.GetValue(4)),
                Dim = r.GetInt32(5),
                ModelId = r.GetString(6),
                Title = r.GetString(7),
                Source = r.GetString(8),
            });
        return list;
    }

    /// <summary>关键词候选：不走向量，直接在切片正文里找。没配嵌入模型时这就是全部检索能力。</summary>
    public static async Task<List<KbChunk>> SearchChunksByTextAsync(string libraryId, string keyword, int limit)
    {
        var list = new List<KbChunk>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.document_id, c.ord, COALESCE(c.heading, ''), c.text, c.dim,
                   COALESCE(d.title, ''), COALESCE(d.path, '')
            FROM kb_chunks c
            LEFT JOIN kb_documents d ON d.id = c.document_id
            WHERE c.library_id = $lib AND (c.text LIKE $kw OR COALESCE(c.heading, '') LIKE $kw)
            ORDER BY c.document_id, c.ord
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$kw", "%" + EscapeLike(keyword) + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KbChunk
            {
                DocumentId = r.GetString(0),
                Ord = r.GetInt32(1),
                Heading = r.GetString(2),
                Text = r.GetString(3),
                Dim = r.GetInt32(4),
                Title = r.GetString(5),
                Source = r.GetString(6),
            });
        return list;
    }

    /// <summary>关键词候选：笔记按标题与正文匹配。</summary>
    public static async Task<List<KbNote>> SearchNotesByTextAsync(string libraryId, string keyword, int limit)
    {
        var list = new List<KbNote>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, library_id, title, COALESCE(content, ''), created_at, updated_at
            FROM kb_notes
            WHERE library_id = $lib AND (title LIKE $kw OR COALESCE(content, '') LIKE $kw)
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$kw", "%" + EscapeLike(keyword) + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new KbNote
            {
                Id = r.GetString(0),
                LibraryId = r.GetString(1),
                Title = r.GetString(2),
                Content = r.GetString(3),
                CreatedAt = r.GetInt64(4),
                UpdatedAt = r.GetInt64(5),
            });
        return list;
    }

    public static async Task<KbLibraryStats> GetStatsAsync(string libraryId)
    {
        var stats = new KbLibraryStats();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT (SELECT COUNT(1) FROM kb_notes     WHERE library_id = $lib),
                       (SELECT COUNT(1) FROM kb_documents WHERE library_id = $lib),
                       (SELECT COUNT(1) FROM kb_chunks    WHERE library_id = $lib),
                       (SELECT COUNT(1) FROM kb_documents WHERE library_id = $lib AND status = 'pending'),
                       (SELECT COUNT(1) FROM kb_documents WHERE library_id = $lib AND status = 'failed');
                """;
            cmd.Parameters.AddWithValue("$lib", libraryId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                stats.Notes = r.GetInt32(0);
                stats.Documents = r.GetInt32(1);
                stats.Chunks = r.GetInt32(2);
                stats.Pending = r.GetInt32(3);
                stats.Failed = r.GetInt32(4);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(model_id, ''), dim FROM kb_chunks
                WHERE library_id = $lib AND model_id IS NOT NULL AND model_id <> ''
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$lib", libraryId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                stats.ModelId = r.GetString(0);
                stats.Dim = r.GetInt32(1);
            }
        }

        return stats;
    }

    // ---- 内部 ----

    private static async Task TouchLibraryAsync(SqliteConnection conn, string libraryId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE kb_libraries SET updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", libraryId);
        cmd.Parameters.AddWithValue("$now", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    private static KbDocument MapDocument(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        LibraryId = r.GetString(1),
        Path = r.GetString(2),
        Title = r.GetString(3),
        Hash = r.GetString(4),
        Size = r.GetInt64(5),
        ModifiedAt = r.GetInt64(6),
        ChunkCount = r.GetInt32(7),
        IndexedAt = r.GetInt64(8),
        Status = r.GetString(9),
        Error = r.IsDBNull(10) ? null : r.GetString(10),
    };

    /// <summary>LIKE 的转义：用户搜的内容里带 % 或 _ 时不能当成通配符。</summary>
    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
