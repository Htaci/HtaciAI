using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Avalonia.Styling;

namespace HtaciAI.Mobile.Views;

public partial class MainView : UserControl
{
    public string Greeting => "Welcome to Htaci2";

    private readonly TranslateTransform _menuTransform;
    private readonly TranslateTransform _contentTransform;

    public MainView()
    {
        InitializeComponent();
        DataContext = this;
        LoadSvgIcon();

        _menuTransform = (TranslateTransform)MenuGrid.RenderTransform!;
        _contentTransform = (TranslateTransform)ContentGrid.RenderTransform!;
    }

    private void LoadSvgIcon()
    {
        var svgSource = SvgSource.Load(
            "avares://HtaciAI.Mobile/Assets/Controlicon/导航菜单.svg", null);
        NavigateIcon.Source = new SvgImage { Source = svgSource };
    }

    private async void OnNavigateButtonClick(object? sender, RoutedEventArgs e)
    {
        var width = Bounds.Width;
        if (width <= 0) return;

        var duration = TimeSpan.FromMilliseconds(350);

        // 菜单页从左滑入
        var menuAnim = new Animation
        {
            Duration = duration,
            Easing = new CubicEaseInOut()
        };
        menuAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(TranslateTransform.XProperty, -width) }
        });
        menuAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.XProperty, 0d) }
        });

        // 内容页向右滑出
        var contentAnim = new Animation
        {
            Duration = duration,
            Easing = new CubicEaseInOut()
        };
        contentAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(TranslateTransform.XProperty, 0d) }
        });
        contentAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.XProperty, width) }
        });

        await Task.WhenAll(
            menuAnim.RunAsync(_menuTransform),
            contentAnim.RunAsync(_contentTransform)
        );
    }

    private async void OnBackButtonClick(object? sender, RoutedEventArgs e)
    {
        var width = Bounds.Width;
        if (width <= 0) return;

        var duration = TimeSpan.FromMilliseconds(350);

        // 菜单页向左滑出
        var menuAnim = new Animation
        {
            Duration = duration,
            Easing = new CubicEaseInOut()
        };
        menuAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(TranslateTransform.XProperty, 0d) }
        });
        menuAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.XProperty, -width) }
        });

        // 内容页从右边滑回
        var contentAnim = new Animation
        {
            Duration = duration,
            Easing = new CubicEaseInOut()
        };
        contentAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Setter(TranslateTransform.XProperty, width) }
        });
        contentAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.XProperty, 0d) }
        });

        await Task.WhenAll(
            menuAnim.RunAsync(_menuTransform),
            contentAnim.RunAsync(_contentTransform)
        );
    }
}
