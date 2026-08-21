using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Tools;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>
/// 工具仓储（tool_toolsets / tool_tools / tool_tool_links 三表）。
/// 工具与工具集为多对多；未指定归属的工具自动归入内置默认集。
/// </summary>
public static class ToolRepository
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- 工具集 ----

    public static async Task<List<Toolset>> GetAllToolsetsAsync()
    {
        var list = new List<Toolset>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, is_builtin FROM tool_toolsets ORDER BY is_builtin DESC, name ASC;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new Toolset
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Description = r.IsDBNull(2) ? "" : r.GetString(2),
                IsBuiltin = r.GetInt32(3) != 0,
            });
        return list;
    }

    public static async Task CreateToolsetAsync(Toolset ts)
    {
        var now = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_toolsets (id, name, description, is_builtin, created_at, updated_at)
            VALUES ($id, $name, $description, $is_builtin, $created_at, $updated_at);
            """;
        cmd.Parameters.AddWithValue("$id", ts.Id);
        cmd.Parameters.AddWithValue("$name", ts.Name);
        cmd.Parameters.AddWithValue("$description", (object?)ts.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_builtin", ts.IsBuiltin ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at", now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteToolsetAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tool_toolsets WHERE id = $id AND is_builtin = 0;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 工具 ----

    public static async Task<List<ToolDefinition>> GetAllAsync()
    {
        var tools = new List<ToolDefinition>();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, description, source, runtime, target, input_schema, enabled FROM tool_tools ORDER BY created_at ASC;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                tools.Add(new ToolDefinition
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    Description = r.IsDBNull(2) ? "" : r.GetString(2),
                    Source = ParseSource(r.IsDBNull(3) ? "" : r.GetString(3)),
                    Runtime = ParseRuntime(r.IsDBNull(4) ? "" : r.GetString(4)),
                    Target = r.IsDBNull(5) ? null : r.GetString(5),
                    InputSchemaJson = r.IsDBNull(6) ? "{}" : r.GetString(6),
                    Enabled = r.GetInt32(7) != 0,
                });
            }
        }

        // 批量回填工具集关联
        var links = new Dictionary<string, List<string>>();
        using (var linkCmd = conn.CreateCommand())
        {
            linkCmd.CommandText = "SELECT tool_id, toolset_id FROM tool_tool_links;";
            await using var lr = await linkCmd.ExecuteReaderAsync();
            while (await lr.ReadAsync())
            {
                var toolId = lr.GetString(0);
                if (!links.TryGetValue(toolId, out var list))
                {
                    list = new List<string>();
                    links[toolId] = list;
                }
                list.Add(lr.GetString(1));
            }
        }
        foreach (var t in tools)
            if (links.TryGetValue(t.Id, out var ids) && ids.Count > 0)
                t.ToolsetIds = ids;

        return tools;
    }

    public static async Task CreateAsync(ToolDefinition tool)
    {
        var now = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tool_tools (id, name, description, source, runtime, target, input_schema, enabled, created_at, updated_at)
                VALUES ($id, $name, $description, $source, $runtime, $target, $input_schema, $enabled, $created_at, $updated_at);
                """;
            cmd.Parameters.AddWithValue("$id", tool.Id);
            cmd.Parameters.AddWithValue("$name", tool.Name);
            cmd.Parameters.AddWithValue("$description", (object?)tool.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", tool.Source.ToString());
            cmd.Parameters.AddWithValue("$runtime", tool.Runtime.ToString());
            cmd.Parameters.AddWithValue("$target", (object?)tool.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$input_schema", (object?)tool.InputSchemaJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", tool.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$created_at", now);
            cmd.Parameters.AddWithValue("$updated_at", now);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var tsId in tool.EffectiveToolsetIds)
            await InsertLinkAsync(conn, tool.Id, tsId);
    }

    public static async Task UpdateAsync(ToolDefinition tool)
    {
        var now = Now();
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tool_tools
                SET name = $name, description = $description, source = $source, runtime = $runtime,
                    target = $target, input_schema = $input_schema, enabled = $enabled, updated_at = $updated_at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", tool.Id);
            cmd.Parameters.AddWithValue("$name", tool.Name);
            cmd.Parameters.AddWithValue("$description", (object?)tool.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", tool.Source.ToString());
            cmd.Parameters.AddWithValue("$runtime", tool.Runtime.ToString());
            cmd.Parameters.AddWithValue("$target", (object?)tool.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$input_schema", (object?)tool.InputSchemaJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", tool.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated_at", now);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM tool_tool_links WHERE tool_id = $tool_id;";
            del.Parameters.AddWithValue("$tool_id", tool.Id);
            await del.ExecuteNonQueryAsync();
        }
        foreach (var tsId in tool.EffectiveToolsetIds)
            await InsertLinkAsync(conn, tool.Id, tsId);
    }

    public static async Task DeleteAsync(string id)
    {
        await using var conn = await DatabaseService.OpenWithForeignKeysAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tool_tools WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertLinkAsync(SqliteConnection conn, string toolId, string toolsetId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tool_tool_links (tool_id, toolset_id) VALUES ($tool_id, $toolset_id) ON CONFLICT DO NOTHING;";
        cmd.Parameters.AddWithValue("$tool_id", toolId);
        cmd.Parameters.AddWithValue("$toolset_id", toolsetId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ToolSource ParseSource(string s)
        => Enum.TryParse<ToolSource>(s, out var v) ? v : ToolSource.Script;

    private static ScriptRuntimeKind ParseRuntime(string s)
        => Enum.TryParse<ScriptRuntimeKind>(s, out var v) ? v : default;
}
