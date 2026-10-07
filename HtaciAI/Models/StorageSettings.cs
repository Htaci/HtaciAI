namespace HtaciAI.Models;

/// <summary>
/// 存储引导配置（bootstrap），落盘于<b>固定位置</b> <c>%APPDATA%\HtaciAI\storage.json</c>。
///
/// 之所以不放在数据根目录里：这份配置本身决定了根目录在哪（鸡生蛋），所以位置必须与模式无关。
/// 只放「根目录怎么选」和迁移意图，不放任何业务设置——业务设置应随数据根走，见后续的
/// <c>&lt;Root&gt;\settings.json</c>。
/// </summary>
public sealed class StorageSettings
{
    /// <summary>存储模式。初始为「默认」。</summary>
    public StorageMode Mode { get; set; } = StorageMode.Default;

    /// <summary>下次启动时是否执行数据迁移（由 UI 在切换模式时置位）。</summary>
    public bool MigrateOnNextStart { get; set; }

    /// <summary>
    /// 旧版散落布局（<c>~/.htaci</c> 下的 skills / tool_tools）是否已接入统一数据根。
    /// 一次性升级动作，做完置位，避免每次启动都去合并。
    /// </summary>
    public bool LegacyAdopted { get; set; }

    /// <summary>迁移源：切换模式前生效的数据根目录。</summary>
    public string? PreviousRoot { get; set; }

    /// <summary>切换前的模式，仅用于展示与回退。</summary>
    public StorageMode PreviousMode { get; set; } = StorageMode.Default;

    /// <summary>最近一次迁移的结果描述，供数据管理页展示。</summary>
    public string? LastMigrationMessage { get; set; }
}
