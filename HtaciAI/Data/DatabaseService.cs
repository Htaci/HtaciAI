using System;
using System.IO;
using System.Threading.Tasks;
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

    /// <summary>打开连接并启用外键（PRAGMA 是连接级设置，需每个连接单独开启）。</summary>
    public static async Task<SqliteConnection> OpenWithForeignKeysAsync()
    {
        var conn = CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync();
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
            version = 1;
            SetVersion(conn, version);
        }

        if (version < 2)
        {
            RunMigrationV2(conn);
            version = 2;
            SetVersion(conn, version);
        }

        if (version < 3)
        {
            RunMigrationV3(conn);
            version = 3;
            SetVersion(conn, version);
        }

        if (version < 4)
        {
            RunMigrationV4(conn);
            version = 4;
            SetVersion(conn, version);
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

    /// <summary>模型管理：服务商表 + 模型表（自定义模型）。</summary>
    private static void RunMigrationV2(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS ai_provider (
                id                     TEXT PRIMARY KEY,
                name                   TEXT NOT NULL,
                description            TEXT,
                protocol               TEXT NOT NULL DEFAULT 'OpenAIEx',
                endpoint               TEXT NOT NULL,
                api_key                TEXT,
                thinking_field         TEXT NOT NULL DEFAULT 'think',
                supports_array_content INTEGER NOT NULL DEFAULT 1,
                supports_streaming     INTEGER NOT NULL DEFAULT 1,
                is_enabled             INTEGER NOT NULL DEFAULT 1,
                sort_order             INTEGER NOT NULL DEFAULT 0,
                created_at             INTEGER NOT NULL,
                updated_at             INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_model (
                id                  TEXT PRIMARY KEY,
                provider_id         TEXT NOT NULL,
                call_id             TEXT NOT NULL,
                display_name        TEXT NOT NULL,
                supports_streaming  INTEGER NOT NULL DEFAULT 1,
                supports_thinking   INTEGER NOT NULL DEFAULT 0,
                thinking_strengths  TEXT,
                capabilities        TEXT,
                context_window      INTEGER NOT NULL DEFAULT -1,
                price_input         REAL,
                price_cache_hit     REAL,
                price_output        REAL,
                currency            TEXT NOT NULL DEFAULT 'CNY',
                is_enabled          INTEGER NOT NULL DEFAULT 1,
                sort_order          INTEGER NOT NULL DEFAULT 0,
                created_at          INTEGER NOT NULL,
                updated_at          INTEGER NOT NULL,
                FOREIGN KEY (provider_id) REFERENCES ai_provider(id) ON DELETE CASCADE,
                UNIQUE (provider_id, call_id)
            );

            CREATE INDEX IF NOT EXISTS idx_model_provider ON ai_model(provider_id);
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>工具管理：工具集表 + 工具表 + 多对多关联表，内置默认集。</summary>
    private static void RunMigrationV3(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS tool_toolsets (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                description TEXT,
                is_builtin  INTEGER NOT NULL DEFAULT 0,
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tool_tools (
                id           TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                description  TEXT,
                source       TEXT NOT NULL DEFAULT 'Script',
                runtime      TEXT,
                target       TEXT,
                input_schema TEXT,
                enabled      INTEGER NOT NULL DEFAULT 1,
                created_at   INTEGER NOT NULL,
                updated_at   INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tool_tool_links (
                tool_id    TEXT NOT NULL,
                toolset_id TEXT NOT NULL,
                PRIMARY KEY (tool_id, toolset_id),
                FOREIGN KEY (tool_id)    REFERENCES tool_tools(id)    ON DELETE CASCADE,
                FOREIGN KEY (toolset_id) REFERENCES tool_toolsets(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_tool_link_toolset ON tool_tool_links(toolset_id);

            INSERT INTO tool_toolsets (id, name, description, is_builtin, created_at, updated_at)
            VALUES ('default', '默认集', '未指定归属的工具默认所在的工具集', 1, 0, 0)
            ON CONFLICT(id) DO NOTHING;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>会话软删除：chat_sessions 增加 is_deleted / deleted_at（消息表已有，无需重复）。</summary>
    private static void RunMigrationV4(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE chat_sessions ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE chat_sessions ADD COLUMN deleted_at INTEGER;
            """;
        cmd.ExecuteNonQuery();
    }
}
