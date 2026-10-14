using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HtaciAI.Services.ModelTable;

/// <summary>
/// 内置模型表的运行时索引：把生成出来的常量表建成字典与正则，供查找使用。
///
/// 匹配是<b>两级</b>的，且顺序固定：
/// <list type="number">
///   <item>精确/别名命中模型条目 —— 配置完整，无提示。</item>
///   <item>只反推出开发商 —— 只能定图标，配置缺失，调用方应挂黄叹号。</item>
///   <item>都没命中 —— 调用方应挂灰问号，且所有能力字段取保守默认。</item>
/// </list>
///
/// 保守默认值是这套设计的关键：<b>匹配不到时绝不猜</b>。
/// 猜错「支持数组 content」会让不支持的服务商直接报错，猜错「支持思考」会下发非法字段，
/// 所以未知一律退到最安全的一侧，由黄叹号提示用户去补。
/// </summary>
public static class ModelTableIndex
{
    private static readonly Dictionary<string, ModelInfo> ByExactId =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ModelInfo> ByNormalizedKey =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, VendorInfo> VendorById =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<(VendorInfo Vendor, Regex Pattern)> VendorPatterns = new();

    static ModelTableIndex()
    {
        foreach (var v in BuiltinModelTable.Vendors)
        {
            VendorById[v.Id] = v;

            // 用户/维护者写坏一条正则不该把整个索引炸掉：跳过并留白，让其余厂商照常工作。
            if (string.IsNullOrWhiteSpace(v.Match)) continue;
            try
            {
                VendorPatterns.Add((v, new Regex(v.Match, RegexOptions.IgnoreCase | RegexOptions.Compiled)));
            }
            catch (ArgumentException)
            {
                // 非法正则：忽略该条 match，不影响其它厂商。
            }
        }

        foreach (var m in BuiltinModelTable.Models)
        {
            // 同 id 后写的不覆盖先写的：表里重复定义属于编辑失误，保留首条更可预期。
            ByExactId.TryAdd(m.Id, m);

            var key = Normalize(m.Id);
            if (key.Length > 0) ByNormalizedKey.TryAdd(key, m);

            foreach (var alias in m.Aliases)
            {
                var aliasKey = Normalize(alias);
                if (aliasKey.Length > 0) ByNormalizedKey.TryAdd(aliasKey, m);
            }
        }
    }

    /// <summary>表里定义的模型条目数（供设置页展示与自检）。</summary>
    public static int ModelCount => BuiltinModelTable.Models.Length;

    /// <summary>表里定义的开发商数。</summary>
    public static int VendorCount => BuiltinModelTable.Vendors.Length;

    /// <summary>按开发商 id 取开发商信息；找不到返回 null。</summary>
    public static VendorInfo? FindVendor(string? vendorId)
        => string.IsNullOrWhiteSpace(vendorId) ? null : VendorById.GetValueOrDefault(vendorId);

    /// <summary>解析一个模型调用 id：精确 → 别名/规范化 → 开发商反推 → 放弃。</summary>
    public static TableMatch Resolve(string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId)) return TableMatch.None;

        var raw = callId.Trim();

        if (ByExactId.TryGetValue(raw, out var exact))
            return new TableMatch(TableMatchKind.Model, exact, FindVendor(exact.Vendor));

        var normalized = Normalize(raw);
        if (normalized.Length > 0 && ByNormalizedKey.TryGetValue(normalized, out var fuzzy))
            return new TableMatch(TableMatchKind.Model, fuzzy, FindVendor(fuzzy.Vendor));

        if (MatchVendor(normalized, out var vendor))
            return new TableMatch(TableMatchKind.VendorOnly, null, vendor);

        return TableMatch.None;
    }

    /// <summary>按顺序试所有开发商正则，返回首个命中的。</summary>
    /// <remarks>
    /// 顺序即生成文件里的声明顺序。正则应当锚定开头（<c>^</c>），
    /// 否则跨厂商的 id（如 <c>deepseek-grok</c>）会由声明顺序而不是前缀决定归属。
    /// </remarks>
    private static bool MatchVendor(string normalizedId, out VendorInfo vendor)
    {
        foreach (var (candidate, pattern) in VendorPatterns)
        {
            if (pattern.IsMatch(normalizedId))
            {
                vendor = candidate;
                return true;
            }
        }

        vendor = null!;
        return false;
    }

    /// <summary>
    /// 把一个模型 id 折叠成匹配用的规范形式。只做<b>无歧义的机械变换</b>：
    /// 小写、去命名空间前缀、下划线转连字符、去变体后缀（:free / (beta) …）、去日期快照。
    ///
    /// 刻意<b>不做</b>判断类变换 —— 「-thinking 到底算不算同一个模型」这种问题没有确定答案，
    /// 交给表里的 <c>aliases</c> 显式声明。多归一化一步就可能把两个不同型号折叠成一个，
    /// 那比匹配不上更糟。
    /// </summary>
    public static string Normalize(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return "";

        var s = rawId.Trim().ToLowerInvariant();

        // 命名空间前缀（openai/gpt-4o → gpt-4o）。中转过一层就会带上它。
        var slash = s.LastIndexOf('/');
        if (slash >= 0 && slash < s.Length - 1)
            s = s[(slash + 1)..];

        s = s.Replace('_', '-');

        // 变体后缀和日期快照会互相遮挡（gpt-4o-2024-08-06:free），迭代到稳定为止。
        for (; ; )
        {
            var before = s;
            s = StripDateSnapshot(s);
            s = StripVariantSuffix(s);
            if (string.Equals(s, before, StringComparison.Ordinal)) break;
        }

        return s;
    }

    // 列表服务的变体后缀（OpenRouter 的 :free、部分中转的 (beta) 等）。这些是<b>售卖形式</b>
    // 而非型号差异，剥离后指向同一个模型。
    private static readonly string[] ColonVariants =
        { ":free", ":nitro", ":cloud", ":extended", ":beta", ":preview", ":thinking", ":latest", ":exacto" };

    private static readonly string[] ParenVariants =
        { "(free)", "(beta)", "(preview)", "(thinking)" };

    private static string StripVariantSuffix(string id)
    {
        foreach (var suffix in ColonVariants)
            if (id.EndsWith(suffix, StringComparison.Ordinal))
                return id[..^suffix.Length];

        foreach (var suffix in ParenVariants)
            if (id.EndsWith(suffix, StringComparison.Ordinal))
                return id[..^suffix.Length];

        return id;
    }

    // 日期快照：claude-sonnet-4-5-20250929 / gpt-4o-2024-08-06 / kimi-k2-250905。
    // 月份限定 01-12、日期限定 01-31，这样参数量（-30b）和版本号（-4-5）不会被误吃。
    private static readonly Regex FullDateSnapshot = new(
        @"-20\d{2}-?(?:0[1-9]|1[0-2])-?(?:[0-2]\d|3[01])$",
        RegexOptions.Compiled);

    private static readonly Regex ShortDateSnapshot = new(
        @"-2\d(?:0[1-9]|1[0-2])(?:[0-2]\d|3[01])$",
        RegexOptions.Compiled);

    // 尾随的 @ 修订标记（gemini-2-0@001）。
    private static readonly Regex AtRevision = new(@"@.*$", RegexOptions.Compiled);

    private static string StripDateSnapshot(string id)
    {
        var s = AtRevision.Replace(id, "");
        s = FullDateSnapshot.Replace(s, "");
        s = ShortDateSnapshot.Replace(s, "");
        return s;
    }
}
