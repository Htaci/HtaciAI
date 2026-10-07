using HtaciAI.Models;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 业务设置的读写与内存缓存。文件位置见 <see cref="AppPaths.Settings"/>（随数据根走）。
/// 读失败/文件损坏一律退回默认值，不阻断启动。
/// </summary>
public static class AppSettingsStore
{
    private static AppSettingsData? _current;

    public static string FilePath => AppPaths.Settings;

    /// <summary>当前设置（首次访问时从磁盘加载并缓存）。</summary>
    public static AppSettingsData Current => _current ??= Load();

    public static AppSettingsData Load() => JsonFileStore.Load<AppSettingsData>(FilePath) ?? new AppSettingsData();

    /// <summary>把当前内存里的设置写回磁盘。</summary>
    public static bool Save() => Save(Current);

    public static bool Save(AppSettingsData data)
    {
        _current = data;
        return JsonFileStore.Save(FilePath, data);
    }

    /// <summary>丢弃缓存，下次访问重新从磁盘读。切换数据根后应调用。</summary>
    public static void Reload() => _current = null;
}
