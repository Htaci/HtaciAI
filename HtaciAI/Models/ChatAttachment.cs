using System.Collections.Generic;
using System.Text.Json;

namespace HtaciAI.Models;

/// <summary>
/// 一条随用户消息发出的附件。只记路径与展示信息 —— 文件本体就在磁盘上：
/// 图片由模型通过 view_image 工具读取，视频通过 view_video，其余文件由 read 工具读取。
/// 消息正文里保留绝对路径（模型据此调用工具），界面渲染时按这里的路径换成 chip。
/// </summary>
public sealed class ChatAttachment
{
    /// <summary>绝对路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>chip 上显示的文字（图片 / 视频 / 文本 / Word / 后缀名…），由扩展名推导。</summary>
    public string Label { get; set; } = "";

    /// <summary>分类：image / video / text / office / other。</summary>
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

/// <summary>
/// 工具结果里携带的图片：落库时存路径，构造请求时再读文件转 data URI。
/// 一个工具结果可能带多张（view_video_frames 抽出来的帧），所以存的是数组。
/// </summary>
public static class ToolImageJson
{
    private const string ListKey = "image_paths";

    /// <summary>单图历史消息用过的键。只读不写：新结果一律写 image_paths。</summary>
    private const string LegacyKey = "image_path";

    public static string Serialize(IReadOnlyList<string> imagePaths)
        => JsonSerializer.Serialize(new Dictionary<string, object> { [ListKey] = imagePaths });

    /// <summary>
    /// 读出一组图片路径。新格式 <c>image_paths</c> 优先；
    /// 旧消息只有一个 <c>image_path</c> 时也能读出来，否则那些历史图片会突然发不出去。
    /// </summary>
    public static List<string> DeserializeMany(string? metadata)
    {
        var list = ToolMediaJson.ReadList(metadata, ListKey);
        if (list.Count > 0) return list;

        var single = ToolMediaJson.ReadPath(metadata, LegacyKey);
        return single is null ? new() : new List<string> { single };
    }
}

/// <summary>
/// 工具结果里携带的视频：与图片同一套「落库存路径、请求时再编码」的做法，
/// 只是键名不同 —— 两者互斥，客户端按模型能力决定要不要读。
/// </summary>
public static class ToolVideoJson
{
    private const string Key = "video_path";

    public static string Serialize(string videoPath)
        => JsonSerializer.Serialize(new Dictionary<string, string> { [Key] = videoPath });

    public static string? Deserialize(string? metadata) => ToolMediaJson.ReadPath(metadata, Key);
}

/// <summary>
/// image_paths / video_path 共用的读取逻辑。差别只有键名，
/// 各写一份必然会在改键名或改损坏处理时漏改一边。
/// </summary>
internal static class ToolMediaJson
{
    public static string? ReadPath(string? metadata, string key)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
        }
        catch
        {
            // 损坏的 metadata 当没有媒体处理
        }
        return null;
    }

    /// <summary>读一个字符串数组。缺键、类型不对、有非字符串元素时只取认得的那些。</summary>
    public static List<string> ReadList(string? metadata, string key)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(metadata)) return result;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(key, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path)
                    result.Add(path);
            }
        }
        catch
        {
            // 损坏的 metadata 当没有媒体处理
        }
        return result;
    }
}
