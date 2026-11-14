using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HtaciAI.Controls;

/// <summary>
/// 「点空白处让输入框失焦」。
///
/// 为什么需要：设置页里的输入框都是失焦即保存 / 失焦即收起高亮，而 Avalonia 只在
/// <b>点到另一个可聚焦控件</b>时才转移焦点。点到标签、卡片、面板这些不可聚焦的地方，
/// 焦点原地不动 —— 表现就是那圈高亮边框一直亮着，看起来像「这个框还在编辑态」。
///
/// 做法：在隧道阶段先看一眼按下的位置，不在任何 <see cref="TextBox"/> 里就把焦点清掉。
/// 只清「当前焦点确实在 TextBox 上」的情况 —— 焦点在按钮、下拉菜单之类的地方时抢着清掉，
/// 会让键盘操作的可视焦点莫名消失。
/// </summary>
public static class TextBoxFocusCleanup
{
    /// <summary>给页面 / 窗口挂上这个行为。同一个宿主只需挂一次。</summary>
    public static void Attach(Control host)
    {
        host.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (IsInsideTextBox(e.Source as Visual, host)) return;
            if (TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement() is not TextBox) return;

            // 延后一拍：焦点是隧道阶段由 FocusManager 分配的，当场清会被随后那次赋值盖掉
            Dispatcher.UIThread.Post(
                () =>
                {
                    var top = TopLevel.GetTopLevel(host);
                    if (top?.FocusManager?.GetFocusedElement() is TextBox)
                        top.FocusManager?.Focus(null);
                },
                DispatcherPriority.Background);
        }
    }

    /// <summary>从落点向上找到 <paramref name="boundary"/> 为止，路径上有没有 TextBox。</summary>
    private static bool IsInsideTextBox(Visual? source, Visual boundary)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, boundary); v = v.GetVisualParent())
            if (v is TextBox) return true;

        return false;
    }
}
