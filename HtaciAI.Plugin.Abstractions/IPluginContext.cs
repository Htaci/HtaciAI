using System;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 宿主交给插件的门面。M0 只提供插件自己就需要的那几样（日志 / 私有存储 / 资源 / 元信息），
/// 对话、UI、工具等能力随各自里程碑实装、再挂到这里 —— 不提前塞一堆拿不到东西的空属性。
/// </summary>
public interface IPluginContext
{
    /// <summary>插件 id（等于目录名）。</summary>
    string PluginId { get; }

    /// <summary>插件所在目录的绝对路径。资源、图标都从这里取。</summary>
    string PluginDirectory { get; }

    /// <summary>解析后的 <c>plugin.json</c>。</summary>
    PluginManifest Manifest { get; }

    /// <summary>当前宿主版本，形如 <c>1.0.0</c>。</summary>
    string HostVersion { get; }

    /// <summary>日志。写进宿主的插件日志文件，带上插件 id 前缀。</summary>
    IPluginLogger Log { get; }

    /// <summary>插件私有持久化（JSON）。与插件目录分开存放，插件被替换时数据不会丢。</summary>
    IPluginStorage Storage { get; }

    /// <summary>插件目录内的资源定位（图标、配置模板等）。</summary>
    IResourceLocator Resources { get; }
}

/// <summary>
/// 插件日志出口。三个方法都<b>不会抛异常</b>（写盘失败也只是静默丢弃），
/// 插件因此不需要给日志调用套 try。
/// </summary>
public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);

    /// <summary><paramref name="ex"/> 可为 null。</summary>
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// 插件私有存储。每份数据一个 JSON 文件，落在宿主的插件数据目录里。
///
/// 读写都吞异常：读失败返回 <c>null</c>，写失败返回 <c>false</c>。
/// 这样插件不会因为磁盘问题把整个会话搞崩。
/// </summary>
public interface IPluginStorage
{
    /// <summary>该插件的数据目录绝对路径。</summary>
    string DataDirectory { get; }

    /// <summary>
    /// 读一份数据。<paramref name="name"/> 是文件名主干（不含扩展名），
    /// 只允许字母、数字与 <c>.</c> <c>_</c> <c>-</c>；非法名字直接返回 <c>null</c>。
    /// </summary>
    T? Load<T>(string name) where T : class;

    /// <summary>写一份数据。返回是否成功。</summary>
    bool Save<T>(string name, T value) where T : class;
}

/// <summary>
/// 插件目录内的资源定位。
/// </summary>
public interface IResourceLocator
{
    /// <summary>
    /// 把相对路径解析成绝对路径。<b>解析结果越出插件目录时返回 <c>null</c></b>
    /// （<c>..</c> 之类的路径穿越一律拒绝）。
    ///
    /// 只做「拼路径 + 越界检查」，<b>不检查文件是否存在</b>：检查存在性会让「文件缺失」
    /// 与「路径非法」都表现成 null，插件没法区分该报哪种错。要判存在用 <c>File.Exists</c>。
    /// </summary>
    string? ResolvePath(string relativePath);
}
