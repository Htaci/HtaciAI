namespace HtaciAI.Services;

/// <summary>
/// 测试模型配置（临时写死，后续接入模型服务配置后替换）
/// </summary>
public static class ChatConfig
{
    public const string Endpoint = "https://api.deepseek.com/v1/chat/completions";
    public const string Model = "deepseek-v4-flash";
    public const string ApiKey = "sk-a59a52a52a0c47b48beb53f68a53895e";
    public const string SystemPrompt = "";
}
