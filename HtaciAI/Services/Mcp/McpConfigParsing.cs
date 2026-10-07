using System;
using System.Collections.Generic;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// MCP 配置里那些「用户手写原文」字段的解析。
///
/// 一律按原文保存、运行时才解析：用户要能直接编辑，且顺序与注释都不丢。
/// 请求头、环境变量、参数三者都是这个路子。
/// </summary>
public static class McpConfigParsing
{
    /// <summary>
    /// 每行一条 <c>Key=Value</c>（也接受 <c>Key: Value</c>）。
    /// 空行与 <c>#</c> 开头的注释行忽略；键名大小写不敏感。请求头与环境变量共用。
    /// </summary>
    public static Dictionary<string, string> ParseKeyValues(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var separator = text.IndexOf('=');
            if (separator < 0) separator = text.IndexOf(':');
            if (separator <= 0) continue;

            var key = text[..separator].Trim();
            var value = text[(separator + 1)..].Trim();
            if (key.Length == 0) continue;

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// 命令行参数，一行一个。
    ///
    /// 刻意<b>不</b>按空格拆分：路径里的空格太常见（<c>C:\Program Files\...</c>），
    /// 按空格切会把一个参数劈成两个。一行一个既没有歧义，也不需要在参数里写转义引号。
    /// 空行与 <c>#</c> 注释行忽略。
    /// </summary>
    public static List<string> ParseArguments(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            result.Add(text);
        }

        return result;
    }
}
