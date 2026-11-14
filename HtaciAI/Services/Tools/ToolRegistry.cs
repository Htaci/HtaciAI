using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtaciAI.Data;

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

    /// <summary>
    /// 用户给单个工具配过的超时（秒，0 = 一直等）。存在库里，但注册时要落到定义上 ——
    /// 内置工具的定义是代码里 new 出来的，插件与 MCP 工具是运行时注册的，
    /// 它们没有别的地方能承接这份配置，所以缓存一份在这里、Register 时套用。
    /// </summary>
    private readonly Dictionary<string, int> _timeouts = new(StringComparer.Ordinal);

    private ToolRegistry()
    {
        RegisterToolset(Toolset.All);
        RegisterToolset(Toolset.Builtin);
        RegisterBuiltins();
    }

    /// <summary>
    /// 从数据库加载工具集与工具到内存（替换当前内容），再补注册内置工具。
    /// 「全部」「内置」的语义只存在于代码里（库里至多有同名行），所以加载完必须用代码定义覆盖一次。
    /// </summary>
    public async Task LoadFromDbAsync()
    {
        _toolsets.Clear();
        _byId.Clear();
        _byName.Clear();
        _toolsetMembers.Clear();

        // 超时覆盖要在注册工具之前装进缓存：Register 时才套得到定义上
        _timeouts.Clear();
        foreach (var (toolId, seconds) in await ToolRepository.GetTimeoutsAsync())
            _timeouts[toolId] = seconds;

        foreach (var ts in await ToolRepository.GetAllToolsetsAsync())
            RegisterToolset(ts);

        RegisterToolset(Toolset.All);
        RegisterToolset(Toolset.Builtin);

        foreach (var tool in await ToolRepository.GetAllAsync())
            Register(tool);

        RegisterBuiltins();
    }

    /// <summary>注册内置工具（幂等：同名 id 覆盖为内存定义，不入库）。</summary>
    public void RegisterBuiltins()
    {
        foreach (var tool in BuiltinTools.GetAll())
            Register(tool);
    }

    // ---- 工具集 ----

    public void RegisterToolset(Toolset ts)
    {
        _toolsets[ts.Id] = ts;
        _toolsetMembers.TryAdd(ts.Id, new HashSet<string>());
    }

    /// <summary>
    /// 注销一个工具集（插件卸载时用）。
    ///
    /// <b>只允许注销非内置集合</b>：「全部」「内置」是代码定义出来的，
    /// 删掉之后 <see cref="GetToolsets"/> 会少两项、选择器也会少两行 ——
    /// 那不是插件该能造成的影响。
    /// </summary>
    public void UnregisterToolset(string id)
    {
        if (Toolset.IsAutoId(id)) return;
        if (!_toolsets.Remove(id)) return;

        _toolsetMembers.Remove(id);

        // 成员工具上的归属链接也要摘掉：留着的话下次同名工具复用同一个 id
        // 时会莫名其妙地「已经在某个集合里」。
        foreach (var tool in _byId.Values)
        {
            if (tool.ToolsetIds.Contains(id))
                tool.ToolsetIds = tool.ToolsetIds.Where(x => x != id).ToList();
        }
    }

    /// <summary>全部集合：自动集合固定在最前（全部 → 内置），其余按名称排。</summary>
    public IReadOnlyList<Toolset> GetToolsets()
        => _toolsets.Values
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();

    // ---- 工具 ----

    public void Register(ToolDefinition tool)
    {
        // 用户配过的超时优先于代码里的默认值（插件与 MCP 工具是运行时注册的，只有这一处能接上）
        if (_timeouts.TryGetValue(tool.Id, out var seconds)) tool.TimeoutSeconds = seconds;

        _byId[tool.Id] = tool;
        _byName[tool.Name] = tool;

        // 自动集合的成员是算出来的，不维护成员表
        foreach (var tsId in tool.ToolsetIds.Where(id => !Toolset.IsAutoId(id)))
            MemberSet(tsId).Add(tool.Id);
    }

    /// <summary>某个工具当前配的超时（秒，0 = 一直等）；null = 没单独配，走默认。</summary>
    public int? GetTimeoutSeconds(string toolId)
        => _timeouts.TryGetValue(toolId, out var seconds) ? seconds : null;

    /// <summary>
    /// 改一个工具的超时：写库 + 立刻落到内存里的定义上（不必重启）。
    /// <paramref name="seconds"/> 为 null = 恢复默认。
    /// <b>不经过 <see cref="ToolRepository.UpdateAsync"/></b>：那个方法只认 tool_tools 里的行，
    /// 内置 / 插件 / MCP 工具根本没有那一行，写不进去。
    /// </summary>
    public async Task SetTimeoutSecondsAsync(string toolId, int? seconds)
    {
        await ToolRepository.SetTimeoutAsync(toolId, seconds);

        if (seconds is null) _timeouts.Remove(toolId);
        else _timeouts[toolId] = Math.Max(0, seconds.Value);

        if (_byId.TryGetValue(toolId, out var tool))
            tool.TimeoutSeconds = seconds is null ? null : Math.Max(0, seconds.Value);
    }

    /// <summary>把一个已有工具加进某个自建集合（多对多，重复添加无副作用）。</summary>
    public void AddToolToToolset(string toolId, string toolsetId)
    {
        if (Toolset.IsAutoId(toolsetId)) return;
        if (!_byId.TryGetValue(toolId, out var tool)) return;
        if (tool.ToolsetIds.Contains(toolsetId)) return;

        tool.ToolsetIds = tool.ToolsetIds.Append(toolsetId).ToList();
        MemberSet(toolsetId).Add(toolId);
    }

    private HashSet<string> MemberSet(string toolsetId)
    {
        if (_toolsetMembers.TryGetValue(toolsetId, out var set)) return set;
        set = new HashSet<string>();
        _toolsetMembers[toolsetId] = set;
        return set;
    }

    public void Unregister(string id)
    {
        if (!_byId.Remove(id, out var tool)) return;
        _byName.Remove(tool.Name);
        foreach (var set in _toolsetMembers.Values)
            set.Remove(id);
    }

    public IReadOnlyList<ToolDefinition> GetAll() => _byId.Values.ToList();

    public IReadOnlyList<ToolDefinition> GetEnabled() => _byId.Values.Where(t => t.Enabled && !t.IsInternal).ToList();

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
            if (_byId.TryGetValue(id, out var t) && t.Enabled && !t.IsInternal)
                result.Add(t);
        }
        return result;
    }

    /// <summary>某工具集下的已启用工具。「全部」「内置」按规则算，其余按成员表。</summary>
    public IReadOnlyList<ToolDefinition> GetByToolset(string toolsetId)
    {
        if (toolsetId == Toolset.AllId)
            return GetEnabled();

        if (toolsetId == Toolset.BuiltinId)
            return _byId.Values
                .Where(t => t.Enabled && !t.IsInternal && t.Source == ToolSource.Builtin)
                .ToList();

        if (!_toolsetMembers.TryGetValue(toolsetId, out var ids))
            return Array.Empty<ToolDefinition>();
        return ids.Where(id => _byId.TryGetValue(id, out var t) && t.Enabled && !t.IsInternal)
                  .Select(id => _byId[id])
                  .ToList();
    }
}
