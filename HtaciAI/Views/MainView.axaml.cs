using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Svg.Skia;

namespace HtaciAI.Views;

public partial class MainView : UserControl
{
    public string Greeting => "Welcome to Htaci2";

    public MainView()
    {
        InitializeComponent();
        DataContext = this;
        LoadSvgIcon();
    }

    private void LoadSvgIcon()
    {
        var svgSource = SvgSource.Load(
            "avares://HtaciAI/Assets/Controlicon/导航菜单.svg", null);
        NavigateIcon.Source = new SvgImage { Source = svgSource };
    }

    private void OnNavigateButtonClick(object? sender, RoutedEventArgs e)
    {
        // TODO: 导航菜单点击逻辑待实现
    }
}
