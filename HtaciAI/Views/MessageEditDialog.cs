using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Controls;

namespace HtaciAI.Views;

/// <summary>消息编辑窗口的结局。</summary>
public enum MessageEditResult
{
    /// <summary>取消，什么都不做。</summary>
    Cancel,

    /// <summary>只保存改后的文本，不重新发送。</summary>
    Save,

    /// <summary>保存并重新生成（走重发链路）。</summary>
    Send,
}

/// <summary>
/// 用户消息编辑窗口（程序化构建）：多行文本框 + 「取消 / 保存 / 发送」。
/// 「保存」只写库，「发送」才触发重新生成 —— 用户改完往往还要再看看别的，
/// 不该一按就重发。
/// </summary>
public static class MessageEditDialog
{
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F7F8FA"));
    private static readonly IBrush Border = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush TextFg = new SolidColorBrush(Color.Parse("#374151"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));

    public static async Task<(MessageEditResult Result, string Text)> ShowAsync(
        Window? owner, string title, string initialText)
    {
        var box = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            MinHeight = 140,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        var cancelBtn = BuildButton("取消", Surface, TextFg, Border);
        var saveBtn = BuildButton("保存", Brushes.White, TextFg, Border);
        var sendBtn = BuildButton("发送", Accent, Brushes.White, Accent);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, saveBtn, sendBtn },
            Margin = new Thickness(0, 10, 0, 0),
        };

        // 三行：标题栏 / 输入框 / 按钮。
        // 标题栏必须单独占一行 —— 之前它和输入框同处默认的第 0 行，两者叠在一起，
        // 标题直接压在输入框上（截图里看到的就是这个）。
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16, 0, 16, 14),
        };
        Grid.SetRow(box, 1);
        body.Children.Add(box);
        Grid.SetRow(buttons, 2);
        body.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 300,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Surface,
            // 与主窗口一致：去掉系统标题栏，客户区扩展，下面自绘标题栏承担拖拽
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            WindowDecorations = WindowDecorations.BorderOnly,
            Content = body,
        };

        var titleBar = new Border
        {
            Height = 40,
            Background = Surface,
            Child = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextFg,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        titleBar.PointerPressed += (_, e) => dlg.BeginMoveDrag(e);
        Grid.SetRow(titleBar, 0);
        body.Children.Add(titleBar);

        var result = MessageEditResult.Cancel;

        cancelBtn.Click += (_, _) => dlg.Close();
        saveBtn.Click += (_, _) => { result = MessageEditResult.Save; dlg.Close(); };
        sendBtn.Click += (_, _) => { result = MessageEditResult.Send; dlg.Close(); };

        // 编辑框里 Cmd/Ctrl+Enter 直接发送、Esc 取消：改完不必再去够按钮。
        // 修饰键随平台（macOS 上是 Command），见 InputGestures 的说明。
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && InputGestures.HasPrimaryCommand(e))
            {
                result = MessageEditResult.Send;
                dlg.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dlg.Close();
                e.Handled = true;
            }
        };

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();

        return (result, box.Text ?? "");
    }

    private static Button BuildButton(string text, IBrush bg, IBrush fg, IBrush border)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 13, Foreground = fg },
            MinWidth = 76,
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(8),
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        return btn;
    }
}
