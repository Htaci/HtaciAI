using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Tools;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 全进程的插件工具登记表：谁登记了哪个工具名、执行体是谁。
///
/// <b>为什么要有这么一层，而不是直接把执行体塞进 <see cref="ToolDefinition"/></b>：
/// <see cref="ToolDefinition"/> 是「给模型看的描述 + 路由依据」，它在工具页、选择器、
/// 请求体里到处被复制与展示；执行体是插件代码，混进去之后任何一处留着那份定义
/// 就等于留着插件程序集（禁用后永远「需重启」）。所以执行体单独按插件 id 存，
/// 卸载插件时一次清掉。
///
/// <b>线程</b>：登记/注销加锁（可能来自插件自己的线程）；执行按名字查表，返回后不再持有。
/// </summary>
public static class PluginToolRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> ByName = new(StringComparer.Ordinal);

    internal sealed record Entry(string PluginId, PluginToolHandler Handler);

    /// <summary>工具名规则。与 AI 自举创建工具的规则保持一致：小写字母开头，只含小写字母数字下划线。</summary>
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    /// <summary>
    /// 登记一个插件的工具：校验 → 写进 <see cref="ToolRegistry"/>（让模型与工具页看见）→ 记下执行体。
    /// 校验不过时返回 <c>null</c> 并记日志（不抛：一个工具名写错不该让整个插件加载失败）。
    /// </summary>
    internal static IDisposable? Register(
        string pluginId, PluginToolDefinition definition, PluginToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);

        var name = (definition.Name ?? "").Trim();

        if (!NamePattern.IsMatch(name))
        {
            PluginLog.Warn(pluginId, $"工具名不合规（要求小写字母开头、只含 a-z0-9_，2–64 字符）：{name}");
            return null;
        }

        if (!IsValidRootSchema(definition.InputSchemaJson))
        {
            PluginLog.Warn(pluginId, $"工具 {name} 的 InputSchemaJson 不是合法的 JSON Schema（根必须是 type=object）");
            return null;
        }

        // 重名检查：内置/脚本/MCP/别的插件都不能撞 —— 名字是模型唯一的调用入口
        if (ToolRegistry.Instance.ResolveByName(name) is not null)
        {
            PluginLog.Warn(pluginId, $"工具 {name} 与已有工具重名，已拒绝登记");
            return null;
        }

        // 工具 id 带上插件 id：状态存储、会话里选中的工具 id 都靠它区分是谁的
        var toolId = BuildToolId(pluginId, name);

        var tool = new ToolDefinition
        {
            Id = toolId,
            Name = name,
            Description = definition.Description ?? "",
            InputSchemaJson = string.IsNullOrWhiteSpace(definition.InputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : definition.InputSchemaJson,
            Source = ToolSource.Plugin,

            // Target 存插件 id：分派时用它 + 工具名找回执行体
            Target = pluginId,
            DangerLevel = Map(definition.DangerLevel),

            // 插件工具默认就启用：用户装插件就是为了用它的工具。
            // 但会话里仍然要显式勾选才会传给模型（与会话自带的 EnabledToolIds 一致）。
            Enabled = true,
        };

        ToolRegistry.Instance.Register(tool);

        lock (Gate)
            ByName[name] = new Entry(pluginId, handler);

        PluginLog.Info(pluginId, $"已登记工具 {name}（等级 {tool.DangerLevel}）");

        return new Registration(pluginId, name, toolId);
    }

    /// <summary>这个工具 id 是不是插件工具，以及属于哪个插件。</summary>
    public static string BuildToolId(string pluginId, string toolName) => $"plugin:{pluginId}:{toolName}";

    /// <summary>注销某个插件的全部工具（插件被禁用 / 消失时调用）。</summary>
    internal static void RemovePlugin(string pluginId)
    {
        List<string> names;
        lock (Gate)
        {
            names = new List<string>();
            foreach (var (name, entry) in ByName)
                if (string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal))
                    names.Add(name);

            foreach (var name in names)
                ByName.Remove(name);
        }

        foreach (var name in names)
            ToolRegistry.Instance.Unregister(BuildToolId(pluginId, name));

        if (names.Count > 0)
            PluginLog.Info(pluginId, $"已注销 {names.Count} 个工具：{string.Join(", ", names)}");
    }

    /// <summary>
    /// 执行一个插件工具。找不到执行体（插件刚被禁用）时如实失败，让模型知道这个工具不可用。
    /// </summary>
    public static async Task<ToolExecution> InvokeAsync(
        ToolDefinition tool, ChatToolCall call, CancellationToken ct)
    {
        Entry? entry;
        lock (Gate)
            ByName.TryGetValue(tool.Name, out entry);

        if (entry is null)
            return ToolExecution.Fail($"工具 {tool.Name} 所属的插件当前不可用");

        try
        {
            var result = await entry.Handler(new PluginToolCall
            {
                Name = tool.Name,
                ArgumentsJson = call.Arguments ?? "",
            }, ct);

            return result.Success
                ? ToolExecution.Ok(result.Content ?? "")
                : ToolExecution.Fail(result.Content ?? "插件工具返回了失败，但没有给出原因");
        }
        catch (OperationCanceledException)
        {
            // 停止或超时：外层的超时/停止守卫已经会写结果，这里只需要别把它当成插件错误
            throw;
        }
        catch (Exception ex)
        {
            // 插件代码抛异常不能掀掉整轮对话：写成一条失败结果回给模型
            PluginLog.Error(entry.PluginId, $"工具 {tool.Name} 执行时抛异常", ex);
            return ToolExecution.Fail($"插件工具 {tool.Name} 执行失败：{OneLine(ex.Message)}");
        }
    }

    /// <summary>宿主有没有为某个插件登记过工具（诊断 / 插件页用）。</summary>
    public static int CountFor(string pluginId)
    {
        lock (Gate)
        {
            var count = 0;
            foreach (var entry in ByName.Values)
                if (string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal))
                    count++;

            return count;
        }
    }

    private static bool IsValidRootSchema(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;   // 空 = 用默认的空 object schema

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && string.Equals(type.GetString(), "object", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ToolDangerLevel Map(PluginToolDangerLevel level) => level switch
    {
        PluginToolDangerLevel.Risk => ToolDangerLevel.Risk,
        PluginToolDangerLevel.Danger => ToolDangerLevel.Danger,
        _ => ToolDangerLevel.Safe,
    };

    private static string OneLine(string? text)
    {
        var flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private sealed class Registration(string pluginId, string name, string toolId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (Gate)
            {
                // 换代过（同名工具已被别的插件重新登记）就不要误删
                if (ByName.TryGetValue(name, out var entry) &&
                    string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal))
                    ByName.Remove(name);
            }

            ToolRegistry.Instance.Unregister(toolId);
        }
    }
}

/// <summary>
/// <see cref="IToolRegistryApi"/> 的宿主实现：一个插件一个实例。
/// <see cref="Dispose"/>（禁用插件时由宿主调用）会把该插件的工具全部注销。
/// </summary>
public sealed class PluginToolFacade(string pluginId) : IToolRegistryApi, IDisposable
{
    private bool _disposed;

    public IDisposable Register(PluginToolDefinition definition, PluginToolHandler execute)
    {
        if (_disposed) return new Ignored();

        var registration = PluginToolRegistry.Register(pluginId, definition, execute);
        return registration ?? new Ignored();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PluginToolRegistry.RemovePlugin(pluginId);
    }

    private sealed class Ignored : IDisposable
    {
        public void Dispose() { }
    }
}
