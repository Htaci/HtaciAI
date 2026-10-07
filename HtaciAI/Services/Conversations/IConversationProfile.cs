using System.Threading.Tasks;

namespace HtaciAI.Services.Conversations;

/// <summary>
/// 草稿持久化能力：输入框内容随输入实时落库，重开会话时恢复。
/// 两类会话各有自己的草稿列（chat_sessions.draft / workspace_sessions.draft），
/// 实现分别落在各自的 Profile 里；接口上保持可选，避免为将来可能出现的不支持草稿的
/// 会话类型留一个假实现。
/// </summary>
public interface IDraftStore
{
    /// <summary>读取上次未发送的输入草稿。</summary>
    string? Load();

    /// <summary>落库草稿。</summary>
    Task SaveAsync(string draft);
}

/// <summary>
/// 一次对话的「档案」：把两种会话模型（<see cref="Models.ChatSession"/> /
/// <see cref="Models.WorkspaceChatSession"/>）的**真正差异**隔离在视图之外。
///
/// 刻意只保留确有差异的成员——消息读写两边都走共享的 <see cref="Data.ChatRepository"/>
/// （消息表本身是多态关联），因此不进本接口，留在视图里。
/// </summary>
public interface IConversationProfile
{
    /// <summary>视图读写的会话状态。</summary>
    ConversationState State { get; }

    /// <summary>工具执行器的基准目录（bash / read / write / glob / grep 的相对路径都基于它）。</summary>
    string BaseDirectory { get; }

    /// <summary>草稿能力；两类会话目前都支持，接口上保留可空以便将来出现不支持草稿的会话类型。</summary>
    IDraftStore? Draft { get; }

    /// <summary>载入/刷新会话内容到 <see cref="State"/>。</summary>
    Task LoadAsync();

    /// <summary>把 <see cref="State"/> 写回底层会话模型并落库。</summary>
    Task SaveAsync();

    /// <summary>组装本轮请求的 system 提示词。</summary>
    string BuildSystemPrompt(string modelId);
}
