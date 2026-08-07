using System;

namespace HtaciAI.Models;

/// <summary>
/// 聊天消息（chat_message 表，共享表，session_id 可指向智能对话或工作空间会话）
/// </summary>
public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public int SequenceNumber { get; set; }
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public string? Thinking { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string Status { get; set; } = "completed";
    public string? UsageJson { get; set; }
    public string? ModelName { get; set; }
    public string? Metadata { get; set; }
    public bool IsDeleted { get; set; }
    public long? DeletedAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
