using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views.Tools;

/// <summary>
/// 新建工具集对话框：只填名称与描述，落库后注册进 <see cref="ToolRegistry"/>。
/// 工具归属在工具页用「添加工具」维护，这里不涉及。
/// </summary>
public partial class CreateToolsetWindow : Window
{
    /// <summary>创建成功后的工具集（供调用方读取）。</summary>
    public Toolset? Result { get; private set; }

    public CreateToolsetWindow()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("名称不能为空");
            return;
        }

        if (ToolRegistry.Instance.GetToolsets().Any(t => t.Name == name))
        {
            SetError($"已存在同名工具集「{name}」");
            return;
        }

        var toolset = new Toolset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = DescBox.Text?.Trim() ?? "",
        };

        try
        {
            await ToolRepository.CreateToolsetAsync(toolset);
        }
        catch (Exception ex)
        {
            SetError("保存失败：" + ex.Message);
            return;
        }

        ToolRegistry.Instance.RegisterToolset(toolset);
        Result = toolset;
        Close();
    }

    private void SetError(string text)
    {
        ErrorText.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        ErrorText.Text = text;
    }
}
