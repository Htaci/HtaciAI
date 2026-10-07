using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 禁用原生选择的可选择文本块：选择逻辑完全交由 <see cref="SelectionManager"/> 统一协调，
/// 本块只负责「按管理器设置的范围渲染高亮」以及「对外暴露文本命中测试」。
/// 继承自 Avalonia 自带的 <see cref="SelectableTextBlock"/>，用其 SelectionBrush/SelectionForegroundBrush 渲染选中高亮。
/// </summary>
public sealed class CustomSelectableTextBlock : SelectableTextBlock
{
    private bool _nativeSelectionEnabled;

    public CustomSelectableTextBlock()
    {
        // 默认不可选中 → 用普通箭头光标；置为可选中时才切 I 形，避免暗示"这里的文字能选"
        Cursor = new Cursor(StandardCursorType.Arrow);
        // 显式设置选中高亮：让 SelectionBrush/SelectionForegroundBrush 生效，避免默认透明导致看不到
        SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));
        SelectionForegroundBrush = Brushes.White;
    }

    /// <summary>
    /// 是否启用原生选择。默认 false：交给外部管理器协调（见 <see cref="SelectionManager"/>）。
    /// 置 true 后本块自己处理指针事件，可独立拖选/双击选词/Ctrl+C。
    /// </summary>
    public bool NativeSelectionEnabled
    {
        get => _nativeSelectionEnabled;
        set
        {
            if (_nativeSelectionEnabled == value) return;
            _nativeSelectionEnabled = value;
            Cursor = new Cursor(value ? StandardCursorType.Ibeam : StandardCursorType.Arrow);
        }
    }

    /// <summary>管理器设置选中范围，触发本块高亮（自动钳制到文本长度内）。</summary>
    public void ApplySelection(int start, int end)
    {
        var len = Text?.Length ?? 0;
        var s = Math.Max(0, Math.Min(start, len));
        var e = Math.Max(0, Math.Min(end, len));
        SelectionStart = Math.Min(s, e);
        SelectionEnd = Math.Max(s, e);
        InvalidateVisual();
    }

    /// <summary>命中文本索引：返回 <paramref name="point"/>（相对本块）处的字符索引，用于管理器跨块排序。</summary>
    public int GetIndexAtPoint(Point point)
    {
        var layout = TextLayout;
        if (layout == null || string.IsNullOrEmpty(Text))
            return 0;
        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        return layout.HitTestPoint(local).TextPosition;
    }

    // ---- 默认禁用原生选择：拦截鼠标，不让 SelectableTextBlock 自己开始选择 ----
    // NativeSelectionEnabled=true 时走 base，交还原生选择（拖选/双击选词/键盘操作）。
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (NativeSelectionEnabled)
        {
            base.OnPointerPressed(e);
            return;
        }
        // 不调用 base：不触发原生拖选；事件继续冒泡到宿主的 SelectionManager
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (NativeSelectionEnabled)
        {
            base.OnPointerMoved(e);
            return;
        }
        // 禁用原生
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (NativeSelectionEnabled)
        {
            base.OnPointerReleased(e);
            return;
        }
        // 禁用原生
    }
}
