using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HtaciAI.Views.Settings;

namespace HtaciAI.Views;

public partial class SettingsPage : UserControl
{
    private readonly List<(string Name, Control Page)> _sections = new();
    private int _selectedIndex;

    public SettingsPage()
    {
        InitializeComponent();

        _sections.Add(("模型服务", new ModelServicePage()));
        _sections.Add(("默认配置", new DefaultConfigPage()));
        _sections.Add(("常规设置", new GeneralSettingsPage()));
        _sections.Add(("环境配置", new RuntimeSettingsPage()));
        _sections.Add(("个性化设置", new PersonalizationPage()));
        _sections.Add(("数据管理", new DataManagementPage()));
        _sections.Add(("关于我们", new AboutPage()));

        for (int i = 0; i < _sections.Count; i++)
            NavPanel.Children.Add(CreateNavItem(_sections[i].Name, i));

        SelectSection(0);
    }

    private Border CreateNavItem(string name, int index)
    {
        var text = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(12, 0)
        };

        var item = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = text
        };

        item.PointerEntered += (s, e) =>
        {
            if (index != _selectedIndex)
                item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (s, e) =>
        {
            if (index != _selectedIndex)
                item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (s, e) =>
        {
            SelectSection(index);
            e.Handled = true;
        };

        return item;
    }

    private void SelectSection(int index)
    {
        _selectedIndex = index;

        for (int i = 0; i < NavPanel.Children.Count; i++)
        {
            var item = (Border)NavPanel.Children[i];
            var text = (TextBlock)item.Child!;

            if (i == index)
            {
                item.Background = Brushes.White;
                item.BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
                item.BorderThickness = new Avalonia.Thickness(1);
                text.Foreground = new SolidColorBrush(Color.Parse("#1A1A2E"));
                text.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                item.Background = Brushes.Transparent;
                item.BorderBrush = null;
                item.BorderThickness = new Avalonia.Thickness(0);
                text.Foreground = new SolidColorBrush(Color.Parse("#374151"));
                text.FontWeight = FontWeight.Normal;
            }
        }

        PageHost.Content = _sections[index].Page;
    }
}
