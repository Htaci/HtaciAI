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
///
/// 性能优化（长历史不卡）：
///  - 层级：以"每条消息/turn 的 StackPanel"为 level-1 容器，缓存其内容 Y（稳定，不随滚动变）与内部可见文本块；
///  - 空白命中：指针内容 Y 先二分/粗筛到附近几个候选容器，再只用窗口坐标对候选容器内的少量块精确找最近块；
///  - 只重绘区间：ApplySelectionToRange 只清除上一帧区间 + 应用新区间，不再对全部块 reset；
///  - 块索引用字典缓存（O(1) IndexOf）。
/// </summary>
public sealed class SelectionManager
{
    private sealed class Container
    {
        public List<CustomSelectableTextBlock> Blocks = new();
        public double Top;
        public double Bottom;
    }

    private Control? _host;
    private CustomSelectableTextBlock? _anchorBlock;
    private int _anchorIndex;
    private bool _selecting;
    private bool _selectionActive; // 是否已真正起选（移动超过 DragThreshold 后才置 true）

    private List<Container>? _containers;
    private List<CustomSelectableTextBlock>? _ordered;
    private Dictionary<CustomSelectableTextBlock, int>? _orderIndex;
    private int _prevStart = -1;
    private int _prevEnd = -1;
    private Point _pressPoint;
    private const double DragThreshold = 4; // 按下后移动超过该距离才视为拖选，否则是点击（不框选）

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
        _containers = null;
        _ordered = null;
        _orderIndex = null;
    }

    public void ClearSelection()
    {
        EnsureCache();
        if (_ordered != null)
            foreach (var b in _ordered)
                b.ApplySelection(0, 0);
        _prevStart = _prevEnd = -1;
    }

    /// <summary>拼接所有被选中块的文本（按视觉顺序）。</summary>
    public string GetSelectedText()
    {
        if (_host == null) return "";
        EnsureCache();
        if (_ordered == null) return "";
        var sb = new StringBuilder();
        foreach (var b in _ordered)
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

        // 每次开始选择重建层级缓存（确保含最新流式块）；拖选期间复用
        _containers = null;
        _ordered = null;
        _orderIndex = null;
        _prevStart = _prevEnd = -1;
        _pressPoint = e.GetPosition(_host);

        var (block, index) = ResolveEndpoint(e, out var isButton);
        if (isButton) return; // 按钮/滚动条，放行（不进入选择，让它们可点击）

        ClearSelection(); // 开始新选择：先清空旧的（点击空白/文字都取消之前选区）

        // 仅点到文本/附近空白才进入选择；点到远处空白直接返回（保留该处滚动拖拽）
        if (block == null) return;

        // 按下只记录候选锚点，先不应用选区：
        // 单次点击（未超过 DragThreshold）不该选中任何文本块，否则点击空白会被 ResolveEndpoint
        // 解析到最近块而误把整块（如标题）高亮。真正拖动超过阈值后才在 OnPointerMoved 里起选。
        _anchorBlock = block;
        _anchorIndex = index;
        _selecting = true;
        _selectionActive = false;
        e.Pointer.Capture(_host);
        _host.Focus();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_host == null) return;
        if (!_selecting || e.Pointer.Captured != _host) return;

        // 移动未超过阈值（纯点击）不扩展选区，避免"没拖动却选出一段"
        var pp = e.GetPosition(_host);
        var dist = Math.Sqrt(Math.Pow(pp.X - _pressPoint.X, 2) + Math.Pow(pp.Y - _pressPoint.Y, 2));
        if (dist < DragThreshold) return;

        // 首次越过阈值才算真正起选：按下时来自空白的锚点（ResolveEndpoint 的最近块）直接复用；
        // 若按下时未能解析到块（远处空白），则以此刻指针正下方块为锚。
        if (!_selectionActive)
        {
            if (_anchorBlock == null)
            {
                var (b, i) = ResolveEndpoint(e, out _);
                if (b == null) return;
                _anchorBlock = b;
                _anchorIndex = i;
            }
            _selectionActive = true;
        }

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
            _selectionActive = false;
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

    private (CustomSelectableTextBlock? Block, int Index) ResolveEndpoint(PointerEventArgs e, out bool isButton)
    {
        isButton = false;
        if (_host == null) return (null, 0);

        // 1) 精确命中：指针在文本块上 → 直接用该块（InputHitTest 屏幕坐标命中，滚动无关）
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
                isButton = true; // 滚动条放行
                return (null, 0);
            }
            if (hit is CustomSelectableTextBlock b && IsEffectivelyShown(b))
            {
                var idx = b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b));
                return (b, idx);
            }
            hit = hit.GetVisualParent() as Visual;
        }

        // 2) 空白：level-1 容器粗筛 → 候选容器内少量块用窗口坐标精确找最近块（避免和全部块对比）
        EnsureCache();
        if (_containers == null || _containers.Count == 0) return (null, 0);

        var frame = TopLevel.GetTopLevel(_host);
        if (frame == null) return (null, 0);
        var p = e.GetPosition(frame);
        var candidates = NearbyContainers(e.GetPosition(_host).Y);

        CustomSelectableTextBlock? best = null;
        double bestDist = double.MaxValue;
        int bestIdx = 0;
        foreach (var ct in candidates)
            foreach (var b in ct.Blocks)
            {
                if (!IsEffectivelyShown(b)) continue;
                if (b.Bounds.Width <= 0 || b.Bounds.Height <= 0) continue;
                var tr = b.TransformToVisual(frame);
                if (tr == null) continue;
                var or = tr.Value.Transform(default);
                var rect = new Rect(or, b.Bounds.Size);
                if (rect.Contains(p))
                {
                    var idx = b.GetIndexAtPoint(ClampToBlock(e.GetPosition(b), b));
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

        return best is not null ? (best, bestIdx) : (null, 0);
    }

    // ---- 层级缓存 ----

    /// <summary>重建/获取容器与块缓存。宿主直接子级的 StackPanel(每条消息/turn) 为 level-1 容器。</summary>
    private void EnsureCache()
    {
        if (_containers != null) return;
        var host = _host;
        var containers = new List<Container>();
        var ordered = new List<CustomSelectableTextBlock>();
        if (host != null)
        {
            foreach (var child in host.GetVisualChildren())
            {
                if (child is not StackPanel sp) continue;
                var blocks = new List<CustomSelectableTextBlock>();
                CollectBlocks(sp, blocks);
                blocks = blocks.Where(IsEffectivelyShown).ToList();
                if (blocks.Count == 0) continue;
                var tr = sp.TransformToVisual(host);
                var top = tr?.Transform(default).Y ?? 0;
                containers.Add(new Container { Blocks = blocks, Top = top, Bottom = top + sp.Bounds.Height });
            }
            containers.Sort((a, b) => a.Top.CompareTo(b.Top));
            foreach (var c in containers) ordered.AddRange(c.Blocks);
        }
        _containers = containers;
        _ordered = ordered;
        _orderIndex = new Dictionary<CustomSelectableTextBlock, int>();
        for (var i = 0; i < ordered.Count; i++) _orderIndex[ordered[i]] = i;
    }

    /// <summary>按内容 Y 找指针所在/最近的容器，返回该容器 + 上下各一个（最多3个候选）。</summary>
    private List<Container> NearbyContainers(double y)
    {
        var list = new List<Container>();
        if (_containers == null || _containers.Count == 0) return list;
        int lo = 0, hi = _containers.Count - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_containers[mid].Top <= y) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        for (var i = -1; i <= 1; i++)
        {
            var j = idx + i;
            if (j >= 0 && j < _containers.Count) list.Add(_containers[j]);
        }
        return list;
    }

    // ---- 选中范围计算（只重绘区间） ----

    private void ApplySelectionToRange(CustomSelectableTextBlock current, int currentIndex, double pointerY)
    {
        EnsureCache();
        var ordered = _ordered;
        var index = _orderIndex;
        var anchor = _anchorBlock;
        if (anchor == null || ordered == null || index == null)
        {
            current.ApplySelection(currentIndex, currentIndex);
            return;
        }

        // 清除上一帧选中区间
        if (_prevStart >= 0 && _prevEnd >= 0 && _prevEnd < ordered.Count)
            for (var i = _prevStart; i <= _prevEnd; i++)
                ordered[i].ApplySelection(0, 0);

        if (!index.TryGetValue(anchor, out var anchorPos) || !index.TryGetValue(current, out var curPos))
        {
            anchor.ApplySelection(_anchorIndex, _anchorIndex);
            current.ApplySelection(currentIndex, currentIndex);
            _prevStart = _prevEnd = -1;
            return;
        }

        // 指针 Y vs 锚点块中心 Y 强制方向；端点落在反侧时钳回锚点，避免"向下拖反而往上是选"
        var down = pointerY >= HostCenterY(anchor);
        if (down && curPos < anchorPos) { curPos = anchorPos; current = anchor; currentIndex = _anchorIndex; }
        else if (!down && curPos > anchorPos) { curPos = anchorPos; current = anchor; currentIndex = _anchorIndex; }

        var startPos = Math.Min(anchorPos, curPos);
        var endPos = Math.Max(anchorPos, curPos);
        var startIdx = anchorPos <= curPos ? _anchorIndex : currentIndex;
        var endIdx = anchorPos <= curPos ? currentIndex : _anchorIndex;

        for (var i = startPos; i <= endPos; i++)
        {
            var b = ordered[i];
            var len = b.Text?.Length ?? 0;
            if (i == startPos) b.ApplySelection(startIdx, len);
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

    private static void Log(string s)
    {
        // Console.WriteLine(s);
        // Debug.WriteLine(s);
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
}
