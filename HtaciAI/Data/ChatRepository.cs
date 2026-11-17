using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HtaciAI.Models;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 鏅鸿兘瀵硅瘽浠撳偍锛氫細璇濅笌娑堟伅鐨勮鍐欍€?
/// </summary>
public static class ChatRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 浼氳瘽 ----

    /// <summary>
    /// 鍏ㄩ儴鏈垹闄や細璇濓紝鎸?b>鐪熷疄瀵硅瘽鏃堕棿</b>鍊掑簭锛堟渶杩戝彂杩囨秷鎭殑鍦ㄥ墠锛夈€?
    ///
    /// 鈿狅笍 鎺掑簭鍒绘剰鐢?<c>COALESCE(last_message_at, created_at)</c> 鑰?b>涓嶆槸</b> <c>updated_at</c>锛?
    /// <c>updated_at</c> 浼氳浠讳綍涓€娆″啓搴撴姮楂樷€斺€旀敼妯″瀷/鎶€鑳?宸ュ叿/MCP/鏉冮檺銆佹敼鍚嶏紝鐢氳嚦鍙槸
    /// <b>鎵撳紑浼氳瘽</b>锛堟墦寮€浼氭仮澶嶈崏绋匡紝鑽夌鍒椾竴鍙樺氨浼氬啓涓€娆″簱锛夈€傜敤瀹冩帓搴忕殑缁撴灉鏄€岀偣寮€鐪嬩竴鐪?
    /// 灏变細璺冲埌鍒楄〃鏈€鍓嶉潰銆嶏紝鑰岀敤鎴疯鐨勬槸銆屽彧鏈夊彂浜嗘柊璇锋眰鎵嶇畻鏈€杩戠敤杩囥€嶃€?
    /// <c>last_message_at</c> 鍏ㄤ粨鍙湪鐪熸鍙戣姹傛椂鍐欙紝姝ｆ槸杩欎釜璇箟锛涙病鏈夋秷鎭殑浼氳瘽閫€鍥炲垱寤烘椂闂达紝
    /// 浜庢槸鍒氬缓鍑烘潵鐨勭┖浼氳瘽浠嶇劧鎺掑湪鏈€鍓嶏紙杩欐槸瀵圭殑锛屽畠纭疄鏄柊鐨勶級銆?
    /// </summary>
    public static async Task<List<ChatSession>> GetAllAsync()
    {
        var list = new List<ChatSession>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, model, system_prompt, thinking, is_deleted, deleted_at, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, draft, mcp_servers, kb_id
            FROM chat_sessions WHERE is_deleted = 0
            ORDER BY COALESCE(last_message_at, created_at) DESC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapSession(r));
        return list;
    }

    public static async Task<ChatSession?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, model, system_prompt, thinking, is_deleted, deleted_at, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, draft, mcp_servers, kb_id
            FROM chat_sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? MapSession(r) : null;
    }

    /// <summary>
    /// 鏈€杩戜竴娆＄敤杩囩殑妯″瀷鍚嶄笌浣跨敤鏃堕棿锛屼緵銆屾柊浼氳瘽榛樿妯″瀷 = 涓婃浣跨敤鐨勬ā鍨嬨€嶄娇鐢ㄣ€?
    /// 鎺掑簭鍙ｅ緞涓?<see cref="GetAllAsync"/> 涓€鑷达紙鍙湅鐪熷疄瀵硅瘽鏃堕棿锛夛紝鍚﹀垯
    /// 銆屼笂娆′娇鐢ㄧ殑妯″瀷銆嶄細琚€岄殢鎵嬬偣寮€鏌愪釜浼氳瘽銆嶈繖绉嶅姩浣滄薄鏌撱€?
    /// 娌℃湁鍙敤浼氳瘽鏃惰繑鍥?null銆?
    /// </summary>
    public static async Task<(string Model, long At)?> GetLastUsedModelAsync()
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT model, COALESCE(last_message_at, created_at) AS at FROM chat_sessions
            WHERE is_deleted = 0 AND model IS NOT NULL AND model <> ''
            ORDER BY at DESC
            LIMIT 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    public static async Task CreateAsync(ChatSession s)
    {
        var now = Now();
        if (s.CreatedAt == 0) s.CreatedAt = now;
        if (s.UpdatedAt == 0) s.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_sessions (id, title, model, system_prompt, thinking, created_at, updated_at, enabled_skills, enabled_tool_ids, last_message_at, permission_mode, mcp_servers, kb_id)
            VALUES ($id, $title, $model, $system_prompt, $thinking, $created_at, $updated_at, $enabled_skills, $enabled_tool_ids, $last_message_at, $permission_mode, $mcp_servers, $kb_id);
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$created_at", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeStringList(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeStringList(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kb_id", (object?)s.KnowledgeBaseId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(ChatSession s)
    {
        s.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_sessions
            SET title = $title, model = $model, system_prompt = $system_prompt,
                thinking = $thinking, enabled_skills = $enabled_skills,
                enabled_tool_ids = $enabled_tool_ids, last_message_at = $last_message_at,
                permission_mode = $permission_mode, mcp_servers = $mcp_servers, kb_id = $kb_id,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$system_prompt", (object?)s.SystemPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", s.Thinking);
        cmd.Parameters.AddWithValue("$enabled_skills", (object?)SerializeSkills(s.EnabledSkills) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled_tool_ids", (object?)SerializeStringList(s.EnabledToolIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_message_at", (object?)s.LastMessageAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$permission_mode", (int)s.ToolPermissionMode);
        cmd.Parameters.AddWithValue("$mcp_servers", (object?)SerializeStringList(s.McpServers) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kb_id", (object?)s.KnowledgeBaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 鍙洿鏂拌崏绋垮垪锛屼笉纰板叾浣欏瓧娈碉紙<b>灏ゅ叾涓嶇 updated_at</b>锛夈€?
    /// 涓?<see cref="UpdateAsync"/> 鍒嗗紑鏄埢鎰忕殑锛氭暣琛ㄦ洿鏂颁細鎶婁細璇濆璞′笂鐨勬棫 draft 鍐欏洖鍘伙紝
    /// 鍐叉帀銆屽垰鏁茶繘鍘汇€佽繕娌℃潵寰楀強鍚屾鍒颁細璇濆璞°€嶇殑鍐呭銆?
    ///
    /// 涓轰粈涔堜笉鍔?updated_at锛氳崏绋挎槸銆屾墦瀛楄繃绋嬨€嶈€屼笉鏄€屼細璇濇椿鍔ㄣ€嶃€傚畠浼氶殢鐫€姣忔杈撳叆钀藉簱锛?
    /// 鑰屼笖鍦?b>鎵撳紑浼氳瘽鎭㈠鑽夌</b>鏃朵篃浼氳瑙﹀彂涓€娆♀€斺€旀棭鏈熺増鏈『甯﹀啓浜?updated_at锛?
    /// 浜庢槸銆岀偣寮€鐪嬩竴鐪笺€嶅氨鏀瑰啓浜嗕細璇濊褰曘€傛帓搴忓垪宸茬粡涓嶄緷璧?updated_at 浜嗭紙瑙?<see cref="GetAllAsync"/>锛夛紝
    /// 杩欓噷鍐嶆柇寮€涓€娆★紝鍏嶅緱灏嗘潵鍙堟湁浜烘嬁 updated_at 褰撱€屾渶杩戜娇鐢ㄣ€嶇敤銆?
    /// </summary>
    public static async Task SaveDraftAsync(string id, string draft)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET draft = $draft WHERE id = $id;";
        cmd.Parameters.AddWithValue("$draft", draft);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>杞垹闄や細璇濓細鏍囪 is_deleted=1 骞惰褰曞垹闄ゆ椂闂达紙娑堟伅淇濈暀锛屼究浜庡垹闄や細璇濈鐞嗛〉鎭㈠锛夈€?/summary>
    public static async Task SoftDeleteSessionAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_sessions SET is_deleted = 1, deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 娑堟伅 ----

    /// <summary>杞垹闄や竴杞紙AI 鍥炲锛夛細鏍囪璇?turn_id 涓嬫墍鏈夋秷鎭负宸插垹闄ゃ€?/summary>
    public static async Task SoftDeleteTurnAsync(string sessionId, string turnId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_message SET is_deleted = 1, deleted_at = $now WHERE session_id = $session_id AND turn_id = $turn_id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$turn_id", turnId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>杞垹闄ゅ崟鏉℃秷鎭紙鐢ㄦ埛娑堟伅锛夈€?/summary>
    public static async Task SoftDeleteMessageAsync(string sessionId, string messageId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_message SET is_deleted = 1, deleted_at = $now WHERE session_id = $session_id AND id = $message_id;";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$message_id", messageId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 鍙敼鐢ㄦ埛娑堟伅鐨勬鏂囷紙缂栬緫鐢ㄦ埛娑堟伅鐢級銆?
    /// 鍗曠嫭寮€涓€涓柟娉曡€屼笉鏄鐢?<see cref="UpdateMessageAsync"/>锛氬悗鑰呬細杩?thinking / 鐢ㄩ噺 / 鐘舵€?
    /// 涓€璧峰啓锛岄偅浜涘瓧娈靛涓€鏉″凡瀹屾垚鐨勭敤鎴锋秷鎭病鏈夋剰涔夛紝鍐欏洖鍘诲彧浼氭墿澶у嚭閿欓潰銆?
    /// </summary>
    public static async Task UpdateMessageContentAsync(string messageId, string content)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_message SET content = $content, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", messageId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<ChatMessage>> GetBySessionAsync(string sessionId)
    {
        var list = new List<ChatMessage>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, turn_id, sequence_number, role, content, thinking,
                   tool_call_id, tool_name, status, usage_json, model_name, metadata,
                   is_deleted, deleted_at, created_at, updated_at, duration_ms, continued_from
            FROM chat_message
            WHERE session_id = $session_id AND is_deleted = 0
            ORDER BY sequence_number ASC;
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapMessage(r));
        return list;
    }

    /// <summary>
    /// 鍙彇銆屽甫 usage 鐨勬秷鎭€嶏紝鎸変細璇濆垎缁勩€?b>姣忕粍浠庢柊鍒版棫</b>銆?
    ///
    /// <b>涓轰粈涔堜笉澶嶇敤 <see cref="GetBySessionAsync"/></b>锛氶偅涓柟娉曡鍏ㄩ噺鍘嗗彶锛堝惈姣忔潯姝ｆ枃锛夛紝
    /// 鑰岀敤閲忔眹鎬诲彧闇€瑕?usage_json銆傛彃浠朵竴娆￠棶鍗佷釜浼氳瘽鐨勭敤閲忔椂瑕佽鍗佷唤闀夸細璇濓紝
    /// 灏变负浜嗗姞鍑犱釜鏁板瓧 鈥斺€?杩欓噷鍙崬蹇呰鐨勫垪銆?
    ///
    /// 璋冪敤鏂规寜 <see cref="ContextUsage"/> 鐨勫彛寰勬壂姣忕粍鐨勫ご閮紙鏈€鏂颁紭鍏堬級锛?
    /// 鍙栧埌鐨勫氨鏄€屽綋鍓嶄笂涓嬫枃鍗犵敤銆嶏紝涓庣晫闈笂鐨勭敤閲忕幆鏄悓涓€涓暟銆?
    /// </summary>
    public static async Task<Dictionary<string, List<ChatMessage>>> GetUsageMessagesAsync(
        IReadOnlyList<string> sessionIds)
    {
        var result = new Dictionary<string, List<ChatMessage>>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        // 鍙傛暟閫愪釜缁戯細SQLite 鐨?IN 鍒楄〃娌℃湁鍙睍寮€鐨勫弬鏁拌娉?
        var names = new List<string>(sessionIds.Count);
        for (var i = 0; i < sessionIds.Count; i++)
        {
            var name = "$s" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, sessionIds[i]);
        }

        cmd.CommandText = $"""
            SELECT session_id, role, content, metadata, usage_json, sequence_number, created_at
            FROM chat_message
            WHERE session_id IN ({string.Join(", ", names)})
              AND is_deleted = 0
              AND usage_json IS NOT NULL
              AND usage_json <> ''
            ORDER BY session_id ASC, sequence_number DESC;
            """;

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var sessionId = r.GetString(0);
            if (!result.TryGetValue(sessionId, out var list))
            {
                list = new List<ChatMessage>();
                result[sessionId] = list;
            }

            list.Add(new ChatMessage
            {
                SessionId = sessionId,
                Role = r.GetString(1),
                Content = r.IsDBNull(2) ? null : r.GetString(2),
                Metadata = r.IsDBNull(3) ? null : r.GetString(3),
                UsageJson = r.IsDBNull(4) ? null : r.GetString(4),
                SequenceNumber = r.GetInt32(5),
                CreatedAt = r.GetInt64(6),
            });
        }

        return result;
    }

    public static async Task<int> GetNextSequenceAsync(string sessionId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM chat_message WHERE session_id = $session_id;";
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// 涓嬩竴涓?turn_id锛堜細璇濆唴閫掑鏁存暟 1銆?銆?鈥︼級銆傜敤鎴蜂笌 AI 鍚勫崰涓€涓柊 turn锛?
    /// 鍥犳姣忔 ChatAsync 浼氳璋冪敤涓ゆ锛堝厛鐢ㄦ埛鍚?AI锛夈€傚巻鍙叉暟鎹负 GUID 鏃?CAST 涓?0锛屽拷鐣ヤ笉璁°€?
    /// </summary>
    public static async Task<string> GetNextTurnIdAsync(string sessionId)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(CAST(turn_id AS INTEGER)), 0) + 1
            FROM chat_message WHERE session_id = $session_id;
            """;
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result).ToString();
    }

    /// <summary>
    /// 鎶?<paramref name="orderedIds"/> 鎸夌粰瀹氶『搴忔彃鍒扮 <paramref name="afterExclusive"/> 鏉′箣鍚?
    /// 锛堝嵆 sequence_number &gt; afterExclusive 鐨勬渶鍓嶉潰锛夛紝鍏朵綑娑堟伅鏁翠綋鍚庣Щ锛岀浉瀵归『搴忎笉鍙樸€?
    ///
    /// 閲嶆柊鐢熸垚涓棿鏌愪竴杞椂蹇呴』鐢ㄥ畠锛氭柊鍥炲鏄竟娴佸紡杈硅惤搴撶殑锛岃惤搴撴椂鍙兘鎷垮埌銆屽綋鍓嶆渶澶у€?+ 1銆嶏紝
    /// 涔熷氨鏄細璇濇湯灏撅紱涓嶆尓鍥炴潵鐨勮瘽锛岄噸鐢熸垚鐨勫洖澶嶄細璺戝埌鏈€鍚庝竴杞悗闈㈠幓銆?
    ///
    /// 鑵句綅缃垎涓夋锛屽厛鎶婅繖涓€娈垫暣浣撴尓杩?b>璐熸暟鍖洪棿</b>锛氱洿鎺ュ師鍦?+N 浼氭挒涓?
    /// <c>UNIQUE(session_id, sequence_number)</c> 鈥斺€?SQLite 鎸?rowid 椤哄簭閫愯鏇存柊锛?
    /// 鍓嶄竴琛屽姞瀹屽氨鍘嬪埌杩樻病绉诲姩鐨勮涓婏紝绔嬪埢鍐茬獊锛圲PDATE 涔熶笉鏀寔 ORDER BY 鏉ュ€掔潃璧帮級銆?
    /// 鎸埌璐熸暟鍖轰笌姝ゅ墠鐘舵€侊紙鍏ㄩ儴涓烘锛変笉閲嶅彔锛屾渶鍚庡啀缁熶竴绉诲洖銆傛暣涓繃绋嬪湪涓€涓簨鍔￠噷銆?
    /// </summary>
    public static async Task InsertMessagesAtAsync(string sessionId, int afterExclusive, IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count == 0) return;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        void Exec(string sql, Action<SqliteCommand>? bind = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }

        Exec(
            "UPDATE chat_message SET sequence_number = -sequence_number WHERE session_id = $sid AND sequence_number > $after;",
            c =>
            {
                c.Parameters.AddWithValue("$sid", sessionId);
                c.Parameters.AddWithValue("$after", afterExclusive);
            });

        for (var i = 0; i < orderedIds.Count; i++)
        {
            Exec(
                "UPDATE chat_message SET sequence_number = $seq WHERE id = $id;",
                c =>
                {
                    c.Parameters.AddWithValue("$seq", afterExclusive + 1 + i);
                    c.Parameters.AddWithValue("$id", orderedIds[i]);
                });
        }

        Exec(
            "UPDATE chat_message SET sequence_number = -sequence_number + $count WHERE session_id = $sid AND sequence_number < 0;",
            c =>
            {
                c.Parameters.AddWithValue("$count", orderedIds.Count);
                c.Parameters.AddWithValue("$sid", sessionId);
            });

        tx.Commit();
    }

    public static async Task InsertAsync(ChatMessage m)
    {
        var now = Now();
        if (m.CreatedAt == 0) m.CreatedAt = now;
        if (m.UpdatedAt == 0) m.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_message (id, session_id, turn_id, sequence_number, role, content,
                thinking, tool_call_id, tool_name, status, usage_json, model_name, metadata,
                is_deleted, deleted_at, created_at, updated_at, duration_ms, continued_from)
            VALUES ($id, $session_id, $turn_id, $sequence_number, $role, $content,
                $thinking, $tool_call_id, $tool_name, $status, $usage_json, $model_name, $metadata,
                $is_deleted, $deleted_at, $created_at, $updated_at, $duration_ms, $continued_from);
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$session_id", m.SessionId);
        cmd.Parameters.AddWithValue("$turn_id", m.TurnId);
        cmd.Parameters.AddWithValue("$sequence_number", m.SequenceNumber);
        cmd.Parameters.AddWithValue("$role", m.Role);
        cmd.Parameters.AddWithValue("$content", (object?)m.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", (object?)m.Thinking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_call_id", (object?)m.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_name", (object?)m.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", m.Status);
        cmd.Parameters.AddWithValue("$usage_json", (object?)m.UsageJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model_name", (object?)m.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)m.Metadata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_deleted", m.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted_at", (object?)m.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", m.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at", m.UpdatedAt);
        cmd.Parameters.AddWithValue("$duration_ms", (object?)m.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$continued_from", (object?)m.ContinuedFrom ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 娴佸紡缁撴潫鍚庡洖鍐欙細姝ｆ枃銆佹帹鐞嗗唴瀹广€佺姸鎬併€佺敤閲忋€?
    /// </summary>
    public static async Task UpdateMessageAsync(ChatMessage m)
    {
        m.UpdatedAt = Now();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE chat_message
            SET content = $content, thinking = $thinking, tool_call_id = $tool_call_id,
                tool_name = $tool_name, status = $status, usage_json = $usage_json,
                model_name = $model_name, metadata = $metadata, duration_ms = $duration_ms,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$content", (object?)m.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thinking", (object?)m.Thinking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_call_id", (object?)m.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool_name", (object?)m.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", m.Status);
        cmd.Parameters.AddWithValue("$usage_json", (object?)m.UsageJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model_name", (object?)m.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)m.Metadata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration_ms", (object?)m.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", m.UpdatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 鏄犲皠 ----

    private static ChatSession MapSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Model = r.IsDBNull(2) ? "" : r.GetString(2),
        SystemPrompt = r.IsDBNull(3) ? null : r.GetString(3),
        Thinking = r.GetInt32(4),
        IsDeleted = r.GetInt32(5) != 0,
        DeletedAt = r.IsDBNull(6) ? null : r.GetInt64(6),
        CreatedAt = r.GetInt64(7),
        UpdatedAt = r.GetInt64(8),
        EnabledSkills = r.IsDBNull(9) ? new() : ParseSkills(r.GetString(9)),
        EnabledToolIds = r.IsDBNull(10) ? new() : ParseStringList(r.GetString(10)),
        LastMessageAt = r.IsDBNull(11) ? null : r.GetInt64(11),
        ToolPermissionMode = (PermissionMode)r.GetInt32(12),
        Draft = r.IsDBNull(13) ? null : r.GetString(13),
        McpServers = r.IsDBNull(14) ? new() : ParseStringList(r.GetString(14)),
        KnowledgeBaseId = r.IsDBNull(15) ? null : r.GetString(15),
    };

    /// <summary>鎶婁細璇濆惎鐢ㄦ妧鑳藉簭鍒楀寲涓?JSON 鏁扮粍鏂囨湰锛堜緵 enabled_skills 鍒楀瓨鍌級銆?/summary>
    private static string SerializeSkills(List<SessionSkill> skills)
    {
        try { return JsonSerializer.Serialize(skills ?? new()); }
        catch { return "[]"; }
    }

    /// <summary>浠?JSON 鏁扮粍鏂囨湰瑙ｆ瀽浼氳瘽鍚敤鎶€鑳斤紱绌?鎹熷潖鏃惰繑鍥炵┖鍒楄〃銆?/summary>
    private static List<SessionSkill> ParseSkills(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<SessionSkill>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>鎶婂瓧绗︿覆鍒楄〃搴忓垪鍖栦负 JSON 鏁扮粍鏂囨湰锛坋nabled_tool_ids / mcp_servers 鍏辩敤锛夈€?/summary>
    private static string SerializeStringList(List<string> values)
    {
        try { return JsonSerializer.Serialize(values ?? new()); }
        catch { return "[]"; }
    }

    /// <summary>浠?JSON 鏁扮粍鏂囨湰瑙ｆ瀽瀛楃涓插垪琛紱绌?鎹熷潖鏃惰繑鍥炵┖鍒楄〃銆?/summary>
    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static ChatMessage MapMessage(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        SessionId = r.GetString(1),
        TurnId = r.GetString(2),
        SequenceNumber = r.GetInt32(3),
        Role = r.GetString(4),
        Content = r.IsDBNull(5) ? null : r.GetString(5),
        Thinking = r.IsDBNull(6) ? null : r.GetString(6),
        ToolCallId = r.IsDBNull(7) ? null : r.GetString(7),
        ToolName = r.IsDBNull(8) ? null : r.GetString(8),
        Status = r.GetString(9),
        UsageJson = r.IsDBNull(10) ? null : r.GetString(10),
        ModelName = r.IsDBNull(11) ? null : r.GetString(11),
        Metadata = r.IsDBNull(12) ? null : r.GetString(12),
        IsDeleted = r.GetInt32(13) != 0,
        DeletedAt = r.IsDBNull(14) ? null : r.GetInt64(14),
        CreatedAt = r.GetInt64(15),
        UpdatedAt = r.GetInt64(16),
        DurationMs = r.IsDBNull(17) ? null : r.GetInt64(17),
        ContinuedFrom = r.IsDBNull(18) ? null : r.GetString(18),
    };
}
