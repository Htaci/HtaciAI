using System;
using System.IO;
using HtaciAI.Models;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 存储引导配置的读写。有两个候选位置，<b>便携那份优先</b>：
/// <list type="bullet">
///   <item><b>便携</b>：<c>&lt;程序目录&gt;/data/storage.json</c> —— 随程序一起走。
///     便携版拷到别的设备后它跟着走，于是新设备（没有机器级配置）也能认出「我是便携版」，
///     而不至于回落到 AppData；</item>
///   <item><b>机器级</b>：<c>%APPDATA%\HtaciAI\storage.json</c> —— 位置恒定、与数据根无关
///     （否则换模式就找不到「该用哪个模式」的记录了）。</item>
/// </list>
///
/// <b>便携那份存在的意义是「自包含」</b>：一旦它的模式是便携，读写就全部落在它身上 ——
/// 不读 AppData、不写 AppData、连 <c>%APPDATA%\HtaciAI</c> 目录都不会创建出来。
/// 这样装在移动盘上的便携版在任何设备上都跟着自己走，不会被那台机器上
/// 既有的 AppData 配置劫持（那正是「同一个 exe 在不同机器上跑出两套数据」的来源）。
///
/// macOS 例外：便携模式在 Mac 上整体被禁用（见 <see cref="AppPaths.PreviewRoot"/>），
/// 所以那边不认便携这份配置 —— 否则会读写到 <c>.app</c> 包内部去。
/// </summary>
public static class StorageBootstrap
{
    private const string FolderName = "HtaciAI";
    private const string FileName = "storage.json";

    /// <summary>机器级配置目录（恒定，与数据根无关）。</summary>
    public static string MachineConfigDirectory { get; } = ResolveConfigDirectory();

    /// <summary>机器级配置文件：<c>%APPDATA%\HtaciAI\storage.json</c>。</summary>
    public static string MachineConfigFile { get; } = Path.Combine(MachineConfigDirectory, FileName);

    /// <summary>便携配置文件：<c>&lt;程序目录&gt;/data/storage.json</c>。存在时优先于机器级那份。</summary>
    public static string LocalConfigFile { get; } = Path.Combine(AppPaths.PortableRoot, FileName);

    /// <summary>便携配置在本次运行中是否参与判定（macOS 上不参与）。</summary>
    private static bool UseLocalConfig => !OperatingSystem.IsMacOS();

    /// <summary>
    /// 当前生效的配置文件：便携那份存在就用它，否则用机器级那份。
    /// 写入也走这里 —— 便携版带了自己的配置就<b>不会顺手把 AppData 目录创建出来</b>。
    /// </summary>
    public static string ActiveConfigFile
        => UseLocalConfig && File.Exists(LocalConfigFile) ? LocalConfigFile : MachineConfigFile;

    /// <summary>读取配置（便携优先）；两份都缺失或损坏时返回默认值（模式 = Default）。</summary>
    public static StorageSettings Read()
    {
        if (UseLocalConfig && JsonFileStore.Load<StorageSettings>(LocalConfigFile) is { } local)
            return local;

        return JsonFileStore.Load<StorageSettings>(MachineConfigFile) ?? new StorageSettings();
    }

    /// <summary>写回配置。失败不抛异常，返回 false 由调用方决定是否提示。</summary>
    public static bool Write(StorageSettings settings) => JsonFileStore.Save(ActiveConfigFile, settings);

    /// <summary>
    /// 把配置固化到便携目录里，让这份安装自包含。
    /// 由「切换到便携模式」调用：不这么做的话，便携意图只记在 AppData 上，
    /// 换台有自己 AppData 配置的机器就会认不出它是便携版。
    /// </summary>
    public static bool WriteLocal(StorageSettings settings)
        => UseLocalConfig && JsonFileStore.Save(LocalConfigFile, settings);

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
