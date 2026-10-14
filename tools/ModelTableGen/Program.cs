using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HtaciAI.Tools.ModelTableGen;

/// <summary>
/// 从 <c>models.ini</c> 生成 <c>BuiltinModelTable.g.cs</c>。
///
/// 设计取向是「宁可生成失败，也不要静默丢配置」：
/// 未知的键、格式错误的行、引用了不存在的开发商，一律报错并以非零码退出。
/// 一个写错的键名如果被静默忽略，表现是「某个字段莫名其妙没生效」，
/// 那种问题在几百行的表里极难排查 —— 所以这里选择直接拦住。
/// </summary>
internal static class Program
{
    // 能力取值必须与 HtaciAI/Models/ModelCapabilities.cs 的 wire 值保持一致。
    // 对不上只警告不报错，免得模型表因为 UI 加了个新标签就生成不出来。
    private static readonly string[] KnownCapabilities =
        { "reasoning", "tools", "completion", "vision", "audio", "video", "embedding", "rerank" };

    private static readonly string[] KnownStrengths =
        { "minimal", "low", "medium", "high", "xhigh", "max" };

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"生成失败：{ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var root = FindRepoRoot();
        var iniPath = ArgValue(args, "--ini") ?? Path.Combine(root, "HtaciAI", "Services", "ModelTable", "models.ini");
        var outPath = ArgValue(args, "--out") ?? Path.Combine(root, "HtaciAI", "Services", "ModelTable", "BuiltinModelTable.g.cs");
        var iconsDir = ArgValue(args, "--icons") ?? Path.Combine(root, "HtaciAI", "Assets", "icon", "models");

        if (!File.Exists(iniPath))
        {
            Console.Error.WriteLine($"找不到模型表：{iniPath}");
            return 1;
        }

        var problems = new List<string>();
        var warnings = new List<string>();

        var sections = ParseIni(File.ReadAllLines(iniPath), problems);
        var vendors = new List<VendorRow>();
        var models = new List<ModelRow>();

        foreach (var section in sections)
        {
            switch (section.Kind)
            {
                case "vendor":
                    vendors.Add(ReadVendor(section, problems));
                    break;
                case "model":
                    models.Add(ReadModel(section, problems, warnings));
                    break;
                default:
                    problems.Add($"[{section.Kind}:{section.Name}] 未知的段类型，只支持 vendor / model");
                    break;
            }
        }

        // 引用完整性：模型指向不存在的开发商，说明表里打错了字，必须拦住。
        var vendorIds = new HashSet<string>(vendors.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            if (m.Vendor.Length > 0 && !vendorIds.Contains(m.Vendor))
                problems.Add($"[model:{m.Id}] vendor = \"{m.Vendor}\" 没有对应的 [vendor:...] 段");
        }

        // 开发商 Match 正则的可编译性在这里先验一遍，避免坏正则进了生成物才在运行时被跳过。
        foreach (var v in vendors)
        {
            if (v.Match is null) continue;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(v.Match);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"[vendor:{v.Id}] match 不是合法正则：{ex.Message}");
            }
        }

        // 重复 id 会让索引里的「先写者优先」变成静默覆盖，直接报出来。
        foreach (var dup in models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"[model:{dup.Key}] 重复定义（出现 {dup.Count()} 次）");

        if (problems.Count > 0)
        {
            Console.Error.WriteLine("models.ini 有错误，未生成：");
            foreach (var p in problems) Console.Error.WriteLine($"  ✗ {p}");
            return 1;
        }

        var iconsLight = ScanIcons(Path.Combine(iconsDir, "light"));
        var iconsDark = ScanIcons(Path.Combine(iconsDir, "dark"));

        var source = Emit(vendors, models, iconsLight, iconsDark);

        // 内容没变就不重写文件，避免每次生成都刷新时间戳、让 git diff 无端变脏。
        if (File.Exists(outPath) && File.ReadAllText(outPath) == source)
        {
            Console.WriteLine($"无变化：{Relative(root, outPath)}（{models.Count} 个模型 / {vendors.Count} 个开发商）");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine($"已生成 {Relative(root, outPath)}：{models.Count} 个模型 / {vendors.Count} 个开发商");

        // 图标缺失只提示不报错：开发商可以先用通用图标，不该因为少一张 svg 就卡住模型表。
        var vendorWithoutIcon = vendors
            .Where(v => !iconsLight.Contains(v.Icon ?? v.Id) && !iconsDark.Contains(v.Icon ?? v.Id))
            .Select(v => v.Id)
            .ToList();

        if (vendorWithoutIcon.Count == vendors.Count && vendors.Count > 0)
        {
            // 一个图标都没有是合法状态（图标资源是外部取得的，可能整体缺席）。
            // 这时逐条列 20 多个名字只是噪音，汇总一行并指向清单文档即可。
            warnings.Add($"当前没有任何开发商图标（{vendors.Count} 个都没有），头像位置会留空。" +
                         "图标文件与获取方式见 docs/icon-inventory.md");
        }
        else if (vendorWithoutIcon.Count > 0)
        {
            warnings.Add($"这些开发商缺图标，头像位置会留空：{string.Join(", ", vendorWithoutIcon)}");
        }

        foreach (var w in warnings) Console.WriteLine($"  ! {w}");

        return 0;
    }

    // ── ini 解析 ────────────────────────────────────────────────────────────

    private sealed record Section(string Kind, string Name, Dictionary<string, string> Values, int Line);

    private static List<Section> ParseIni(string[] lines, List<string> problems)
    {
        var sections = new List<Section>();
        Section? current = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var header = line[1..^1].Trim();

                // 模型 id 本身可能带冒号（qwen2.5:7b），所以只按<b>第一个</b>冒号切分，
                // 剩下的整体当作名字，否则这类 id 永远写不进表。
                var colon = header.IndexOf(':');
                if (colon <= 0 || colon == header.Length - 1)
                {
                    problems.Add($"第 {lineNo} 行：段名格式应为 [vendor:xxx] 或 [model:xxx]，实际是 [{header}]");
                    current = null;
                    continue;
                }

                var kind = header[..colon].Trim().ToLowerInvariant();
                var name = header[(colon + 1)..].Trim();

                var key = $"{kind}:{name}";
                if (!seen.Add(key))
                    problems.Add($"第 {lineNo} 行：[{key}] 重复定义");

                current = new Section(kind, name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), lineNo);
                sections.Add(current);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                problems.Add($"第 {lineNo} 行：应为 key = value，实际是「{line}」");
                continue;
            }

            if (current is null)
            {
                problems.Add($"第 {lineNo} 行：键值对出现在任何 [段] 之前");
                continue;
            }

            var k = line[..eq].Trim().ToLowerInvariant();
            var v = line[(eq + 1)..].Trim();
            current.Values[k] = v;
        }

        return sections;
    }

    /// <summary>去掉整行注释与行内注释。<c>#</c> 和 <c>;</c> 都认，但只在前有空白时才算行内注释。</summary>
    private static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) return "";

        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] is not ('#' or ';')) continue;
            if (char.IsWhiteSpace(line[i - 1])) return line[..i];
        }

        return line;
    }

    // ── 行模型 ──────────────────────────────────────────────────────────────

    private sealed record VendorRow(string Id, string Name, string? Icon, string? Match);

    private sealed record ModelRow(
        string Id,
        string Vendor,
        string? Name,
        string[] Aliases,
        long Context,
        long MaxOut,
        string[] Capabilities,
        string ArrayContent,
        bool Thinking,
        string[] Strengths,
        string Temperature,
        string? Source,
        string? Note);

    private static readonly string[] VendorKeys = { "name", "icon", "match" };

    private static readonly string[] ModelKeys =
    {
        "vendor", "name", "aliases", "context", "max_out",
        "capabilities", "array_content", "thinking", "strengths", "temperature", "source", "note"
    };

    private static VendorRow ReadVendor(Section s, List<string> problems)
    {
        ReportUnknownKeys(s, VendorKeys, problems);

        var name = s.Values.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name))
            problems.Add($"[vendor:{s.Name}] 缺少 name");

        var match = s.Values.GetValueOrDefault("match");
        var icon = s.Values.GetValueOrDefault("icon");

        return new VendorRow(
            s.Name,
            name ?? s.Name,
            string.IsNullOrWhiteSpace(icon) ? null : icon,
            string.IsNullOrWhiteSpace(match) ? null : match);
    }

    private static ModelRow ReadModel(Section s, List<string> problems, List<string> warnings)
    {
        ReportUnknownKeys(s, ModelKeys, problems);

        var vendor = s.Values.GetValueOrDefault("vendor") ?? "";
        if (vendor.Length == 0)
            problems.Add($"[model:{s.Name}] 缺少 vendor（开发商，决定模型头像）");

        var capabilities = SplitList(s.Values.GetValueOrDefault("capabilities"));
        foreach (var c in capabilities.Where(c => !KnownCapabilities.Contains(c, StringComparer.OrdinalIgnoreCase)))
            warnings.Add($"[model:{s.Name}] 能力值「{c}」不在已知列表里（{string.Join("/", KnownCapabilities)}）");

        var strengths = SplitList(s.Values.GetValueOrDefault("strengths"));
        foreach (var st in strengths.Where(st => !KnownStrengths.Contains(st, StringComparer.OrdinalIgnoreCase)))
            warnings.Add($"[model:{s.Name}] 思考强度「{st}」不在已知列表里（{string.Join("/", KnownStrengths)}）");

        // thinking 是有没有思考能力，strengths 是能被下发的档位集合。
        // 只有布尔开关、没有档位的模型（Qwen 的 enable_thinking 这类）本来就该没有 strengths，
        // 所以这里不把「thinking=yes 但没给档位」当问题 —— 那是合法形态，不是漏填。
        var thinking = ParseYesNo(s, "thinking", problems) ?? false;

        return new ModelRow(
            Id: s.Name,
            Vendor: vendor,
            Name: s.Values.GetValueOrDefault("name"),
            Aliases: SplitList(s.Values.GetValueOrDefault("aliases")),
            Context: ParseSize(s, "context", problems),
            MaxOut: ParseSize(s, "max_out", problems),
            Capabilities: capabilities,
            ArrayContent: ParseTriState(s, "array_content", problems),
            Thinking: thinking,
            Strengths: strengths,
            Temperature: ParseTriState(s, "temperature", problems),
            Source: s.Values.GetValueOrDefault("source"),
            Note: s.Values.GetValueOrDefault("note"));
    }

    private static void ReportUnknownKeys(Section s, string[] known, List<string> problems)
    {
        foreach (var key in s.Values.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"[{s.Kind}:{s.Name}] 未知的键「{key}」，可用：{string.Join(", ", known)}");
    }

    private static string[] SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>读一个 token 数。<c>unknown</c> / 省略 / 非正数都归一成 -1（未知）。</summary>
    private static long ParseSize(Section s, string key, List<string> problems)
    {
        var raw = s.Values.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw)) return -1;
        if (raw.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return -1;

        if (!long.TryParse(raw.Replace("_", "").Replace(",", ""), out var v) || v <= 0)
        {
            problems.Add($"[{s.Kind}:{s.Name}] {key} = \"{raw}\" 不是合法数字（未知请写 unknown）");
            return -1;
        }

        return v;
    }

    /// <summary>读 yes/no/unknown，归一成生成物里的 <c>TriState.Yes/No/Unknown</c>。</summary>
    private static string ParseTriState(Section s, string key, List<string> problems)
    {
        var raw = s.Values.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw)) return "TriState.Unknown";

        return raw.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => "TriState.Yes",
            "no" or "false" or "0" => "TriState.No",
            "unknown" => "TriState.Unknown",
            _ => UnknownTriState(s, key, raw, problems),
        };

        static string UnknownTriState(Section s, string key, string raw, List<string> problems)
        {
            problems.Add($"[{s.Kind}:{s.Name}] {key} = \"{raw}\" 只能是 yes / no / unknown");
            return "TriState.Unknown";
        }
    }

    private static bool? ParseYesNo(Section s, string key, List<string> problems)
    {
        var raw = s.Values.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return raw.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => Reject(),
        };

        bool? Reject()
        {
            problems.Add($"[{s.Kind}:{s.Name}] {key} = \"{raw}\" 只能是 yes / no");
            return null;
        }
    }

    // ── 生成 ────────────────────────────────────────────────────────────────

    private static string Emit(
        List<VendorRow> vendors,
        List<ModelRow> models,
        List<string> iconsLight,
        List<string> iconsDark)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("//");
        sb.AppendLine("// 本文件由 tools/ModelTableGen 从 models.ini 生成，请勿手改 —— 改动会在下次生成时丢失。");
        sb.AppendLine("// 要增删模型或修参数，请编辑同目录下的 models.ini，然后执行：");
        sb.AppendLine("//");
        sb.AppendLine("//     dotnet run --project tools/ModelTableGen");
        sb.AppendLine("//");
        sb.AppendLine("// 表里的 context / max_out 等数字都应当有官方文档依据（见各条目的 source）。");
        sb.AppendLine("// 不知道的值写 unknown，不要填猜测值 —— 匹配不到时保守取值并提示用户，比猜错安全。");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        // 空集合用 Array.Empty<string>() 表达，所以要带上 System。
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine("namespace HtaciAI.Services.ModelTable;");
        sb.AppendLine();
        sb.AppendLine("internal static class BuiltinModelTable");
        sb.AppendLine("{");

        sb.AppendLine("    /// <summary>开发商表。顺序即 id 反推的匹配优先级。</summary>");
        sb.AppendLine("    public static readonly VendorInfo[] Vendors =");
        sb.AppendLine("    {");
        foreach (var v in vendors)
        {
            sb.AppendLine($"        new()");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            Id = {Lit(v.Id)},");
            sb.AppendLine($"            Name = {Lit(v.Name)},");
            if (v.Icon is not null) sb.AppendLine($"            Icon = {Lit(v.Icon)},");
            if (v.Match is not null) sb.AppendLine($"            Match = {Lit(v.Match)},");
            sb.AppendLine($"        }},");
        }
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>内置模型配置表。</summary>");
        sb.AppendLine("    public static readonly ModelInfo[] Models =");
        sb.AppendLine("    {");
        foreach (var m in models)
        {
            sb.AppendLine($"        // {m.Id}");
            sb.AppendLine($"        new()");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            Id = {Lit(m.Id)},");
            sb.AppendLine($"            Vendor = {Lit(m.Vendor)},");
            if (m.Name is not null) sb.AppendLine($"            Name = {Lit(m.Name)},");
            if (m.Aliases.Length > 0) sb.AppendLine($"            Aliases = {StrArray(m.Aliases)},");
            if (m.Context > 0) sb.AppendLine($"            ContextWindow = {m.Context},");
            if (m.MaxOut > 0) sb.AppendLine($"            MaxOutputTokens = {m.MaxOut},");
            if (m.Capabilities.Length > 0) sb.AppendLine($"            Capabilities = {StrArray(m.Capabilities)},");
            if (m.ArrayContent != "TriState.Unknown") sb.AppendLine($"            ArrayContent = {m.ArrayContent},");
            if (m.Thinking) sb.AppendLine($"            SupportsThinking = true,");
            if (m.Strengths.Length > 0) sb.AppendLine($"            ThinkingStrengths = {StrArray(m.Strengths)},");
            if (m.Temperature != "TriState.Unknown") sb.AppendLine($"            Temperature = {m.Temperature},");
            if (m.Source is not null) sb.AppendLine($"            Source = {Lit(m.Source)},");
            if (m.Note is not null) sb.AppendLine($"            Note = {Lit(m.Note)},");
            sb.AppendLine($"        }},");
        }
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>Assets/icon/models/light 下实际存在的图标名（生成时扫描目录得到）。</summary>");
        sb.AppendLine($"    public static readonly string[] IconNamesLight = {StrArray(iconsLight)};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Assets/icon/models/dark 下实际存在的图标名。通常是 light 的子集，缺的走浅色兜底。</summary>");
        sb.AppendLine($"    public static readonly string[] IconNamesDark = {StrArray(iconsDark)};");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string Lit(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// 生成一个 string 数组字面量。空集合必须走 <c>Array.Empty</c> ——
    /// <c>new[] { }</c> 是没有元素可推断的隐式类型数组，编译期报 CS0826。
    /// 这个坑只在集合为空时出现，所以图标目录清空后才暴露出来。
    /// </summary>
    private static string StrArray(IEnumerable<string> values)
    {
        var items = values.Select(Lit).ToList();
        return items.Count == 0
            ? "Array.Empty<string>()"
            : "new[] { " + string.Join(", ", items) + " }";
    }

    private static List<string> ScanIcons(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();

        return Directory.EnumerateFiles(dir, "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── 杂项 ────────────────────────────────────────────────────────────────

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HtaciAI.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        // 找不到解决方案就退回当前目录：显式传 --ini / --out 时依然能用。
        return Directory.GetCurrentDirectory();
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path);
}
