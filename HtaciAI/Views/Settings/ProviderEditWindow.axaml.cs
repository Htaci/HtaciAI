using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Models;

namespace HtaciAI.Views.Settings;

/// <summary>服务商编辑对话框（添加/编辑）。</summary>
public partial class ProviderEditWindow : Window
{
    private readonly AiProvider? _existing;
    private readonly List<string> _thinkingFields = new() { "think", "enable_thinking", "none" };

    public AiProvider? Result { get; private set; }

    /// <summary>供 XAML 运行时加载器使用（实际均由带参构造创建）。</summary>
    public ProviderEditWindow() : this(null) { }

    public ProviderEditWindow(AiProvider? existing)
    {
        InitializeComponent();
        _existing = existing;

        TitleText.Text = existing is null ? "添加服务商" : "编辑服务商";
        Title = TitleText.Text;

        ProtocolCombo.ItemsSource = new List<string> { "OpenAI(Ex) API" };
        ProtocolCombo.SelectedIndex = 0;
        ThinkingCombo.ItemsSource = _thinkingFields;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            EndpointBox.Text = existing.Endpoint;
            ApiKeyBox.Text = existing.ApiKey ?? "";
            DescriptionBox.Text = existing.Description ?? "";
            ArrayContentCheck.IsChecked = existing.SupportsArrayContent;
            StreamingCheck.IsChecked = existing.SupportsStreaming;
            ThinkingCombo.SelectedIndex = existing.ThinkingField switch
            {
                ThinkingFieldKind.EnableThinking => 1,
                ThinkingFieldKind.None => 2,
                _ => 0,
            };
        }
        else
        {
            ThinkingCombo.SelectedIndex = 0;
            ArrayContentCheck.IsChecked = true;
            StreamingCheck.IsChecked = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var endpoint = EndpointBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(endpoint))
        {
            TitleText.Text = "服务商名称与目标 URL 不能为空";
            return;
        }

        var p = _existing ?? new AiProvider();
        p.Name = name;
        p.Endpoint = endpoint;
        p.ApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Text) ? null : ApiKeyBox.Text.Trim();
        p.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        p.Protocol = ModelProtocol.OpenAIEx; // 当前仅支持 OpenAI(Ex)
        p.ThinkingField = ThinkingCombo.SelectedIndex switch
        {
            1 => ThinkingFieldKind.EnableThinking,
            2 => ThinkingFieldKind.None,
            _ => ThinkingFieldKind.Think,
        };
        p.SupportsArrayContent = ArrayContentCheck.IsChecked ?? true;
        p.SupportsStreaming = StreamingCheck.IsChecked ?? true;

        Result = p;
        Close();
    }
}
