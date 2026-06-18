using Avalonia.Controls;
using Avalonia.Input;

namespace HtaciAI.Views;

public partial class MainWindow : Window
{
    public string Greeting => "Welcome to HtaciAI!";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }


    // 自定义关闭按钮行为
    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        
        this.Close();
    }
    // 自定义窗口拖动行为
    private void Form_OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse) this.BeginMoveDrag(e);
    }
}