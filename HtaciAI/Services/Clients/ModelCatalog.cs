using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Services;

/// <summary>
/// 模型目录：读取服务商/模型两表，装配成统一 <see cref="ModelDetails"/>，并按协议构建客户端。
/// 内置模型后续改为直接请求自有服务返回列表，当前仅管理自定义模型。
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

    /// <summary>列出所有启用的模型详情（供模型选择器/UI）。</summary>
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
        // 内置默认模型（ChatConfig 配置，未落库）始终作为兜底追加到列表尾部（按调用 id 去重）。
        // 否则模型选择器会把它误判为"不在列表"而切到第一个自定义模型，会话恢复也匹配不到它，
        // 导致选了 DeepSeek V4 Flash 却实际调用自定义模型。
        if (list.All(m => m.ModelName != ModelDetails.Default.ModelName))
            list.Add(ModelDetails.Default);
        return list;
    }

    /// <summary>按模型详情的协议构建对应客户端。</summary>
    public static IChatClient BuildClient(ModelDetails details)
    {
        return details.Protocol switch
        {
            ModelProtocol.OpenAIEx => new OpenAiExClient(
                details.Endpoint, details.ApiKey, details.ModelName, details.ThinkingField),
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
