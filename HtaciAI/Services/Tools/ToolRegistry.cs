using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 工具注册表：管理工具集与工具定义，提供 id / name 双索引解析与按 id 集合去重解析。
/// 全局单例（跨页面、跨对话共享）。当前为内存态，后续从数据库加载
/// （tool_toolsets / tool_tools / tool_tool_links 三表）。
/// </summary>
public sealed class ToolRegistry
{
    /// <summary>全局单例。</summary>
    public static ToolRegistry Instance { get; } = new();

    private readonly Dictionary<string, Toolset> _toolsets = new();
    private readonly Dictionary<string, ToolDefinition> _byId = new();
    private readonly Dictionary<string, ToolDefinition> _byName = new();
    private readonly Dictionary<string, HashSet<string>> _toolsetMembers = new();

    private ToolRegistry()
    {
        RegisterToolset(Toolset.Default);
    }

    /// <summary>从数据库加载工具集与工具到内存（替换当前内容；默认集兜底）。</summary>
    public async Task LoadFromDbAsync()
    {
        _toolsets.Clear();
        _byId.Clear();
        _byName.Clear();
        _toolsetMembers.Clear();

        foreach (var ts in await ToolRepository.GetAllToolsetsAsync())
            RegisterToolset(ts);
        if (!_toolsets.ContainsKey(Toolset.Default.Id))
            RegisterToolset(Toolset.Default);

        foreach (var tool in await ToolRepository.GetAllAsync())
            Register(tool);
    }

    private bool _seeded;

    /// <summary>
    /// 写入内置示例脚本并注册为示例工具（幂等：文件已存在不覆盖，工具已注册不重复）。
    /// 供端到端验证与新手体验；示例脚本位于用户数据目录 tools/examples 下。
    /// </summary>
    public void SeedExamples()
    {
        if (_seeded) return;
        _seeded = true;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".htaci", "tool_tools");
            Directory.CreateDirectory(dir);

            var addPy = Path.Combine(dir, "add.py");
            var upperJs = Path.Combine(dir, "uppercase.js");
            if (!File.Exists(addPy)) File.WriteAllText(addPy, ExampleAddPy);
            if (!File.Exists(upperJs)) File.WriteAllText(upperJs, ExampleUppercaseJs);

            if (ResolveById(ExampleAddId) is null)
                Register(new ToolDefinition
                {
                    Id = ExampleAddId,
                    Name = "add",
                    Description = "两数相加（Python 示例脚本工具）",
                    Source = ToolSource.Script,
                    Runtime = ScriptRuntimeKind.Python,
                    Target = addPy,
                    InputSchemaJson =
                        "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\",\"description\":\"加数\"},\"b\":{\"type\":\"integer\",\"description\":\"加数\"}},\"required\":[\"a\",\"b\"]}",
                    ToolsetIds = new[] { Toolset.Default.Id },
                });

            if (ResolveById(ExampleUppercaseId) is null)
                Register(new ToolDefinition
                {
                    Id = ExampleUppercaseId,
                    Name = "uppercase",
                    Description = "将字符串转为大写（Node.js 示例脚本工具）",
                    Source = ToolSource.Script,
                    Runtime = ScriptRuntimeKind.Node,
                    Target = upperJs,
                    InputSchemaJson =
                        "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"要转换的文本\"}},\"required\":[\"text\"]}",
                    ToolsetIds = new[] { Toolset.Default.Id },
                });
        }
        catch
        {
            // 示例种子失败不影响主流程
        }
    }

    private const string ExampleAddId = "example-add";
    private const string ExampleUppercaseId = "example-uppercase";

    private const string ExampleAddPy = """
        # 两数相加（HtaciAI 示例脚本工具）。参数经 stdin 传入 JSON，结果输出到 stdout。
        import json, sys

        def run(a, b):
            # 加数 / 加数
            return a + b

        if __name__ == "__main__":
            print(json.dumps(run(**json.loads(sys.stdin.read()))))
        """;

    private const string ExampleUppercaseJs = """
        // 字符串转大写（HtaciAI 示例脚本工具）。参数经 stdin 传入 JSON，结果输出到 stdout。
        const fs = require('fs');
        const input = fs.readFileSync(0, 'utf-8');
        const args = JSON.parse(input);
        console.log(args.text.toUpperCase());
        """;

    // ---- 工具集 ----

    public void RegisterToolset(Toolset ts)
    {
        _toolsets[ts.Id] = ts;
        _toolsetMembers.TryAdd(ts.Id, new HashSet<string>());
    }

    public IReadOnlyList<Toolset> GetToolsets()
        => _toolsets.Values
            .OrderBy(t => t.IsBuiltin ? 0 : 1)
            .ThenBy(t => t.Name)
            .ToList();

    // ---- 工具 ----

    public void Register(ToolDefinition tool)
    {
        _byId[tool.Id] = tool;
        _byName[tool.Name] = tool;

        foreach (var tsId in tool.EffectiveToolsetIds)
        {
            if (!_toolsetMembers.TryGetValue(tsId, out var set))
            {
                set = new HashSet<string>();
                _toolsetMembers[tsId] = set;
            }
            set.Add(tool.Id);
        }
    }

    public void Unregister(string id)
    {
        if (!_byId.Remove(id, out var tool)) return;
        _byName.Remove(tool.Name);
        foreach (var set in _toolsetMembers.Values)
            set.Remove(id);
    }

    public IReadOnlyList<ToolDefinition> GetAll() => _byId.Values.ToList();

    public IReadOnlyList<ToolDefinition> GetEnabled() => _byId.Values.Where(t => t.Enabled).ToList();

    public ToolDefinition? ResolveById(string id)
        => _byId.TryGetValue(id, out var t) ? t : null;

    public ToolDefinition? ResolveByName(string name)
        => _byName.TryGetValue(name, out var t) ? t : null;

    /// <summary>
    /// 按 id 集合解析：按 id 去重（同一工具可能在多个工具集），仅返回已启用工具。
    /// 用于"对话激活的工具集/工具混合选择"后生成实际传入 LLM 的工具列表。
    /// </summary>
    public IReadOnlyList<ToolDefinition> ResolveByIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>();
        var result = new List<ToolDefinition>();
        foreach (var id in ids)
        {
            if (!seen.Add(id)) continue;
            if (_byId.TryGetValue(id, out var t) && t.Enabled)
                result.Add(t);
        }
        return result;
    }

    /// <summary>某工具集下的已启用工具（按注册顺序）。</summary>
    public IReadOnlyList<ToolDefinition> GetByToolset(string toolsetId)
    {
        if (!_toolsetMembers.TryGetValue(toolsetId, out var ids))
            return Array.Empty<ToolDefinition>();
        return ids.Where(id => _byId.TryGetValue(id, out var t) && t.Enabled)
                  .Select(id => _byId[id])
                  .ToList();
    }
}
