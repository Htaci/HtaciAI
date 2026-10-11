using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 禁用原生选择的可选择文本块：禁用时选择逻辑交由所在轮次的
/// <see cref="TurnTextSelector"/> 统一协调，本块只负责「按选择器设置的范围渲染高亮」以及
/// 「对外暴露文本命中测试」。
/// 继承自 Avalonia 自带的 <see cref="SelectableTextBlock"/>，用其 SelectionBrush/SelectionForegroundBrush 渲染选中高亮。
/// </summary>
public sealed class CustomSelectableTextBlock : SelectableTextBlock
{
    private bool _nativeSelectionEnabled;

    public CustomSelectableTextBlock()
    {
        // 本块的内容总能被选中（原生选择，或所在轮次的 TurnTextSelector），因此统一用 I 形光标
        Cursor = new Cursor(StandardCursorType.Ibeam);
        // 显式设置选中高亮：让 SelectionBrush/SelectionForegroundBrush 生效，避免默认透明导致看不到
        SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));
        SelectionForegroundBrush = Brushes.White;
    }

    /// <summary>
    /// 本块参与选择的文本。
    /// <b>Markdown 段落把内容放在 <see cref="TextBlock.Inlines"/> 里，此时 <see cref="TextBlock.Text"/> 是 null</b>，
    /// 只看 Text 会得到长度 0，选区就永远为空 —— 这正是「拖选什么都选不中」的原因。
    /// <see cref="InlineCollection.Text"/> 与框架内部（钳制选区、SelectAll）用的是同一个来源，
    /// 且会递归处理 Span / LineBreak，比自己遍历 Run 可靠。
    /// </summary>
    public string SelectionText
        => Inlines is { Count: > 0 } inlines ? inlines.Text ?? "" : Text ?? "";

    /// <summary><see cref="SelectionText"/> 的长度，供拖选过程中的高频长度查询使用。</summary>
    public int SelectionTextLength => SelectionText.Length;

    /// <summary>
    /// 是否启用原生选择。默认 false：交给所在轮次的选择器协调（见 <see cref="TurnTextSelector"/>）。
    /// 置 true 后本块自己处理指针事件，可独立拖选/双击选词/Ctrl+C。
    /// </summary>
    public bool NativeSelectionEnabled
    {
        get => _nativeSelectionEnabled;
        set => _nativeSelectionEnabled = value;
    }

    /// <summary>管理器设置选中范围，触发本块高亮（自动钳制到文本长度内）。</summary>
    public void ApplySelection(int start, int end)
    {
        var len = SelectionTextLength;
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
        if (layout == null || SelectionTextLength == 0)
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
        // 不调用 base：不触发原生拖选；事件继续冒泡到所在轮次的选择器（TurnTextSelector）
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
