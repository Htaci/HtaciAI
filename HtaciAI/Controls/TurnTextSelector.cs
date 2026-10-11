using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace HtaciAI.Controls;

/// <summary>
/// 轮内文本选择器：挂到<b>一轮消息</b>的内容容器上，只允许在同一轮内跨块选中。
///
/// 为什么以「轮」为界而不是整页：
///  - 一轮的块数量有限，每次起选线性重建块列表即可，不需要容器缓存 / 二分粗筛 / 索引字典；
///  - 语义上更贴合实际需求（复制一轮回答），也不会把上下两轮的内容连成一段；
///  - 与原生选择的块不冲突：本选择器只接管「原生选择被禁用」的块
///    （见 <see cref="CustomSelectableTextBlock.NativeSelectionEnabled"/>），
///    用户气泡、代码块这些自己就能拖选的块一律放行。
///
/// 与 <see cref="TurnSelectionHost"/> 的「原文模式」是两条独立路径：原文模式把块切成原生可选，
/// 本选择器则在<b>渲染态</b>下就能选中已渲染的文本，两者互不干扰。
/// </summary>
public sealed class TurnTextSelector
{
    /// <summary>按下后移动超过该距离才算拖选，否则视为点击（不选中任何内容）。</summary>
    private const double DragThreshold = 4;

    /// <summary>最后接管选择的实例（全局唯一）：别的轮开始选择时先清掉它，避免两轮同时高亮。</summary>
    private static TurnTextSelector? _active;

    private Control? _host;
    private List<CustomSelectableTextBlock>? _blocks;

    private CustomSelectableTextBlock? _anchor;
    private int _anchorIndex;
    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private int _prevStart = -1;
    private int _prevEnd = -1;

    /// <summary>挂到本轮的内容容器上（该容器必须是各文本块的祖先）。</summary>
    public void AttachHost(Control host)
    {
        DetachHost();
        _host = host;
        host.Focusable = true;      // Ctrl+C 需要焦点落在这里
        host.PointerPressed += OnPointerPressed;
        host.PointerMoved += OnPointerMoved;
        host.PointerReleased += OnPointerReleased;
        host.KeyDown += OnKeyDown;
    }

    public void DetachHost()
    {
        if (_host == null) return;
        _host.PointerPressed -= OnPointerPressed;
        _host.PointerMoved -= OnPointerMoved;
        _host.PointerReleased -= OnPointerReleased;
        _host.KeyDown -= OnKeyDown;
        if (ReferenceEquals(_active, this)) _active = null;
        _host = null;
        _blocks = null;
    }

    /// <summary>清空本轮高亮（不重建块列表）。</summary>
    public void ClearSelection()
    {
        if (_blocks != null)
            foreach (var b in _blocks)
                b.ApplySelection(0, 0);
        if (ReferenceEquals(_active, this)) _active = null;
        _prevStart = _prevEnd = -1;
    }

    /// <summary>按视觉顺序拼接选中的文本。</summary>
    public string GetSelectedText()
    {
        if (_blocks == null) return "";
        var sb = new StringBuilder();
        foreach (var b in _blocks)
        {
            var s = Math.Min(b.SelectionStart, b.SelectionEnd);
            var e = Math.Max(b.SelectionStart, b.SelectionEnd);
            var text = b.SelectionText;
            if (s >= e || e > text.Length) continue;
            sb.Append(text, s, e - s);
        }
        return sb.ToString();
    }

    public async void CopyAsync()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(_host)?.Clipboard;
        if (clipboard == null) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
    }

    // ---- 宿主指针 / 键盘事件 ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_host == null) return;
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) return;

        _pressPoint = e.GetPosition(_host);

        // 每次起选重建块列表：流式期间会不断新增块，缓存住会漏掉新内容
        _blocks = null;
        var blocks = GetBlocks();

        var (block, index, barrier) = ResolveEndpoint(e);

        // 本轮内按下即视为重新开始：清掉上一段高亮（别的轮次的先清，避免两轮同时高亮）。
        // 放在 barrier 判断之前 —— 点到按钮/原生可选块时也该收起旧高亮。
        if (_active is { } previous && !ReferenceEquals(previous, this)) previous.ClearSelection();
        foreach (var b in blocks) b.ApplySelection(0, 0);
        _prevStart = _prevEnd = -1;
        _active = this;

        if (barrier) return;        // 按钮 / 滚动条 / 原生可选块：放行，交给它们自己处理
        if (block == null) return;  // 点到远处空白：不进入选择，保留该处的滚动拖拽

        // 按下只记锚点，先不应用选区：单次点击（未越过阈值）不该选中任何文本
        _anchor = block;
        _anchorIndex = index;
        _pressed = true;
        _dragging = false;
        e.Pointer.Capture(_host);
        e.Handled = true;
        // 这里不抢焦点：单纯点一下正文就把焦点从输入框拿走，会让「点一下再按回车发送」失灵。
        // 焦点在真正开始拖选时才拿（见 OnPointerMoved），Ctrl+C 需要它。
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_host == null) return;
        if (!_pressed || e.Pointer.Captured != _host) return;

        var p = e.GetPosition(_host);
        if (!_dragging)
        {
            var dist = Math.Sqrt(Math.Pow(p.X - _pressPoint.X, 2) + Math.Pow(p.Y - _pressPoint.Y, 2));
            if (dist < DragThreshold) return;   // 纯点击：不扩展选区
            _dragging = true;
            _host.Focus();
        }

        var (block, index, barrier) = ResolveEndpoint(e);
        if (barrier || block == null) return;
        ApplySelectionToRange(block, index, p.Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != _host) return;
        _pressed = false;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var keymap = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keymap != null && keymap.Copy.Any(g => g.Matches(e)))
        {
            CopyAsync();
            e.Handled = true;
        }
    }

    // ---- 命中测试 ----

    private (CustomSelectableTextBlock? Block, int Index, bool Barrier) ResolveEndpoint(PointerEventArgs e)
    {
        if (_host == null) return (null, 0, false);

        // 1) 精确命中：指针在文本块上就直接用该块（坐标是窗口坐标，与滚动无关）
        var hit = _host.InputHitTest(e.GetPosition(_host)) as Visual;
        while (hit != null)
        {
            if (hit is Button or ScrollBar or Thumb or RepeatButton)
                return (null, 0, true);
            if (hit is CustomSelectableTextBlock b && IsEffectivelyShown(b))
            {
                if (b.NativeSelectionEnabled) return (null, 0, true);
                return (b, b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b)), false);
            }
            hit = hit.GetVisualParent() as Visual;
        }

        // 2) 空白：取纵向最近的块（一轮内块不多，线性遍历足够）
        var p = e.GetPosition(_host);
        CustomSelectableTextBlock? best = null;
        var bestDist = double.MaxValue;
        var bestIndex = 0;
        foreach (var b in GetBlocks())
        {
            var tr = b.TransformToVisual(_host);
            if (tr == null) continue;
            var rect = new Rect(tr.Value.Transform(default), b.Bounds.Size);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            if (rect.Contains(p))
                return (b, b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b)), false);

            var dist = VerticalDistToRect(p, rect);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = b;
                bestIndex = p.Y < rect.Center.Y ? 0 : b.SelectionTextLength;
            }
        }
        return (best, bestIndex, false);
    }

    /// <summary>本轮内参与选择的块（视觉顺序）：原生选择被禁用的、且真实可见的文本块。</summary>
    private List<CustomSelectableTextBlock> GetBlocks()
    {
        if (_blocks != null) return _blocks;
        var list = new List<CustomSelectableTextBlock>();
        if (_host != null) Collect(_host, list);
        _blocks = list;
        return list;
    }

    private static void Collect(Control root, List<CustomSelectableTextBlock> result)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is not Visual visual) continue;

            if (visual is CustomSelectableTextBlock b)
            {
                if (!b.NativeSelectionEnabled && IsEffectivelyShown(b)) result.Add(b);
                continue;   // 文本块内部没有别的可选块
            }

            if (visual is Control c) Collect(c, result);
        }
    }

    // ---- 选中范围计算（只重绘上一帧区间） ----

    private void ApplySelectionToRange(CustomSelectableTextBlock current, int currentIndex, double pointerY)
    {
        var blocks = GetBlocks();
        var anchor = _anchor;
        if (anchor == null)
        {
            current.ApplySelection(currentIndex, currentIndex);
            return;
        }

        // 清除上一帧的选中区间
        if (_prevStart >= 0 && _prevEnd >= 0 && _prevEnd < blocks.Count)
            for (var i = _prevStart; i <= _prevEnd; i++)
                blocks[i].ApplySelection(0, 0);

        var anchorPos = blocks.IndexOf(anchor);
        var curPos = blocks.IndexOf(current);
        if (anchorPos < 0 || curPos < 0)
        {
            anchor.ApplySelection(_anchorIndex, _anchorIndex);
            current.ApplySelection(currentIndex, currentIndex);
            _prevStart = _prevEnd = -1;
            return;
        }

        // 指针 Y 决定方向；端点落在锚点反侧时钳回锚点，避免「向下拖反而往上是选」
        var down = pointerY >= HostCenterY(anchor);
        if (down && curPos < anchorPos) { curPos = anchorPos; currentIndex = _anchorIndex; }
        else if (!down && curPos > anchorPos) { curPos = anchorPos; currentIndex = _anchorIndex; }

        var startPos = Math.Min(anchorPos, curPos);
        var endPos = Math.Max(anchorPos, curPos);
        var startIdx = anchorPos <= curPos ? _anchorIndex : currentIndex;
        var endIdx = anchorPos <= curPos ? currentIndex : _anchorIndex;

        for (var i = startPos; i <= endPos; i++)
        {
            var b = blocks[i];
            var len = b.SelectionTextLength;
            // 同一个块内：两个索引要按大小排，反着拖（锚点在后）才不会选错区间
            if (i == startPos && i == endPos) b.ApplySelection(Math.Min(startIdx, endIdx), Math.Max(startIdx, endIdx));
            else if (i == startPos) b.ApplySelection(startIdx, len);
            else if (i == endPos) b.ApplySelection(0, endIdx);
            else b.ApplySelection(0, len);
        }
        _prevStart = startPos;
        _prevEnd = endPos;
    }

    /// <summary>块顶中心在宿主坐标系下的 Y，用于方向判定。</summary>
    private double HostCenterY(CustomSelectableTextBlock b)
    {
        if (_host == null) return 0;
        var tr = b.TransformToVisual(_host);
        if (tr == null) return 0;
        return tr.Value.Transform(default).Y + b.Bounds.Height / 2;
    }

    /// <summary>块是否真实可见：祖先 IsVisible=false / Opacity=0 / 被折叠(MaxHeight==0) 视为不可见，跳过。</summary>
    private static bool IsEffectivelyShown(CustomSelectableTextBlock b)
    {
        var v = (Visual)b;
        while (v != null)
        {
            if (v is Control c)
            {
                if (!c.IsVisible) return false;
                if (c.Opacity <= 0) return false;
                if (c is Layoutable l && l.MaxHeight == 0) return false;
            }
            v = v.GetVisualParent() as Visual;
        }
        return true;
    }

    /// <summary>把点钳到块内，避免 TextLayout 对越界点返回怪异索引（下方→块尾、上方→块首）。</summary>
    private static Point ClampToBlock(Point p, CustomSelectableTextBlock b)
        => new(
            Math.Max(0, Math.Min(p.X, b.Bounds.Width)),
            Math.Max(0, Math.Min(p.Y, b.Bounds.Height)));

    /// <summary>点到块矩形之外时的距离，纵向优先。</summary>
    private static double VerticalDistToRect(Point p, Rect r)
    {
        var dx = p.X < r.Left ? r.Left - p.X : (p.X > r.Right ? p.X - r.Right : 0);
        var dy = p.Y < r.Top ? r.Top - p.Y : (p.Y > r.Bottom ? p.Y - r.Bottom : 0);
        return dy * 10 + dx;
    }
}
