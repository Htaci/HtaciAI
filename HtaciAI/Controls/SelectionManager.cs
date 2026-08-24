using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// 跨块文本选择协调器（非 UI 类）。挂到消息列表宿主上，监听未处理的指针事件：
/// 命中测试当前悬停的 <see cref="CustomSelectableTextBlock"/>，按视觉顺序计算跨块选中范围，
/// 通过 <see cref="CustomSelectableTextBlock.ApplySelection"/> 让各块渲染高亮。
/// 提供 GetSelectedText / CopyAsync 用于统一复制。宿主内的 Button 会先行消费指针事件，不进入选择。
/// </summary>
public sealed class SelectionManager
{
    private Control? _host;
    private CustomSelectableTextBlock? _anchorBlock;
    private int _anchorIndex;
    private bool _selecting;

    public void AttachHost(Control host)
    {
        if (_host != null) DetachHost();
        _host = host;
        host.Focusable = true;
        host.PointerPressed += OnPointerPressed;
        host.PointerMoved += OnPointerMoved;
        host.PointerReleased += OnPointerReleased;
        host.PointerWheelChanged += OnPointerWheelChanged;
        host.KeyDown += OnHostKeyDown;
    }

    public void DetachHost()
    {
        if (_host == null) return;
        _host.PointerPressed -= OnPointerPressed;
        _host.PointerMoved -= OnPointerMoved;
        _host.PointerReleased -= OnPointerReleased;
        _host.PointerWheelChanged -= OnPointerWheelChanged;
        _host.KeyDown -= OnHostKeyDown;
        _host = null;
    }

    public void ClearSelection()
    {
        foreach (var b in OrderedBlocks())
            b.ApplySelection(0, 0);
    }

    /// <summary>拼接所有被选中块的文本（按视觉顺序）。</summary>
    public string GetSelectedText()
    {
        if (_host == null) return "";
        var sb = new StringBuilder();
        foreach (var b in OrderedBlocks())
        {
            var s = Math.Min(b.SelectionStart, b.SelectionEnd);
            var e = Math.Max(b.SelectionStart, b.SelectionEnd);
            var len = b.Text?.Length ?? 0;
            if (s >= e || e > len) continue;
            sb.Append(b.Text!.Substring(s, e - s));
        }
        return sb.ToString();
    }

    public async void CopyAsync()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(_host)?.Clipboard;
        if (clip == null) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clip.SetDataAsync(transfer);
    }

    // ---- 宿主指针事件 ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_host == null) return;
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) return;

        var (block, index) = ResolveEndpoint(e, out var isButton);
        var hp = e.GetPosition(_host);
        var rp = e.GetPosition(null);
        Log($"[Sel] 按下 host=({hp.X:F0},{hp.Y:F0}) root=({rp.X:F0},{rp.Y:F0}) isButton={isButton} 命中={HitDesc(block)} idx={index}");
        if (isButton) return; // 按钮，放行事件（不进入选择，让按钮可点击）

        if (block == null)
        {
            Log("[Sel] 按下命中块为空 → 未起选");
            ClearSelection();
            return;
        }

        _anchorBlock = block;
        _anchorIndex = index;
        _selecting = true;
        ApplySelectionToRange(block, index, e.GetPosition(_host).Y);
        e.Pointer.Capture(_host);
        _host.Focus();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_host == null) return;
        if (!_selecting || e.Pointer.Captured != _host) return;

        var (block, index) = ResolveEndpoint(e, out _);
        if (block == null) return;
        ApplySelectionToRange(block, index, e.GetPosition(_host).Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == _host)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    /// <summary>选择拖拽时：把滚轮转给指针正下方的那个 ScrollViewer（可能是思考卡内层或根），而不是滚动根列表。</summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_selecting || _host == null) return;

        var sv = FindScrollViewerUnderPointer(e);
        if (sv == null) return;

        var delta = -e.Delta.Y * 60;
        var target = sv.Offset.Y + delta;
        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        target = Math.Max(0, Math.Min(target, max));
        sv.Offset = new Vector(sv.Offset.X, target);
        e.Handled = true;
    }

    private ScrollViewer? FindScrollViewerUnderPointer(PointerEventArgs e)
    {
        if (_host == null) return null;
        var hit = _host.InputHitTest(e.GetPosition(_host)) as Visual;
        while (hit != null)
        {
            if (hit is ScrollViewer sv) return sv;
            hit = hit.GetVisualParent() as Visual;
        }
        return null;
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        var keymap = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keymap != null && keymap.Copy.Any(g => g.Matches(e)))
        {
            CopyAsync();
            e.Handled = true;
        }
    }

    // ---- 命中测试 ----

    /// <summary>
    /// 解算指针落在哪个块/哪个索引。优先精确命中（点在文字上）；落在空白处（消息间隙/文字下方/右侧）
    /// 则回退到最近块并钳制到块首/块尾——模拟浏览器"拖到结尾空白也选满"的体验。
    /// </summary>
    private (CustomSelectableTextBlock? Block, int Index) ResolveEndpoint(PointerEventArgs e, out bool isButton)
    {
        isButton = false;
        if (_host == null) return (null, 0);

        // 1) 精确命中：指针在文本块上 → 直接用该块（InputHitTest 是屏幕坐标命中，滚动无关，任意消息/跨行都可靠）
        var hit = _host.InputHitTest(e.GetPosition(_host)) as Visual;
        while (hit != null)
        {
            if (hit is Button)
            {
                isButton = true;
                return (null, 0);
            }
            if (hit is ScrollBar or Thumb or RepeatButton)
            {
                // 滚动条 Thumb/RepeatButton：放行，让滚动条拖拽正常工作，不进入选择
                isButton = true;
                return (null, 0);
            }
            if (hit is CustomSelectableTextBlock b && IsEffectivelyShown(b))
            {
                var idx = b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b));
                Log($"[Sel] 精确命中文本块 {HitDesc(b)} idx={idx}");
                return (b, idx);
            }
            hit = hit.GetVisualParent() as Visual;
        }

        // 2) 空白：用顶层(场景根, null)坐标系找"纵向最近"块——root 坐标含全部变换(含滚动偏移)，
        //    指针 e.GetPosition(null) 与块 TransformToVisual(null) 永远一致，任何滚动/任何消息都有效。
        var frame = TopLevel.GetTopLevel(_host);
        if (frame == null) return (null, 0);
        var p = e.GetPosition(frame);
        var blocks = OrderedBlocks();
        Log($"[Sel] 空白路线: 块数={blocks.Count} 指针root=({p.X:F0},{p.Y:F0})");
        CustomSelectableTextBlock? best = null;
        double bestDist = double.MaxValue;
        int bestIdx = 0;
        foreach (var b in blocks)
        {
            if (!IsEffectivelyShown(b)) continue; // 隐藏/折叠(思考卡)块不参与，避免命中折叠内容
            if (b.Bounds.Width <= 0 || b.Bounds.Height <= 0) continue; // 未测量/空块跳过
            var tr = b.TransformToVisual(frame);
            if (tr == null) continue;
            var or = tr.Value.Transform(default);
            var rect = new Rect(or, b.Bounds.Size);
            Log($"[Sel]   块 {HitDesc(b)} rect=({or.X:F0},{or.Y:F0},{b.Bounds.Width:F0}x{b.Bounds.Height:F0})");
            if (rect.Contains(p))
            {
                var idx = b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b));
                Log($"[Sel]   命中 in-rect {HitDesc(b)} idx={idx}");
                return (b, idx);
            }

            var dist = VerticalDistToRect(p, rect);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = b;
                bestIdx = p.Y < rect.Center.Y ? 0 : (b.Text?.Length ?? 0);
            }
        }

        Log($"[Sel] 空白最近={HitDesc(best)} idx={bestIdx} dist={bestDist:F1}");
        return best is not null ? (best, bestIdx) : (null, 0);
    }

    private static void Log(string s)
    {
        Console.WriteLine(s);
        Debug.WriteLine(s);
    }

    private static string HitDesc(CustomSelectableTextBlock? b)
    {
        if (b == null) return "null";
        var t = b.Text ?? "";
        var snippet = t.Length > 12 ? t[..12] + "…" : t;
        return $"[{b.GetType().Name}:{snippet}](len={t.Length})";
    }

    /// <summary>把空白点到块的本地坐标钳到块内，避免 TextLayout 对越界点返回怪异索引（下方→块尾、上方→块首）。</summary>
    private static Point ClampToBlock(Point p, CustomSelectableTextBlock b)
        => new(
            Math.Max(0, Math.Min(p.X, b.Bounds.Width)),
            Math.Max(0, Math.Min(p.Y, b.Bounds.Height)));

    /// <summary>滚动视图坐标系中，点到块矩形之外的纵向优先距离。</summary>
    private static double VerticalDistToRect(Point p, Rect r)
    {
        var dx = p.X < r.Left ? r.Left - p.X : (p.X > r.Right ? p.X - r.Right : 0);
        var dy = p.Y < r.Top ? r.Top - p.Y : (p.Y > r.Bottom ? p.Y - r.Bottom : 0);
        return dy * 10 + dx;
    }

    // ---- 选中范围计算 ----

    private void ApplySelectionToRange(CustomSelectableTextBlock current, int currentIndex, double pointerY)
    {
        var blocks = OrderedBlocks();
        var anchor = _anchorBlock;
        if (anchor == null) { current.ApplySelection(currentIndex, currentIndex); return; }

        foreach (var b in blocks)
            b.ApplySelection(0, 0);

        var anchorPos = blocks.IndexOf(anchor);
        var curPos = blocks.IndexOf(current);
        if (anchorPos < 0 || curPos < 0)
        {
            anchor.ApplySelection(_anchorIndex, _anchorIndex);
            current.ApplySelection(currentIndex, currentIndex);
            return;
        }

        // 用指针 Y vs 锚点块中心 Y 强制方向；端点落在反侧时钳回锚点，避免"向下拖反而往上是选"
        var down = pointerY >= HostCenterY(anchor);
        if (down && curPos < anchorPos) { curPos = anchorPos; current = anchor; currentIndex = _anchorIndex; }
        else if (!down && curPos > anchorPos) { curPos = anchorPos; current = anchor; currentIndex = _anchorIndex; }

        if (anchorPos == curPos)
        {
            anchor.ApplySelection(Math.Min(_anchorIndex, currentIndex), Math.Max(_anchorIndex, currentIndex));
            return;
        }

        var startPos = Math.Min(anchorPos, curPos);
        var endPos = Math.Max(anchorPos, curPos);
        var startIdx = anchorPos <= curPos ? _anchorIndex : currentIndex;
        var endIdx = anchorPos <= curPos ? currentIndex : _anchorIndex;

        for (var i = startPos; i <= endPos; i++)
        {
            var b = blocks[i];
            var len = b.Text?.Length ?? 0;
            if (i == startPos) b.ApplySelection(startIdx, len);
            else if (i == endPos) b.ApplySelection(0, endIdx);
            else b.ApplySelection(0, len);
        }
    }

    /// <summary>块顶中心在宿主坐标系下的 Y，用于方向判定。</summary>
    private double HostCenterY(CustomSelectableTextBlock b)
    {
        if (_host == null) return 0;
        var tr = b.TransformToVisual(_host);
        if (tr == null) return 0;
        return tr.Value.Transform(default).Y + b.Bounds.Height / 2;
    }

    /// <summary>收集宿主内"实际可见"的 CustomSelectableTextBlock 并按视觉位置（Y 后 X）排序。</summary>
    private List<CustomSelectableTextBlock> OrderedBlocks()
    {
        var list = new List<CustomSelectableTextBlock>();
        var host = _host;
        if (host == null) return list;
        CollectBlocks(host, list);

        return list
            .Where(IsEffectivelyShown)
            .Select(b => new { B = b, P = b.TransformToVisual(host)?.Transform(default) ?? default })
            .OrderBy(x => x.P.Y)
            .ThenBy(x => x.P.X)
            .Select(x => x.B)
            .ToList();
    }

    /// <summary>块是否真实可见：祖先 IsVisible=false / Opacity=0 / 被折叠(MaxHeight==0) 视为不可见，跳过其参与选择。</summary>
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

    private static void CollectBlocks(Control root, List<CustomSelectableTextBlock> result)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Visual visual)
            {
                if (visual is CustomSelectableTextBlock b)
                    result.Add(b);
                if (visual is Control c)
                    CollectBlocks(c, result);
            }
        }
    }
}
