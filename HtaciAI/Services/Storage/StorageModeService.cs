using HtaciAI.Models;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 存储模式的切换入口。模式写进位置恒定的 bootstrap 配置，重启后由 <see cref="AppPaths"/> 重新解析生效。
/// </summary>
public static class StorageModeService
{
    /// <param name="Saved">配置是否成功落盘。</param>
    /// <param name="RestartRequired">目标根与当前根不同，必须重启才生效。</param>
    /// <param name="TargetRoot">该模式下解析出的目标根目录。</param>
    /// <param name="Message">给用户看的一句话结果。</param>
    public sealed record ChangeResult(bool Saved, bool RestartRequired, string TargetRoot, string Message);

    /// <summary>
    /// 切换存储模式。<paramref name="migrateExistingData"/> 为 true 且目标根与当前根不同时，
    /// 额外登记一次「下次启动把当前根的数据搬过去」的意图（见 <see cref="DataMigrator"/>）。
    /// </summary>
    public static ChangeResult Apply(StorageMode mode, bool migrateExistingData)
    {
        var settings = StorageBootstrap.Read();
        var previousMode = AppPaths.Mode;
        settings.Mode = mode;

        var targetRoot = AppPaths.PreviewRoot(mode);
        var currentRoot = AppPaths.Root;
        var rootWillChange = !AppPaths.PathEquals(targetRoot, currentRoot);

        if (rootWillChange && migrateExistingData)
        {
            settings.MigrateOnNextStart = true;
            settings.PreviousRoot = currentRoot;
            settings.PreviousMode = previousMode;
        }
        else
        {
            settings.MigrateOnNextStart = false;
            settings.PreviousRoot = null;
        }

        var saved = StorageBootstrap.Write(settings);
        if (!saved)
            return new ChangeResult(false, rootWillChange, targetRoot, "设置写入失败，请检查配置目录权限。");

        if (!rootWillChange)
            return new ChangeResult(true, false, targetRoot, "已保存。当前模式与目标位置一致，无需重启。");

        var suffix = settings.MigrateOnNextStart ? "，重启后会把现有数据迁移过去" : "";
        return new ChangeResult(true, true, targetRoot, $"已保存，重启后生效{suffix}。");
    }

    /// <summary>登记一次「从指定目录迁移到当前根」的意图，重启后执行。用于「导入旧数据」入口。</summary>
    public static bool RequestImport(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || AppPaths.PathEquals(sourceRoot, AppPaths.Root))
            return false;

        var settings = StorageBootstrap.Read();
        settings.MigrateOnNextStart = true;
        settings.PreviousRoot = sourceRoot;
        settings.PreviousMode = settings.Mode;
        return StorageBootstrap.Write(settings);
    }
}
