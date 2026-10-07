using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 模型目录：读取服务商/模型两表，装配成统一 <see cref="ModelDetails"/>，并按协议构建客户端。
/// 可用模型<b>完全由用户配置决定</b>：一个都没配时列表就是空的，调用方必须自己处理这种情况。
/// </summary>
public static class ModelCatalog
{
    /// <summary>按模型 id（UUID 或调用 id）解析模型详情；未找到或未启用返回 null。</summary>
    public static async Task<ModelDetails?> ResolveAsync(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var model = await ModelRepository.GetAsync(modelId)
                    ?? await ModelRepository.GetByCallIdAsync(modelId);
        if (model is null || !model.IsEnabled) return null;

        var provider = await ProviderRepository.GetAsync(model.ProviderId);
        if (provider is null || !provider.IsEnabled) return null;

        return Assemble(model, provider);
    }

    /// <summary>
    /// 列出所有启用的模型详情（供模型选择器/UI）。服务商或模型被停用都会排除在外，
    /// 结果可能为空 —— 调用方要能处理「一个模型都没有」。
    /// </summary>
    public static async Task<List<ModelDetails>> ListEnabledAsync()
    {
        var list = new List<ModelDetails>();
        foreach (var provider in await ProviderRepository.GetAllAsync())
        {
            if (!provider.IsEnabled) continue;
            foreach (var model in await ModelRepository.GetByProviderAsync(provider.Id))
            {
                if (!model.IsEnabled) continue;
                list.Add(Assemble(model, provider));
            }
        }
        return list;
    }

    /// <summary>按模型详情的协议构建对应客户端。</summary>
    public static IChatClient BuildClient(ModelDetails details)
    {
        return details.Protocol switch
        {
            ModelProtocol.OpenAIEx => new OpenAiExClient(
                details.Endpoint, details.ApiKey, details.ModelName, details.ThinkingField),
            // LM Studio：OpenAI 兼容，但思考/强度走 reasoning_effort 而非 thinking 开关对象，固定用 ReasoningEffort。
            ModelProtocol.LMStudio => new OpenAiExClient(
                details.Endpoint, details.ApiKey, details.ModelName, ThinkingFieldKind.ReasoningEffort),
            _ => throw new NotSupportedException($"暂不支持协议：{details.Protocol}"),
        };
    }

    private static ModelDetails Assemble(AiModel m, AiProvider p) => new()
    {
        ModelId = m.Id,
        DisplayName = m.DisplayName,
        ProviderName = p.Name,
        Protocol = p.Protocol,
        Endpoint = p.Endpoint,
        ApiKey = p.ApiKey ?? "",
        ModelName = m.CallId,
        ThinkingField = p.ThinkingField,
        SupportsThinking = m.SupportsThinking,
        ThinkingStrengths = m.ThinkingStrengths,
        Capabilities = m.Capabilities,
        SupportsArrayContent = p.SupportsArrayContent,
        SupportsStreaming = p.SupportsStreaming && m.SupportsStreaming,
        ContextWindow = m.ContextWindow,
    };
}
