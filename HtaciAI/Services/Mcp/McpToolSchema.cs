using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HtaciAI.Services.Mcp;

/// <param name="Name">参数名。</param>
/// <param name="Type">类型标签（string / integer / array / enum / any…）。</param>
/// <param name="Description">参数说明，可能为空。</param>
/// <param name="Required">是否必填（来自 schema 的 required 数组）。</param>
public sealed record McpToolParameter(string Name, string Type, string Description, bool Required);

/// <summary>
/// 把 MCP 工具的 <c>inputSchema</c>（JSON Schema）解析成可展示的参数列表。
/// 只做界面需要的那点事——取 properties / required / description——不实现完整的 JSON Schema 语义。
/// </summary>
public static class McpToolSchema
{
    public static List<McpToolParameter> ParseParameters(string? schemaJson)
    {
        var parameters = new List<McpToolParameter>();
        if (string.IsNullOrWhiteSpace(schemaJson)) return parameters;

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
                return parameters;

            var required = ReadRequired(root);

            foreach (var property in properties.EnumerateObject())
            {
                var description = property.Value.ValueKind == JsonValueKind.Object &&
                                  property.Value.TryGetProperty("description", out var d)
                    ? d.GetString() ?? ""
                    : "";

                parameters.Add(new McpToolParameter(
                    property.Name,
                    ReadType(property.Value),
                    description,
                    required.Contains(property.Name)));
            }
        }
        catch (JsonException)
        {
            // schema 坏了不该让整页空掉：能解析出多少算多少
        }

        return parameters;
    }

    private static HashSet<string> ReadRequired(JsonElement root)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("required", out var array) || array.ValueKind != JsonValueKind.Array)
            return required;

        foreach (var item in array.EnumerateArray())
            if (item.GetString() is { Length: > 0 } name)
                required.Add(name);

        return required;
    }

    private static string ReadType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return "any";

        if (schema.TryGetProperty("type", out var type))
        {
            switch (type.ValueKind)
            {
                case JsonValueKind.String:
                    return type.GetString() ?? "any";

                // JSON Schema 允许 type 是数组（如 ["string","null"]）
                case JsonValueKind.Array:
                    var parts = type.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(s => !string.IsNullOrEmpty(s));
                    var joined = string.Join(" | ", parts!);
                    if (joined.Length > 0) return joined;
                    break;
            }
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
            return "enum";

        return "any";
    }
}
