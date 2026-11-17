using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.Network;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.KnowledgeBase;

/// <summary>
/// 本地向量模型（Qwen3-Embedding 0.6B）的落盘位置、安装检测与下载。
///
/// <b>为什么不随发布包分发</b>：量化后仍有 500MB 以上，塞进安装包会让所有人的下载都变成几百兆，
/// 而不用知识库的人一个字节都不需要。所以模型放在 <see cref="AppPaths.Models"/> 下，由用户按需装，
/// 装完就是普通文件 —— 换个目录、拷到另一台机器都能直接用（<see cref="Detect"/> 会认）。
///
/// <b>为什么用 q8（<c>model_quantized.onnx</c>）而不是 fp32</b>：fp32 那份是 2.3GB
/// （<c>model.onnx</c> + 外置的 <c>model.onnx_data</c>），且必须两个文件同时到位才能加载；
/// q8 是单文件 585MB，CPU 上以整型矩阵乘为主，速度与内存都明显更好，向量质量的下降在检索里看不出来。
///
/// <b>为什么校验字节数而不是哈希</b>：585MB 算一次 SHA256 要几秒，而下载中断只会让文件变短，
/// 不会让文件变长。下载完成时把每份文件的实际长度写进 <c>manifest.json</c>，
/// 启动检测时比对长度即可 —— 手工拷进来的文件没有 manifest，那就退回「大于最低体积」这条兜底规则。
/// </summary>
public static class LocalEmbeddingModel
{
    /// <summary>模型 id，同时也是 <see cref="AppPaths.Models"/> 下的目录名。</summary>
    public const string Id = "qwen3-embedding-0.6b";

    /// <summary>界面上显示的名字。</summary>
    public const string DisplayName = "Qwen3-Embedding 0.6B";

    /// <summary>一句话说明它干什么用（卡片副标题）。</summary>
    public const string Description = "向量库的本地嵌入模型 · 1024 维 · q8 量化 · 约 585 MB";

    private const string Repo = "onnx-community/Qwen3-Embedding-0.6B-ONNX";
    private const string Revision = "main";
    private const string ManifestName = "manifest.json";

    /// <summary>
    /// 需要下载的文件。<see cref="FileSpec.MinBytes"/> 是「看起来完整」的下限，
    /// 只在没有 manifest 时用于兜底判断（比如用户手工把文件拷进来）。
    /// </summary>
    private static readonly FileSpec[] Specs =
    {
        new("onnx/model_quantized.onnx", 500L * 1024 * 1024),
        new("tokenizer.json", 8L * 1024 * 1024),
        new("vocab.json", 1024 * 1024),
        new("merges.txt", 1024 * 1024),
        new("config.json", 256),
        new("tokenizer_config.json", 128),
        new("special_tokens_map.json", 64),
        new("added_tokens.json", 64),
    };

    /// <summary>目录（不存在时创建）。</summary>
    public static string DirectoryPath => AppPaths.ModelDirectory(Id);

    /// <summary>ONNX 文件的完整路径。加载时用它。</summary>
    public static string ModelPath => Path.Combine(DirectoryPath, "onnx", "model_quantized.onnx");

    /// <summary>分词器文件的完整路径。</summary>
    public static string TokenizerPath => Path.Combine(DirectoryPath, "tokenizer.json");

    /// <summary>模型总目录（<c>models</c>），用于「打开所在目录」这类操作。</summary>
    public static string RootDirectory => AppPaths.Models;

    /// <summary>
    /// 检测本机是否已装好。只读文件系统，不加载、不联网，可以在界面线程上直接调。
    /// </summary>
    public static ModelStatus Detect()
    {
        string dir;
        try
        {
            // 刻意用 Combine 而不是 ModelDirectory(Id)：检测不该有副作用，
            // 否则光是打开设置页就会在数据目录里造出一堆空文件夹
            dir = Path.Combine(AppPaths.Models, Id);
        }
        catch (Exception ex)
        {
            return new ModelStatus(false, "", 0, 0, "数据目录不可用：" + ex.Message);
        }

        var total = 0L;
        var missing = new List<string>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in Specs)
        {
            var file = Path.Combine(dir, spec.Path);
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                missing.Add(spec.Path);
                continue;
            }

            sizes[spec.Path] = info.Length;
            total += info.Length;
        }

        // 中断的下载会留 .part，看到它就说明「下过但没下完」，比笼统的「未安装」更有用
        var hasPart = HasPartialFiles(dir);

        if (missing.Count > 0)
        {
            var reason = hasPart ? "上次下载没有完成" : "尚未下载";
            return new ModelStatus(false, dir, total, 0, reason, missing);
        }

        // 有 manifest：长度必须逐份对上 —— 下到一半断电的文件就是靠这条抓出来的
        var manifest = ReadManifest(dir);
        if (manifest is not null)
        {
            foreach (var pair in manifest)
            {
                if (!sizes.TryGetValue(pair.Key, out var actual))
                {
                    return new ModelStatus(false, dir, total, 0, "文件不齐：" + pair.Key, new[] { pair.Key });
                }

                if (actual != pair.Value)
                {
                    return new ModelStatus(false, dir, total, 0,
                        $"文件不完整：{Path.GetFileName(pair.Key)}（{actual:N0} / {pair.Value:N0} 字节）");
                }
            }

            return new ModelStatus(true, dir, total, total, "已安装");
        }

        // 没有 manifest（手工拷进来的）：退回体积下限
        foreach (var spec in Specs)
        {
            if (sizes[spec.Path] < spec.MinBytes)
            {
                return new ModelStatus(false, dir, total, 0,
                    $"文件不完整：{Path.GetFileName(spec.Path)}（{sizes[spec.Path]:N0} 字节）");
            }
        }

        return new ModelStatus(true, dir, total, total, "已安装（未校验，手动放置）");
    }

    /// <summary>
    /// 下载全部文件到模型目录。支持取消；取消时会把写了一半的 <c>.part</c> 清掉，
    /// 免得下次检测报「上次下载没有完成」却看不见任何进度。
    ///
    /// <paramref name="onProgress"/> 在后台线程上被调用（每读完一个缓冲块一次），
    /// 调用方自己负责切回界面线程。
    /// </summary>
    public static async Task DownloadAsync(
        Action<DownloadProgress>? onProgress,
        CancellationToken ct)
    {
        var dir = AppPaths.ModelDirectory(Id);

        // 先挑源：这一步连不上就别往下走，否则卡在「准备中」谁也看不出是网络问题
        onProgress?.Invoke(new DownloadProgress(0, 0, "", 0, "正在连接模型源…"));
        var endpoint = await ResolveEndpointAsync(ct).ConfigureAwait(false);

        var client = HttpClientProvider.Get(DownloadTimeout);

        // 再量一遍总长度：进度条要一个稳定的分母，边下边涨的分母看起来像倒着走。
        // 探测失败不要紧（有些镜像不返回 Content-Length），退回图片段里的最低体积。
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        var index = 0;
        foreach (var spec in Specs)
        {
            index++;
            onProgress?.Invoke(new DownloadProgress(0, 0, "", 0,
                $"正在读取文件清单（{index}/{Specs.Length}）…"));

            var size = await ProbeSizeAsync(client, endpoint, spec.Path, ct).ConfigureAwait(false);
            expected[spec.Path] = size > 0 ? size : spec.MinBytes;
            totalBytes += expected[spec.Path];
        }

        var received = 0L;
        var watch = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew();
        var lastReport = 0L;

        try
        {
            foreach (var spec in Specs)
            {
                ct.ThrowIfCancellationRequested();

                var target = Path.Combine(dir, spec.Path);
                var part = target + ".part";
                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

                using (var response = await client
                    .GetAsync(Url(endpoint, spec.Path), HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                        1 << 20, useAsync: true);

                    var buffer = new byte[1 << 20];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read <= 0) break;

                        await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        received += read;

                        // 每 100ms 报一次：每块都报的话，界面线程会被刷屏
                        if (clock.ElapsedMilliseconds - lastReport < 100) continue;
                        lastReport = clock.ElapsedMilliseconds;
                        onProgress?.Invoke(new DownloadProgress(
                            received, totalBytes, spec.Path,
                            watch.Elapsed.TotalSeconds > 0.5 ? received / watch.Elapsed.TotalSeconds : 0));
                    }
                }

                File.Move(part, target, overwrite: true);
                onProgress?.Invoke(new DownloadProgress(received, totalBytes, spec.Path,
                    watch.Elapsed.TotalSeconds > 0.5 ? received / watch.Elapsed.TotalSeconds : 0));
            }

            // 全部落盘之后才写 manifest：中途失败时留着它只会让人以为装好了
            WriteManifest(dir, expected);
        }
        catch
        {
            CleanPartFiles(dir);
            throw;
        }
    }

    /// <summary>取消后调用：把残留的 .part 清掉。不抛异常（清理失败不该盖住取消本身）。</summary>
    public static void CleanPartFiles(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* 被占用就留着，下次覆盖写 */ }
            }
        }
        catch
        {
            // 清不掉不影响功能：下次下载会直接覆盖 .part
        }
    }

    private static bool HasPartialFiles(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return false;
            foreach (var _ in System.IO.Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Url(string endpoint, string relative)
        => $"{endpoint}/{Repo}/resolve/{Revision}/{relative}";

    /// <summary>
    /// 模型源，按顺序试。官方站排第一，被网络环境拦住时退到镜像站
    /// （hf-mirror.com 是同一份仓库结构，路径完全一致，换的只是域名）。
    ///
    /// 顺序是有意的：能直连就别走镜像 —— 镜像是第三方在转发，可能落后于上游的 revision。
    /// 但国内直连官方站基本连不上，而且表现是「TCP 握手一直不返回」而不是干脆的拒绝，
    /// 用户看到的就是一个停在「准备中」的窗口 —— 所以必须有镜像这条路。
    /// </summary>
    private static readonly string[] Endpoints =
    {
        "https://huggingface.co",
        "https://hf-mirror.com",
    };

    /// <summary>探测用的超时：只是发一个 HEAD，连不上就该赶紧换下一个。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>探测连通性用的文件：清单里最小的一个（几百字节），HEAD 只要响应头。</summary>
    private const string ProbeFile = "special_tokens_map.json";

    /// <summary>下载用的超时：这是「等响应头」的超时，不是整个下载的总时长。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 挑一个能连上的模型源。全部连不上就抛异常 —— 让界面立刻显示原因，
    /// 而不是把一个永远不动的进度条摆在那里。
    /// </summary>
    private static async Task<string> ResolveEndpointAsync(CancellationToken ct)
    {
        var probeClient = HttpClientProvider.Get(ProbeTimeout);
        var errors = new List<string>();

        foreach (var endpoint in Endpoints)
        {
            try
            {
                // 挑最小的一个文件试：HEAD 只要一个响应头，不必拿 585MB 的那份去试
                using var request = new HttpRequestMessage(HttpMethod.Head, Url(endpoint, ProbeFile));
                using var response = await probeClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // 只看「有没有拿到响应」，不要求 2xx：镜像站会先回一个 307 再转发到缓存节点，
                // 能跳转本身就说明网络通。真正的成败交给后面那次 GET 的 EnsureSuccessStatusCode。
                if ((int)response.StatusCode < 400) return endpoint;
                errors.Add($"{HostOf(endpoint)} 返回 {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 超时、域名解析失败、连接被重置都走这里 —— 记下来，试下一个
                errors.Add($"{HostOf(endpoint)} 连不上（{Describe(ex)}）");
            }
        }

        throw new InvalidOperationException(
            "连不上模型源，无法下载。" +
            (errors.Count > 0 ? " " + string.Join("；", errors) + "。" : "") +
            "请检查网络，或在设置里改用其他网络环境（镜像站 hf-mirror.com 也已尝试）。");
    }

    private static string HostOf(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;

    /// <summary>把常见的网络异常翻译成一句人能读的话。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "超时",
        HttpRequestException { InnerException: { } inner } => inner.Message,
        HttpRequestException http => http.Message,
        _ => ex.Message,
    };

    /// <summary>
    /// 用 HEAD 取 Content-Length。取不到就返回 -1，交给调用方用最低体积估算 ——
    /// 拿不到长度不影响下载，只是进度条的分母不准。
    /// </summary>
    private static async Task<long> ProbeSizeAsync(HttpClient client, string endpoint, string relative,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Url(endpoint, relative));
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return -1;
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static Dictionary<string, long>? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        try
        {
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            if (!json.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return null;

            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files.EnumerateArray())
            {
                var name = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                map[name] = item.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
            }

            return map.Count > 0 ? map : null;
        }
        catch
        {
            // manifest 坏掉就当没有：退回体积下限，总比把好好的模型判成「未安装」强
            return null;
        }
    }

    private static void WriteManifest(string dir, Dictionary<string, long> sizes)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"repo\": \"").Append(Repo).Append("\",\n");
        builder.Append("  \"model\": \"").Append(Id).Append("\",\n");
        builder.Append("  \"downloaded_at\": ").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(",\n");
        builder.Append("  \"files\": [\n");

        var first = true;
        foreach (var pair in sizes)
        {
            if (!first) builder.Append(",\n");
            first = false;
            builder.Append("    { \"path\": \"").Append(pair.Key).Append("\", \"size\": ").Append(pair.Value).Append(" }");
        }

        builder.Append("\n  ]\n}\n");

        File.WriteAllText(Path.Combine(dir, ManifestName), builder.ToString());
    }

    /// <summary>一份需要下载的文件及其最低体积。</summary>
    private sealed record FileSpec(string Path, long MinBytes);

    /// <summary>安装状态。只读快照，界面直接拿去渲染。</summary>
    public sealed record ModelStatus(
        bool Installed,
        string Directory,
        long Bytes,
        long ExpectedBytes,
        string Message,
        IReadOnlyList<string>? Missing = null)
    {
        /// <summary>已占用的空间（人类可读）。</summary>
        public string SizeText => Bytes <= 0 ? "—" : Format(Bytes);

        public static string Format(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            var value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
        }
    }

    /// <summary>
    /// 下载进度。每 100ms 一次。
    ///
    /// <paramref name="Stage"/> 是「连接 / 读清单」这类还没开始数字节的阶段说明：
    /// 那时候还没有总量，进度条只能空着，但界面得有字可写 —— 否则就是一句不动的「准备中」。
    /// </summary>
    public sealed record DownloadProgress(
        long Received,
        long Total,
        string CurrentFile,
        double BytesPerSecond,
        string? Stage = null)
    {
        public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);

        public string CurrentName => Path.GetFileName(CurrentFile);

        public string SpeedText => BytesPerSecond <= 0
            ? ""
            : ModelStatus.Format(BytesPerSecond) + "/s";

        /// <summary>界面第二行要显示的字：有阶段说明就优先它。</summary>
        public string DetailText => Stage is { Length: > 0 } stage
            ? stage
            : SpeedText.Length > 0 ? $"{CurrentName} · {SpeedText}" : CurrentName;
    }
}
