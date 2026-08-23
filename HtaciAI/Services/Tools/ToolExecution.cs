namespace HtaciAI.Services.Tools;

/// <summary>
/// 一次工具调用的执行结果。区分「成功」与「失败」：失败时 ChatGateway 会把该 assistant 的
/// tool_call 原地替换为 <c>invalid</c>，并附上 <see cref="Error"/> 作为失败原因。
/// </summary>
public sealed record ToolExecution(bool Success, string Content, string? Error = null)
{
    public static ToolExecution Ok(string content) => new(true, content, null);

    public static ToolExecution Fail(string error) => new(false, "", error);

    /// <summary>失败时返回的展示文本：优先 Error，否则退回 Content。</summary>
    public string Display => Success ? Content : (Error ?? Content);
}
