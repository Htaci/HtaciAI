using System;
using System.Collections.Generic;
using System.Linq;

namespace HtaciAI.Models;

/// <summary>
/// 模型能力选项（wire 值 → 中文展示名）。
/// wire 值为模型存储/请求用的内部标识，中文名用于设置页与编辑窗展示。
/// </summary>
public static class ModelCapabilities
{
    /// <summary>能力选项，顺序即展示顺序。</summary>
    public static readonly IReadOnlyList<(string Wire, string Label)> Options = new[]
    {
        ("reasoning", "推理"),
        ("tools", "工具"),
        ("completion", "补全"),
        ("vision", "图像"),
        ("audio", "音频"),
        ("video", "视频"),
        ("embedding", "嵌入"),
        ("rerank", "重排"),
    };

    /// <summary>wire 值 → 中文展示名（大小写不敏感）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels =
        Options.ToDictionary(o => o.Wire, o => o.Label, StringComparer.OrdinalIgnoreCase);
}
