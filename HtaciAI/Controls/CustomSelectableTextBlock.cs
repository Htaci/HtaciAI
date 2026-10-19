using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 可选择文本块：两套选择路径共用它。
/// <list type="bullet">
///   <item><b>原生选择</b>（<see cref="NativeSelectionEnabled"/> = true，用户气泡 / 代码块 / 错误卡 / 工具入参 / 全部原文）：
///     块自己处理指针，拖选、双击选词、Ctrl+C 都由框架完成；这类块<b>不参与</b>轮内自定义选区
///     （<see cref="TurnTextSelector"/> 只收非原生的块）。</item>
///   <item><b>自定义选择</b>（默认 false，Markdown 正文 / 思考卡）：块内选择被拦下，由所在轮次的选择器统一协调跨块选区。</item>
/// </list>
///
/// 两条路径的选区范围都记在框架的 <c>SelectionStart/SelectionEnd</c> 上；高亮的矩形也由框架的
/// 命中测试算（<see cref="DrawSelectionHighlight"/>），只是绘制这一步由本块自己发起，为的是能兜住异常。
/// </summary>
public sealed class CustomSelectableTextBlock : SelectableTextBlock
{
    /// <summary>选中高亮色。</summary>
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(100, 51, 153, 255));

    private bool _nativeSelectionEnabled;

    public CustomSelectableTextBlock()
    {
        // 本块的内容总能被选中（原生选择，或所在轮次的 TurnTextSelector），因此统一用 I 形光标
        Cursor = new Cursor(StandardCursorType.Ibeam);

        // ⚠️ 选区高亮刻意不交给框架画（SelectionBrush 保持 null，见 DrawSelectionHighlight）：
        // 框架那条路径没有异常保护，而它在某些文本形状下真的会抛，抛在渲染阶段就是整个程序退出。
        // 选中文字变白由框架的字形覆盖实现，与这支画刷无关，保留。
        SelectionBrush = null;
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
    /// 置 true 后本块自己处理指针事件，可独立拖选/双击选词/Ctrl+C，并且不参与轮内的自定义选区。
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

    // ---- 选区高亮 ----

    /// <summary>
    /// 画选区高亮，然后交给基类画文字。
    ///
    /// <b>为什么自己逐行算矩形、而不是用框架的 <c>TextLayout.HitTestTextRange</c></b>：
    /// 那个方法从 0.11.x 起实测会在两种情况下算错，而两者在长文本里几乎是必然出现的 ——
    /// <list type="number">
    ///   <item>它遍历 <c>TextLines</c> 时会自行消耗 <c>start</c>/<c>length</c>，一旦某行没有可绘制的 run
    ///     （空行）就<b>提前 break</b>，后面的行整段漏掉；</item>
    ///   <item><c>TextLayout</c> 会按可用高度补出大量 <b>len=0 的空行</b>（行数 ≈ 高度 ÷ 行高，
    ///     实测：控件高 1200 / 行高 19 → 63 行，其中 61 行是空的）。空行会让上面的消耗逻辑
    ///     把索引用光，于是「高亮只画了前几行，后面要么没有、要么错位」。</item>
    /// </list>
    /// 逐行调用 <c>TextLine.GetTextBounds(绝对索引, 长度)</c> 就没有这两个问题：
    /// 每行独立算、索引是全局的、空行直接跳过。矩形坐标与框架内部完全一致
    /// （同样用行高累加定位行、同样做 <c>PixelRect.FromRect(rect, 1).ToRect(1)</c> 像素吸附）。
    /// </summary>
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        DrawSelectionHighlight(context, origin);
        base.RenderTextLayout(context, origin);
    }

    private void DrawSelectionHighlight(DrawingContext context, Point origin)
    {
        foreach (var rect in BuildSelectionRects())
            context.FillRectangle(
                HighlightBrush,
                PixelRect.FromRect(new Rect(rect.X + origin.X, rect.Y + origin.Y, rect.Width, rect.Height), 1).ToRect(1));
    }

    /// <summary>
    /// 逐行算出当前选区的矩形（相对本块，未做像素吸附）。渲染与测试共用这一份几何。
    /// 没有选区、拿不到布局时返回空列表。
    /// </summary>
    internal IReadOnlyList<Rect> BuildSelectionRects()
    {
        var layout = TextLayout;
        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);

        if (layout is null || end <= start) return Array.Empty<Rect>();

        var rects = new List<Rect>();
        var y = 0.0;

        foreach (var line in layout.TextLines)
        {
            // 布局为填满高度补出来的尾部空行：不参与选区，直接跳过
            // （它们既不显示文字，也不该占用选区的索引）
            if (line.Length <= 0 && line.NewLineLength <= 0)
            {
                y += line.Height;
                continue;
            }

            var from = Math.Max(start, line.FirstTextSourceIndex);
            var to = Math.Min(end, line.FirstTextSourceIndex + line.Length);

            if (to > from)
            {
                try
                {
                    foreach (var bounds in line.GetTextBounds(from, to - from))
                    {
                        var r = bounds.Rectangle;
                        rects.Add(new Rect(r.X, r.Y + y, r.Width, r.Height));
                    }
                }
                catch (Exception)
                {
                    // 单行取不到字形范围就跳过这一行：宁可少一段高亮，也不能让渲染抛异常
                }
            }

            y += line.Height;
        }

        return rects;
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
