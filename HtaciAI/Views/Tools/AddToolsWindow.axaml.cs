using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views.Tools;

/// <summary>
/// 「添加工具」对话框：把已经有归属的工具（或还没归属的工具）勾进当前工具集。
/// 只展示<b>还没进这个集合</b>的工具——已经在里面的没必要再列一遍。
/// </summary>
public partial class AddToolsWindow : Window
{
    /// <summary>用户勾选、待加入当前集合的工具 id。</summary>
    public List<string> SelectedToolIds { get; } = new();

    /// <summary>无参构造只为满足 XAML 加载器/设计器（缺了会报 AVLN3001），正常调用走带参那个。</summary>
    public AddToolsWindow() : this(new Toolset(), Array.Empty<ToolDefinition>()) { }

    public AddToolsWindow(Toolset target, IReadOnlyList<ToolDefinition> candidates)
    {
        InitializeComponent();

        SubtitleText.Text = $"勾选要放进「{target.Name}」的工具，可以多选。已在集合里的工具不会出现。";

        if (candidates.Count == 0)
        {
            ToolPanel.Children.Add(new TextBlock
            {
                Text = "没有可添加的工具：所有工具都已经在这个集合里了",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var tool in candidates)
        {
            var id = tool.Id;
            var cb = new CheckBox
            {
                Content = $"{tool.Name} · {SourceLabel(tool)}",
                Tag = id,
            };
            ToolTip.SetTip(cb, string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : tool.Description);
            ToolPanel.Children.Add(cb);
        }
    }

    private static string SourceLabel(ToolDefinition tool) => tool.Source switch
    {
        ToolSource.Builtin => "内置",
        ToolSource.Mcp => "MCP",
        _ => tool.Runtime == Services.ScriptRuntimes.ScriptRuntimeKind.Node ? "Node.js" : "Python",
    };

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        SelectedToolIds.Clear();
        foreach (var child in ToolPanel.Children)
            if (child is CheckBox { IsChecked: true, Tag: string id })
                SelectedToolIds.Add(id);

        Close();
    }
}
