using System;
using System.Linq;
using HtaciAI.Models;
using HtaciAI.Services.ModelTable;

namespace HtaciAI.Services;

/// <summary>
/// 新建模型的默认配置来源。「获取模型」批量添加时，每个模型都经由 <see cref="ForCallId"/> 取初始配置，
/// 于是「新模型长什么样」这件事只有一个出口。
///
/// 内置模型表（<c>Services/ModelTable/models.ini</c>）在这里接入：按模型调用 id 查表，
/// 命中就把能力 / 上下文 / 思考档位等覆盖到默认值上。三种命中结果对应三档可信度：
///
/// <list type="bullet">
///   <item><b>命中模型条目</b> —— 配置完整，无提示。</item>
///   <item><b>只反推出开发商</b> —— 头像和归属对得上，其余配置缺失。挂黄叹号提示用户补。</item>
///   <item><b>都没命中</b> —— 全部取保守默认，挂灰问号。</item>
/// </list>
///
/// 关键在于<b>没命中时不猜</b>：能力列表留空、思考关闭、数组 content 留 null（跟随服务商）。
/// 猜错的代价比留空大得多 —— 猜「支持数组 content」会让不支持的服务端拒绝整个请求，
/// 而留空只是少两个标签，且有提示引导用户去补。
/// </summary>
public static class BuiltinModelConfigs
{
    /// <summary>为某个服务商下的模型 id 生成一份可直接入库的默认配置。</summary>
    public static AiModel ForCallId(string providerId, string callId)
    {
        var match = ModelTableIndex.Resolve(callId);

        // 其余字段交给 AiModel 自身的默认值（与「+ 添加模型」对话框里不填时的结果一致），
        // 免得默认值散落在两处、日后只改了一边。
        var model = new AiModel
        {
            ProviderId = providerId,
            CallId = callId,
            DisplayName = callId,   // 表里没给备注名时，名称直接用模型 id
            ConfigOrigin = match.Kind switch
            {
                TableMatchKind.Model => ModelConfigOrigin.Builtin,
                TableMatchKind.VendorOnly => ModelConfigOrigin.VendorOnly,
                _ => ModelConfigOrigin.Unmatched,
            },
        };

        // 开发商是两级共有的：只命中开发商时也该拿到头像。
        if (match.Vendor is not null)
            model.Vendor = match.Vendor.Id;

        if (match.Model is { } info)
            ApplyModelInfo(model, info, callId);

        return model;
    }

    /// <summary>查表命中后的唯一解析点：表里的三态值到 <see cref="AiModel"/> 的落库形式都在这。</summary>
    public static void ApplyModelInfo(AiModel model, ModelInfo info, string fallbackName)
    {
        model.DisplayName = string.IsNullOrWhiteSpace(info.Name) ? fallbackName : info.Name!;

        // 表里 -1 是「未知」，不要覆盖成 -1 把用户/默认值拍平；未知就保持原样。
        if (info.ContextWindow > 0) model.ContextWindow = info.ContextWindow;
        if (info.MaxOutputTokens > 0) model.MaxOutputTokens = info.MaxOutputTokens;

        model.Capabilities = info.Capabilities.ToList();
        model.SupportsThinking = info.SupportsThinking;
        model.ThinkingStrengths = info.ThinkingStrengths.ToList();

        model.SupportsArrayContent = ToNullableBool(info.ArrayContent);
        model.TemperatureSupported = ToNullableBool(info.Temperature);
    }

    /// <summary>
    /// 三态 → 可空布尔。Unknown 落成 null 而不是 false：
    /// 「没查过」和「确定不支持」在界面上是两回事，合并之后就没法再区分了。
    /// </summary>
    private static bool? ToNullableBool(TriState value) => value switch
    {
        TriState.Yes => true,
        TriState.No => false,
        _ => null,
    };
}
