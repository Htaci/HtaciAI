using System;
using System.Collections.Generic;
using System.IO;

namespace HtaciAI.Services.Storage;

/// <summary>
/// 切换数据根目录时的旧数据搬迁。
///
/// 设计取舍：
/// <list type="bullet">
///   <item><b>意图驱动</b>：只有 UI 显式置位 <c>MigrateOnNextStart</c> 才会跑。
///   不做「检测到新根为空就自动搬」——那会让便携版插到别人机器上时误灌本机数据。</item>
///   <item><b>只拷贝不删除</b>：源目录原封不动，用户确认无误后自行清理。代价是暂时占双份磁盘。</item>
///   <item><b>不覆盖已有文件</b>：目标已存在的文件一律保留，包括用户手改过的 SKILL.md。</item>
/// </list>
/// </summary>
public static class DataMigrator
{
    /// <summary>随根目录一起搬迁的子目录（相对数据根）。</summary>
    private static readonly string[] DataFolders = { "attachments", "skills", "tools" };

    /// <summary>SQLite 的三件套后缀。</summary>
    private static readonly string[] DatabaseSuffixes = { "", "-wal", "-shm" };

    /// <summary>根目录下需要跟着一起搬的普通文件。</summary>
    private static readonly string[] RootFiles = { "settings.json" };

    private const string DatabaseName = "data.db";

    /// <summary>
    /// 执行挂起的迁移，返回结果描述（无可迁移内容时返回空串）。
    ///
    /// ⚠️ 必须在 <c>DatabaseService.Initialize()</c> <b>之前</b>调用：那时没有任何 SQLite 连接，
    /// 才能安全搬运 <c>-wal</c> / <c>-shm</c>。
    /// </summary>
    public static string RunStartupMigration()
    {
        var settings = StorageBootstrap.Read();
        var parts = new List<string>();
        var dirty = false;

        // 1) 旧版散落布局一次性接入。不做的话，升级后用户会看到自己的技能凭空消失。
        //    只在目标缺文件时补，所以即使标记位因故丢失、重复执行也无害。
        if (!settings.LegacyAdopted)
        {
            var adopted = AdoptLegacyLayout();
            if (!string.IsNullOrEmpty(adopted)) parts.Add(adopted);
            settings.LegacyAdopted = true;
            dirty = true;
        }

        // 2) 模式切换迁移，意图驱动。
        //    先清意图再执行：失败不会卡在「每次启动都重试」里，用户可以再从数据管理页触发一次。
        if (settings.MigrateOnNextStart)
        {
            settings.MigrateOnNextStart = false;
            dirty = true;

            var source = settings.PreviousRoot;
            if (!string.IsNullOrWhiteSpace(source) && !AppPaths.PathEquals(source, AppPaths.Root))
                parts.Add(Migrate(source!, AppPaths.Root));
        }

        if (!dirty) return "";

        var message = string.Join("；", parts);
        settings.LastMigrationMessage = string.IsNullOrEmpty(message) ? null : message;
        StorageBootstrap.Write(settings);
        return message;
    }

    /// <summary>
    /// 把旧版 <c>%USERPROFILE%\.htaci</c> 下的技能与脚本工具合并进统一数据根
    /// （旧版这两处不在数据根里，升级后就找不到了）。
    /// </summary>
    private static string AdoptLegacyLayout()
    {
        var parts = new List<string>();

        var skillsCount = MergeCopyIfOutsideRoot(AppPaths.LegacySkillsRoot, Path.Combine(AppPaths.Root, "skills"));
        if (skillsCount > 0) parts.Add($"旧技能 {skillsCount} 个文件");

        var toolsCount = MergeCopyIfOutsideRoot(AppPaths.LegacyToolsRoot, Path.Combine(AppPaths.Root, "tools"));
        if (toolsCount > 0) parts.Add($"旧脚本工具 {toolsCount} 个文件");

        return parts.Count > 0 ? $"已接管 {string.Join("、", parts)}" : "";
    }

    private static int MergeCopyIfOutsideRoot(string source, string target)
    {
        if (AppPaths.PathEquals(source, target)) return 0;
        return Directory.Exists(source) ? MergeCopy(source, target) : 0;
    }

    /// <summary>该数据根下是否已有数据库。用于判断旧位置有没有数据、目标是不是空的。</summary>
    public static bool HasDatabase(string? root)
        => !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root!, DatabaseName));

    private static string Migrate(string source, string target)
    {
        if (!Directory.Exists(source)) return $"未迁移：源目录不存在 {source}";

        var copied = new List<string>();
        var notes = new List<string>();

        foreach (var name in DataFolders)
        {
            var from = Path.Combine(source, name);
            if (!Directory.Exists(from)) continue;

            var count = MergeCopy(from, Path.Combine(target, name));
            if (count > 0) copied.Add($"{name} {count} 个文件");
        }

        // 业务设置也要跟着走，否则换存储模式后默认配置会「凭空消失」。
        foreach (var name in RootFiles)
        {
            var from = Path.Combine(source, name);
            var to = Path.Combine(target, name);
            if (!File.Exists(from) || File.Exists(to)) continue;

            try
            {
                Directory.CreateDirectory(target);
                File.Copy(from, to, overwrite: false);
                copied.Add(name);
            }
            catch
            {
                // 单个文件失败不中断整体迁移
            }
        }

        if (File.Exists(Path.Combine(source, DatabaseName)))
        {
            if (File.Exists(Path.Combine(target, DatabaseName)))
                notes.Add("目标已有 data.db，未覆盖");
            else
            {
                try
                {
                    CopyDatabase(source, target);
                    copied.Add(DatabaseName);
                }
                catch (Exception ex)
                {
                    notes.Add($"data.db 拷贝失败：{ex.Message}");
                }
            }
        }

        if (copied.Count == 0 && notes.Count == 0) return "未迁移：旧位置没有找到数据";

        var text = copied.Count > 0 ? $"已迁移 {string.Join("、", copied)}" : "未迁移";
        if (notes.Count > 0) text += $"（{string.Join("；", notes)}）";
        return text;
    }

    /// <summary>
    /// 先全部拷成 <c>.migrating</c> 临时文件，再逐个改名落定，避免半拷贝的库被当成完整库打开。
    /// 改名顺序让 data.db 先行：<c>-wal</c>/<c>-shm</c> 找不到主库时 SQLite 会忽略它们，
    /// 反过来只是丢掉最近的未合并事务，两种情况都不会损坏数据库。
    /// </summary>
    private static void CopyDatabase(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);

        var staged = new List<(string Temp, string Final)>();
        try
        {
            foreach (var suffix in DatabaseSuffixes)
            {
                var from = Path.Combine(sourceRoot, DatabaseName) + suffix;
                if (!File.Exists(from)) continue;

                var final = Path.Combine(targetRoot, DatabaseName) + suffix;
                var temp = final + ".migrating";
                File.Copy(from, temp, overwrite: true);
                staged.Add((temp, final));
            }

            foreach (var (temp, final) in staged)
                File.Move(temp, final, overwrite: true);
        }
        finally
        {
            foreach (var (temp, _) in staged)
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // 残留的 .migrating 不影响使用，清理失败无需上报
                }
            }
        }
    }

    /// <summary>合并式递归拷贝：目标已存在的文件不覆盖。</summary>
    private static int MergeCopy(string source, string target)
    {
        Directory.CreateDirectory(target);
        var count = 0;

        foreach (var dir in Directory.GetDirectories(source))
            count += MergeCopy(dir, Path.Combine(target, Path.GetFileName(dir)));

        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(dest)) continue;

            try
            {
                File.Copy(file, dest, overwrite: false);
                count++;
            }
            catch
            {
                // 单个文件失败不中断整体迁移
            }
        }

        return count;
    }
}
