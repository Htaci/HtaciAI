using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 内置浏览器的访问历史（<b>插件自己记的</b>，与 Chromium 的 History 库无关）。
///
/// <b>为什么不读 <c>EBWebView\Default\History</c></b>：那是 SQLite。读它要么给插件塞一个
/// SQLite 原生依赖（又多一个要跟着发版、要自己定址的文件），要么在浏览器进程持锁时读失败。
/// 而这里真正要的信息只有「地址 + 标题 + 时间」三样 —— 记在自己的文件里，
/// 清理就是删自己的文件，不会出现「界面上清了、库里还剩」的困惑。
///
/// <b>存储格式是手写的 JSON 数组</b>，不用 <c>Storage.Save&lt;T&gt;</c>：
/// 后者会把插件类型缓存进运行时的静态结构，导致插件禁用后无法卸载（契约里写明了）。
///
/// <b>线程</b>：都只在 UI 线程调用（导航事件在 UI 线程，设置页也在 UI 线程）。
/// </summary>
internal sealed class BrowserHistory
{
    /// <summary>最多留多少条。够回溯一阵，也不至于让文件无限长。</summary>
    private const int MaxEntries = 500;

    private readonly List<Entry> _entries = new();
    private readonly Func<string?> _load;
    private readonly Action<string> _save;
    private readonly Action<string> _log;

    /// <param name="load">读整个 JSON 文本；没有时返回 null。</param>
    /// <param name="save">写入整个 JSON 文本。</param>
    public BrowserHistory(Func<string?> load, Action<string> save, Action<string> log)
    {
        _load = load;
        _save = save;
        _log = log;
        Load();
    }

    /// <summary>一条访问记录。</summary>
    internal sealed record Entry(string Url, string Title, DateTime Time);

    /// <summary>最新的在前。</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// 记一次访问。<b>同一地址连续访问只更新时间</b>：刷新一次就多一行，列表会迅速变成噪音。
    /// </summary>
    public void Record(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // 起始页与空白页不该进历史，否则「清理历史」永远清不掉最新那条
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        var now = DateTime.Now;
        var name = string.IsNullOrWhiteSpace(title) ? url : title;

        if (_entries.Count > 0 && string.Equals(_entries[0].Url, url, StringComparison.Ordinal))
        {
            _entries[0] = _entries[0] with { Title = name, Time = now };
            return;
        }

        _entries.Insert(0, new Entry(url, name, now));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
        Save();
    }

    /// <summary>
    /// 补上标题。标题通常在导航之后才拿到（<c>SourceChanged</c> 早于文档标题就绪），
    /// 所以先记地址、再补标题，比等到有标题再记更不容易漏。
    /// </summary>
    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return;
        if (_entries.Count == 0) return;
        if (!string.Equals(_entries[0].Url, url, StringComparison.Ordinal)) return;
        if (_entries[0].Title == title) return;

        _entries[0] = _entries[0] with { Title = title };
        Save();
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Save();
    }

    // ---- 落盘 ----

    private void Load()
    {
        string? text;
        try { text = _load(); }
        catch (Exception ex) { _log($"读历史失败：{ex.Message}"); return; }

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                _entries.Add(new Entry(
                    url,
                    GetString(item, "title") ?? url,
                    DateTime.TryParse(GetString(item, "time"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var time) ? time : DateTime.Now));

                if (_entries.Count >= MaxEntries) break;
            }
        }
        catch (JsonException ex)
        {
            // 文件坏了不该让插件加载失败：丢掉历史比打不开浏览器轻得多
            _log($"历史文件损坏，已忽略：{ex.Message}");
        }
    }

    private void Save()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = _entries[i];
            sb.Append("{\"url\":").Append(JsonSerializer.Serialize(e.Url))
              .Append(",\"title\":").Append(JsonSerializer.Serialize(e.Title))
              .Append(",\"time\":").Append(JsonSerializer.Serialize(e.Time.ToString("o", CultureInfo.InvariantCulture)))
              .Append('}');
        }
        sb.Append(']');

        try { _save(sb.ToString()); }
        catch (Exception ex) { _log($"写历史失败：{ex.Message}"); }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
