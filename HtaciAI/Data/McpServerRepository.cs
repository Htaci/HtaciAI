using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Models;
using Microsoft.Data.Sqlite;

namespace HtaciAI.Data;

/// <summary>MCP 服务端配置仓储（mcp_servers 表）。</summary>
public static class McpServerRepository
{
    private const string Columns =
        "id, name, transport, url, headers_raw, timeout_seconds, enabled, created_at, updated_at, " +
        "command, command_args, env, working_directory";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static async Task<List<McpServerConfig>> GetAllAsync()
    {
        var list = new List<McpServerConfig>();
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mcp_servers ORDER BY created_at;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Map(r));
        return list;
    }

    public static async Task<McpServerConfig?> GetAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mcp_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public static async Task CreateAsync(McpServerConfig server)
    {
        var now = Now();
        if (server.CreatedAt == 0) server.CreatedAt = now;
        server.UpdatedAt = now;

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mcp_servers (id, name, transport, url, headers_raw, timeout_seconds, enabled, created_at, updated_at,
                                     command, command_args, env, working_directory)
            VALUES ($id, $name, $transport, $url, $headers, $timeout, $enabled, $created, $updated,
                    $command, $args, $env, $cwd);
            """;
        AddParams(cmd, server);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateAsync(McpServerConfig server)
    {
        server.UpdatedAt = Now();

        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mcp_servers
            SET name = $name, transport = $transport, url = $url, headers_raw = $headers,
                timeout_seconds = $timeout, enabled = $enabled, updated_at = $updated,
                command = $command, command_args = $args, env = $env, working_directory = $cwd
            WHERE id = $id;
            """;
        AddParams(cmd, server);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteAsync(string id)
    {
        await using var conn = DatabaseService.CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mcp_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParams(SqliteCommand cmd, McpServerConfig s)
    {
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$name", s.Name);
        cmd.Parameters.AddWithValue("$transport", s.Transport.ToString());
        cmd.Parameters.AddWithValue("$url", (object?)s.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headers", (object?)s.HeadersRaw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$timeout", s.TimeoutSeconds);
        cmd.Parameters.AddWithValue("$enabled", s.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", s.CreatedAt);
        cmd.Parameters.AddWithValue("$updated", s.UpdatedAt);
        cmd.Parameters.AddWithValue("$command", (object?)s.Command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$args", (object?)s.ArgumentsRaw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$env", (object?)s.EnvRaw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cwd", (object?)s.WorkingDirectory ?? DBNull.Value);
    }

    private static McpServerConfig Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Transport = ParseTransport(r.IsDBNull(2) ? null : r.GetString(2)),
        Url = r.IsDBNull(3) ? "" : r.GetString(3),
        HeadersRaw = r.IsDBNull(4) ? "" : r.GetString(4),
        TimeoutSeconds = r.IsDBNull(5) ? 60 : r.GetInt32(5),
        Enabled = r.IsDBNull(6) || r.GetInt32(6) != 0,
        CreatedAt = r.GetInt64(7),
        UpdatedAt = r.GetInt64(8),
        Command = r.IsDBNull(9) ? "" : r.GetString(9),
        ArgumentsRaw = r.IsDBNull(10) ? "" : r.GetString(10),
        EnvRaw = r.IsDBNull(11) ? "" : r.GetString(11),
        WorkingDirectory = r.IsDBNull(12) ? "" : r.GetString(12),
    };

    /// <summary>入库写的是枚举名（StreamableHttp），这里忽略大小写解析，兼容手工写的 sse / streamableHttp。</summary>
    private static McpTransport ParseTransport(string? value)
        => Enum.TryParse<McpTransport>(value, ignoreCase: true, out var transport)
            ? transport
            : McpTransport.StreamableHttp;
}
