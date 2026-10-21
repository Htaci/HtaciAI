using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Plugin.Sample;

/// <summary>
/// 最小可用插件：只用到日志与私有存储，不做任何界面或对话改动。
///
/// 它的用途是「一个能被照着抄的骨架」和「M0 的端到端验证」：
/// 加载后能在 <c>&lt;数据根&gt;/logs/plugin.log</c> 里看到它写下的两行日志，
/// 并在 <c>&lt;数据根&gt;/plugin-data/htaci.sample/</c> 里看到 <c>stats.json</c>。
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private CancellationTokenSource? _timerCts;
    private Task? _worker;

    /// <summary>插件私有数据。字段名随便取，宿主只负责按文件存 JSON。</summary>
    private sealed class Stats
    {
        public int LaunchCount { get; set; }
        public string? LastStartedAt { get; set; }
    }

    /// <summary>从 "次数\n时间" 的文本里取次数；读不出来按 0。</summary>
    private static int ParseCount(string? raw)
        => int.TryParse(raw?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var n) ? n : 0;

    private static string ParseTime(string? raw)
        => raw?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "(无)";

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log.Info($"启动：宿主 {context.HostVersion}，插件目录 {context.PluginDirectory}");

        // 演示私有存储。用文本接口而不是泛型接口：
        // 泛型版会用反射式 JSON 序列化处理插件自己的类型，而运行时会把那些类型永久缓存住，
        // 结果就是这个插件禁用后无法卸载、必须重启程序。存成自己约定的文本就没这问题。
        var raw = context.Storage.LoadText("stats");
        var count = ParseCount(raw) + 1;
        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        context.Storage.SaveText("stats", $"{count}\n{now}");
        context.Log.Info($"这是第 {count} 次启动（上次：{ParseTime(raw)}）");

        // 会话门面：清单里声明了 chat.control 才有，没声明就是 null。
        // 这个示例只用它确认注入生效，<b>不主动发消息</b> —— 每次启动都往会话里塞一句会很打扰。
        // 真正要用时（比如有窗口和按钮的桌宠）大致是：
        //   var result = await context.Sessions!.SendAsync(sessionId, "你好");
        if (context.Sessions is null)
            context.Log.Info("未声明 chat.control：主动发起对话不可用");
        else
            context.Log.Info("已获得会话门面：可以主动向某个会话发起一轮");

        // 演示「插件必须自己停掉后台任务」：宿主没法从外部停它，
        // 留着运行中的定时器会让程序集无法卸载，禁用后就只能提示「需重启」。
        _timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _timerCts.Token;
        _worker = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    context.Log.Info("心跳");
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }, token);

        await Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        // 停掉自己起的任务 —— 这一步漏了，禁用插件就永远只能「需重启」。
        _timerCts?.Cancel();

        // 光 Cancel 不够：任务可能正跑在 catch 里，它的状态机仍然引用着本程序集的类型。
        // 等它真的结束，宿主才可能把程序集回收掉。
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch { /* 超时或取消就算了，宿主那边会有兜底 */ }
        }

        _worker = null;
        _timerCts?.Dispose();
        _timerCts = null;
    }
}
