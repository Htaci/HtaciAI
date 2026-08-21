using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HtaciAI.Data;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views.Tools;

/// <summary>
/// 创建工具对话框：选择脚本 + --describe 自动生成 schema + 归属工具集，
/// 保存后入库并注册到注册表。
/// </summary>
public partial class CreateToolWindow : Window
{
    private readonly List<CheckBox> _toolsetChecks = new();

    /// <summary>保存成功后的工具定义（供调用方读取）。</summary>
    public ToolDefinition? Result { get; private set; }

    public CreateToolWindow()
    {
        InitializeComponent();

        RuntimeCombo.ItemsSource = new List<string> { "Python", "Node.js" };
        RuntimeCombo.SelectedIndex = 0;

        // 归属工具集勾选（默认勾选内置默认集）
        foreach (var ts in ToolRegistry.Instance.GetToolsets())
        {
            var cb = new CheckBox
            {
                Content = ts.Name,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                IsChecked = ts.Id == Toolset.Default.Id,
                Tag = ts.Id,
            };
            _toolsetChecks.Add(cb);
            ToolsetPanel.Children.Add(cb);
        }
    }

    private ScriptRuntimeKind SelectedRuntime
        => RuntimeCombo.SelectedIndex == 1 ? ScriptRuntimeKind.Node : ScriptRuntimeKind.Python;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnPickClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择脚本文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("脚本文件") { Patterns = new[] { "*.py", "*.js", "*.ts" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            },
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            ScriptPathBox.Text = path;
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        var path = ScriptPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            SetError("请先选择脚本文件");
            return;
        }

        GenerateBtn.IsEnabled = false;
        try
        {
            var result = await ToolSchemaProbe.ProbeAsync(SelectedRuntime, path);
            if (!result.Success)
            {
                SetError("生成失败：" + result.Error);
                return;
            }
            NameBox.Text = result.Name;
            DescBox.Text = result.Description;
            SchemaBox.Text = result.ParametersJson;
            SetSuccess("✓ 已从脚本生成，请确认后保存");
        }
        finally
        {
            GenerateBtn.IsEnabled = true;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var path = ScriptPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetError("工具名称不能为空");
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            SetError("请选择脚本文件");
            return;
        }

        var schema = SchemaBox.Text?.Trim();
        if (!string.IsNullOrEmpty(schema))
        {
            try { JsonDocument.Parse(schema); }
            catch
            {
                SetError("入参 Schema 不是有效 JSON");
                return;
            }
        }

        var selectedSets = _toolsetChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        if (selectedSets.Count == 0) selectedSets.Add(Toolset.Default.Id);

        var tool = new ToolDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = DescBox.Text?.Trim() ?? "",
            Source = ToolSource.Script,
            Runtime = SelectedRuntime,
            Target = path,
            InputSchemaJson = string.IsNullOrWhiteSpace(schema) ? "{}" : schema,
            ToolsetIds = selectedSets,
            Enabled = true,
        };

        try
        {
            await ToolRepository.CreateAsync(tool);
            ToolRegistry.Instance.Register(tool);
        }
        catch (Exception ex)
        {
            SetError("保存失败：" + ex.Message);
            return;
        }

        Result = tool;
        Close();
    }

    private void SetError(string text)
    {
        ErrorText.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        ErrorText.Text = text;
    }

    private void SetSuccess(string text)
    {
        ErrorText.Foreground = new SolidColorBrush(Color.Parse("#16A34A"));
        ErrorText.Text = text;
    }
}
