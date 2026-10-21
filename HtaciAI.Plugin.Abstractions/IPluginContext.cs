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

    /// <summary>
    /// 主动操作会话：发起一轮、停止、改权限档位、压缩、读最近消息。
    ///
    /// <b>只有清单里声明了 <see cref="PluginCapabilities.ChatControl"/> 时才不是 null。</b>
    /// 没声明却用到了，这里会在第一行就炸 —— 这是刻意的：空实现会让「我忘了声明」
    /// 一路伪装成「插件就是不工作」，那种问题最难查。
    /// </summary>
    ISessionFacade? Sessions { get; }
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
/// 插件私有存储。每份数据一个文件，落在宿主的插件数据目录里。
///
/// 读写都吞异常：读失败返回 <c>null</c>，写失败返回 <c>false</c>。
/// 这样插件不会因为磁盘问题把整个会话搞崩。
///
/// <b>优先用 <see cref="LoadText"/> / <see cref="SaveText"/>。</b>
/// 两个泛型方法（<see cref="Load{T}"/> / <see cref="Save{T}"/>）虽然方便，
/// 但它们内部会用反射式 JSON 序列化去处理<b>插件自己的类型</b>，
/// 而 System.Text.Json 会把这些类型永久缓存进运行时的静态结构 ——
/// 后果是这个插件被禁用后<b>无法卸载</b>，必须重启程序才能彻底释放。
/// 详见 <see cref="Save{T}"/> 的说明。
/// </summary>
public interface IPluginStorage
{
    /// <summary>该插件的数据目录绝对路径。</summary>
    string DataDirectory { get; }

    /// <summary>
    /// 读一段文本。<paramref name="name"/> 是文件名主干（不含扩展名），
    /// 只允许字母、数字与 <c>.</c> <c>_</c> <c>-</c>；非法名字或文件不存在返回 <c>null</c>。
    ///
    /// 存什么格式由插件自己决定（JSON / ini / 纯文本都行）。
    /// <b>这是推荐用法</b>：宿主只当它是字符串，不碰插件类型，插件因此可以干净卸载。
    /// </summary>
    string? LoadText(string name);

    /// <summary>写一段文本。返回是否成功。</summary>
    bool SaveText(string name, string text);

    /// <summary>
    /// 读一份数据（JSON 反序列化）。
    ///
    /// ⚠️ <b>用它会让该插件需要重启才能完全卸载。</b>
    /// System.Text.Json 会把 <typeparamref name="T"/> 的元数据缓存进进程级静态结构，
    /// 而 <typeparamref name="T"/> 通常定义在插件程序集里 —— 那份缓存会一直持有插件，
    /// 导致 <c>AssemblyLoadContext</c> 无法回收。已实测：仅调用一次
    /// <c>Save&lt;插件类型&gt;</c> 就足以让卸载失败（换 options、换 resolver、冻结 options 都不行）。
    ///
    /// 需要可卸载就改用 <see cref="LoadText"/>，自己解析。
    /// </summary>
    T? Load<T>(string name) where T : class;

    /// <summary>写一份数据（JSON 序列化）。⚠️ 同 <see cref="Load{T}"/>：会导致需要重启才能卸载。</summary>
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
