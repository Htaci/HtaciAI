using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services.Providers;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 服务商设置窗：名称、图标与那些「配一次就不管」的开关（思考字段 / 多模态 / 流式 / 描述）。
///
/// API 密钥与端点留在详情页里就地改 —— 那几个是配服务商时手脚最频繁的地方，
/// 来回开窗反而更烦；名称一旦定下来基本不动，放这里比在标题上点来点去更稳妥
/// （标题处就地可编辑时，误点就进入编辑态，还不如一个明确的入口）。
/// </summary>
public partial class ProviderSettingsWindow : Window
{
    private readonly AiProvider _provider;

    /// <summary>图标下拉里的第一项：不用图标文件，用名称首字生成头像。</summary>
    private const string InitialAvatarLabel = "首字母头像（不使用图标）";

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

        // 点到标签 / 空白处就收起输入框的高亮边框（不然改完名那一圈蓝边一直亮着）
        TextBoxFocusCleanup.Attach(this);

        TitleText.Text = $"{provider.Name} · 设置";
        Title = TitleText.Text;

        NameBox.Text = provider.Name;

        var icons = new List<string> { InitialAvatarLabel };
        icons.AddRange(ProviderIcons.Available);
        IconCombo.ItemsSource = icons;
        IconCombo.SelectedIndex = Math.Max(0, icons.FindIndex(
            name => string.Equals(name, provider.Icon, StringComparison.OrdinalIgnoreCase)));

        ThinkingCombo.ItemsSource = ThinkingFields;
        ThinkingCombo.SelectedIndex = ThinkingIndex(provider.ThinkingField);
        ArrayContentCheck.IsChecked = provider.SupportsArrayContent;
        StreamingCheck.IsChecked = provider.SupportsStreaming;
        DescriptionBox.Text = provider.Description ?? "";

        IconCombo.SelectionChanged += (_, _) => UpdateIconPreview();
        NameBox.TextChanged += (_, _) => UpdateIconPreview();
        UpdateIconPreview();

        // 打开就把光标放进名称框：这个窗口最常被打开的理由就是改名（新建服务商后尤其如此）。
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>下拉里选中的图标名；第一项表示「不用图标」，返回 null。</summary>
    private string? SelectedIcon
    {
        get
        {
            var index = IconCombo.SelectedIndex;
            if (index <= 0) return null;

            return IconCombo.ItemsSource is IEnumerable<string> items
                ? items.ElementAtOrDefault(index)
                : null;
        }
    }

    /// <summary>预览用当前名称与当前图标名重新画一次头像（名称决定了首字与底色）。</summary>
    private void UpdateIconPreview()
    {
        IconPreviewHost.Child = ProviderAvatar.Create(NameBox.Text, SelectedIcon, 30);
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

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("名称不能为空。");
            NameBox.Focus();
            return;
        }

        // 重名会让卡片、模型归属和选择器里出现两个一模一样的服务商，事后没法分辨谁是谁。
        // 只比对名字（忽略大小写与首尾空白），排除自己 —— 改别的字段时名字当然还是它自己。
        try
        {
            var others = (await ProviderRepository.GetAllAsync())
                .Where(p => !string.Equals(p.Id, _provider.Id, StringComparison.Ordinal));

            if (others.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowError($"已存在同名服务商「{name}」，请换一个名字。");
                NameBox.Focus();
                return;
            }
        }
        catch (Exception ex)
        {
            // 查重都查不了就没法保证不重名，宁可不保存 —— 放过去就是库里两条同名记录
            ShowError($"校验名称失败：{ex.Message}");
            return;
        }

        // 直接改调用方传进来的对象：调用方拿着同一个实例去落库，省掉一次「结果回传再合并」。
        _provider.Name = name;
        _provider.Icon = SelectedIcon;
        _provider.ThinkingField = ThinkingFromIndex(ThinkingCombo.SelectedIndex);
        _provider.SupportsArrayContent = ArrayContentCheck.IsChecked ?? true;
        _provider.SupportsStreaming = StreamingCheck.IsChecked ?? true;
        _provider.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();

        Changed = true;
        Close();
    }

    private void ShowError(string message) => ErrorText.Text = message;
}
