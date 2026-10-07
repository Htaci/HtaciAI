using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Services.Updates;

/// <summary>一次检查更新的结果状态。</summary>
public enum UpdateStatus
{
    /// <summary>没有配置清单地址（或填的是空串）。</summary>
    NotConfigured,

    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>有新版本可用。</summary>
    UpdateAvailable,

    /// <summary>检查失败：网络、超时、清单格式不对。原因见 <see cref="UpdateCheckResult.Error"/>。</summary>
    Failed,
}

/// <summary>检查更新的结果。<paramref name="CurrentVersion"/> 始终有值，便于界面直接展示。</summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? Notes = null,
    string? Error = null)
{
    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>
/// 更新检查：拉取一个 JSON 清单，与本程序版本比较。
///
/// 清单格式（键名大小写不敏感，多余字段忽略）：
/// <code>
/// { "version": "1.2.0", "url": "https://…/HtaciAI-1.2.0.zip", "notes": "本次更新…" }
/// </code>
///
/// 为什么是可配置地址而不是写死 GitHub：仓库的 Releases API 目前是 404，
/// 而且以后想换成自己的服务器 / 网盘都不该改代码。
///
/// 本类<b>不抛异常</b>：所有失败都收敛成 <see cref="UpdateStatus.Failed"/> + 原因，
/// 因为它的调用点都在 UI 上，抛出去只能变成崩溃对话框。
/// </summary>
public static class UpdateChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>照 BuiltinToolExecutor 的先例：类级静态单例 + 显式超时。</summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout };

    public static async Task<UpdateCheckResult> CheckAsync(string? manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return new UpdateCheckResult(UpdateStatus.NotConfigured, AppVersion.Current);

        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current,
                Error: "更新地址不是有效的 http/https 链接");
        }

        Manifest? manifest;
        try
        {
            using var response = await Http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current,
                    Error: $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current,
                Error: $"请求超时（{Timeout.TotalSeconds:0} 秒）");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current, Error: "已取消");
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current,
                Error: "更新清单不是合法 JSON：" + ex.Message);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current, Error: ex.Message);
        }

        var latest = manifest?.Version?.Trim();
        if (string.IsNullOrWhiteSpace(latest))
            return new UpdateCheckResult(UpdateStatus.Failed, AppVersion.Current, Error: "更新清单里没有 version 字段");

        if (!IsNewer(latest, AppVersion.Current))
            return new UpdateCheckResult(UpdateStatus.UpToDate, AppVersion.Current, LatestVersion: latest);

        return new UpdateCheckResult(
            UpdateStatus.UpdateAvailable,
            AppVersion.Current,
            LatestVersion: latest,
            DownloadUrl: string.IsNullOrWhiteSpace(manifest!.Url) ? null : manifest.Url.Trim(),
            Notes: string.IsNullOrWhiteSpace(manifest.Notes) ? null : manifest.Notes.Trim());
    }

    /// <summary>
    /// 候选版本是否比当前版本新。两边都能解析成 <see cref="Version"/> 时按数字比；
    /// 任一边解析失败（比如自定义的日期式版本号）就退化成「不相等即视为有新版本」，
    /// 宁可提示用户去更新，也不要因为版本号写法不合规就永远检查不出来。
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = AppVersion.Parse(candidate);
        var b = AppVersion.Parse(current);
        if (a is not null && b is not null) return a > b;

        return !string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Manifest
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }
}
