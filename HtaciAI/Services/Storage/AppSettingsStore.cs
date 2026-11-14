using System;
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
        var saved = JsonFileStore.Save(FilePath, data);

        // 每次保存都通知一遍（不是「内容变了才通知」）：设置页改动即存、一次点击一次落盘，
        // 订阅方自己比对关心的那个值就够了。已经建好、甚至被缓存的窗口靠它刷新
        // 「按设置算出来的界面」（如回复总结的按钮），不必等重建。
        Changed?.Invoke();
        return saved;
    }

    /// <summary>
    /// 设置刚写回磁盘。订阅方应<b>先比对自己关心的那个值</b>再决定要不要重绘 ——
    /// 这个事件在一次「改个阈值」里同样会触发，无条件响应等于给每次点击都配一次整块重画。
    /// </summary>
    public static event Action? Changed;

    /// <summary>丢弃缓存，下次访问重新从磁盘读。切换数据根后应调用。</summary>
    public static void Reload() => _current = null;
}
