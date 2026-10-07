using System;
using System.IO;
using HtaciAI.Models;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 应用数据根目录的<b>唯一真相来源</b>。所有落盘位置都必须经由这里取路径，不要再自己拼。
///
/// 统一布局：
/// <code>
/// &lt;Root&gt;/
///   data.db          (+ data.db-wal / data.db-shm)
///   attachments/     剪贴板图片缓存
///   skills/          技能定义（&lt;id&gt;/SKILL.md），用户可手改
///   tools/           脚本工具（原 ~/.htaci/tool_tools）
///   settings.json    业务设置（默认配置页的项）
/// </code>
///
/// 根目录由 <c>storage.json</c> 里的 <see cref="StorageMode"/> 决定：
/// <list type="bullet">
///   <item><see cref="StorageMode.Portable"/>：程序目录下的 <c>data</c>。</item>
///   <item><see cref="StorageMode.AppData"/>：<c>%APPDATA%\HtaciAI</c>。</item>
///   <item><see cref="StorageMode.Default"/>：程序目录下<b>存在</b> <c>data</c> 就用它（等价便携），否则 <c>%APPDATA%\HtaciAI</c>。
///   便携版拷到别的机器上时本机没有 storage.json，靠这条自动跟着程序走。</item>
/// </list>
///
/// 「程序目录」取 <see cref="AppContext.BaseDirectory"/>，即 exe 旁边。发布配置（FolderProfile.pubxml）开了
/// <c>PublishSingleFile</c>，此时它就是 exe 所在目录，语义正确。
/// ⚠️ 今后<b>不要</b>给 csproj 开 <c>IncludeAllContentForSelfExtract</c>，否则单文件会先解压到临时目录，
/// 该属性会指向临时目录，便携根就跑到 %TEMP% 里去了。
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "HtaciAI";
    private const string LegacyUserFolder = ".htaci";

    private static readonly object Gate = new();
    private static bool _initialized;

    private static string _root = "";
    private static string _databaseFile = "";
    private static string _attachments = "";
    private static string _skills = "";
    private static string _tools = "";
    private static string _settings = "";
    private static string _requestedRoot = "";
    private static StorageMode _mode = StorageMode.Default;
    private static string? _initError;

    /// <summary>当前生效的数据根目录。</summary>
    public static string Root { get { EnsureInitialized(); return _root; } }

    /// <summary>SQLite 库文件。</summary>
    public static string DatabaseFile { get { EnsureInitialized(); return _databaseFile; } }

    /// <summary>附件缓存目录。</summary>
    public static string Attachments { get { EnsureInitialized(); return _attachments; } }

    /// <summary>技能目录。</summary>
    public static string Skills { get { EnsureInitialized(); return _skills; } }

    /// <summary>脚本工具目录。</summary>
    public static string Tools { get { EnsureInitialized(); return _tools; } }

    /// <summary>业务设置文件（默认配置页的那些项）。随数据根走，便携版拷走时设置跟着走。</summary>
    public static string Settings { get { EnsureInitialized(); return _settings; } }

    /// <summary>当前配置的模式。</summary>
    public static StorageMode Mode { get { EnsureInitialized(); return _mode; } }

    /// <summary>解析期间发生的异常描述（如目标目录不可写）。非 null 时数据管理页应展示。</summary>
    public static string? InitError { get { EnsureInitialized(); return _initError; } }

    /// <summary>期望的根目录（兜底回退之前的）。仅当 <see cref="InitError"/> 非 null 时有展示意义。</summary>
    public static string RequestedRoot { get { EnsureInitialized(); return _requestedRoot; } }

    // ---- 固定位置：AppData / 便携根，以及旧版路径（迁移用） ----

    /// <summary>AppData 模式的目标，也是旧版的数据目录：<c>%APPDATA%\HtaciAI</c>。</summary>
    public static string AppDataRoot { get; } = BuildAppDataRoot();

    /// <summary>便携模式的目标：<c>&lt;程序目录&gt;\data</c>。</summary>
    public static string PortableRoot { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>旧版技能目录 <c>%USERPROFILE%\.htaci\skills</c>。</summary>
    public static string LegacySkillsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LegacyUserFolder, "skills");

    /// <summary>旧版脚本工具目录 <c>%USERPROFILE%\.htaci\tool_tools</c>。</summary>
    public static string LegacyToolsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LegacyUserFolder, "tool_tools");

    /// <summary>幂等。应在启动最早期调用一次（见 <c>App.OnFrameworkInitializationCompleted</c>）。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            Resolve();
        }
    }

    /// <summary>属性 getter 自愈：即便调用方忘了 Initialize，也不会拿到空路径。</summary>
    private static void EnsureInitialized() => Initialize();

    /// <summary>预览某个模式会解析到哪个根目录，不改变当前状态。供数据管理页展示切换结果。</summary>
    public static string PreviewRoot(StorageMode mode) => mode switch
    {
        StorageMode.Portable => PortableRoot,
        StorageMode.AppData => AppDataRoot,
        _ => Directory.Exists(PortableRoot) ? PortableRoot : AppDataRoot,
    };

    private static void Resolve()
    {
        var bootstrap = StorageBootstrap.Read();
        _mode = bootstrap.Mode;

        Apply(PreviewRoot(_mode));
        _requestedRoot = _root;

        // 只建根目录，不预建子目录：迁移要靠「目标目录是否存在」判断目标是否已有数据。
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex)
        {
            _initError = $"无法创建数据目录 {_root}：{ex.Message}";
            if (PathEquals(_root, AppDataRoot)) return;

            Apply(AppDataRoot);
            try
            {
                Directory.CreateDirectory(_root);
                _initError += $"；已回退到 {_root}";
            }
            catch (Exception fallbackEx)
            {
                _initError += $"；回退 {_root} 同样失败：{fallbackEx.Message}";
            }
        }
    }

    private static string BuildAppDataRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = AppContext.BaseDirectory;

        return Path.Combine(appData, AppFolderName);
    }

    private static void Apply(string root)
    {
        _root = root;
        _databaseFile = Path.Combine(root, "data.db");
        _attachments = Path.Combine(root, "attachments");
        _skills = Path.Combine(root, "skills");
        _tools = Path.Combine(root, "tools");
        _settings = Path.Combine(root, "settings.json");
    }

    /// <summary>Windows 下路径大小写不敏感；末尾分隔符差异也不该算作不同目录。</summary>
    public static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                TrimSeparator(Path.GetFullPath(a)),
                TrimSeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string TrimSeparator(string path)
        => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
