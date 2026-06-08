using Avalonia.Controls;

namespace HtaciAI.Views;

public partial class MainWindow : Window
{
    public string Greeting => "Welcome to HtaciAI!";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }
}