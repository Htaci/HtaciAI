using System;
using Avalonia.Controls;

namespace HtaciAI.Views;

public partial class NewTabPage : UserControl
{
    public event EventHandler? SmartChatSelected;

    public NewTabPage()
    {
        InitializeComponent();
    }

    private void OnSmartChatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SmartChatSelected?.Invoke(this, EventArgs.Empty);
    }
}
