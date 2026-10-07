using System;
using System.Threading.Tasks;
using HtaciAI.Services.Storage;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// SQLite 数据库服务：负责迁移与连接创建。库文件位置由 <see cref="AppPaths"/> 统一决定。
/// 每个操作独立开连接（本地库开销可忽略），并启用 WAL。
/// </summary>
public static class DatabaseService
{
    /// <summary>库文件路径，转发自 <see cref="AppPaths.DatabaseFile"/>。</summary>
    public static string DbPath => AppPaths.DatabaseFile;

    public static void Initialize()
    {
        using var conn = CreateConnection();
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            // 「重启以切换数据目录」时上一实例可能刚释放文件，等一下比直接抛 SQLITE_BUSY 好。
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
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

        if (version < 5)
        {
            RunMigrationV5(conn);
            version = 5;
            SetVersion(conn, version);
        }

        if (version < 6)
        {
            RunMigrationV6(conn);
            version = 6;
            SetVersion(conn, version);
        }

        if (version < 7)
        {
            RunMigrationV7(conn);
            version = 7;
            SetVersion(conn, version);
        }

        if (version < 8)
        {
            RunMigrationV8(conn);
            version = 8;
            SetVersion(conn, version);
        }

        if (version < 9)
        {
            RunMigrationV9(conn);
            version = 9;
            SetVersion(conn, version);
        }

        if (version < 10)
        {
            RunMigrationV10(conn);
            version = 10;
            SetVersion(conn, version);
        }

        if (version < 11)
        {
            RunMigrationV11(conn);
            version = 11;
            SetVersion(conn, version);
        }

        if (version < 12)
        {
            RunMigrationV12(conn);
            version = 12;
            SetVersion(conn, version);
        }

        if (version < 13)
        {
            RunMigrationV13(conn);
            version = 13;
            SetVersion(conn, version);
        }

        if (version < 14)
        {
            RunMigrationV14(conn);
            version = 14;
            SetVersion(conn, version);
        }

        if (version < 15)
        {
            RunMigrationV15(conn);
            version = 15;
            SetVersion(conn, version);
        }

        if (version < 16)
        {
            RunMigrationV16(conn);
            version = 16;
            SetVersion(conn, version);
        }

        if (version < 17)
        {
            RunMigrationV17(conn);
            version = 17;
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
                thinking      INTEGER NOT NULL DEFAULT 1,
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

    /// <summary>工具管理：工具集表 + 工具表 + 多对多关联表（内置集合的语义见 V15 与 <c>Toolset</c>）。</summary>
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

    /// <summary>会话级技能启用列表：chat_sessions 增加 enabled_skills（JSON 数组，元素含 id / status）。</summary>
    private static void RunMigrationV5(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE chat_sessions ADD COLUMN enabled_skills TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>会话级配置：增加 enabled_tool_ids（激活工具 id 的 JSON 数组）与 last_message_at（实际对话请求时间，用于排序）。</summary>
    private static void RunMigrationV6(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE chat_sessions ADD COLUMN enabled_tool_ids TEXT;
            ALTER TABLE chat_sessions ADD COLUMN last_message_at INTEGER;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>工具权限等级：tool_tools 增加 danger_level（缺省 Danger，兼容旧行）。</summary>
    private static void RunMigrationV7(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE tool_tools ADD COLUMN danger_level TEXT NOT NULL DEFAULT 'Danger';";
        cmd.ExecuteNonQuery();
    }

    /// <summary>会话级工具权限档位：chat_sessions 增加 permission_mode（0=严格 1=普通 2=宽松 3=自由，缺省普通）。</summary>
    private static void RunMigrationV8(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE chat_sessions ADD COLUMN permission_mode INTEGER NOT NULL DEFAULT 1;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>自定义智能体蓝图：agents 表（系统提示词 + 默认工具/技能/MCP，均为 JSON 数组列）。</summary>
    private static void RunMigrationV9(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS agents (
                id               TEXT PRIMARY KEY,
                name             TEXT NOT NULL,
                description      TEXT,
                system_prompt    TEXT,
                enabled_tool_ids TEXT,
                enabled_skills   TEXT,
                mcp_servers      TEXT,
                is_enabled       INTEGER NOT NULL DEFAULT 1,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>工作空间（持久化）：agent_id 活引用 + 系统提示词 + 默认工具/技能/MCP + 权限档位。</summary>
    private static void RunMigrationV10(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS workspaces (
                id               TEXT PRIMARY KEY,
                name             TEXT NOT NULL,
                description      TEXT,
                path             TEXT,
                agent_id         TEXT,
                system_prompt    TEXT,
                enabled_tool_ids TEXT,
                enabled_skills   TEXT,
                mcp_servers      TEXT,
                permission_mode  INTEGER NOT NULL DEFAULT 1,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>工作区会话（持久化）：归属于工作区，继承 Agent/工作区配置；含会话级提示词与草稿。</summary>
    private static void RunMigrationV11(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS workspace_sessions (
                id               TEXT PRIMARY KEY,
                workspace_id     TEXT NOT NULL,
                agent_id         TEXT,
                title            TEXT NOT NULL,
                model            TEXT,
                thinking         INTEGER NOT NULL DEFAULT 1,
                session_prompt   TEXT,
                enabled_tool_ids TEXT,
                enabled_skills   TEXT,
                mcp_servers      TEXT,
                permission_mode  INTEGER NOT NULL DEFAULT 1,
                draft            TEXT,
                last_message_at  INTEGER,
                is_deleted       INTEGER NOT NULL DEFAULT 0,
                deleted_at       INTEGER,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>普通会话草稿：chat_sessions 增加 draft（工作区会话那边早就有同名列）。</summary>
    private static void RunMigrationV12(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE chat_sessions ADD COLUMN draft TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 一轮回复的耗时：chat_message 增加 duration_ms，写在该轮最后一条 assistant 消息上。
    /// 光靠时间戳推不出来 —— ChatGateway 给一轮内所有消息用的是同一个 now，
    /// 单轮消息的 created_at 与 updated_at 相等。旧数据没有这一列，视图会退回时间戳推算。
    /// </summary>
    private static void RunMigrationV13(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE chat_message ADD COLUMN duration_ms INTEGER;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// MCP 服务：新建 mcp_servers 表存放服务本体（名称/地址/类型/请求头/超时），
    /// 并给 chat_sessions 补 mcp_servers 列（存启用的服务 id 列表），
    /// 与既有 agents / workspaces / workspace_sessions 三张表的同名列保持一致。
    /// </summary>
    private static void RunMigrationV14(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS mcp_servers (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                transport       TEXT NOT NULL DEFAULT 'StreamableHttp',
                url             TEXT,
                headers_raw     TEXT,
                timeout_seconds INTEGER NOT NULL DEFAULT 60,
                enabled         INTEGER NOT NULL DEFAULT 1,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL
            );
            ALTER TABLE chat_sessions ADD COLUMN mcp_servers TEXT;
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 工具集语义调整：V3 种下的「默认集」改成自动集合「全部」，并补上同样自动的「内置」。
    /// 集合的判定规则在代码里（见 Toolset），这里只是让库里的行名与之一致，别让人看库时误解。
    /// </summary>
    private static void RunMigrationV15(SqliteConnection conn)
    {
        const string schema = """
            UPDATE tool_toolsets
            SET name = '全部', description = '所有工具，无论属于哪个工具集', is_builtin = 1
            WHERE id = 'default';

            INSERT INTO tool_toolsets (id, name, description, is_builtin, created_at, updated_at)
            VALUES ('builtin', '内置', '程序内置的工具', 1, 0, 0)
            ON CONFLICT(id) DO NOTHING;
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 两件互不相干但都只涉及「改数据/加列」的事，合成一次迁移：
    ///
    /// <b>1) 回填 last_message_at</b>：这一列是后加的，加之前的老会话全是 NULL，
    /// 而侧栏排序从 V16 起改用 <c>COALESCE(last_message_at, created_at)</c>（原来兜底的是
    /// updated_at，导致「点开看一眼」就会把会话顶到最前）。不回填的话，所有老会话会掉到按
    /// 「创建时间」排的位置，和它们真实的最后对话时间对不上。取该会话未删除消息的最大 created_at，
    /// 与 <c>ChatRepository</c> 里写入时的口径一致。没有消息的会话保持 NULL（本来就该按创建时间排）。
    ///
    /// <b>2) mcp_servers 补 stdio 所需的四列</b>：命令、参数、环境变量、工作目录。
    /// 与 url / headers_raw 一样按「用户原文」保存，运行时才解析。
    /// </summary>
    private static void RunMigrationV16(SqliteConnection conn)
    {
        const string schema = """
            UPDATE chat_sessions SET last_message_at = (
                SELECT MAX(created_at) FROM chat_message
                WHERE chat_message.session_id = chat_sessions.id AND is_deleted = 0)
            WHERE last_message_at IS NULL
              AND EXISTS (SELECT 1 FROM chat_message
                          WHERE session_id = chat_sessions.id AND is_deleted = 0);

            UPDATE workspace_sessions SET last_message_at = (
                SELECT MAX(created_at) FROM chat_message
                WHERE chat_message.session_id = workspace_sessions.id AND is_deleted = 0)
            WHERE last_message_at IS NULL
              AND EXISTS (SELECT 1 FROM chat_message
                          WHERE session_id = workspace_sessions.id AND is_deleted = 0);

            ALTER TABLE mcp_servers ADD COLUMN command TEXT;
            ALTER TABLE mcp_servers ADD COLUMN command_args TEXT;
            ALTER TABLE mcp_servers ADD COLUMN env TEXT;
            ALTER TABLE mcp_servers ADD COLUMN working_directory TEXT;
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 放开 <c>chat_message.role</c> 的取值范围：加上 <c>'agent'</c>。
    ///
    /// 这一列在 V1 建表时带了 <c>CHECK (role IN ('user','assistant','tool'))</c>，而上下文压缩要往
    /// 消息流里插一条 <c>role='agent'</c> 的记录（见 <c>ContextCompression</c>）。SQLite 不能删改
    /// CHECK 约束，唯一的办法是<b>重建表</b>：建新表 → 拷数据 → 删旧表 → 改名 → 重建索引与触发器。
    ///
    /// 重建是破坏性操作，所以做了三件保命的事：
    /// <list type="number">
    ///   <item>整个流程在一个事务里，中途任何一步失败都整体回滚，不会留下半个表；</item>
    ///   <item>拷完立刻核对行数，对不上就抛异常触发回滚；</item>
    ///   <item>先删掉 <c>trg_msg_delete_chat</c>、最后原样建回来 —— 改名那一步会检查整个 schema，
    ///     而它引用的旧表在中间那个瞬间是不存在的，留着它会让 RENAME 直接报错。</item>
    /// </list>
    /// 新表的列与约束必须与当前实际结构一致（包含 V13 用 ALTER 补上的 duration_ms），
    /// 少一列就等于把那一列的数据丢掉。
    /// </summary>
    private static void RunMigrationV17(SqliteConnection conn)
    {
        const string createTable = """
            CREATE TABLE chat_message_v17 (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                turn_id         TEXT NOT NULL,
                sequence_number INTEGER NOT NULL,
                role            TEXT NOT NULL CHECK (role IN ('user','assistant','tool','agent')),
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
                duration_ms     INTEGER,
                UNIQUE (session_id, sequence_number)
            );
            """;

        const string copyRows = """
            INSERT INTO chat_message_v17
                (id, session_id, turn_id, sequence_number, role, content, thinking, tool_call_id, tool_name,
                 status, usage_json, model_name, metadata, is_deleted, deleted_at, created_at, updated_at, duration_ms)
            SELECT
                 id, session_id, turn_id, sequence_number, role, content, thinking, tool_call_id, tool_name,
                 status, usage_json, model_name, metadata, is_deleted, deleted_at, created_at, updated_at, duration_ms
            FROM chat_message;
            """;

        const string recreateIndexes = """
            CREATE INDEX IF NOT EXISTS idx_msg_session ON chat_message(session_id, sequence_number);
            CREATE INDEX IF NOT EXISTS idx_msg_turn    ON chat_message(session_id, turn_id);
            """;

        const string recreateTrigger = """
            CREATE TRIGGER IF NOT EXISTS trg_msg_delete_chat
            AFTER DELETE ON chat_sessions
            BEGIN
                DELETE FROM chat_message WHERE session_id = OLD.id;
            END;
            """;

        var before = CountMessages(conn, null);

        using var transaction = conn.BeginTransaction();
        try
        {
            Execute(conn, transaction, "DROP TRIGGER IF EXISTS trg_msg_delete_chat;");
            Execute(conn, transaction, createTable);
            Execute(conn, transaction, copyRows);

            var copied = CountMessages(conn, transaction);
            if (copied != before)
                throw new InvalidOperationException($"chat_message 重建时行数对不上：{before} → {copied}");

            Execute(conn, transaction, "DROP TABLE chat_message;");
            Execute(conn, transaction, "ALTER TABLE chat_message_v17 RENAME TO chat_message;");
            Execute(conn, transaction, recreateIndexes);
            Execute(conn, transaction, recreateTrigger);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static int CountMessages(SqliteConnection conn, SqliteTransaction? transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM chat_message;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction transaction, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
