using System;
using System.Threading;
using System.Threading.Tasks;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 上下文贡献登记处（需要声明 <see cref="PluginCapabilities.PromptContribute"/>）。
///
/// <b>它做的事很小</b>：把一段文本追加到本轮 system 提示词的末尾。宿主每轮发送前问一次，
/// 你返回的字符串就是「你要模型额外知道的东西」。
///
/// <b>有几条硬约束</b>（宿主强制执行，不靠插件自律）：
/// <list type="bullet">
///   <item>贡献长度<b>不限</b>，写多少就拼多少（截断已移除；真正吃不下的是模型窗口，由宿主与模型自己承担）；</item>
///   <item>每个贡献者在 <b>500 ms</b> 内必须返回，超时按失败处理并跳过；</item>
///   <item>抛异常只会跳过你这一次，<b>不会</b>让这一轮发送失败；</item>
///   <item>内容会被包上一行来源标记（<c>&lt;!-- plugin: 你的 id --&gt;</c>），出问题时能一眼看出是谁写的。</item>
/// </list>
///
/// <b>这个入口能悄悄改变模型行为</b>，所以插件页会显示每个插件最近一次实际贡献了多少字。
///
/// <b>线程</b>：<see cref="IPromptContributor.BuildAsync"/> 在宿主发送前的准备阶段调用，
/// 那时不保证在 UI 线程上 —— 不要在里面建控件。
/// </summary>
public interface IPromptRegistry
{
    /// <summary>
    /// 登记一个贡献者。返回的 <see cref="IDisposable"/> 用来撤销；
    /// 插件被禁用时宿主也会替你撤销（不必依赖它，但漏了也不会继续生效）。
    /// </summary>
    IDisposable Add(IPromptContributor contributor);
}

/// <summary>
/// 一段要加进 system 提示词的内容。
/// </summary>
public interface IPromptContributor
{
    /// <summary>你自己识别用的短标识（日志与配额提示里会带）。不是插件 id，重复也无所谓。</summary>
    string Id { get; }

    /// <summary>
    /// 排序。数值小的先追加，相同则按插件 id 排（保证每轮顺序稳定，不随机抖动）。
    /// </summary>
    int Order { get; }

    /// <summary>
    /// 返回要追加的文本；返回 <c>null</c> 或空白表示这一轮不加东西。
    /// </summary>
    ValueTask<string?> BuildAsync(PromptContext context, CancellationToken ct);
}

/// <summary>本轮的一些上下文，供贡献者决定要不要加、加什么。</summary>
/// <param name="SessionId">会话 id。</param>
/// <param name="ModelId">本轮使用的模型调用 id。</param>
public sealed record PromptContext(string SessionId, string ModelId);
