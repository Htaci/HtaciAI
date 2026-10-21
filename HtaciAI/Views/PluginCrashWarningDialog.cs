using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services.Plugins;

namespace HtaciAI.Views;

/// <summary>
/// 「该插件疑似导致过崩溃」的启用前确认。
///
/// <b>为什么不用 <see cref="ConfirmDialog"/></b>：这里必须同时让用户决定「崩溃后是否仍自动加载」，
/// 而那个选项默认<b>不勾</b> —— 勾上等于放弃「下次启动跳过它」这层保护，
/// 用户应该是在读完整段后果说明之后主动去勾，而不是顺手点掉一个已经默认打开的框。
/// </summary>
public static class PluginCrashWarningDialog
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#5A5A72"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    /// <summary>
    /// 返回用户的选择。<paramref name="Enable"/> 为 false 时调用方应保持插件停用。
    /// </summary>
    public static async Task<(bool Enable, bool AutoLoad)> ShowAsync(Window? owner, PluginInfo plugin)
    {
        if (owner is null) return (false, false);

        var autoLoad = new CheckBox
        {
            Content = "崩溃后仍自动加载该插件",
            FontSize = 13,
            IsChecked = false,
        };

        var body = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(20, 8, 20, 16),
            Children =
            {
                new TextBlock
                {
                    Text = $"「{plugin.Name}」上次退出时疑似导致程序崩溃（已记录 {plugin.CrashCount} 次）。",
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "继续启用会重新加载它的代码。如果它再次导致崩溃，程序会直接退出，" +
                           "未保存的对话可能丢失。",
                    FontSize = 12.5,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                autoLoad,
                new TextBlock
                {
                    Text = "不勾选（默认）：崩溃一次就把它标记为暂停，下次启动跳过加载，" +
                           "避免陷入「一启动就崩、连设置页都进不去」的循环。",
                    FontSize = 11.5,
                    Foreground = TextFaint,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 0, 0, 0),
                },
            },
        };

        var cancelBtn = CreateButton("取消", "#374151", new SolidColorBrush(Color.Parse("#F1F3F5")), new SolidColorBrush(Color.Parse("#E5E7EB")));
        var enableBtn = CreateButton("仍然启用", "#FFFFFF", Danger, Danger);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, enableBtn },
            Margin = new Thickness(20, 0, 20, 18),
        };

        var dlg = new Window
        {
            Title = "插件崩溃提醒",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            // 与主窗口一致：去系统标题栏，客户区扩展到装饰区，拖拽由自绘标题栏承担
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            WindowDecorations = WindowDecorations.BorderOnly,
        };

        var titleBar = new Border
        {
            Height = 40,
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = "插件崩溃提醒",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                Margin = new Thickness(20, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        titleBar.PointerPressed += (_, e) => dlg.BeginMoveDrag(e);

        var root = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // 最后一个子元素会填满剩余区域：正文必须放在最后
        root.Children.Add(body);
        dlg.Content = root;

        var enable = false;
        cancelBtn.Click += (_, _) => dlg.Close();
        enableBtn.Click += (_, _) =>
        {
            enable = true;
            dlg.Close();
        };

        await dlg.ShowDialog(owner);

        return (enable, autoLoad.IsChecked == true);
    }

    private static Button CreateButton(string text, string foreground, IBrush background, IBrush border)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse(foreground)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            MinWidth = 76,
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(8),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        return button;
    }
}
