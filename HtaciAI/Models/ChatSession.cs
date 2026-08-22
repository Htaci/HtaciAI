using System;

namespace HtaciAI.Models;

/// <summary>
/// 智能对话会话（chat_sessions 表，会话级"当前配置"）
/// </summary>
public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Model { get; set; } = "";
    public string? SystemPrompt { get; set; }
    public int Thinking { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    /// <summary>是否软删除（隐藏于会话列表，供删除会话管理页恢复）。</summary>
    public bool IsDeleted { get; set; }
    public long? DeletedAt { get; set; }
}
