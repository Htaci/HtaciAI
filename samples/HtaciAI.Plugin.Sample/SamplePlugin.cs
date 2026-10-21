using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HtaciAI.Plugin.Abstractions;

namespace HtaciAI.Plugin.Sample;

/// <summary>
/// 最小可用插件：日志、私有存储、上下文贡献、会话事件订阅，以及界面插槽与窗口。
///
/// 它的用途是「一个能被照着抄的骨架」和端到端验证：
/// 加载后能在 <c>&lt;数据根&gt;/logs/plugin.log</c> 里看到它写下的日志，
/// 并在 <c>&lt;数据根&gt;/plugin-data/htaci.sample/</c> 里看到 <c>stats.json</c>。
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private CancellationTokenSource? _timerCts;
    private Task? _worker;

    /// <summary>上下文贡献者：往 system 末尾追加一行，让模型知道这个插件在。</summary>
    private sealed class MarkerContributor : IPromptContributor
    {
        public string Id => "sample-marker";

        // 数字小的先追加。多个插件时按它排序，稳定输出
        public int Order => 100;

        public ValueTask<string?> BuildAsync(PromptContext context, CancellationToken ct)
            => ValueTask.FromResult<string?>(
                $"(示例插件在场：会话 {context.SessionId[..Math.Min(8, context.SessionId.Length)]}，" +
                $"模型 {context.ModelId})");
    }

    /// <summary>事件接收方：只记日志，不碰界面。</summary>
    private sealed class LogSink(IPluginLogger log) : IChatEventSink
    {
        public ValueTask OnEventAsync(ChatEvent e, CancellationToken ct)
        {
            switch (e.Kind)
            {
                case ChatEventKind.RoundStarted:
                    log.Info($"[{e.SessionId[..Math.Min(8, e.SessionId.Length)]}] 一轮开始");
                    break;

                case ChatEventKind.ToolCallStarted:
                    log.Info($"调用工具：{e.ToolName}");
                    break;

                case ChatEventKind.RoundCompleted:
                    log.Info("这一轮完成");
                    break;

                case ChatEventKind.RoundFailed:
                    log.Info($"这一轮结束（{e.Status}）：{e.Text}");
                    break;

                // ContentDelta 一个 token 一条，全部记下来会把日志刷爆；
                // 真正做桌宠气泡时在这里处理（宿主已经按 50 ms 合并过）
            }

            return ValueTask.CompletedTask;
        }
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

        // 上下文贡献：同样「声明了才有」。禁用插件时宿主会自动撤销，不必自己记着。
        context.Prompts?.Add(new MarkerContributor());
        if (context.Prompts is not null)
            context.Log.Info("已登记上下文贡献者（每轮往 system 末尾追加一行）");

        // 会话事件：会话窗口没打开也能收到（事件来自会话运行时，不是界面）。
        context.Chat?.Subscribe(new LogSink(context.Log));
        if (context.Chat is not null)
            context.Log.Info("已订阅会话事件");

        // 界面插槽：同样「声明了才有」。
        // 注意工厂会被反复调用（界面重建时），所以里面不要存瞬时状态。
        context.Slots?.AddLauncherCard(new LauncherCard
        {
            Title = "示例插件",
            Description = "打开一个示例窗口，演示插件窗口与位置记忆",
            Glyph = "\uE9EA",
            AccentColor = "#7C3AED",
            OnClick = () => OpenDemoWindow(context),
        });

        context.Slots?.AddControl(PluginSlots.ConversationHeader, () => new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F3FF")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Child = new TextBlock
            {
                Text = "示例插件：这是 conversation.header 插槽里的内容",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse("#6D28D9")),
            },
        });

        if (context.Slots is not null)
            context.Log.Info("已登记一张入口卡片与一个会话头部控件");

        if (context.Windows is null)
            context.Log.Info("未声明 ui.window：不能建自己的窗口");

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

    /// <summary>
    /// 演示窗口宿主：透明/无边框/置顶/记忆位置都由宿主配好，插件只放内容。
    /// <b>不要在 InitializeAsync 里就弹窗</b>——每次启动都弹一个窗口是很打扰的行为。
    /// </summary>
    private static void OpenDemoWindow(IPluginContext context)
    {
        if (context.Windows is null)
        {
            context.Log.Warn("清单里没有声明 ui.window，无法建窗口");
            return;
        }

        var window = context.Windows.CreateStandalone(new WindowOptions
        {
            Title = "示例插件窗口",
            Width = 280,
            Height = 170,
        });

        window.Content = new TextBlock
        {
            Text = "这是插件自己的窗口。\n按住空白处可以拖动，位置会被记住。",
            Margin = new Thickness(20),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
        };

        window.Show();
        context.Log.Info("打开了一个示例窗口");
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
