using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 标签多选控件：一组可点击切换的标签（chip），选中态高亮（蓝底白字）。
/// 用于模型能力、思考强度等多选配置。<see cref="Items"/> 为全部可选标签，
/// <see cref="SelectedItems"/> 为当前选中集合（与 <see cref="Items"/> 文本一致），
/// 变化通过 <see cref="SelectionChanged"/> 通知。
/// </summary>
public class TagSelectControl : UserControl
{
    private readonly WrapPanel _panel = new()
    {
        Orientation = Orientation.Horizontal,
    };

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUpdating;

    public TagSelectControl()
    {
        Content = _panel;
    }

    /// <summary>全部可选标签（显示文本）。</summary>
    public static readonly StyledProperty<IEnumerable<string>> ItemsProperty =
        AvaloniaProperty.Register<TagSelectControl, IEnumerable<string>>(
            nameof(Items),
            defaultValue: Array.Empty<string>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<string>());

    public IEnumerable<string> Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>当前选中的标签集合。</summary>
    public static readonly StyledProperty<IEnumerable<string>> SelectedItemsProperty =
        AvaloniaProperty.Register<TagSelectControl, IEnumerable<string>>(
            nameof(SelectedItems),
            defaultValue: Array.Empty<string>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<string>());

    public IEnumerable<string> SelectedItems
    {
        get => GetValue(SelectedItemsProperty);
        set => SetValue(SelectedItemsProperty, value);
    }

    /// <summary>选中集合变化时触发。</summary>
    public event EventHandler? SelectionChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            Rebuild();
        }
        else if (change.Property == SelectedItemsProperty && !_isUpdating)
        {
            _selected.Clear();
            foreach (var item in SelectedItems ?? Array.Empty<string>())
                _selected.Add(item);
            RefreshVisualState();
        }
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var item in Items ?? Array.Empty<string>())
            _panel.Children.Add(CreateChip(item));
        RefreshVisualState();
    }

    private Border CreateChip(string item)
    {
        var border = new Border
        {
            Tag = item,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = item,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        border.PointerPressed += (_, _) => Toggle(item);
        return border;
    }

    private void Toggle(string item)
    {
        if (!_selected.Add(item))
            _selected.Remove(item);
        RefreshVisualState();

        // 回写依赖属性并触发通知；用 _isUpdating 避免 onChange 里重建状态
        _isUpdating = true;
        SetValue(SelectedItemsProperty, _selected.ToList());
        _isUpdating = false;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshVisualState()
    {
        foreach (var child in _panel.Children)
        {
            if (child is not Border border || border.Tag is not string key) continue;

            var active = _selected.Contains(key);
            border.Background = active
                ? new SolidColorBrush(Color.Parse("#4A90D9"))
                : Brushes.Transparent;
            if (border.Child is TextBlock tb)
                tb.Foreground = active
                    ? Brushes.White
                    : new SolidColorBrush(Color.Parse("#374151"));
        }
    }
}
