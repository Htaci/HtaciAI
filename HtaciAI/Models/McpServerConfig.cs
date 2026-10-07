using System;
using System.Linq;

namespace HtaciAI.Models;

/// <summary>MCP 传输方式。三种都已实现，见 <c>Services/Mcp</c> 下的三个 Transport。</summary>
public enum McpTransport
{
    /// <summary>标准输入/输出：拉起本地子进程，JSON-RPC 走它的 stdin/stdout（换行分隔）。</summary>
    Stdio,

    /// <summary>服务器发送事件（旧版 HTTP+SSE）：一条长 GET 流收响应，POST 到握手拿到的 endpoint 发请求。</summary>
    Sse,

    /// <summary>可流式传输的 HTTP：每个请求一次 POST，响应可为 JSON 或 SSE 流。</summary>
    StreamableHttp,
}

/// <summary>
/// 一个 MCP 服务端配置（mcp_servers 表）。
/// 名称、类型必填；按类型再要地址（stdio 之外）或启动命令（stdio）。
/// 请求头 / 参数 / 环境变量都以「用户自由输入的原文」保存，运行时才解析成键值对 —— 用户要能直接编辑，
/// 且顺序与注释不该被程序吃掉。
/// </summary>
public sealed class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public McpTransport Transport { get; set; } = McpTransport.StreamableHttp;

    /// <summary>服务地址（streamableHttp / sse 用）。</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 自定义请求头原文，每行一条 <c>Key=Value</c>（也接受 <c>Key: Value</c>）。
    /// 保留原文而不是解析成字典：用户要能直接编辑，且顺序/注释不丢。
    /// </summary>
    public string HeadersRaw { get; set; } = "";

    /// <summary>启动命令（stdio 用）。可只写名字（如 <c>npx</c>），运行时按 PATH 解析。</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// 命令行参数原文，<b>一行一个</b>（stdio 用）。
    /// 不按空格拆分：路径里的空格太常见，按空格切会把一个参数劈成两个。
    /// </summary>
    public string ArgumentsRaw { get; set; } = "";

    /// <summary>追加给子进程的环境变量原文，每行一条 <c>KEY=VALUE</c>（stdio 用）。</summary>
    public string EnvRaw { get; set; } = "";

    /// <summary>子进程的工作目录（stdio 用）。空 = 继承本进程的当前目录。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>请求超时（秒），默认 60。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public bool Enabled { get; set; } = true;

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>传输方式的显示文案与线上取值。</summary>
public static class McpTransportInfo
{
    public static readonly McpTransport[] All =
    {
        McpTransport.Stdio, McpTransport.Sse, McpTransport.StreamableHttp
    };

    public static string Label(McpTransport transport) => transport switch
    {
        McpTransport.Stdio => "标准输入/输出 (stdio)",
        McpTransport.Sse => "服务器发送事件 (sse)",
        _ => "可流式传输的 HTTP (streamableHttp)",
    };

    /// <summary>线上/入库用的规范写法。</summary>
    public static string Wire(McpTransport transport) => transport switch
    {
        McpTransport.Stdio => "stdio",
        McpTransport.Sse => "sse",
        _ => "streamableHttp",
    };

    /// <summary>该传输是否用得上「地址 / 请求头」这两项（stdio 用不上，它要的是命令与参数）。</summary>
    public static bool UsesUrl(McpTransport transport) => transport != McpTransport.Stdio;

    /// <summary>
    /// 这个配置能不能连。返回 null 表示没问题，否则返回面向用户的原因。
    ///
    /// 必填项按传输分叉：stdio 要命令，另外两个要地址。以前这里是「非 streamableHttp 一律
    /// 暂未实现」，三种传输都做完之后换成真正的校验。
    /// </summary>
    public static string? Validate(McpServerConfig config) => config.Transport switch
    {
        McpTransport.Stdio => string.IsNullOrWhiteSpace(config.Command)
            ? "未填写启动命令"
            : null,
        _ => string.IsNullOrWhiteSpace(config.Url)
            ? "未填写服务地址"
            : null,
    };

    /// <summary>列表 / 介绍页上「连到哪里」的一行摘要。</summary>
    public static string Describe(McpServerConfig config) => config.Transport == McpTransport.Stdio
        ? (string.IsNullOrWhiteSpace(config.Command) ? "(未填命令)" : CommandLine(config))
        : config.Url;

    private static string CommandLine(McpServerConfig config)
    {
        var arguments = string.Join(' ', config.ArgumentsRaw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')));

        return arguments.Length == 0 ? config.Command : $"{config.Command} {arguments}";
    }
}
