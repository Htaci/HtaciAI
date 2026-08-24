using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Diagnostics;

namespace HtaciAI.Controls;

public partial class ExScrollViewer : UserControl
{
    private ScrollViewer? _scrollViewer;
    private double _targetOffset = 0;
    private double _currentOffset = 0;
    private double _velocity = 0;
    private const double SmoothScrollSpeed = 0.18;
    private const double ScrollMultiplier = 80;
    private const double Acceleration = 1.5;
    private const double Deceleration = 0.92;
    private const double MinVelocity = 0.1;
    private DateTime _lastScrollTime = DateTime.MinValue;
    private readonly object _scrollLock = new object();
    private DispatcherTimer? _animationTimer;
    private bool _isAnimating = false;
    private bool _initialized = false;

    public ExScrollViewer()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 查找内部的 ScrollViewer
        _scrollViewer = this.Content as ScrollViewer ?? this.FindDescendantOfType<ScrollViewer>();

        if (_scrollViewer == null)
        {
            Debug.WriteLine("[ExScrollViewer] Error: No ScrollViewer found in content");
            return;
        }

        // 初始化动画定时器
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 120.0)
        };
        _animationTimer.Tick += OnAnimationTick;

        // 在 ScrollViewer 上监听滚轮事件
        _scrollViewer.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);

        // 监听滚动变化
        _scrollViewer.ScrollChanged += OnScrollChanged;

        // 关键：等待布局完全完成后初始化
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer != null)
            {
                // 强制同步当前偏移量
                _currentOffset = _scrollViewer.Offset.Y;
                _targetOffset = _currentOffset;
                _velocity = 0; // 确保初始速度为0
                _initialized = true;

                //Debug.WriteLine($"[ExScrollViewer] Initialized - Offset: {_currentOffset:F2}, Extent: {_scrollViewer.Extent.Height:F2}, Viewport: {_scrollViewer.Viewport.Height:F2}");
            }
        }, DispatcherPriority.Loaded);

        //Debug.WriteLine($"[ExScrollViewer] Loaded - ScrollViewer found");

        DumpContentTree();
    }

    private void DumpContentTree()
    {
        if (_scrollViewer?.Content is not Panel panel) return;

        //Debug.WriteLine($"[DumpContentTree] ScrollViewer.Content is Panel with {panel.Children.Count} children");
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            //Debug.WriteLine($"[DumpContentTree]   Child[{i}] = {child.GetType().Name}, Name={child.Name ?? "null"}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_scrollViewer != null)
        {
            _scrollViewer.RemoveHandler(PointerWheelChangedEvent, OnPointerWheelChanged);
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }

        StopAnimation();

        if (_animationTimer != null)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= OnAnimationTick;
            _animationTimer = null;
        }

        _initialized = false;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 仅在需要时更新初始化状态
        if (_scrollViewer != null && !_initialized && _scrollViewer.Extent.Height > 0)
        {
            //Debug.WriteLine($"[ExScrollViewer] First ScrollChanged - Extent: {_scrollViewer.Extent.Height:F2}, Viewport: {_scrollViewer.Viewport.Height:F2}");
            _initialized = true;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_scrollViewer == null) return;

        // 只处理无修饰键的滚轮
        if (e.KeyModifiers != KeyModifiers.None) return;

        //Debug.WriteLine($"[ExScrollViewer] Wheel detected, Delta={e.Delta.Y}, Handling it");

        // 阻止 ScrollViewer 默认滚动行为
        e.Handled = true;

        lock (_scrollLock)
        {
            var now = DateTime.Now;
            var timeSinceLastScroll = (now - _lastScrollTime).TotalMilliseconds;
            _lastScrollTime = now;

            // 关键：每次滚动都重新获取当前偏移量，确保与 ScrollViewer 同步
            _currentOffset = _scrollViewer.Offset.Y;

            var scrollDelta = -e.Delta.Y * ScrollMultiplier;
            var newTargetOffset = _targetOffset + scrollDelta;

            var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            newTargetOffset = Math.Max(0, Math.Min(newTargetOffset, maxOffset));

            var currentDirection = Math.Sign(scrollDelta);
            var velocityDirection = Math.Sign(_velocity);

            //Debug.WriteLine($"[ExScrollViewer] Wheel logic - scrollDelta={scrollDelta:F2}, newTarget={newTargetOffset:F2}, maxOffset={maxOffset:F2}, currentOffset={_currentOffset:F2}");

            if (timeSinceLastScroll < 200 && currentDirection == velocityDirection && currentDirection != 0)
            {
                _velocity = _velocity + scrollDelta * Acceleration;
                _targetOffset = newTargetOffset;
            }
            else if (currentDirection != 0 && velocityDirection != 0 && currentDirection != velocityDirection)
            {
                _velocity = scrollDelta;
                _targetOffset = newTargetOffset;
                _currentOffset = _scrollViewer.Offset.Y;
            }
            else
            {
                _velocity = scrollDelta;
                _targetOffset = newTargetOffset;
            }

            //Debug.WriteLine($"[ExScrollViewer] After processing - _currentOffset={_currentOffset:F2}, _targetOffset={_targetOffset:F2}, _velocity={_velocity:F2}");

            StartAnimation();
        }
    }

    private void StartAnimation()
    {
        if (_isAnimating || _animationTimer == null) return;

        _isAnimating = true;
        _animationTimer.Start();
        //Debug.WriteLine("[ExScrollViewer] Animation started");
    }

    private void StopAnimation()
    {
        if (!_isAnimating || _animationTimer == null) return;

        _isAnimating = false;
        _animationTimer.Stop();
        //Debug.WriteLine("[ExScrollViewer] Animation stopped");
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null)
        {
            StopAnimation();
            return;
        }

        lock (_scrollLock)
        {
            var diff = _targetOffset - _currentOffset;
            _velocity *= Deceleration;

            //Debug.WriteLine($"[ExScrollViewer] Tick - diff={diff:F2}, vel={_velocity:F2}, cur={_currentOffset:F2}, tgt={_targetOffset:F2}");

            // 如果已经很接近目标且速度很小，停止动画
            if (Math.Abs(diff) < 0.5 && Math.Abs(_velocity) < MinVelocity)
            {
                _currentOffset = _targetOffset;
                _velocity = 0;
                _scrollViewer.Offset = new Vector(0, _currentOffset);
                StopAnimation();
                Debug.WriteLine("[ExScrollViewer] Animation complete");
                return;
            }

            // 平滑插值移动
            _currentOffset += diff * SmoothScrollSpeed;

            // 限制在有效范围内
            var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            _currentOffset = Math.Max(0, Math.Min(_currentOffset, maxOffset));

            _scrollViewer.Offset = new Vector(0, _currentOffset);
            //Debug.WriteLine($"[ExScrollViewer] Tick set offset={_currentOffset:F2}");
        }
    }
}
