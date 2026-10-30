using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Models;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 服务商设置窗：名称与那些「配一次就不管」的开关（思考字段 / 多模态 / 流式 / 描述）。
///
/// API 密钥与端点留在详情页里就地改 —— 那几个是配服务商时手脚最频繁的地方，
/// 来回开窗反而更烦；名称一旦定下来基本不动，放这里比在标题上点来点去更稳妥
/// （标题处就地可编辑时，误点就进入编辑态，还不如一个明确的入口）。
/// </summary>
public partial class ProviderSettingsWindow : Window
{
    private readonly AiProvider _provider;

    /// <summary>思考开关字段下拉文案；索引 0/1/2/3 = think / enable_thinking / reasoning_effort / none。</summary>
    private static readonly List<string> ThinkingFields = new() { "think", "enable_thinking", "reasoning_effort", "none" };

    /// <summary>保存后是否有改动（调用方据此决定要不要重建页面）。</summary>
    public bool Changed { get; private set; }

    /// <summary>供 XAML 运行时加载器使用（实际均由带参构造创建）。</summary>
    public ProviderSettingsWindow() : this(new AiProvider()) { }

    public ProviderSettingsWindow(AiProvider provider)
    {
        InitializeComponent();
        _provider = provider;

        TitleText.Text = $"{provider.Name} · 设置";
        Title = TitleText.Text;

        NameBox.Text = provider.Name;
        ThinkingCombo.ItemsSource = ThinkingFields;
        ThinkingCombo.SelectedIndex = ThinkingIndex(provider.ThinkingField);
        ArrayContentCheck.IsChecked = provider.SupportsArrayContent;
        StreamingCheck.IsChecked = provider.SupportsStreaming;
        DescriptionBox.Text = provider.Description ?? "";

        // 打开就把光标放进名称框：这个窗口最常被打开的理由就是改名（新建服务商后尤其如此）。
        Opened += (_, _) => NameBox.Focus();
    }

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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            TitleText.Text = "名称不能为空";
            return;
        }

        // 直接改调用方传进来的对象：调用方拿着同一个实例去落库，省掉一次「结果回传再合并」。
        _provider.Name = name;
        _provider.ThinkingField = ThinkingFromIndex(ThinkingCombo.SelectedIndex);
        _provider.SupportsArrayContent = ArrayContentCheck.IsChecked ?? true;
        _provider.SupportsStreaming = StreamingCheck.IsChecked ?? true;
        _provider.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();

        Changed = true;
        Close();
    }
}
