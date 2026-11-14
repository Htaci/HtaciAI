using System;
using System.Collections.Generic;
using System.Linq;

namespace HtaciAI.Models;

/// <summary>
/// 服务商使用的 API 协议类型。
/// OpenAI：OpenAI /chat/completions 兼容协议；
/// OpenAIResponses：OpenAI /responses 协议（instructions + input items + 强类型流式事件）；
/// LMStudio：LM Studio（本地推理，OpenAI 兼容但思考/强度字段不同）；
/// Anthropic：Anthropic /v1/messages 协议（block 数组 + tool_use / tool_result）。
///
/// <b>只许在末尾追加。</b>这个名字以字符串形式存在 ai_provider_endpoint.protocol 里
/// （见 <c>ProviderRepository.MapProtocol</c>），顺序变了会让老库里的端点静默换成另一种协议。
/// </summary>
public enum ModelProtocol
{
    OpenAI,
    OpenAIResponses,
    LMStudio,
    Anthropic,
}

/// <summary>服务商（ai_provider 表）。一个服务商包含若干模型。</summary>
public class AiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// 头像图标名（<c>Assets/icon/providers/light</c> 下的文件名，不带扩展名）。
    /// 从预设服务商添加时自动带上；手工新建的服务商为空，界面退回「首字 + 底色」头像。
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// 实际请求走哪个协议的镜像，恒等于<b>默认端点</b>的协议。
    /// 请求层（<c>ModelCatalog</c>）只认它，不必知道端点的存在；多端点是界面的组织方式。
    /// </summary>
    public ModelProtocol Protocol { get; set; } = ModelProtocol.OpenAI;

    /// <summary>实际请求打哪个地址的镜像，恒等于默认端点。</summary>
    public string Endpoint { get; set; } = "";

    public string? ApiKey { get; set; }

    /// <summary>思考开关字段的写法：think / enable_thinking / none（不支持该字段）。</summary>
    public ThinkingFieldKind ThinkingField { get; set; } = ThinkingFieldKind.Think;
    public bool SupportsArrayContent { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    /// <summary>
    /// 该服务商配置的端点（可以同时配多个，各自用不同协议）。从 <c>ai_provider_endpoint</c> 读出，
    /// 不落在 ai_provider 行里。空表示还没配 —— 新建的服务商就是这样，界面上引导用户添加。
    /// </summary>
    public List<AiProviderEndpoint> Endpoints { get; set; } = new();

    /// <summary>默认端点：优先 <see cref="AiProviderEndpoint.IsDefault"/>，否则退回第一个。</summary>
    public AiProviderEndpoint? DefaultEndpoint
        => Endpoints.FirstOrDefault(e => e.IsDefault) ?? Endpoints.FirstOrDefault();

    /// <summary>
    /// 把默认端点的协议/地址同步到 <see cref="Protocol"/> / <see cref="Endpoint"/>。
    /// 保存前调用，保证镜像与端点列表不会各说各话。
    /// </summary>
    public void SyncDefaultEndpoint()
    {
        if (DefaultEndpoint is not { } endpoint) return;

        Protocol = endpoint.Protocol;
        Endpoint = endpoint.Endpoint;
    }
}

/// <summary>
/// 一个 API 端点（ai_provider_endpoint 表）。同一服务商可以配多个端点，
/// 每个端点有自己的协议 —— 中转站常常同时提供 /chat/completions 与 /responses 两套。
/// 请求只打<b>默认端点</b>，其余端点留着备用或给不同模型用。
/// </summary>
public class AiProviderEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderId { get; set; } = "";
    public ModelProtocol Protocol { get; set; } = ModelProtocol.OpenAI;
    public string Endpoint { get; set; } = "";

    /// <summary>是否为默认端点。同一服务商下最多一个为真。</summary>
    public bool IsDefault { get; set; }

    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
