using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace HtaciAI.Views;

public partial class NewChatView : UserControl
{
    public event EventHandler<string>? SendRequested;

    public NewChatView()
    {
        InitializeComponent();
        this.Focusable = true;
    }

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            SendRequested?.Invoke(this, text);
            InputBox.Text = "";
        }
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点击输入框内部时不抢焦点
        if (e.Source is Visual src && src.GetSelfAndVisualAncestors().Contains(InputBox))
            return;

        this.Focus();
    }
}
