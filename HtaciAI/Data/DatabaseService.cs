using System;
using System.Collections.Generic;
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

        if (version < 18)
        {
            RunMigrationV18(conn);
            version = 18;
            SetVersion(conn, version);
        }

        if (version < 19)
        {
            RunMigrationV19(conn);
            version = 19;
            SetVersion(conn, version);
        }

        if (version < 20)
        {
            RunMigrationV20(conn);
            version = 20;
            SetVersion(conn, version);
        }

        if (version < 21)
        {
            RunMigrationV21(conn);
            version = 21;
            SetVersion(conn, version);
        }

        if (version < 22)
        {
            RunMigrationV22(conn);
            version = 22;
            SetVersion(conn, version);
        }

        if (version < 23)
        {
            RunMigrationV23(conn);
            version = 23;
            SetVersion(conn, version);
        }

        if (version < 24)
        {
            RunMigrationV24(conn);
            version = 24;
            SetVersion(conn, version);
        }

        if (version < 25)
        {
            RunMigrationV25(conn);
            version = 25;
            SetVersion(conn, version);
        }

        if (version < 26)
        {
            RunMigrationV26(conn);
            version = 26;
            SetVersion(conn, version);
        }

        if (version < 27)
        {
            RunMigrationV27(conn);
            version = 27;
            SetVersion(conn, version);
        }

        if (version < 28)
        {
            RunMigrationV28(conn);
            version = 28;
            SetVersion(conn, version);
        }

        if (version < 29)
        {
            RunMigrationV29(conn);
            version = 29;
            SetVersion(conn, version);
        }

        if (version < 30)
        {
            RunMigrationV30(conn);
            version = 30;
            SetVersion(conn, version);
        }

        if (version < 31)
        {
            RunMigrationV31(conn);
            version = 31;
            SetVersion(conn, version);
        }

        if (version < 32)
        {
            RunMigrationV32(conn);
            version = 32;
            SetVersion(conn, version);
        }

        if (version < 33)
        {
            RunMigrationV33(conn);
            version = 33;
            SetVersion(conn, version);
        }

        if (version < 34)
        {
            RunMigrationV34(conn);
            version = 34;
            SetVersion(conn, version);
        }
    }

    /// <summary>
    /// 智能体定义（agents）扩成「角色 + 子 Agent」共用：新增 kind 分类列与角色的默认配置列。
    ///
    /// <b>为什么合一</b>：角色与子 Agent 的字段完全一样（提示词 + 工具 + 技能 + MCP），
    /// 差别只在「谁选中它」—— 角色由用户在会话里选，子 Agent 由模型用 task 派发。
    /// 单开一张表会让两边的编辑界面、seed、仓储全部重复一份。
    ///
    /// <b>历史行一律是 subagent</b>：V9 建表时这张表只服务子 Agent，默认值直接给 'subagent'，
    /// 所以老数据不用回填。角色是新增的一类，只在新建时显式写 'role'。
    /// </summary>
    private static void RunMigrationV34(SqliteConnection conn)
    {
        const string schema = """
            ALTER TABLE agents ADD COLUMN kind TEXT NOT NULL DEFAULT 'subagent';
            ALTER TABLE agents ADD COLUMN model TEXT;
            ALTER TABLE agents ADD COLUMN kb_id TEXT;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 定时任务：新建 task_defs（定义）与 task_runs（每次运行 = 一个会话）两张表。
    ///
    /// <b>为什么分成两张表</b>：定义是「每周一 08:30 跑这件事」，运行是「上周一那次跑成什么样」。
    /// 前者只有一条、天天被调度器扫；后者会无限增长、只在点开时才看。合成一张表要么调度器
    /// 每秒扫一遍历史，要么历史被反复覆盖。
    ///
    /// <b>task_runs 与 subagent_sessions 同构</b>：一行就是一个会话，消息照旧写 chat_message
    /// （多态关联、无外键），上下文压缩、工具调用修复、持久化全部复用现有那套 —— 解析入口仍只有
    /// <c>SessionProfileResolver</c> 一处，会话列表那些整表扫描一行都不用改。
    ///
    /// <b>下次触发时间落库</b>：程序关掉期间错过的触发靠 <c>next_run_at</c> 判断，
    /// 调度器不必回头翻历史；一次性任务跑完后把它置 NULL 即自然停用。
    /// </summary>
    private static void RunMigrationV33(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS task_defs (
                id                TEXT PRIMARY KEY,
                title             TEXT NOT NULL,
                prompt            TEXT NOT NULL,
                base_directory    TEXT,
                model             TEXT,
                thinking          INTEGER NOT NULL DEFAULT 1,
                enabled_tool_ids  TEXT,
                enabled_skills    TEXT,
                mcp_servers       TEXT,
                permission_mode   INTEGER NOT NULL DEFAULT 1,
                max_turns         INTEGER NOT NULL DEFAULT 20,
                trigger_kind      TEXT NOT NULL DEFAULT 'daily'
                                  CHECK (trigger_kind IN ('once','daily','weekly','monthly')),
                trigger_days      TEXT,
                trigger_time      TEXT,
                once_at           INTEGER,
                next_run_at       INTEGER,
                last_run_at       INTEGER,
                is_enabled        INTEGER NOT NULL DEFAULT 1,
                failure_count     INTEGER NOT NULL DEFAULT 0,
                is_deleted        INTEGER NOT NULL DEFAULT 0,
                deleted_at        INTEGER,
                created_at        INTEGER NOT NULL,
                updated_at        INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_task_defs_due ON task_defs(is_enabled, next_run_at);

            CREATE TABLE IF NOT EXISTS task_runs (
                id                TEXT PRIMARY KEY,
                task_id           TEXT NOT NULL,
                title             TEXT NOT NULL,
                model             TEXT,
                thinking          INTEGER NOT NULL DEFAULT 1,
                session_prompt    TEXT,
                base_directory    TEXT,
                enabled_tool_ids  TEXT,
                enabled_skills    TEXT,
                mcp_servers       TEXT,
                permission_mode   INTEGER NOT NULL DEFAULT 1,
                max_turns         INTEGER NOT NULL DEFAULT 20,
                turn_count        INTEGER NOT NULL DEFAULT 0,
                status            TEXT NOT NULL DEFAULT 'running'
                                  CHECK (status IN ('running','ok','failed','skipped')),
                error             TEXT,
                summary           TEXT,
                last_message_at   INTEGER,
                finished_at       INTEGER,
                is_deleted        INTEGER NOT NULL DEFAULT 0,
                deleted_at        INTEGER,
                created_at        INTEGER NOT NULL,
                updated_at        INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_task_runs_task ON task_runs(task_id, created_at DESC);

            CREATE TRIGGER IF NOT EXISTS trg_task_run_delete_chat
            AFTER DELETE ON task_runs
            BEGIN
                DELETE FROM chat_message WHERE session_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_task_orphan_runs
            AFTER DELETE ON task_defs
            BEGIN
                DELETE FROM task_runs WHERE task_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_task_hide_runs
            AFTER UPDATE OF is_deleted ON task_defs
            WHEN NEW.is_deleted = 1 AND OLD.is_deleted = 0
            BEGIN
                UPDATE task_runs
                SET is_deleted = 1, deleted_at = COALESCE(NEW.deleted_at, OLD.deleted_at)
                WHERE task_id = NEW.id AND is_deleted = 0;
            END;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 笔记改用标题寻址，并给向量库的条目分出来源。
    ///
    /// <b>笔记为什么可以用标题当主键</b>：给 AI 一个 32 位十六进制 id，它会抄错、会臆造、
    /// 会在改笔记时把 id 写进正文里；而标题是人本来就会说的东西（「把部署那条笔记改一下」）。
    /// 代价是同库内标题必须唯一 —— 这条约束换来的是「AI 说标题就一定能定位到唯一一条」。
    ///
    /// <b>建唯一索引前要先清重</b>：老库里如果已经存在同名笔记（初始版本没有约束），
    /// 直接建索引会抛 UNIQUE constraint failed，整个升级卡死。这里按 updated_at 保留最新的那条，
    /// 其余删掉 —— 同名笔记里的旧内容本来也已经被覆盖掉了。
    ///
    /// <b>kb_documents 加 kind</b>：文件导入的条目归文件所有（删了会随下次索引复活），
    /// AI 用工具插进来的文字没有磁盘文件对应（只能靠工具删）。这两种条目在删除行为上完全不同，
    /// 得能分辨，否则「删除」要么删不动、要么删了又回来。
    /// </summary>
    private static void RunMigrationV32(SqliteConnection conn)
    {
        foreach (var sql in new[]
                 {
                     "ALTER TABLE kb_notes ADD COLUMN summary TEXT;",
                     "ALTER TABLE kb_documents ADD COLUMN kind TEXT NOT NULL DEFAULT 'file';",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ROW_NUMBER 而不是相关子查询：后者要针对每一行重算一次「同组最新」，
        // 而且一旦子查询返回 NULL，NOT IN 会整体判假、一条都删不掉。
        const string dedupe = """
            DELETE FROM kb_notes WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid,
                           ROW_NUMBER() OVER (
                               PARTITION BY library_id, title ORDER BY updated_at DESC
                           ) AS rn
                    FROM kb_notes
                ) WHERE rn > 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_kb_notes_title ON kb_notes(library_id, title);
            """;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = dedupe;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 知识库：四个新表 + 三处外挂列。
    ///
    /// <b>为什么笔记与向量分表</b>：笔记是用户手写、随手改的短文本，读它不需要嵌入模型；
    /// 向量那部分是「文件 → 切块 → 向量」。两者唯一的共同点是挂在同一个库里，
    /// 硬塞进一张表会让「没配嵌入模型也照样用笔记」这件事变成到处加过滤条件。
    ///
    /// <b>kb_chunks 存的是切块后的正文，不是文件路径</b>：原文件被删掉之后，已经索引的内容
    /// 仍然可检索 —— 索引一旦建好就是知识库里的一份数据，不该被外部文件的状态牵制。
    /// 向量用 float32 小端原始字节存 blob，<c>dim</c> 一并记下：
    /// 反序列化时按维度自己算，不依赖 sqlite-vec 这类需要分发的原生扩展。
    ///
    /// <b>会话与工作空间各加一列 kb_id / default_kb_id</b>：与 permission_mode 同一个思路 ——
    /// 会话上的是「这个会话用哪个库」，工作空间上的是「在这里新建会话时默认用哪个」。
    /// </summary>
    private static void RunMigrationV31(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS kb_libraries (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                description TEXT,
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS kb_notes (
                id         TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                title      TEXT NOT NULL,
                content    TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_kb_notes_lib ON kb_notes(library_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS kb_documents (
                id          TEXT PRIMARY KEY,
                library_id  TEXT NOT NULL,
                path        TEXT NOT NULL,
                title       TEXT NOT NULL,
                hash        TEXT,
                size        INTEGER NOT NULL DEFAULT 0,
                modified_at INTEGER NOT NULL DEFAULT 0,
                chunk_count INTEGER NOT NULL DEFAULT 0,
                indexed_at  INTEGER NOT NULL DEFAULT 0,
                status      TEXT NOT NULL DEFAULT 'pending',
                error       TEXT,
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_kb_docs_path ON kb_documents(library_id, path);

            CREATE TABLE IF NOT EXISTS kb_chunks (
                id          TEXT PRIMARY KEY,
                library_id  TEXT NOT NULL,
                document_id TEXT NOT NULL,
                ord         INTEGER NOT NULL,
                heading     TEXT,
                text        TEXT NOT NULL,
                embedding   BLOB,
                dim         INTEGER NOT NULL DEFAULT 0,
                model_id    TEXT,
                created_at  INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_kb_chunks_doc ON kb_chunks(document_id, ord);
            CREATE INDEX IF NOT EXISTS idx_kb_chunks_lib ON kb_chunks(library_id);
            """;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = schema;
            cmd.ExecuteNonQuery();
        }

        foreach (var sql in new[]
                 {
                     "ALTER TABLE chat_sessions ADD COLUMN kb_id TEXT;",
                     "ALTER TABLE workspace_sessions ADD COLUMN kb_id TEXT;",
                     "ALTER TABLE workspaces ADD COLUMN default_kb_id TEXT;",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// API 请求流水（api_requests 表）：<b>一次 HTTP 往返一行</b>，供「统计」页聚合。
    ///
    /// <b>为什么另开一张表，而不是用 chat_message.usage_json 现算</b>：
    /// <list type="bullet">
    ///   <item>不是每次请求都有消息：话题命名、上下文压缩、连接测试都发请求但没有 chat_message 行。</item>
    ///   <item>消息会被复制与删除：编辑重发、重新生成、软删都会让同一份用量重复或消失，
    ///         直接加总会算错。</item>
    ///   <item>chat_message 缺关键维度：服务商、HTTP 状态码、首字延迟、错误类型都没有。</item>
    /// </list>
    ///
    /// <b>只存「查得到、算得准」的东西</b>：不存请求体与响应体（体积），不存 api_key，
    /// endpoint 落库前剥掉 query（有的中转站把密钥塞在 URL 里）。
    /// token 用 NULL 表示「这次没拿到用量」，不写 0 —— 0 会混进平均值。
    /// cost 是<b>写入时按当时的价格算好的快照</b>：价格之后被改，历史金额不该跟着变。
    /// </summary>
    private static void RunMigrationV30(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS api_requests (
                id               TEXT PRIMARY KEY,
                kind             TEXT NOT NULL,
                status           TEXT NOT NULL,
                attempt          INTEGER NOT NULL DEFAULT 1,
                session_id       TEXT,
                turn_id          TEXT,
                message_id       TEXT,
                provider_name    TEXT,
                model_id         TEXT,
                model_name       TEXT,
                protocol         TEXT,
                endpoint         TEXT,
                http_status      INTEGER,
                error_kind       TEXT,
                error_message    TEXT,
                input_tokens     INTEGER,
                cache_hit_tokens INTEGER,
                output_tokens    INTEGER,
                cost             REAL,
                currency         TEXT,
                ttft_ms          INTEGER,
                duration_ms      INTEGER,
                message_count    INTEGER,
                tool_count       INTEGER,
                started_at       INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_api_req_started ON api_requests (started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_api_req_session ON api_requests (session_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_api_req_model ON api_requests (model_name, started_at);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 工具超时覆盖（tool_timeouts 表）：一行 = 某个工具单独配过的等待时长。
    ///
    /// <b>为什么不给 tool_tools 加列</b>：内置工具、插件工具、MCP 工具都不在 tool_tools 里
    /// （内置的定义在代码里，插件与 MCP 的是运行时注册的），加列就只能配脚本工具，
    /// 而需要更久的往往正是 bash / 插件这类。一张只有两列的覆盖表四类来源通吃，
    /// 没有覆盖行的工具仍然走默认的 5 分钟。
    ///
    /// <b>seconds = 0 表示一直等</b>：不是「1 秒」，也不是「用默认」——「用默认」由「没有这一行」表达。
    /// </summary>
    private static void RunMigrationV28(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_timeouts (
                tool_id   TEXT PRIMARY KEY,
                seconds   INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 插件会话（plugin_sessions 表）：插件通过 <c>ISessionFacade.CreateSessionAsync</c> 给自己开的会话区。
    ///
    /// <b>为什么单开一张表而不是给 chat_sessions 加 plugin_id 列</b>：会话列表、默认模型、
    /// 「已删除会话」恢复页、通知跳转这些查询都是整表扫描，加列就得给每处补类型过滤，
    /// 漏一处插件会话就会冒进用户界面。单独建表则一处都不用改，解析入口只有
    /// <c>SessionProfileResolver</c>（与 subagent_sessions 同一套约定）。
    ///
    /// 消息仍然复用 chat_message（session_id 即本表 id），所以这里只管「一行会话配置 + 归属」。
    /// <b>不建与 chat_sessions 的级联触发器</b>：插件会话不依附任何用户会话，它的生命周期
    /// 由插件自己调 <c>DeleteSessionAsync</c> 决定。
    /// </summary>
    private static void RunMigrationV27(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS plugin_sessions (
                id               TEXT PRIMARY KEY,
                plugin_id        TEXT NOT NULL,
                title            TEXT NOT NULL,
                model            TEXT,
                thinking         INTEGER NOT NULL DEFAULT 1,
                session_prompt   TEXT,
                enabled_tool_ids TEXT,
                enabled_skills   TEXT,
                mcp_servers      TEXT,
                permission_mode  INTEGER NOT NULL DEFAULT 1,
                last_message_at  INTEGER,
                is_deleted       INTEGER NOT NULL DEFAULT 0,
                deleted_at       INTEGER,
                created_at       INTEGER NOT NULL,
                updated_at       INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_plugin_session_owner ON plugin_sessions(plugin_id, is_deleted);

            CREATE TRIGGER IF NOT EXISTS trg_plugin_delete_chat
            AFTER DELETE ON plugin_sessions
            BEGIN
                DELETE FROM chat_message WHERE session_id = OLD.id;
            END;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
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
                protocol               TEXT NOT NULL DEFAULT 'OpenAI',
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

    /// <summary>
    /// 智能体定义：agents 表（系统提示词 + 默认工具/技能/MCP，均为 JSON 数组列）。
    /// 建表时只服务子 Agent，V34 起加 kind 列与角色的默认配置列，一张表同时装「角色」和「子 Agent」。
    /// </summary>
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
    /// 放开 <c>chat_message.role</c> 的取值范围：加上 <c>'agent'</c>（V17）、<c>'error'</c>（V20）。
    ///
    /// 这一列在 V1 建表时带了 <c>CHECK (role IN ('user','assistant','tool'))</c>：上下文压缩要插
    /// <c>role='agent'</c> 的记录（见 <c>ContextCompression</c>），请求出错要插 <c>role='error'</c>
    /// 的记录（见 <c>ChatError</c>）。SQLite 改不了 CHECK 约束，只能重建表 ——
    /// 流程与注意事项见 <see cref="RebuildChatMessageTable"/>。
    /// </summary>
    private static void RunMigrationV17(SqliteConnection conn)
        => RebuildChatMessageTable(conn, "chat_message_v17", "'user','assistant','tool','agent'");

    /// <summary>
    /// 工作空间默认模型：workspaces 增加 default_model_id（存模型 id，空表示不指定）。
    /// 指定后该工作空间新建的会话用这个模型，否则回落到「上次使用的模型」。
    /// </summary>
    private static void RunMigrationV18(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE workspaces ADD COLUMN default_model_id TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 内置模型表接入后新增的列。
    ///
    /// 全部给了默认值，老行的语义不被改写。特别是 <c>config_origin</c> 默认 3（Manual）：
    /// 升级前用户手填的模型本来就是手工配置的，内置表上线不该让它们突然挂上「配置需要补充」。
    /// 可空的三态列（supports_array_content / temperature_supported）留 NULL，
    /// 表示「不知道」—— 与 0（明确不支持）是两回事，不能合并。
    /// </summary>
    private static void RunMigrationV19(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE ai_model ADD COLUMN vendor TEXT;
            ALTER TABLE ai_model ADD COLUMN max_output_tokens INTEGER NOT NULL DEFAULT -1;
            ALTER TABLE ai_model ADD COLUMN supports_array_content INTEGER;
            ALTER TABLE ai_model ADD COLUMN temperature_supported INTEGER;
            ALTER TABLE ai_model ADD COLUMN config_origin INTEGER NOT NULL DEFAULT 3;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 服务商多端点：新建 ai_provider_endpoint 表，并把每个服务商原有的
    /// protocol / endpoint 迁成它的第一个端点（默认端点）。
    ///
    /// 为什么把端点拆出来：同一家（尤其中转站）常常同时提供 /chat/completions 与 /responses
    /// 两套接口，只留一个协议就只能二选一。ai_provider.protocol / endpoint 保留为
    /// <b>默认端点的镜像</b> —— 请求层照旧只读这两列，不必知道端点表的存在。
    /// </summary>
    private static void RunMigrationV22(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS ai_provider_endpoint (
                id          TEXT PRIMARY KEY,
                provider_id TEXT NOT NULL,
                protocol    TEXT NOT NULL DEFAULT 'OpenAI',
                endpoint    TEXT NOT NULL,
                is_default  INTEGER NOT NULL DEFAULT 0,
                sort_order  INTEGER NOT NULL DEFAULT 0,
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL,
                FOREIGN KEY (provider_id) REFERENCES ai_provider(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_endpoint_provider ON ai_provider_endpoint(provider_id);

            INSERT INTO ai_provider_endpoint
                (id, provider_id, protocol, endpoint, is_default, sort_order, created_at, updated_at)
            SELECT lower(hex(randomblob(16))), id, protocol, endpoint, 1, 0, created_at, updated_at
            FROM ai_provider
            WHERE endpoint IS NOT NULL AND endpoint <> ''
              AND NOT EXISTS (SELECT 1 FROM ai_provider_endpoint e WHERE e.provider_id = ai_provider.id);
            """;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>同 V17，只是把 <c>'error'</c> 也加进 role 的合法取值。</summary>
    private static void RunMigrationV20(SqliteConnection conn)
        => RebuildChatMessageTable(conn, "chat_message_v20", "'user','assistant','tool','agent','error'");

    /// <summary>
    /// 再放开一次 role：加上 <c>'summary'</c>（回复总结，见 <c>ContextCompression.SummaryRole</c>）。
    ///
    /// 漏掉它时「启用回复总结」一打开，第一轮跑完写总结就会撞
    /// <c>CHECK constraint failed: role IN (...)</c> —— 用户看到的是「总结失败」，
    /// 原因是这一列的取值范围还停在 V20 那一版。
    /// </summary>
    private static void RunMigrationV25(SqliteConnection conn)
        => RebuildChatMessageTable(
            conn, "chat_message_v25", "'user','assistant','tool','agent','error','summary'");

    /// <summary>
    /// 「继续」接出来的那一段：chat_message 增加 continued_from（承接的那一轮的 turn_id）。
    ///
    /// 只写在这一段的第一条消息上，其余为 NULL。界面据此把这一段画成<b>同一轮的后半截</b>
    /// （不重复模型头像、不在中间插操作栏），删除时整条链一起走。
    ///
    /// <b>这里是 ALTER 而不是重建表</b>：加一列不触 CHECK 约束，
    /// 重建反而要让前面几个重建迁移（V17 / V20 / V25）认识这一列 —— 那些迁移跑在它之前，
    /// 老库里根本没有这一列，重建时的 INSERT … SELECT 会直接报 no such column。
    /// 因此：将来若再做重建（V27+），必须把 continued_from 一并写进新表结构与拷贝列里。
    /// </summary>
    private static void RunMigrationV26(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE chat_message ADD COLUMN continued_from TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 回复总结改成挂在「那一轮最后一条消息的 metadata」上（键 <c>turn_summary</c>）。
    ///
    /// <b>为什么改</b>：总结单占一行就要新申请一个 sequence_number，而那是「查最大 + 1 再插」的
    /// 两次 I/O —— 一轮任务正在落工具结果时插一条总结，两边会拿到同一个序号、撞上
    /// <c>UNIQUE(session_id, sequence_number)</c>，整个工具循环当场抛异常断在那里
    /// （症状：发送按钮恢复可点、工具卡停在「正在接收参数」、列表一直转圈）。
    /// 写已有行的 metadata 是纯 UPDATE，不碰序号，也不会与任何插入竞争。
    ///
    /// 存量数据要搬过去，否则旧总结会变成孤儿行：既不参与上下文，又占着会话里的位置。
    /// 做法是「先搬到目标行、再软删原行」—— 不硬删，出问题时还能从库里捞回来。
    /// </summary>
    private static void RunMigrationV29(SqliteConnection conn)
    {
        const string migrate = """
            UPDATE chat_message
            SET metadata = json_set(
                    CASE WHEN json_valid(metadata) THEN metadata ELSE '{}' END,
                    '$.turn_summary',
                    (SELECT s.content FROM chat_message s
                     WHERE s.session_id = chat_message.session_id
                       AND s.turn_id = chat_message.turn_id
                       AND s.role = 'summary' AND s.is_deleted = 0
                     ORDER BY s.sequence_number DESC LIMIT 1))
            WHERE is_deleted = 0
              AND role IN ('assistant','tool')
              -- 只写在该轮最后一条 AI 消息上（最后一条可能是 tool：被打断的轮次就停在那里）
              AND sequence_number = (
                    SELECT MAX(x.sequence_number) FROM chat_message x
                    WHERE x.session_id = chat_message.session_id
                      AND x.turn_id = chat_message.turn_id
                      AND x.role IN ('assistant','tool') AND x.is_deleted = 0)
              AND EXISTS (
                    SELECT 1 FROM chat_message s
                    WHERE s.session_id = chat_message.session_id
                      AND s.turn_id = chat_message.turn_id
                      AND s.role = 'summary' AND s.is_deleted = 0);
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = migrate;
        cmd.ExecuteNonQuery();

        using var softDelete = conn.CreateCommand();
        softDelete.CommandText = "UPDATE chat_message SET is_deleted = 1, deleted_at = $now WHERE role = 'summary' AND is_deleted = 0;";
        softDelete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        softDelete.ExecuteNonQuery();
    }

    /// <summary>
    /// 工具来源：tool_tools 增加 created_by（user / agent）。
    ///
    /// 记它是因为 AI 自举之后，「这个工具是谁建的」决定了用户该用什么态度看它 ——
    /// 模型自己写的脚本需要一个显眼的标记，用户才好审计和批量清理。
    /// 存量行（含内置）一律按 user 处理：它们本来就是人工或随程序发布的。
    /// </summary>
    private static void RunMigrationV21(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE tool_tools ADD COLUMN created_by TEXT NOT NULL DEFAULT 'user';";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 服务商头像：ai_provider 增加 icon（图标名；空表示没有 logo，界面退回首字头像）。
    /// 从预设服务商添加时自动写入；老行语义不变，它们本来就是手工配的。
    /// </summary>
    private static void RunMigrationV23(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE ai_provider ADD COLUMN icon TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 子 Agent 会话：新建 subagent_sessions 表。
    ///
    /// <b>为什么不塞进 chat_sessions</b>：会话列表、默认模型、「已删除会话」恢复页都是整表扫描，
    /// 混在一张表里就得给每处查询补 <c>kind</c> 过滤，漏一处的后果是子会话冒进用户界面。
    /// 单独建表则那些查询一行都不用改，解析入口仍只有 <c>SessionProfileResolver</c> 一处。
    /// 消息照旧复用 chat_message（多态关联，无外键）。
    ///
    /// 两个触发器负责「父会话没了，子会话跟着走」：硬删清掉行与消息，软删把 is_deleted 带下去。
    /// 父会话可能是普通会话，也可能是工作空间会话，所以两边都挂。
    /// </summary>
    private static void RunMigrationV24(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS subagent_sessions (
                id                TEXT PRIMARY KEY,
                agent_id          TEXT NOT NULL,
                agent_name        TEXT NOT NULL,
                parent_session_id TEXT NOT NULL,
                title             TEXT NOT NULL,
                model             TEXT,
                thinking          INTEGER NOT NULL DEFAULT 1,
                session_prompt    TEXT,
                base_directory    TEXT,
                enabled_tool_ids  TEXT,
                enabled_skills    TEXT,
                mcp_servers       TEXT,
                permission_mode   INTEGER NOT NULL DEFAULT 1,
                max_turns         INTEGER NOT NULL DEFAULT 20,
                turn_count        INTEGER NOT NULL DEFAULT 0,
                status            TEXT NOT NULL DEFAULT 'running'
                                  CHECK (status IN ('running','ok','max_turns','failed')),
                last_message_at   INTEGER,
                is_deleted        INTEGER NOT NULL DEFAULT 0,
                deleted_at        INTEGER,
                created_at        INTEGER NOT NULL,
                updated_at        INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_subagent_parent ON subagent_sessions(parent_session_id);

            CREATE TRIGGER IF NOT EXISTS trg_subagent_delete_chat
            AFTER DELETE ON subagent_sessions
            BEGIN
                DELETE FROM chat_message WHERE session_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_subagent_orphan_chat
            AFTER DELETE ON chat_sessions
            BEGIN
                DELETE FROM subagent_sessions WHERE parent_session_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_subagent_orphan_ws
            AFTER DELETE ON workspace_sessions
            BEGIN
                DELETE FROM subagent_sessions WHERE parent_session_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_subagent_hide_chat
            AFTER UPDATE OF is_deleted ON chat_sessions
            WHEN NEW.is_deleted = 1 AND OLD.is_deleted = 0
            BEGIN
                UPDATE subagent_sessions
                SET is_deleted = 1, deleted_at = COALESCE(NEW.deleted_at, OLD.deleted_at)
                WHERE parent_session_id = NEW.id AND is_deleted = 0;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_subagent_hide_ws
            AFTER UPDATE OF is_deleted ON workspace_sessions
            WHEN NEW.is_deleted = 1 AND OLD.is_deleted = 0
            BEGIN
                UPDATE subagent_sessions
                SET is_deleted = 1, deleted_at = COALESCE(NEW.deleted_at, OLD.deleted_at)
                WHERE parent_session_id = NEW.id AND is_deleted = 0;
            END;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 重建 chat_message：SQLite 不能删改 CHECK 约束，放开 role 取值只能这么做。
    ///
    /// 重建是破坏性操作，所以做了三件保命的事：
    /// <list type="number">
    ///   <item>整个流程在一个事务里，中途任何一步失败都整体回滚，不会留下半个表；</item>
    ///   <item>拷完立刻核对行数，对不上就抛异常触发回滚；</item>
    ///   <item>先把<b>所有引用 chat_message 的触发器</b>（<c>trg_msg_delete_chat</c>、
    ///     <c>trg_subagent_delete_chat</c> 等）连同 SQL 一起摘下来、最后原样建回来 —— 改名那一步会
    ///     检查整个 schema，而它们引用的旧表在中间那个瞬间是不存在的，留着任何一个都会让 RENAME
    ///     报 <c>no such table: main.chat_message</c>。</item>
    /// </list>
    /// 新表的列与约束必须与当前实际结构一致（含 ALTER 陆续补上的 duration_ms 等），
    /// 少一列就等于把那一列的数据丢掉。
    /// </summary>
    /// <param name="tempTable">中间表名。每个迁移用各自的：中途失败的迁移会把名字占住。</param>
    /// <param name="roleValues">role 的合法取值（已带引号的 SQL 字面量）。</param>
    private static void RebuildChatMessageTable(SqliteConnection conn, string tempTable, string roleValues)
    {
        var createTable = $"""
            CREATE TABLE {tempTable} (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL,
                turn_id         TEXT NOT NULL,
                sequence_number INTEGER NOT NULL,
                role            TEXT NOT NULL CHECK (role IN ({roleValues})),
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

        var copyRows = $"""
            INSERT INTO {tempTable}
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
            // 引用 chat_message 的触发器要全部先摘掉，不能只摘 trg_msg_delete_chat：
            // V24 的 trg_subagent_delete_chat 挂在 subagent_sessions 上、却引用这张表，
            // 留着它会让下面 RENAME 那一刻的 schema 校验直接报
            // "no such table: main.chat_message"。摘之前把 SQL 存下来，改名后原样建回。
            var triggers = ReadTriggersReferencingChatMessage(conn, transaction);
            foreach (var (name, _) in triggers)
                Execute(conn, transaction, $"DROP TRIGGER IF EXISTS \"{name.Replace("\"", "\"\"")}\";");

            Execute(conn, transaction, createTable);
            Execute(conn, transaction, copyRows);

            var copied = CountMessages(conn, transaction);
            if (copied != before)
                throw new InvalidOperationException($"chat_message 重建时行数对不上：{before} → {copied}");

            Execute(conn, transaction, "DROP TABLE chat_message;");
            Execute(conn, transaction, $"ALTER TABLE {tempTable} RENAME TO chat_message;");
            Execute(conn, transaction, recreateIndexes);

            foreach (var (_, sql) in triggers)
                Execute(conn, transaction, sql);
            // 兜底：库里本来就没有 trg_msg_delete_chat 时（理论上不会），重建出的新表也得带上它。
            Execute(conn, transaction, recreateTrigger);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 读出所有引用 chat_message 的触发器（名字 + 原始建表 SQL）。
    ///
    /// 重建表前必须把这类触发器摘掉：<c>DROP TABLE chat_message</c> 之后、临时表改名之前的
    /// 那一瞬间，schema 里还挂着指向旧表的触发器，SQLite 会在 RENAME 时校验 schema 并直接报错。
    /// 按 <c>sqlite_master.sql</c> 抓而不是写死名字 —— 以后再往别的表上挂引用 chat_message 的
    /// 触发器，这里不用跟着改。
    /// </summary>
    private static List<(string Name, string Sql)> ReadTriggersReferencingChatMessage(
        SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT name, sql FROM sqlite_master
            WHERE type = 'trigger' AND sql IS NOT NULL AND sql LIKE '%chat_message%';
            """;

        using var reader = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
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
