using System.Collections.Generic;
using System.Text.Json;

namespace HtaciAI.Models;

/// <summary>
/// 一条随用户消息发出的附件。只记路径与展示信息 —— 文件本体就在磁盘上：
/// 图片由模型通过 view_image 工具读取，其余文件由 read 工具读取。
/// 消息正文里保留绝对路径（模型据此调用工具），界面渲染时按这里的路径换成 chip。
/// </summary>
public sealed class ChatAttachment
{
    /// <summary>绝对路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>chip 上显示的文字（图片 / 文本 / Word / 后缀名…），由扩展名推导。</summary>
    public string Label { get; set; } = "";

    /// <summary>分类：image / text / office / other。</summary>
    public string Kind { get; set; } = "other";
}

/// <summary>
/// chat_message.metadata 里附件部分的读写。
/// 与 assistant 消息上的 tool_calls 共用 metadata 列但互不干扰：各自只认自己的键。
/// </summary>
public static class AttachmentJson
{
    public static string Serialize(IReadOnlyList<ChatAttachment> items)
        => JsonSerializer.Serialize(new { attachments = items });

    public static List<ChatAttachment> Deserialize(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return new();
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("attachments", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                return arr.Deserialize<List<ChatAttachment>>() ?? new();
            }
        }
        catch
        {
            // 损坏的 metadata 当没有附件处理
        }
        return new();
    }
}

/// <summary>工具结果里携带的图片：落库时存路径，构造请求时再读文件转 data URI。</summary>
public static class ToolImageJson
{
    public static string Serialize(string imagePath)
        => JsonSerializer.Serialize(new { image_path = imagePath });

    public static string? Deserialize(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("image_path", out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
        }
        catch
        {
            // 损坏的 metadata 当没有图片
        }
        return null;
    }
}
