using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;


namespace HtaciAI.Controls
{
    /// <summary>
    /// A container that allows text selection across multiple TextBlock controls,
    /// even when nested in complex layouts like tables or custom controls.
    /// </summary>
    public class SelectableTextContainer : ContentControl
    {
        public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
            AvaloniaProperty.Register<SelectableTextContainer, IBrush?>(
                nameof(SelectionBrush),
                defaultValue: new SolidColorBrush(Color.FromArgb(100, 51, 153, 255)));

        public static readonly StyledProperty<IBrush?> SelectionForegroundBrushProperty =
            AvaloniaProperty.Register<SelectableTextContainer, IBrush?>(
                nameof(SelectionForegroundBrush));

        public static readonly StyledProperty<int> SelectionStartProperty =
            AvaloniaProperty.Register<SelectableTextContainer, int>(
                nameof(SelectionStart), defaultValue: 0);

        public static readonly StyledProperty<int> SelectionEndProperty =
            AvaloniaProperty.Register<SelectableTextContainer, int>(
                nameof(SelectionEnd), defaultValue: 0);

        public static readonly DirectProperty<SelectableTextContainer, string> SelectedTextProperty =
            AvaloniaProperty.RegisterDirect<SelectableTextContainer, string>(
                nameof(SelectedText),
                o => o.SelectedText);

        // 附加属性：用于排除某些区域不参与选择
        public static readonly AttachedProperty<bool> IsSelectableProperty =
            AvaloniaProperty.RegisterAttached<SelectableTextContainer, Avalonia.Controls.Control, bool>(
                "IsSelectable", defaultValue: true);

        // 附加属性：手动指定选择顺序
        public static readonly AttachedProperty<int> SelectionIndexProperty =
            AvaloniaProperty.RegisterAttached<SelectableTextContainer, Avalonia.Controls.Control, int>(
                "SelectionIndex", defaultValue: -1);

        private List<TextBlockSelectionInfo> _textBlocks = new();
        private string _virtualText = string.Empty;
        private string _selectedText = string.Empty;
        private bool _isSelecting = false;
        private int _selectionAnchor = 0;
        private ScrollViewer? _scrollViewer;
        private bool _isPointerOverText = false;
        private OverlayLayer? _overlayLayer;
        private bool _contentObserved;

        static SelectableTextContainer()
        {
            FocusableProperty.OverrideDefaultValue<SelectableTextContainer>(true);
        }

        public SelectableTextContainer()
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            LostFocus += (_, _) => ClearSelection();
        }

        public IBrush? SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        public IBrush? SelectionForegroundBrush
        {
            get => GetValue(SelectionForegroundBrushProperty);
            set => SetValue(SelectionForegroundBrushProperty, value);
        }

        public int SelectionStart
        {
            get => GetValue(SelectionStartProperty);
            set => SetValue(SelectionStartProperty, value);
        }

        public int SelectionEnd
        {
            get => GetValue(SelectionEndProperty);
            set => SetValue(SelectionEndProperty, value);
        }

        public string SelectedText
        {
            get => _selectedText;
            private set => SetAndRaise(SelectedTextProperty, ref _selectedText, value);
        }

        public static void SetIsSelectable(Avalonia.Controls.Control element, bool value)
        {
            element.SetValue(IsSelectableProperty, value);
        }

        public static bool GetIsSelectable(Avalonia.Controls.Control element)
        {
            return element.GetValue(IsSelectableProperty);
        }

        public static void SetSelectionIndex(Avalonia.Controls.Control element, int value)
        {
            element.SetValue(SelectionIndexProperty, value);
        }

        public static int GetSelectionIndex(Avalonia.Controls.Control element)
        {
            return element.GetValue(SelectionIndexProperty);
        }

        /// <summary>
        /// 手动刷新 TextBlock 列表（当动态添加内容时调用）
        /// </summary>
        public void RefreshTextBlocks()
        {
            RebuildTextBlocks();
        }

        public async void Copy()
        {
            var text = SelectedText;
            if (string.IsNullOrEmpty(text))
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(transfer);
            }
        }

        public void SelectAll()
        {
            SelectionStart = 0;
            SelectionEnd = _virtualText.Length;
        }

        public void ClearSelection()
        {
            SelectionStart = 0;
            SelectionEnd = 0;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            RebuildTextBlocks();
            AttachScrollViewerEvents();
            SetupOverlayLayer();
            ObserveContentChanges();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            DetachScrollViewerEvents();
            CleanupOverlayLayer();
            DetachContentChanges();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            var keymap = Application.Current?.PlatformSettings?.HotkeyConfiguration;
            if (keymap != null)
            {
                bool Match(List<KeyGesture> gestures) => gestures.Any(g => g.Matches(e));

                if (Match(keymap.Copy))
                {
                    Copy();
                    e.Handled = true;
                }
                else if (Match(keymap.SelectAll))
                {
                    SelectAll();
                    e.Handled = true;
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            // 刷新 TextBlock 列表，确保获取最新的内容
            RebuildTextBlocks();

            var position = e.GetPosition(this);

            // 检查是否点击在文本上
            bool clickedOnText = IsPointerOverSelectableText(position);

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var index = GetTextIndexAtPoint(position);
                SelectionEnd = index;
            }
            else
            {
                // 如果点击在空白处且有现有选择，清除选择
                if (!clickedOnText && (SelectionStart != SelectionEnd))
                {
                    ClearSelection();
                    e.Handled = true;
                    Focus();
                    return;
                }

                // 开始新选择
                var index = GetTextIndexAtPoint(position);
                _selectionAnchor = index;
                SelectionStart = index;
                SelectionEnd = index;
            }

            _isSelecting = true;
            e.Pointer.Capture(this);
            e.Handled = true;

            Focus();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var position = e.GetPosition(this);
            var wasOverText = _isPointerOverText;
            _isPointerOverText = IsPointerOverSelectableText(position);

            if (_isPointerOverText != wasOverText)
            {
                Cursor = _isPointerOverText
                    ? new Cursor(StandardCursorType.Ibeam)
                    : new Cursor(StandardCursorType.Arrow);
            }

            if (!_isSelecting || e.Pointer.Captured != this)
                return;

            var index = GetTextIndexAtPoint(position);

            SelectionStart = _selectionAnchor;
            SelectionEnd = index;

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (e.Pointer.Captured == this)
            {
                _isSelecting = false;
                e.Pointer.Capture(null);
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);

            _isPointerOverText = false;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        // 失去焦点时清除选择（已在构造函数订阅 LostFocus 事件）

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
            {
                UpdateSelectedText();
                _overlayLayer?.InvalidateVisual();
            }
            else if (change.Property == SelectionBrushProperty)
            {
                _overlayLayer?.InvalidateVisual();
            }
            else if (change.Property == ContentProperty)
            {
                RebuildTextBlocks();
                DetachScrollViewerEvents();
                AttachScrollViewerEvents();
                ObserveContentChanges();
            }
        }

        private void ObserveContentChanges()
        {
            if (Content is Panel panel)
            {
                panel.Children.CollectionChanged += OnPanelChildrenChanged;
                _contentObserved = true;
            }
        }

        private void DetachContentChanges()
        {
            if (_contentObserved && Content is Panel panel)
            {
                panel.Children.CollectionChanged -= OnPanelChildrenChanged;
                _contentObserved = false;
            }
        }

        private void OnPanelChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // 延迟刷新，确保布局完成
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RebuildTextBlocks();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void UpdateSelectedText()
        {
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            var length = end - start;

            if (length <= 0 || start >= _virtualText.Length)
            {
                SelectedText = string.Empty;
                return;
            }

            length = Math.Min(length, _virtualText.Length - start);
            SelectedText = _virtualText.Substring(start, length);
        }

        private void SetupOverlayLayer()
        {
            if (_overlayLayer == null)
            {
                _overlayLayer = new OverlayLayer(this);

                // 将 overlay 添加到可视树
                VisualChildren.Add(_overlayLayer);
                LogicalChildren.Add(_overlayLayer);
            }
        }

        private void CleanupOverlayLayer()
        {
            if (_overlayLayer != null)
            {
                VisualChildren.Remove(_overlayLayer);
                LogicalChildren.Remove(_overlayLayer);
                _overlayLayer = null;
            }
        }

        private void RebuildTextBlocks()
        {
            _textBlocks.Clear();
            _virtualText = string.Empty;

            if (Content == null)
                return;

            var textBlocks = FindAllTextBlocks(this);
            var currentIndex = 0;

            Avalonia.Controls.Control? lastContainer = null;
            LayoutContext? lastContext = null;

            foreach (var textBlock in textBlocks)
            {
                var text = textBlock.Text ?? string.Empty;
                var currentContext = AnalyzeLayoutContext(textBlock);

                // 判断是否需要添加换行符
                if (lastContext != null && ShouldAddNewline(lastContext, currentContext))
                {
                    _virtualText += "\n";
                    currentIndex += 1;
                }

                var info = new TextBlockSelectionInfo
                {
                    TextBlock = textBlock,
                    Text = text,
                    StartIndex = currentIndex,
                    EndIndex = currentIndex + text.Length
                };

                _textBlocks.Add(info);
                _virtualText += text;
                currentIndex += text.Length;

                lastContext = currentContext;
            }

            UpdateSelectedText();
        }

        // 分析 TextBlock 的布局上下文
        private LayoutContext AnalyzeLayoutContext(TextBlock textBlock)
        {
            var context = new LayoutContext();

            // 向上遍历父元素
            var current = textBlock.Parent as Avalonia.Controls.Control;
            var depth = 0;

            while (current != null && depth < 10) // 限制深度防止无限循环
            {
                // 检查是否在水平布局中
                if (current is StackPanel sp && sp.Orientation == Orientation.Horizontal)
                {
                    context.HorizontalContainers.Add(sp);

                    // 检查是否是列表项
                    if (IsListItemContainer(sp))
                    {
                        context.ListItem = sp;
                    }
                }

                // 检查是否在垂直布局中
                if (current is StackPanel vsp && vsp.Orientation == Orientation.Vertical)
                {
                    context.VerticalContainers.Add(vsp);
                }

                // 检查是否在 Grid 中（表格）
                if (current is Grid grid)
                {
                    context.GridContainer = grid;
                    context.GridRow = Grid.GetRow(GetChildInGrid(textBlock, grid) ?? textBlock);
                    context.GridColumn = Grid.GetColumn(GetChildInGrid(textBlock, grid) ?? textBlock);
                }

                // 检查是否在 Border 中（可能是引用块、代码块等）
                if (current is Border border)
                {
                    context.Borders.Add(border);
                }

                current = current.Parent as Avalonia.Controls.Control;
                depth++;
            }

            return context;
        }

        // 获取在 Grid 中的直接子元素
        private Avalonia.Controls.Control? GetChildInGrid(Avalonia.Controls.Control control, Grid grid)
        {
            var current = control as Avalonia.Controls.Control;
            while (current != null && current.Parent != grid)
            {
                current = current.Parent as Avalonia.Controls.Control;
            }
            return current;
        }

        // 判断是否是列表项容器（包含项目符号）
        private bool IsListItemContainer(StackPanel panel)
        {
            if (panel.Children.Count == 0)
                return false;

            var firstChild = panel.Children[0];
            if (firstChild is TextBlock tb)
            {
                var text = tb.Text ?? "";
                // 检查是否是项目符号或数字列表标记
                return text.StartsWith("• ") ||
                       text.StartsWith("- ") ||
                       text.StartsWith("* ") ||
                       text.StartsWith("+ ") ||
                       System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\s");
            }

            return false;
        }

        // 判断两个布局上下文之间是否需要换行
        private bool ShouldAddNewline(LayoutContext last, LayoutContext current)
        {
            // 1. 不同的列表项之间换行
            if (last.ListItem != null && current.ListItem != null && last.ListItem != current.ListItem)
            {
                return true;
            }

            // 2. 从列表项到非列表项，或反之
            if ((last.ListItem != null) != (current.ListItem != null))
            {
                return true;
            }

            // 3. 不同的表格行之间换行
            if (last.GridContainer != null && current.GridContainer != null &&
                last.GridContainer == current.GridContainer && last.GridRow != current.GridRow)
            {
                return true;
            }

            // 4. 不同的垂直容器之间换行（如不同段落）
            if (last.VerticalContainers.Count > 0 && current.VerticalContainers.Count > 0)
            {
                // 找到最深的共同垂直容器
                var lastDeepest = last.VerticalContainers.FirstOrDefault();
                var currentDeepest = current.VerticalContainers.FirstOrDefault();

                // 如果在不同的垂直容器直接子元素中
                if (lastDeepest != null && currentDeepest != null)
                {
                    var lastInDeepest = GetDirectChildInContainer(last.HorizontalContainers.FirstOrDefault(), lastDeepest);
                    var currentInDeepest = GetDirectChildInContainer(current.HorizontalContainers.FirstOrDefault(), currentDeepest);

                    if (lastInDeepest != currentInDeepest && lastInDeepest != null && currentInDeepest != null)
                    {
                        return true;
                    }
                }
            }

            // 5. 不同的水平容器，且它们的父容器不同（如不同段落中的不同行内元素组）
            if (last.HorizontalContainers.Count > 0 && current.HorizontalContainers.Count > 0)
            {
                var lastHorizontal = last.HorizontalContainers.FirstOrDefault();
                var currentHorizontal = current.HorizontalContainers.FirstOrDefault();

                if (lastHorizontal != currentHorizontal && lastHorizontal != null && currentHorizontal != null)
                {
                    // 检查它们的父容器是否不同
                    var lastParent = lastHorizontal.Parent;
                    var currentParent = currentHorizontal.Parent;

                    if (lastParent != currentParent)
                    {
                        return true;
                    }
                }
            }

            // 6. 从一个 Border 到另一个 Border（如代码块、引用块之间）
            if (last.Borders.Count > 0 && current.Borders.Count > 0)
            {
                var lastBorder = last.Borders.FirstOrDefault();
                var currentBorder = current.Borders.FirstOrDefault();

                if (lastBorder != currentBorder)
                {
                    return true;
                }
            }

            // 7. 从 Border 内到 Border 外，或反之
            if (last.Borders.Count != current.Borders.Count && Math.Abs(last.Borders.Count - current.Borders.Count) > 0)
            {
                return true;
            }

            return false;
        }

        // 获取在容器中的直接子元素
        private Avalonia.Controls.Control? GetDirectChildInContainer(Avalonia.Controls.Control? control, Avalonia.Controls.Control container)
        {
            if (control == null)
                return null;

            var current = control;
            while (current != null && current.Parent != container)
            {
                current = current.Parent as Avalonia.Controls.Control;
            }
            return current;
        }

        private List<TextBlock> FindAllTextBlocks(Visual visual)
        {
            var result = new List<TextBlock>();
            FindTextBlocksRecursive(visual, result);

            result = result
                .Select(tb => new
                {
                    TextBlock = tb,
                    Position = tb.TransformToVisual(this)?.Transform(default) ?? default,
                    Index = GetSelectionIndex(tb)
                })
                .OrderBy(x => x.Index >= 0 ? x.Index : int.MaxValue)
                .ThenBy(x => x.Position.Y)
                .ThenBy(x => x.Position.X)
                .Select(x => x.TextBlock)
                .ToList();

            return result;
        }

        private void FindTextBlocksRecursive(Visual visual, List<TextBlock> result)
        {
            if (visual is Avalonia.Controls.Control control && !GetIsSelectable(control))
                return;

            if (visual is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
            {
                result.Add(textBlock);
                return;
            }

            var children = visual.GetVisualChildren();
            foreach (var child in children)
            {
                if (child is Visual childVisual)
                {
                    FindTextBlocksRecursive(childVisual, result);
                }
            }
        }

        private int GetTextIndexAtPoint(Point point)
        {
            if (_textBlocks.Count == 0)
                return 0;

            // 首先尝试精确匹配：点击位置在某个 TextBlock 内部
            foreach (var info in _textBlocks)
            {
                var textBlock = info.TextBlock;
                var transform = this.TransformToVisual(textBlock);

                if (transform == null)
                    continue;

                var localPoint = transform.Value.Transform(point);
                localPoint -= new Point(textBlock.Padding.Left, textBlock.Padding.Top);

                var bounds = new Rect(textBlock.Bounds.Size);
                if (bounds.Contains(localPoint))
                {
                    if (textBlock.TextLayout != null)
                    {
                        var hit = textBlock.TextLayout.HitTestPoint(localPoint);
                        return info.StartIndex + hit.TextPosition;
                    }
                }
            }

            // 如果没有精确匹配，找到最近的 TextBlock
            TextBlockSelectionInfo? nearestBlock = null;
            double minDistance = double.MaxValue;

            foreach (var info in _textBlocks)
            {
                var textBlock = info.TextBlock;
                var transform = textBlock.TransformToVisual(this);

                if (transform == null)
                    continue;

                // 获取 TextBlock 在容器中的位置
                var blockPosition = transform.Value.Transform(new Point(0, 0));
                var blockBounds = new Rect(blockPosition, textBlock.Bounds.Size);

                // 计算点到 TextBlock 的距离
                double distance;

                if (point.Y < blockBounds.Top)
                {
                    // 点在 TextBlock 上方
                    if (point.X < blockBounds.Left)
                        distance = Math.Sqrt(Math.Pow(blockBounds.Left - point.X, 2) + Math.Pow(blockBounds.Top - point.Y, 2));
                    else if (point.X > blockBounds.Right)
                        distance = Math.Sqrt(Math.Pow(point.X - blockBounds.Right, 2) + Math.Pow(blockBounds.Top - point.Y, 2));
                    else
                        distance = blockBounds.Top - point.Y;
                }
                else if (point.Y > blockBounds.Bottom)
                {
                    // 点在 TextBlock 下方
                    if (point.X < blockBounds.Left)
                        distance = Math.Sqrt(Math.Pow(blockBounds.Left - point.X, 2) + Math.Pow(point.Y - blockBounds.Bottom, 2));
                    else if (point.X > blockBounds.Right)
                        distance = Math.Sqrt(Math.Pow(point.X - blockBounds.Right, 2) + Math.Pow(point.Y - blockBounds.Bottom, 2));
                    else
                        distance = point.Y - blockBounds.Bottom;
                }
                else
                {
                    // 点在 TextBlock 的垂直范围内
                    if (point.X < blockBounds.Left)
                        distance = blockBounds.Left - point.X;
                    else if (point.X > blockBounds.Right)
                        distance = point.X - blockBounds.Right;
                    else
                        distance = 0; // 这种情况应该在精确匹配中已经处理了
                }

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestBlock = info;
                }
            }

            // 找到最近的 TextBlock 后，判断应该返回开始还是结束位置
            if (nearestBlock != null)
            {
                var textBlock = nearestBlock.TextBlock;
                var transform = textBlock.TransformToVisual(this);

                if (transform != null)
                {
                    var blockPosition = transform.Value.Transform(new Point(0, 0));
                    var blockBounds = new Rect(blockPosition, textBlock.Bounds.Size);
                    var blockCenter = new Point(
                        blockBounds.Left + blockBounds.Width / 2,
                        blockBounds.Top + blockBounds.Height / 2
                    );

                    // 如果点在 TextBlock 的左侧或上方，返回开始位置
                    // 如果点在 TextBlock 的右侧或下方，返回结束位置
                    if (point.Y < blockCenter.Y || (point.Y == blockCenter.Y && point.X < blockCenter.X))
                    {
                        return nearestBlock.StartIndex;
                    }
                    else
                    {
                        return nearestBlock.EndIndex;
                    }
                }
            }

            // 最后的fallback：如果点在容器上半部分返回0，下半部分返回末尾
            return point.Y < (Bounds.Height / 2) ? 0 : _virtualText.Length;
        }

        private bool IsPointerOverSelectableText(Point point)
        {
            foreach (var info in _textBlocks)
            {
                var textBlock = info.TextBlock;

                if (textBlock.TextLayout == null || string.IsNullOrEmpty(textBlock.Text))
                    continue;

                var transform = this.TransformToVisual(textBlock);
                if (transform == null)
                    continue;

                var localPoint = transform.Value.Transform(point);
                localPoint -= new Point(textBlock.Padding.Left, textBlock.Padding.Top);

                // 检查是否在 TextBlock 的边界内
                var bounds = new Rect(textBlock.Bounds.Size);
                if (!bounds.Contains(localPoint))
                    continue;

                // 使用 TextLayout 精确检测是否在文字区域上
                var textLayout = textBlock.TextLayout;

                // 首先检查是否在整体文本区域内
                if (localPoint.X < 0 || localPoint.X > textLayout.WidthIncludingTrailingWhitespace ||
                    localPoint.Y < 0 || localPoint.Y > textLayout.Height)
                    continue;

                // 使用 HitTest 检查是否真的在字符上
                var hit = textLayout.HitTestPoint(localPoint);

                // 检查命中位置是否在有效字符范围内
                if (hit.TextPosition >= 0 && hit.TextPosition <= textBlock.Text.Length)
                {
                    // 改进的检测逻辑：检查当前位置和前一个位置的字符
                    if (hit.TextPosition < textBlock.Text.Length)
                    {
                        // 检查当前字符
                        var charRects = textLayout.HitTestTextRange(hit.TextPosition, 1);
                        foreach (var charRect in charRects)
                        {
                            var expandedRect = charRect.Inflate(1);
                            if (expandedRect.Contains(localPoint))
                            {
                                return true;
                            }
                        }
                    }

                    // 检查前一个字符（处理字符后半部分的情况）
                    if (hit.TextPosition > 0)
                    {
                        var prevCharRects = textLayout.HitTestTextRange(hit.TextPosition - 1, 1);
                        foreach (var charRect in prevCharRects)
                        {
                            var expandedRect = charRect.Inflate(1);
                            if (expandedRect.Contains(localPoint))
                            {
                                return true;
                            }
                        }
                    }

                    // 处理文本末尾的情况
                    if (hit.TextPosition == textBlock.Text.Length && hit.TextPosition > 0)
                    {
                        var lastCharRects = textLayout.HitTestTextRange(hit.TextPosition - 1, 1);
                        if (lastCharRects.Any())
                        {
                            var lastCharRect = lastCharRects.First();
                            if (localPoint.X <= lastCharRect.Right + 2)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private void AttachScrollViewerEvents()
        {
            _scrollViewer = this.FindDescendantOfType<ScrollViewer>();

            if (_scrollViewer != null)
            {
                _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
            }
        }

        private void DetachScrollViewerEvents()
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
                _scrollViewer = null;
            }
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == nameof(ScrollViewer.Offset))
            {
                _overlayLayer?.InvalidateVisual();
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);

            // 确保 overlay 覆盖整个容器
            if (_overlayLayer != null)
            {
                _overlayLayer.Arrange(new Rect(finalSize));
            }

            return result;
        }

        // 布局上下文类，记录 TextBlock 的布局信息
        private class LayoutContext
        {
            public List<StackPanel> HorizontalContainers { get; set; } = new();
            public List<StackPanel> VerticalContainers { get; set; } = new();
            public StackPanel? ListItem { get; set; }
            public Grid? GridContainer { get; set; }
            public int GridRow { get; set; } = -1;
            public int GridColumn { get; set; } = -1;
            public List<Border> Borders { get; set; } = new();
        }

        // 覆盖层控件，用于在所有内容之上绘制选择高亮
        private class OverlayLayer : Avalonia.Controls.Control
        {
            private readonly SelectableTextContainer _container;

            public OverlayLayer(SelectableTextContainer container)
            {
                _container = container;
                IsHitTestVisible = false;
                ZIndex = int.MaxValue; // 确保在最上层
            }

            public override void Render(DrawingContext context)
            {
                var brush = _container.SelectionBrush;
                if (brush == null)
                    return;

                var start = Math.Min(_container.SelectionStart, _container.SelectionEnd);
                var end = Math.Max(_container.SelectionStart, _container.SelectionEnd);

                if (start == end)
                    return;

                foreach (var info in _container._textBlocks)
                {
                    if (end <= info.StartIndex || start >= info.EndIndex)
                        continue;

                    var localStart = Math.Max(0, start - info.StartIndex);
                    var localEnd = Math.Min(info.Text.Length, end - info.StartIndex);

                    if (localStart >= localEnd)
                        continue;

                    var textBlock = info.TextBlock;

                    if (!textBlock.IsVisible || textBlock.TextLayout == null)
                        continue;

                    var rects = textBlock.TextLayout.HitTestTextRange(localStart, localEnd - localStart);
                    var transform = textBlock.TransformToVisual(_container);

                    if (transform == null)
                        continue;

                    var offset = transform.Value.Transform(new Point(textBlock.Padding.Left, textBlock.Padding.Top));

                    using (context.PushTransform(Matrix.CreateTranslation(offset)))
                    {
                        foreach (var rect in rects)
                        {
                            context.FillRectangle(brush, rect);
                        }
                    }
                }
            }
        }
    }

    internal class TextBlockSelectionInfo
    {
        public TextBlock TextBlock { get; set; } = null!;
        public string Text { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
    }
}