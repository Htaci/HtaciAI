using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Plugins;

namespace HtaciAI.Controls;

/// <summary>
/// 插槽里每个插件控件的「安全壳」。
///
/// <b>为什么每个插件控件都要包一层</b>：插件代码是在宿主进程里跑的普通代码，
/// 它的构造函数完全可能抛异常（读图标文件失败、绑定了一个自己删掉的资源…）。
/// 让那个异常穿透到布局测量阶段，坏掉的就不只是这个控件 —— 整个页面都白屏，
/// 用户看到的是「程序坏了」，而他只会知道「装了个插件之后就这样」。
///
/// 这里把异常收在自己身上，整块换成一行灰字「插件 X 显示失败」，
/// 悬停能看到原因，真正的原因在 <c>logs/plugin.log</c>。
/// </summary>
public sealed class PluginControlHost : ContentControl
{
    public PluginControlHost(string pluginId, Func<Control> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentNullException.ThrowIfNull(factory);

        PluginId = pluginId;

        try
        {
            Content = factory();
            ToolTip.SetTip(this, $"来自插件 {pluginId}");
        }
        catch (Exception ex)
        {
            PluginLog.Error(pluginId, "插件控件构造失败，已替换为占位", ex);

            Content = CreateFailureText(pluginId);
            ToolTip.SetTip(this, "插件 " + pluginId + " 显示失败：" + OneLine(ex));
        }
    }

    /// <summary>这个位置属于哪个插件。</summary>
    public string PluginId { get; }

    private static Control CreateFailureText(string pluginId) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#F3F4F6")),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 6),
        Child = new TextBlock
        {
            Text = $"插件 {pluginId} 显示失败",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static string OneLine(Exception ex)
    {
        var text = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) text = ex.GetType().Name;
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}

/// <summary>
/// 把某个插槽的插件控件填进一个面板。
///
/// 每个用到插槽的地方都是同三行：清空 → 逐个包上 <see cref="PluginControlHost"/> →
/// 空了就把整块隐藏。为了不让这几行散落在四个视图里各写一遍（还容易漏掉隐藏那一步），
/// 收在这里。
/// </summary>
public static class PluginSlotPanel
{
    /// <summary>
    /// 用 <paramref name="slotId"/> 的内容重建 <paramref name="target"/>。
    /// <b>没有内容时把 target 隐藏</b>：留一个空的面板会白白占掉一条边距。
    /// </summary>
    public static void Fill(Panel target, string slotId)
    {
        target.Children.Clear();

        foreach (var entry in PluginUiRegistry.Instance.GetSlot(slotId))
            target.Children.Add(new PluginControlHost(entry.PluginId, entry.Factory));

        target.IsVisible = target.Children.Count > 0;
    }

    /// <summary>订阅登记表变化，让视图重建插槽内容。<b>返回的 IDisposable 必须在离开可视树时撤销</b>。</summary>
    public static IDisposable Subscribe(Action refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);

        void Handler() => refresh();

        PluginUiRegistry.Instance.Changed += Handler;
        return new Unsubscriber(() => PluginUiRegistry.Instance.Changed -= Handler);
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            var current = _action;
            if (current is null) return;

            _action = null;
            current();
        }
    }
}
