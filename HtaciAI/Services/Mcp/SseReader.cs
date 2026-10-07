using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace HtaciAI.Services.Mcp;

/// <summary>
/// SSE（<c>text/event-stream</c>）帧读取。两种传输都要用：
/// <list type="bullet">
///   <item>streamableHttp：一次 POST 的响应体可能是一条 SSE 流，从中取 id 匹配的那一帧；</item>
///   <item>sse：一条长流的全部帧里，既有握手用的 endpoint 事件，也有各请求的响应。</item>
/// </list>
///
/// 只处理 MCP 用得到的两个字段：<c>event</c> 与 <c>data</c>。
/// <c>id</c> / <c>retry</c> / 以 <c>:</c> 开头的注释行按规范忽略。
/// </summary>
public static class SseReader
{
    /// <param name="Event">事件名；没写 <c>event:</c> 时为 null（默认为 message）。</param>
    /// <param name="Data">data 字段拼接结果（多个 data 行按规范用换行连接）。</param>
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;   // 流结束

            if (line.Length == 0)
            {
                // 空行 = 一帧结束。全是空行时不产出空帧。
                if (data.Length == 0 && eventName is null) continue;

                yield return (eventName, data.ToString());
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }

        // 流末尾没有空行收尾时，把最后一帧也交出去
        if (data.Length > 0 || eventName is not null)
            yield return (eventName, data.ToString());
    }
}
