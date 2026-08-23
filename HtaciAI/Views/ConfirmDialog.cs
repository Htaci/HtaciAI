using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 通用的确认对话框（程序化构建）：显示消息与「取消 / 确认」按钮。
/// 供删除操作等需要用户二次确认的流程使用。
/// </summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string message, string title = "确认")
    {
        var messageBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Text = message,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 48, 12, 8),
        };

        //var cancelBtn = BuildButton("取消", "#FFFFFF", "#374151", new SolidColorBrush(Color.Parse("#E5E7EB")));
        //var confirmBtn = BuildButton("确认", "#FFFFFF", "#FFFFFF", new SolidColorBrush(Color.Parse("#DC2626")));
        var cancelBtn = new Button
        {
            Content = new TextBlock { Text = "取消", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#374151")) },
            MinWidth = 60,
            Height = 30,
            Padding = new Thickness(0, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var confirmBtn = new Button
        {
            Content = new TextBlock { Text = "确认", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")) },
            MinWidth = 60,
            Height = 30,
            Padding = new Thickness(0, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#DC2626")),
            BorderBrush = new SolidColorBrush(Color.Parse("#DC2626")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };


        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { cancelBtn, confirmBtn },
            Margin = new Thickness(12, 8, 12, 12)
        };

        var dlg = new Window
        {
            Title = title,
            Width = 350,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            // 与主窗口保持一致：去掉系统标题栏，客户区扩展到装饰区，由下方自绘标题栏承担拖拽
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            WindowDecorations = WindowDecorations.BorderOnly,
        };

        // 自绘标题栏（系统已去掉标题栏），显示窗口标题并支持拖拽
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleBar = new Border
        {
            Height = 40,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            Child = titleText,
            VerticalAlignment = VerticalAlignment.Top,
        };
        titleBar.PointerPressed += (_, e) => dlg.BeginMoveDrag(e);

        //var content = new StackPanel
        //{
        //    Spacing = 20,
        //    Margin = new Thickness(8, 8, 8,8),
        //    Children = { messageBlock, buttons },
        //};

        // 标题栏置顶，其余内容填满剩余区域
        var root = new DockPanel();
        var grid = new Grid();
        //DockPanel.SetDock(titleBar, Dock.Top);
        //root.Children.Add(titleBar);
        //root.Children.Add(messageBlock);
        //root.Children.Add(buttons);
        grid.Children.Add(titleBar);
        grid.Children.Add(messageBlock);
        grid.Children.Add(buttons);
        dlg.Content = grid;

        bool confirmed = false;
        cancelBtn.Click += (_, _) => dlg.Close();
        confirmBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();

        return confirmed;
    }

    private static Button BuildButton(string text, string fg, string accent, Brush bg)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse(fg)) },
            MinWidth = 76,
            Height = 34,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(8),
            Background = bg,
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        btn.PointerEntered += (_, _) => btn.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        btn.PointerExited += (_, _) => btn.Background = bg;
        return btn;
    }

    /// <summary>
    /// 工具审批确认框：展示工具名、危险等级与入参，由「允许执行 / 拒绝」决定是否放行。
    /// </summary>
    public static async Task<bool> ShowToolApprovalAsync(Window? owner, ToolDefinition tool, ChatToolCall call)
    {
        // 标题：工具名 + 等级
        var levelColor = tool.DangerLevel switch
        {
            ToolDangerLevel.Safe => "#16A34A",
            ToolDangerLevel.Risk => "#D97706",
            _ => "#DC2626",
        };
        var levelText = tool.DangerLevel switch
        {
            ToolDangerLevel.Safe => "安全",
            ToolDangerLevel.Risk => "风险",
            _ => "危险",
        };
        var level = new Border
        {
            Background = new SolidColorBrush(Color.Parse(levelColor)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = levelText, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")) },
        };
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = tool.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")) }, level },
        };

        var inputBlock = new TextBlock
        {
            Text = FormatJson(call.Arguments),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#333333")),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(12, 10, 12, 4),
        };

        var allowBtn = new Button
        {
            Content = new TextBlock { Text = "允许执行", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")) },
            MinWidth = 76,
            Height = 30,
            Padding = new Thickness(0, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#4A90D9")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4A90D9")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        var denyBtn = new Button
        {
            Content = new TextBlock { Text = "拒绝", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#374151")) },
            MinWidth = 76,
            Height = 30,
            Padding = new Thickness(0, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { denyBtn, allowBtn },
            Margin = new Thickness(12, 8, 12, 12),
        };

        var dlg = new Window
        {
            Title = "工具权限确认",
            Width = 380,
            Height = 230,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            WindowDecorations = WindowDecorations.BorderOnly,
        };

        var titleText = new TextBlock
        {
            Text = "工具权限确认",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleBar = new Border
        {
            Height = 40,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            Child = titleText,
            VerticalAlignment = VerticalAlignment.Top,
        };
        titleBar.PointerPressed += (_, e) => dlg.BeginMoveDrag(e);

        var grid = new Grid();
        grid.Children.Add(titleBar);
        grid.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 44, 12, 0),
            Spacing = 6,
            Children = { titleRow, inputBlock },
        });
        grid.Children.Add(buttons);
        dlg.Content = grid;

        bool allowed = false;
        allowBtn.Click += (_, _) => { allowed = true; dlg.Close(); };
        denyBtn.Click += (_, _) => dlg.Close();

        if (owner is not null)
            await dlg.ShowDialog(owner);
        else
            dlg.Show();

        return allowed;
    }

    private static string FormatJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(无入参)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
