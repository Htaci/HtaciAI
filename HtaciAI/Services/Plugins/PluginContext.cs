using System;
using System.IO;
using System.Text.RegularExpressions;
using HtaciAI.Plugin.Abstractions;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 交给插件的门面集合。哪一项为 null，就说明清单里没声明对应能力。
///
/// 打包成一个对象而不是六个构造参数：每加一个里程碑就多一个参数的话，
/// 调用点会变成一长串 <c>null</c>，看的人根本分不清哪一个是哪一个。
/// </summary>
internal sealed record PluginFacades(
    ISessionFacade? Sessions = null,
    IPromptRegistry? Prompts = null,
    IChatEventBus? Chat = null,
    IPluginWindowHost? Windows = null,
    IUiExtensionRegistry? Slots = null);

/// <summary>
/// <see cref="IPluginContext"/> 的宿主实现。<b>只提供这份清单声明了的能力</b> ——
/// 没声明的属性是 null，插件第一行就会炸出来，而不是伪装成「插件就是不工作」。
/// </summary>
internal sealed partial class PluginContext : IPluginContext
{
    public PluginContext(PluginManifest manifest, string directory, string hostVersion, PluginFacades facades)
    {
        Manifest = manifest;
        PluginId = manifest.Id;
        PluginDirectory = directory;
        HostVersion = hostVersion;
        Log = new ScopedLogger(manifest.Id);
        Storage = new StorageImpl(manifest.Id);
        Resources = new ResourcesImpl(directory);
        Sessions = facades.Sessions;
        Prompts = facades.Prompts;
        Chat = facades.Chat;
        Windows = facades.Windows;
        Slots = facades.Slots;
    }

    public string PluginId { get; }
    public string PluginDirectory { get; }
    public PluginManifest Manifest { get; }
    public string HostVersion { get; }
    public IPluginLogger Log { get; }
    public IPluginStorage Storage { get; }
    public IResourceLocator Resources { get; }
    public ISessionFacade? Sessions { get; }
    public IPromptRegistry? Prompts { get; }
    public IChatEventBus? Chat { get; }
    public IPluginWindowHost? Windows { get; }
    public IUiExtensionRegistry? Slots { get; }

    // ---- 日志 ----

    private sealed class ScopedLogger(string pluginId) : IPluginLogger
    {
        public void Info(string message) => PluginLog.Info(pluginId, message);
        public void Warn(string message) => PluginLog.Warn(pluginId, message);
        public void Error(string message, Exception? ex = null) => PluginLog.Error(pluginId, message, ex);
    }

    // ---- 私有存储 ----

    /// <summary>数据文件名的白名单：挡掉路径穿越，也挡掉非法文件名。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex DataNamePattern();

    private sealed class StorageImpl : IPluginStorage
    {
        public StorageImpl(string pluginId)
        {
            DataDirectory = Path.Combine(AppPaths.PluginData, pluginId);
            try { Directory.CreateDirectory(DataDirectory); } catch { /* 用的时候再试 */ }
        }

        public string DataDirectory { get; }

        public string? LoadText(string name)
        {
            if (!TryPath(name, out var path)) return null;
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        public bool SaveText(string name, string text)
        {
            if (!TryPath(name, out var path)) return false;
            try
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public T? Load<T>(string name) where T : class
        {
            if (!TryPath(name, out var path)) return null;
            return PluginJson.Load<T>(path);
        }

        public bool Save<T>(string name, T value) where T : class
        {
            if (!TryPath(name, out var path)) return false;
            return PluginJson.Save(path, value);
        }

        private bool TryPath(string name, out string path)
        {
            path = "";
            if (string.IsNullOrWhiteSpace(name) || !DataNamePattern().IsMatch(name)) return false;

            try { Directory.CreateDirectory(DataDirectory); } catch { return false; }
            path = Path.Combine(DataDirectory, name + ".json");
            return true;
        }
    }

    // ---- 资源定位 ----

    private sealed class ResourcesImpl(string directory) : IResourceLocator
    {
        private readonly string _root = Path.GetFullPath(directory);

        public string? ResolvePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            try
            {
                var full = Path.GetFullPath(Path.Combine(_root, relativePath));

                // 越界检查：必须是插件目录本身或它的子路径。
                // 用「加分隔符再比较前缀」而不是 StartsWith：否则 C:\plugins\pet2 会被
                // C:\plugins\pet 判成子目录。
                var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
                    ? _root
                    : _root + Path.DirectorySeparatorChar;

                return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), _root, StringComparison.OrdinalIgnoreCase)
                    ? full
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
