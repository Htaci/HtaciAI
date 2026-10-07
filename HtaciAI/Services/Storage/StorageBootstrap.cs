using System;
using System.IO;
using HtaciAI.Models;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 存储引导配置的读写。文件位置<b>恒定</b>为 <c>%APPDATA%\HtaciAI\storage.json</c>，
/// 与当前存储模式无关——否则换模式就找不到「该用哪个模式」的记录了。
/// </summary>
public static class StorageBootstrap
{
    private const string FolderName = "HtaciAI";
    private const string FileName = "storage.json";

    /// <summary>引导配置目录（恒定，与数据根无关）。</summary>
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();

    /// <summary>引导配置文件完整路径。</summary>
    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, FileName);

    /// <summary>读取配置；文件缺失或损坏时返回默认值（模式 = Default）。</summary>
    public static StorageSettings Read() => JsonFileStore.Load<StorageSettings>(ConfigFile) ?? new StorageSettings();

    /// <summary>写回配置。写失败不抛异常，返回 false 由调用方决定是否提示。</summary>
    public static bool Write(StorageSettings settings) => JsonFileStore.Save(ConfigFile, settings);

    /// <summary>
    /// ApplicationData 理论上总是可写，但在精简/受限环境下可能返回空串或不可写，
    /// 因此逐级回退到 LocalApplicationData、最后退回程序目录。
    /// </summary>
    private static string ResolveConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = AppContext.BaseDirectory;

        return Path.Combine(appData, FolderName);
    }
}
