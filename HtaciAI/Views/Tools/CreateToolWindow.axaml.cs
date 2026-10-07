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

    /// <summary>无参构造只为满足 XAML 加载器/设计器（缺了会报 AVLN3001），正常调用走带参那个。</summary>
    public CreateToolWindow() : this(null) { }

    /// <param name="presetToolsetId">
    /// 在工具页某个集合里点「创建新工具」时传入，对应勾选框预选上。自动集合（全部/内置）传 null。
    /// </param>
    public CreateToolWindow(string? presetToolsetId)
    {
        InitializeComponent();

        RuntimeCombo.ItemsSource = new List<string> { "Python", "Node.js" };
        RuntimeCombo.SelectedIndex = 0;

        DangerCombo.ItemsSource = new List<string> { "安全", "风险", "危险" };
        DangerCombo.SelectedIndex = 2; // 未选择时默认「危险」

        // 归属工具集勾选：自动集合（全部/内置）不参与归属，不能勾
        foreach (var ts in ToolRegistry.Instance.GetToolsets().Where(ts => !ts.IsAuto))
        {
            var cb = new CheckBox
            {
                Content = ts.Name,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                IsChecked = ts.Id == presetToolsetId,
                Tag = ts.Id,
            };
            _toolsetChecks.Add(cb);
            ToolsetPanel.Children.Add(cb);
        }

        if (_toolsetChecks.Count == 0)
            ToolsetPanel.Children.Add(new TextBlock
            {
                Text = "还没有自建工具集，创建后可在工具页把工具加进去",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                TextWrapping = TextWrapping.Wrap,
            });
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
            DangerLevel = DangerCombo.SelectedIndex switch
            {
                0 => ToolDangerLevel.Safe,
                1 => ToolDangerLevel.Risk,
                _ => ToolDangerLevel.Danger,
            },
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
