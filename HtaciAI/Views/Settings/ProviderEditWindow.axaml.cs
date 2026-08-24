using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Models;

namespace HtaciAI.Views.Settings;

/// <summary>服务商编辑对话框（添加/编辑）。</summary>
public partial class ProviderEditWindow : Window
{
    private readonly AiProvider? _existing;

    /// <summary>思考开关字段下拉文案；索引 0/1/2/3 = think / enable_thinking / reasoning_effort / none。</summary>
    private static readonly List<string> ThinkingFields = new() { "think", "enable_thinking", "reasoning_effort", "none" };

    /// <summary>协议下拉文案；索引 0/1 = OpenAI(Ex) / LM Studio(本地)。</summary>
    private static readonly List<string> ProtocolLabels = new() { "OpenAI(Ex) API", "LM Studio (本地)" };

    /// <summary>初始化期间为 true，抑制协议切换联动，避免覆盖已存字段。</summary>
    private bool _initializing = true;

    public AiProvider? Result { get; private set; }

    /// <summary>供 XAML 运行时加载器使用（实际均由带参构造创建）。</summary>
    public ProviderEditWindow() : this(null) { }

    public ProviderEditWindow(AiProvider? existing)
    {
        InitializeComponent();
        _existing = existing;

        TitleText.Text = existing is null ? "添加服务商" : "编辑服务商";
        Title = TitleText.Text;

        ProtocolCombo.ItemsSource = ProtocolLabels;
        ThinkingCombo.ItemsSource = ThinkingFields;
        ProtocolCombo.SelectionChanged += OnProtocolSelectionChanged;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            EndpointBox.Text = existing.Endpoint;
            ApiKeyBox.Text = existing.ApiKey ?? "";
            DescriptionBox.Text = existing.Description ?? "";
            ArrayContentCheck.IsChecked = existing.SupportsArrayContent;
            StreamingCheck.IsChecked = existing.SupportsStreaming;

            ProtocolCombo.SelectedIndex = ProtocolIndex(existing.Protocol);
            ApplyProtocolThinking(ProtocolIndex(existing.Protocol), existing.ThinkingField);
        }
        else
        {
            ProtocolCombo.SelectedIndex = 0;
            ThinkingCombo.SelectedIndex = 0;
            ArrayContentCheck.IsChecked = true;
            StreamingCheck.IsChecked = true;
        }

        _initializing = false;
    }

    // ---- 协议与思考字段联动 ----

    /// <summary>协议切换时联动思考字段：LM Studio 固定为 reasoning_effort，其余按已存/默认值。</summary>
    private void OnProtocolSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        ApplyProtocolThinking(ProtocolCombo.SelectedIndex, null);
    }

    private void ApplyProtocolThinking(int protocolIndex, ThinkingFieldKind? existing)
    {
        if (protocolIndex == ProtocolIndex(ModelProtocol.LMStudio))
        {
            ThinkingCombo.SelectedIndex = ThinkingIndex(ThinkingFieldKind.ReasoningEffort);
            ThinkingCombo.IsEnabled = false;
        }
        else
        {
            ThinkingCombo.IsEnabled = true;
            ThinkingCombo.SelectedIndex = existing.HasValue ? ThinkingIndex(existing.Value) : 0;
        }
    }

    private static int ProtocolIndex(ModelProtocol p) => p switch
    {
        ModelProtocol.LMStudio => 1,
        _ => 0,
    };

    private static ModelProtocol ProtocolFromIndex(int i) => i switch
    {
        1 => ModelProtocol.LMStudio,
        _ => ModelProtocol.OpenAIEx,
    };

    private static int ThinkingIndex(ThinkingFieldKind k) => k switch
    {
        ThinkingFieldKind.EnableThinking => 1,
        ThinkingFieldKind.ReasoningEffort => 2,
        ThinkingFieldKind.None => 3,
        _ => 0,
    };

    private static ThinkingFieldKind ThinkingFromIndex(int i) => i switch
    {
        1 => ThinkingFieldKind.EnableThinking,
        2 => ThinkingFieldKind.ReasoningEffort,
        3 => ThinkingFieldKind.None,
        _ => ThinkingFieldKind.Think,
    };

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
        p.Protocol = ProtocolFromIndex(ProtocolCombo.SelectedIndex);
        // LM Studio 固定使用 reasoning_effort，不随用户下拉变化；其余按下拉选择。
        p.ThinkingField = p.Protocol == ModelProtocol.LMStudio
            ? ThinkingFieldKind.ReasoningEffort
            : ThinkingFromIndex(ThinkingCombo.SelectedIndex);
        p.SupportsArrayContent = ArrayContentCheck.IsChecked ?? true;
        p.SupportsStreaming = StreamingCheck.IsChecked ?? true;

        Result = p;
        Close();
    }
}
