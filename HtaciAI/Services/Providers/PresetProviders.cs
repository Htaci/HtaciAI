using System.Collections.Generic;
using HtaciAI.Models;

namespace HtaciAI.Services.Providers;

/// <summary>预设服务商的一个 API 端点。协议决定请求体长什么样，地址填到「版本根」即可。</summary>
public sealed record PresetEndpoint(ModelProtocol Protocol, string Endpoint);

/// <summary>
/// 预设服务商：地址、图标、思考字段这些「同一家所有人都一样」的东西预先配好，
/// 用户只需要填一个密钥。清单见 <see cref="PresetProviders.All"/>。
///
/// 为什么不把这些存进库：预设是随程序发布的常量，改一家地址就是改一行代码；
/// 落到库里就得再考虑「升级时怎么更新、用户改过怎么办」这一整套，而用户真正拥有的是
/// 自己加的那条服务商记录 —— 预设只在「添加」那一刻被读一次。
/// </summary>
public sealed class PresetProvider
{
    /// <summary>稳定标识（界面上不显示，排重与日志用）。</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>一句话说明，显示在选中后的详情里。不写协议细节 —— 端点行上已经标了。</summary>
    public string? Description { get; init; }

    /// <summary><c>Assets/icon/providers/light</c> 下的图标名（不带扩展名）。留空则界面退回「首字 + 底色」头像。</summary>
    public string? Icon { get; init; }

    /// <summary>端点列表，第一个是默认端点。</summary>
    public required IReadOnlyList<PresetEndpoint> Endpoints { get; init; }

    /// <summary>
    /// 是否默认不需要密钥（本地推理）。注意：<b>不代表密钥框要禁用</b> ——
    /// LM Studio / Ollama 配上密钥后可以让局域网里的其他人访问，所以框照填，只是不作必填校验。
    /// </summary>
    public bool RequiresKey { get; init; } = true;

    /// <summary>思考开关字段的写法。</summary>
    public ThinkingFieldKind ThinkingField { get; init; } = ThinkingFieldKind.Think;

    /// <summary>是否支持数组格式的 message content（多模态）。</summary>
    public bool SupportsArrayContent { get; init; } = true;

    /// <summary>控制台 / 密钥页地址，详情里给一个「去拿密钥」的入口。</summary>
    public string? ConsoleUrl { get; init; }

    /// <summary>
    /// 造一条可入库的服务商。<paramref name="defaultEndpoint"/> 是用户选中的默认端点下标，
    /// 越界时退回第一个 —— 端点列表永远从「第一个默认」起步，不会出现没有默认端点的状态。
    /// </summary>
    public AiProvider ToProvider(string? apiKey, int defaultEndpoint = 0)
    {
        var provider = new AiProvider
        {
            Name = Name,
            Description = Description,
            Icon = Icon,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            ThinkingField = ThinkingField,
            SupportsArrayContent = SupportsArrayContent,
        };

        if (defaultEndpoint < 0 || defaultEndpoint >= Endpoints.Count) defaultEndpoint = 0;

        for (var i = 0; i < Endpoints.Count; i++)
        {
            provider.Endpoints.Add(new AiProviderEndpoint
            {
                ProviderId = provider.Id,
                Protocol = Endpoints[i].Protocol,
                Endpoint = Endpoints[i].Endpoint,
                IsDefault = i == defaultEndpoint,
                SortOrder = i,
            });
        }

        provider.SyncDefaultEndpoint();
        return provider;
    }
}

/// <summary>
/// 「添加服务商」里可选的预设。<b>顺序即界面顺序</b> —— KtariFlow 排第一是刻意为之：
/// 它是本项目的自营平台，一个密钥就打通下面这些官方服务商，给自己留一格广告位。
///
/// 端点顺序 = 优先级：<b>能用 Responses 就用 Responses</b>，那是官方主推的新协议；
/// Anthropic 端点一律排在最后，它只是给需要 Messages 协议的场景留的备选。
/// 每家支持哪几种协议是按实际路由验证过的，没验证过的宁可不写 ——
/// 列表里多一条 404 的地址，比少一条更让人困惑。
/// </summary>
public static class PresetProviders
{
    public static IReadOnlyList<PresetProvider> All { get; } = new List<PresetProvider>
    {
        new()
        {
            Key = "ktariflow",
            Name = "KtariFlow",
            Description = "推荐的中转站，主流模型都有",
            Icon = "newapi",
            ConsoleUrl = "https://kf.htaci.cn/console/token",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://kf.htaci.cn"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://kf.htaci.cn"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://kf.htaci.cn"),
            },
        },
        new()
        {
            Key = "deepseek",
            Name = "深度求索（DeepSeek）",
            Description = "探索未至之境，DeepSeek 开放平台",
            Icon = "deepseek",
            ConsoleUrl = "https://platform.deepseek.com/api_keys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.deepseek.com"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.deepseek.com"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.deepseek.com/anthropic"),
            },
        },
        new()
        {
            Key = "openai",
            Name = "OpenAI",
            Description = "全球最著名的人工智能研究机构",
            Icon = "openai",
            ConsoleUrl = "https://platform.openai.com/api-keys",
            ThinkingField = ThinkingFieldKind.ReasoningEffort,
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.openai.com/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.openai.com/v1"),
            },
        },
        new()
        {
            Key = "anthropic",
            Name = "Anthropic",
            Description = "推出了 Claude，Claude 是由 Anthropic 构建的高性能、值得信赖且智能的 AI 平台",
            Icon = "anthropic",
            ConsoleUrl = "https://console.anthropic.com/settings/keys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.anthropic.com"),
            },
        },
        new()
        {
            Key = "moonshot",
            Name = "月之暗面（Moonshot）",
            Description = "月之暗面开放平台，Kimi 系列模型官方平台",
            Icon = "moonshot",
            ConsoleUrl = "https://platform.moonshot.cn/console/api-keys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.moonshot.cn/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.moonshot.cn/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.moonshot.cn/anthropic"),
            },
        },
        new()
        {
            Key = "zhipu",
            Name = "智谱开放平台",
            Description = "智谱开放平台，GLM 系列官方模型",
            Icon = "zhipu",
            ConsoleUrl = "https://open.bigmodel.cn/usercenter/apikeys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "https://open.bigmodel.cn/api/paas/v4"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://open.bigmodel.cn/api/anthropic"),
            },
        },
        new()
        {
            Key = "zai",
            Name = "Z.ai",
            Description = "智谱的国际站（GLM 海外版），接口路径与国内站不同",
            // 暂时复用智谱图标：Z.ai 目前没有单独一张。
            Icon = "zhipu",
            ConsoleUrl = "https://z.ai/manage-apikey/apikey-list",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.z.ai/api/paas/v4"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.z.ai/api/anthropic"),
            },
        },
        new()
        {
            Key = "minimax",
            Name = "MiniMax",
            Description = "MiniMax 开放平台官方接口",
            Icon = "minimax",
            ConsoleUrl = "https://platform.minimaxi.com/user-center/basic-information/interface-key",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.minimax.chat/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.minimax.chat/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.minimax.chat/anthropic"),
            },
        },
        new()
        {
            Key = "grok",
            Name = "Grok",
            Description = "xAI 官方接口，Grok 系列模型",
            Icon = "grok",
            ConsoleUrl = "https://console.x.ai/",
            ThinkingField = ThinkingFieldKind.ReasoningEffort,
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.x.ai/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.x.ai/v1"),
            },
        },
        new()
        {
            Key = "mimo",
            Name = "Xiaomi MiMo",
            Description = "小米 MiMo 开放平台官方接口",
            Icon = "mimo",
            ConsoleUrl = "https://platform.xiaomimimo.com/",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.xiaomimimo.com/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.xiaomimimo.com/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.xiaomimimo.com/anthropic"),
            },
        },
        new()
        {
            Key = "lmstudio",
            Name = "LM Studio",
            Description = "本机推理，无需密钥。先在 LM Studio 里启动本地服务器（默认 1234 端口）",
            Icon = "lmstudio",
            RequiresKey = false,
            ThinkingField = ThinkingFieldKind.ReasoningEffort,
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.LMStudio, "http://localhost:1234/v1"),
            },
        },
        new()
        {
            Key = "ollama",
            Name = "Ollama",
            Description = "本机推理，无需密钥。先运行 ollama serve（默认 11434 端口）",
            Icon = "ollama",
            RequiresKey = false,
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "http://localhost:11434/v1"),
            },
        },
        new()
        {
            Key = "siliconflow",
            Name = "硅基流动",
            Description = "SiliconFlow 开放平台，聚合了大量开源模型",
            Icon = "siliconflow",
            ConsoleUrl = "https://cloud.siliconflow.cn/account/ak",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.siliconflow.cn/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.siliconflow.cn"),
            },
        },
        new()
        {
            Key = "bailian",
            Name = "阿里云百炼",
            Description = "阿里云百炼平台，通义千问系列模型",
            Icon = "alibaba",
            ConsoleUrl = "https://bailian.console.aliyun.com/?apiKey=1",
            ThinkingField = ThinkingFieldKind.EnableThinking,
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://dashscope.aliyuncs.com/compatible-mode/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://dashscope.aliyuncs.com/compatible-mode/v1"),
                // Claude Code 那套兼容网关：Messages 落在 /v1/messages 上
                new PresetEndpoint(ModelProtocol.Anthropic, "https://dashscope.aliyuncs.com/api/v2/apps/claude-code-proxy"),
            },
        },
        new()
        {
            Key = "volcengine",
            Name = "火山引擎",
            Description = "方舟平台提供的豆包系列模型",
            Icon = "volcengine",
            ConsoleUrl = "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://ark.cn-beijing.volces.com/api/v3"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://ark.cn-beijing.volces.com/api/v3"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://ark.cn-beijing.volces.com/api/anthropic"),
            },
        },
        new()
        {
            Key = "hunyuan",
            Name = "腾讯混元",
            Description = "腾讯混元大模型官方接口",
            Icon = "hunyuan",
            ConsoleUrl = "https://console.cloud.tencent.com/hunyuan/api-key",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.hunyuan.cloud.tencent.com/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.hunyuan.cloud.tencent.com/anthropic"),
            },
        },
        new()
        {
            Key = "mistral",
            Name = "Mistral",
            Description = "Mistral AI 官方接口",
            Icon = "mistral",
            ConsoleUrl = "https://console.mistral.ai/api-keys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.mistral.ai/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.mistral.ai/v1"),
            },
        },
        new()
        {
            Key = "longcat",
            Name = "龙猫（LongCat）",
            Description = "美团 LongCat 开放平台官方接口",
            Icon = "longcat",
            ConsoleUrl = "https://longcat.chat/platform/api_keys",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://api.longcat.chat/openai/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.longcat.chat/openai/v1"),
                new PresetEndpoint(ModelProtocol.Anthropic, "https://api.longcat.chat/anthropic"),
            },
        },
        new()
        {
            Key = "huggingface",
            Name = "Hugging Face",
            Description = "Inference Providers 路由，一个令牌调用多家托管模型",
            Icon = "huggingface",
            ConsoleUrl = "https://huggingface.co/settings/tokens",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAIResponses, "https://router.huggingface.co/v1"),
                new PresetEndpoint(ModelProtocol.OpenAI, "https://router.huggingface.co/v1"),
            },
        },
        new()
        {
            Key = "tencentcloud",
            Name = "腾讯云",
            Description = "腾讯云大模型知识引擎（LKEAP）",
            Icon = "tencent",
            ConsoleUrl = "https://console.cloud.tencent.com/lkeap",
            Endpoints = new[]
            {
                new PresetEndpoint(ModelProtocol.OpenAI, "https://api.lkeap.cloud.tencent.com/v1"),
            },
        },
    };
}
