namespace HtaciAI.Services.Tools;

/// <summary>
/// 一次工具调用的执行结果。区分「成功」与「失败」：
/// 失败时，若 <see cref="IsUnknownTool"/> 为 true（工具名未解析或已被禁用，属协议层未知调用），
/// ChatGateway 会把它收敛为哨兵 invalid 工具，返回一条可执行的错误信息；
/// 否则为真实工具运行失败，保持原始 tool_call 不变，把 <see cref="Error"/> 作为该工具结果反馈。
/// </summary>
public sealed record ToolExecution(
    bool Success,
    string Content,
    string? Error = null,
    bool IsUnknownTool = false,
    string? ImagePath = null)
{
    public static ToolExecution Ok(string content) => new(true, content, null);

    /// <summary>
    /// 成功且携带一张图片（view_image）。网关会把路径写进 tool 消息的 metadata，
    /// 构造下一次请求时再读文件、编码成图片 part —— 落库的是路径，发出去的是字节。
    /// </summary>
    public static ToolExecution OkImage(string content, string imagePath)
        => new(true, content, null, false, imagePath);

    public static ToolExecution Fail(string error) => new(false, "", error);

    /// <summary>未知/未解析工具（工具名不存在或已被禁用）：可由网关收敛为 invalid。</summary>
    public static ToolExecution FailUnknownTool(string error) => new(false, "", error, true);

    /// <summary>失败时返回的展示文本：优先 Error，否则退回 Content。</summary>
    public string Display => Success ? Content : (Error ?? Content);
}
