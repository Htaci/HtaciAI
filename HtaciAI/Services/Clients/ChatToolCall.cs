namespace HtaciAI.Services;

/// <summary>
/// 模型请求的一次工具调用。流式场景下工具调用按 index 分片下发，
/// id / function.name 通常只在首块出现，function.arguments 逐块累积。
/// </summary>
public sealed class ChatToolCall
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}
