using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// SQLite 数据库服务：负责库文件位置、迁移与连接创建。
/// 每个操作独立开连接（本地库开销可忽略），并启用 WAL。
/// </summary>
public static class DatabaseService
{
    public static string DbPath { get; private set; } = "";

    public static void Initialize()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HtaciAI");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "data.db");

        using var conn = CreateConnection();
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        Migrate(conn);
    }

    public static SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        return conn;
    }

    private static void Migrate(SqliteConnection conn)
    {
        int version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (version < 1)
        {
            RunMigrationV1(conn);
            SetVersion(conn, 1);
        }
    }

    private static void SetVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void RunMigrationV1(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS chat_sessions (
                id            TEXT PRIMARY KEY,
                title         TEXT NOT NULL,
                model         TEXT,
                system_prompt TEXT,
                thinking      INTEGER NOT NULL DEFAULT 0,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_message (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                turn_id         TEXT NOT NULL,
                sequence_number INTEGER NOT NULL,
                role            TEXT NOT NULL CHECK (role IN ('user','assistant','tool')),
                content         TEXT,
                thinking        TEXT,
                tool_call_id    TEXT,
                tool_name       TEXT,
                status          TEXT NOT NULL DEFAULT 'completed'
                                CHECK (status IN ('completed','failed','interrupted')),
                usage_json      TEXT,
                model_name      TEXT,
                metadata        TEXT,
                is_deleted      INTEGER NOT NULL DEFAULT 0,
                deleted_at      INTEGER,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL,
                UNIQUE (session_id, sequence_number)
            );

            CREATE INDEX IF NOT EXISTS idx_msg_session ON chat_message(session_id, sequence_number);
            CREATE INDEX IF NOT EXISTS idx_msg_turn     ON chat_message(session_id, turn_id);

            CREATE TRIGGER IF NOT EXISTS trg_msg_delete_chat
            AFTER DELETE ON chat_sessions
            BEGIN
                DELETE FROM chat_message WHERE session_id = OLD.id;
            END;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }
}
