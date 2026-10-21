using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件私有存储里「泛型 API」（<see cref="IPluginStorage.Load{T}"/> / <c>Save&lt;T&gt;</c>）的 JSON 读写。
///
/// <b>这条路会让插件需要重启才能完全卸载，已实测确认。</b>
/// System.Text.Json 会把见过的类型元数据缓存进运行时的静态结构，
/// 而插件存的类型通常定义在插件程序集里 —— 那份缓存一直持有插件，ALC 回收不掉。
/// 曾经试过的、都<b>无效</b>的规避手段：
/// 每次新建 <c>JsonSerializerOptions</c>、每次新建 <c>DefaultJsonTypeInfoResolver</c>、
/// 调 <c>MakeReadOnly()</c> 冻结、换成非泛型的 <c>Serialize(object, Type, options)</c>。
/// 所以问题不在我们的缓存，在运行时内部。
///
/// 于是契约里另有 <see cref="IPluginStorage.LoadText"/> / <c>SaveText</c>：
/// 宿主只当字符串处理，插件用自己的方式序列化 —— 那条路能干净卸载。
/// 这个文件仅服务于「图方便、不在乎重启」的场景。
/// </summary>
internal static class PluginJson
{
    private static JsonSerializerOptions Create() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 插件配置文件是给人看的，中文不转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },

        // 独立 resolver：至少别让宿主的全局 metadata 缓存再多持有一份插件类型。
        // 注意它救不了这个 ALC —— 运行时内部还有自己的缓存（见类注释）。
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, Create());
        }
        catch
        {
            return null;
        }
    }

    public static bool Save<T>(string path, T value) where T : class
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 与 JsonFileStore 一致：写临时文件再原子改名，避免写一半断电留下坏文件
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Create()));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
