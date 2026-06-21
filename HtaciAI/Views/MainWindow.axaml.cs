using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace HtaciAI.Views;

public partial class MainWindow : Window
{
    private int _selectedIndex;
    private readonly List<Control> _tabContents = new();
    private readonly List<Border> _tabHeaders = new();
    private readonly List<TextBlock> _tabTitleBlocks = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var initialPage = new NewTabPage();
        initialPage.SmartChatSelected += OnSmartChatSelected;
        AddTab("新标签页", initialPage);
    }

    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        var newTabPage = new NewTabPage();
        newTabPage.SmartChatSelected += OnSmartChatSelected;
        AddTab("新标签页", newTabPage);
    }

    private void OnSmartChatSelected(object? sender, System.EventArgs e)
    {
        if (sender is NewTabPage newTabPage)
        {
            newTabPage.SmartChatSelected -= OnSmartChatSelected;

            var index = _tabContents.IndexOf(newTabPage);
            if (index < 0) return;

            _tabContents[index] = new ChatPage();
            _tabTitleBlocks[index].Text = "智能对话";

            if (index == GetSelectedIndex())
                TabContent.Content = _tabContents[index];
        }
    }

    /// <summary>
    /// 添加新标签页
    /// </summary>
    /// <param name="title"></param>
    /// <param name="content"></param>
    private void AddTab(string title, Control content)
    {
        var index = _tabHeaders.Count;
        var header = CreateTabHeader(title, index);
        _tabHeaders.Add(header);
        _tabContents.Add(content);

        TabHeaderPanel.Children.Insert(TabHeaderPanel.Children.Count - 1, header);
        SelectTab(index);
    }
    /// <summary>
    /// 创建标签页头部
    /// </summary>
    /// <param name="title"></param>
    /// <param name="index"></param>
    /// <returns></returns>
    private Border CreateTabHeader(string title, int index)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0),
            FontSize = 12,
        };
        _tabTitleBlocks.Add(titleBlock);

        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\u00d7",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, -1, 0, 0)
            },
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            Width = 0,
            Height = 16,
            CornerRadius = new Avalonia.CornerRadius(12),
            Padding = new Avalonia.Thickness(0),
            Margin = new Avalonia.Thickness(0),
            Tag = index
        };
        closeBtn.Click += OnTabCloseClick;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { titleBlock, closeBtn }
        };
        Grid.SetColumn(closeBtn, 1);

        var border = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Avalonia.Thickness(5),
            CornerRadius = new Avalonia.CornerRadius(10),
            Tag = index,
            Child = headerGrid,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            BorderBrush = new SolidColorBrush(Color.Parse("#D9DDE3")),
            BorderThickness = new Avalonia.Thickness(1),

            // 添加小阴影效果
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,          // 水平偏移
                OffsetY = 1,          // 垂直偏移（向下 2 像素）
                Blur = 2,             // 模糊半径
                Spread = 0,           // 扩散大小
                Color = Color.FromArgb(30, 0, 0, 0)  // 半透明黑色
            })
        };

        border.PointerEntered += (s, e) =>
        {
            closeBtn.Width = 16;
            closeBtn.Margin = new Avalonia.Thickness(6, 0, 6, 0);
            titleBlock.Margin = new Avalonia.Thickness(10, 0, 2, 0);
            closeBtn.Background = Brushes.Transparent;
            closeBtn.Foreground = Brushes.Gray;
        };

        border.PointerExited += (s, e) =>
        {
            closeBtn.Width = 0;
            closeBtn.Margin = new Avalonia.Thickness(0);
            titleBlock.Margin = new Avalonia.Thickness(10, 0);
            closeBtn.Background = Brushes.Transparent;
            closeBtn.Foreground = Brushes.Gray;
        };

        border.PointerPressed += OnTabHeaderClick;
        return border;
    }

    /// <summary>
    /// 关闭标签页
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
        {
            if (_tabHeaders.Count <= 1)
            {
                this.Close();
                return;
            }

            RemoveTab(index);
            e.Handled = true;
        }
    }

    private void RemoveTab(int index)
    {
        _tabHeaders.RemoveAt(index);
        _tabContents.RemoveAt(index);
        _tabTitleBlocks.RemoveAt(index);
        TabHeaderPanel.Children.RemoveAt(index);

        for (int i = 0; i < _tabHeaders.Count; i++)
            _tabHeaders[i].Tag = i;

        if (_selectedIndex > index)
            _selectedIndex--;
        else if (_selectedIndex >= _tabContents.Count)
            _selectedIndex = _tabContents.Count - 1;

        SelectTab(_selectedIndex);
    }

    private void OnTabHeaderClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border header && header.Tag is int index)
            SelectTab(index);
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabHeaders.Count) return;

        for (int i = 0; i < _tabHeaders.Count; i++)
        {
            _tabHeaders[i].Background = i == index
                ? new SolidColorBrush(Color.Parse("#FFFFFF"))
                : Brushes.Transparent;
        }

        _selectedIndex = index;
        TabContent.Content = _tabContents[index];
    }

    private int GetSelectedIndex() => _selectedIndex;

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void Form_OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) this.BeginMoveDrag(e);
    }
}
