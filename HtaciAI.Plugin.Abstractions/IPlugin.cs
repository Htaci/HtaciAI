using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件的入口。宿主在启用插件时创建一个实例，禁用/退出时调用 <see cref="ShutdownAsync"/>。
///
/// <b>生命周期约定</b>：
/// <list type="bullet">
///   <item><see cref="InitializeAsync"/> 与 <see cref="ShutdownAsync"/> 都在 UI 线程上调用，
///         至少要能在 UI 线程上调用（插件可以直接建控件）。</item>
///   <item>禁用插件时宿主先关掉它的所有投递出口，再给 <see cref="ShutdownAsync"/> 一个有限的时间
///         （当前 3 秒）收尾；超时就不再等，直接摘掉它的窗口与控件。</item>
///   <item><b>插件必须自己停掉在 <see cref="ShutdownAsync"/> 里起的定时器、动画、线程与未完成的 Task。</b>
///         宿主没法从外部停掉它们，而任何一个还活着都会让程序集无法卸载（结果是「需要重启」）。</item>
///   <item>令牌取消时应当尽快返回，不要吞掉取消异常继续干活。</item>
/// </list>
///
/// 实现类需要一个公共无参构造函数。
/// </summary>
public interface IPlugin
{
    /// <summary>插件启动。抛异常视为加载失败，宿主记录原因并把该插件标记为「失败」，不影响其他插件。</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken ct);

    /// <summary>插件停止。宿主会等它完成（有超时），之后才会尝试卸载程序集。</summary>
    Task ShutdownAsync(CancellationToken ct);
}
