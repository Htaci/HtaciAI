using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace HtaciAI.Services.Network;

/// <summary>
/// 进程内共享的 HttpClient 工厂，也是「是否使用系统代理」开关唯一的落点。
///
/// <b>为什么要集中</b>：原先每个类各自 <c>new HttpClient</c>，代理开关就只能改到其中一部分，
/// 出一半走代理一半直连的怪现象。现在对话、列模型、更新检查、web_fetch、MCP 全都从这里取。
///
/// <b>为什么不直接用 <c>HttpClient.DefaultProxy</c></b>（这是踩过的坑）：
/// .NET 在 Windows 上把它实现成 <c>HttpWindowsProxy</c>，进程内会缓存一次系统代理的读取结果。
/// 应用先启动、Clash 后开启时，进程会一直认为「系统没有代理」，于是明明开着代理却全直连 ——
/// 用户看到的就是「没走系统代理」。所以这里<b>每次取客户端前自己读一遍注册表</b>
/// （<c>HKCU\...\Internet Settings</c>，与 IE/Chrome 读的是同一份），
/// 配置变了就把缓存的客户端整批换掉，Clash 切换开关后下一个请求就生效。
///
/// <b>PAC 脚本</b>（<c>AutoConfigURL</c> / 自动检测）自己解析不了，这种情况退回
/// <c>HttpClient.DefaultProxy</c>，交给 WinHTTP 去算 —— 它本来就支持 PAC。
/// </summary>
public static class HttpClientProvider
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>系统代理配置的复查间隔。太小等于每个请求都读一次注册表，太大则切换代理后要等。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    private static readonly object Gate = new();
    private static readonly Dictionary<TimeSpan, HttpClient> Cache = new();

    private static bool _useSystemProxy;
    private static SystemProxy _resolved = SystemProxy.Direct;
    private static DateTime _lastProbeUtc = DateTime.MinValue;

    /// <summary>
    /// 是否走系统代理。默认关（直连）：装了 Clash 这类工具的机器上，系统代理常常指向一个
    /// 本地端口，跟着它走会让「一装上就连不通」变成很难排查的问题。
    /// </summary>
    public static bool UseSystemProxy
    {
        get
        {
            lock (Gate) return _useSystemProxy;
        }
        set
        {
            lock (Gate)
            {
                if (_useSystemProxy == value) return;
                _useSystemProxy = value;
                ResetCache();
                _lastProbeUtc = DateTime.MinValue;   // 下次取客户端时立刻重新判定
            }
        }
    }

    /// <summary>
    /// 取指定超时的共享客户端（第一次取时创建）。返回的实例归本类所有，<b>调用方不要 Dispose</b>。
    /// </summary>
    public static HttpClient Get(TimeSpan timeout)
    {
        lock (Gate)
        {
            RefreshIfStale();

            if (Cache.TryGetValue(timeout, out var cached)) return cached;

            var handler = new SocketsHttpHandler();

            if (_resolved.Proxy is not null)
            {
                handler.UseProxy = true;
                handler.Proxy = _resolved.Proxy;
            }
            else if (_resolved.FollowDefaultProxy)
            {
                // PAC / 自动检测：让 WinHTTP 去算（它支持 PAC，我们不重复实现）
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;   // 直连：连环境变量里的 HTTP_PROXY 也不看
            }

            var client = new HttpClient(handler) { Timeout = timeout };
            Cache[timeout] = client;
            return client;
        }
    }

    /// <summary>
    /// 当前系统代理的解析结果，给设置页显示用。会立即重新读一次注册表，不受上面那层缓存影响。
    /// </summary>
    public static SystemProxy Describe()
    {
        lock (Gate)
        {
            _resolved = Resolve();
            _lastProbeUtc = DateTime.UtcNow;
            return _resolved;
        }
    }

    private static void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastProbeUtc < ProbeInterval) return;

        var next = Resolve();
        _lastProbeUtc = DateTime.UtcNow;
        if (next.SameAs(_resolved)) return;

        _resolved = next;
        ResetCache();
    }

    private static SystemProxy Resolve()
    {
        if (!_useSystemProxy) return SystemProxy.Direct;

        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 上没有「系统代理」这个概念，跟随环境变量（即 DefaultProxy 的本义）
            return new SystemProxy(null, true, null, null, false, "跟随环境变量 HTTP_PROXY / HTTPS_PROXY");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key is null) return new SystemProxy(null, false, null, null, false, "读不到系统代理设置，按直连处理");

            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) != 0;
            var server = (key.GetValue("ProxyServer") as string)?.Trim();
            var bypass = key.GetValue("ProxyOverride") as string;
            var pacUrl = (key.GetValue("AutoConfigURL") as string)?.Trim();
            var autoDetect = Convert.ToInt32(key.GetValue("AutoDetect") ?? 0) != 0;

            if (enabled && !string.IsNullOrWhiteSpace(server))
            {
                var (address, scheme) = PickAddress(server);
                if (address is not null)
                {
                    var proxy = new WebProxy(new Uri(scheme + address))
                    {
                        BypassList = BuildBypassList(bypass),
                    };
                    var kind = scheme.StartsWith("socks", StringComparison.Ordinal) ? "SOCKS" : "HTTP";
                    return new SystemProxy(proxy, false, address, null, false, $"{kind} 代理 {address}");
                }
            }

            if (!string.IsNullOrWhiteSpace(pacUrl))
                return new SystemProxy(null, true, null, pacUrl, autoDetect, $"PAC 脚本 {pacUrl}");

            if (autoDetect)
                return new SystemProxy(null, true, null, null, true, "自动检测代理（WPAD）");

            return new SystemProxy(null, false, null, null, false, "系统未配置代理");
        }
        catch (Exception ex)
        {
            return new SystemProxy(null, false, null, null, false, "读取系统代理失败：" + ex.Message);
        }
    }

    /// <summary>
    /// <c>ProxyServer</c> 有两种写法：<c>host:port</c>，或 <c>http=h:p;https=h:p;socks=h:p</c>。
    /// <see cref="WebProxy"/> 只有一个地址，所以取「https 优先、否则 http、再否则 socks」。
    /// </summary>
    private static (string? Address, string Scheme) PickAddress(string server)
    {
        string? http = null, https = null, socks = null;

        if (server.Contains('='))
        {
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = part.Split('=', 2);
                if (split.Length != 2) continue;

                var value = split[1].Trim().Trim('/');
                if (value.Length == 0) continue;

                switch (split[0].Trim().ToLowerInvariant())
                {
                    case "http": http = value; break;
                    case "https": https = value; break;
                    case "socks": socks = value; break;
                }
            }
        }
        else
        {
            http = https = server.Trim().Trim('/');
        }

        if (https is not null) return (https, "http://");
        if (http is not null) return (http, "http://");
        if (socks is not null) return (socks, "socks5://");
        return (null, "http://");
    }

    /// <summary>
    /// 把 <c>ProxyOverride</c>（形如 <c>localhost;127.*;*.corp.com;&lt;local&gt;</c>）转成
    /// <see cref="WebProxy.BypassList"/> 认的正则。浏览器对回环地址与不带点的内网主机名
    /// 恒直连，所以那几条无条件加上。
    /// </summary>
    private static string[] BuildBypassList(string? overrideValue)
    {
        var list = new List<string>
        {
            @"^localhost$",
            @"^127\.",
            @"^\[?::1\]?$",
            @"^[^.]+$",   // <local>：不带点的内网主机名
        };

        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            foreach (var raw in overrideValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Equals("<local>", StringComparison.OrdinalIgnoreCase)) continue;   // 上面已覆盖
                list.Add("^" + Regex.Escape(raw).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
            }
        }

        return list.ToArray();
    }

    private static void ResetCache()
    {
        foreach (var client in Cache.Values) client.Dispose();
        Cache.Clear();
    }

    /// <summary>
    /// 一次代理解析结果。<see cref="Proxy"/>（显式代理）与 <see cref="FollowDefaultProxy"/>
    /// （PAC / 自动检测，交给 WinHTTP）互斥；两者都为空即直连。
    /// </summary>
    public sealed record SystemProxy(
        IWebProxy? Proxy,
        bool FollowDefaultProxy,
        string? Address,
        string? PacUrl,
        bool AutoDetect,
        string Description)
    {
        public static readonly SystemProxy Direct = new(null, false, null, null, false, "不使用代理（直连）");

        /// <summary>只比较真正影响走向的字段，描述文字变化不必重建客户端。</summary>
        public bool SameAs(SystemProxy other) =>
            Address == other.Address &&
            PacUrl == other.PacUrl &&
            AutoDetect == other.AutoDetect &&
            FollowDefaultProxy == other.FollowDefaultProxy &&
            (Proxy is null) == (other.Proxy is null);
    }
}
