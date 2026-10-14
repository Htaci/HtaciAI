using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Models;
using HtaciAI.Services.ModelTable;

namespace HtaciAI.Views.Settings;

/// <summary>模型编辑对话框（添加/编辑），隶属于某个服务商。</summary>
public partial class ModelEditWindow : Window
{
    /// <summary>
    /// 思考强度可选值（wire 值，按展示顺序）。
    /// 这是「模型可以声明哪些档位」的全集，不是「本次请求用哪个档位」——
    /// 后者见 <c>ThinkingModes.DisplayOrder</c>。
    /// </summary>
    private static readonly string[] ThinkingStrengthOptions =
        { "minimal", "low", "medium", "high", "xhigh", "max" };

    /// <summary>三态选项。顺序与 <see cref="IndexFromTriState"/> / <see cref="TriStateFromIndex"/> 对应。</summary>
    private static readonly string[] TriStateLabels = { "未知", "支持", "不支持" };

    /// <summary>能力中文标签 → wire 值（保存时反查）。</summary>
    private static readonly IReadOnlyDictionary<string, string> LabelToWire =
        ModelCapabilities.Options.ToDictionary(o => o.Label, o => o.Wire, StringComparer.OrdinalIgnoreCase);

    private readonly string _providerId;
    private readonly AiModel? _existing;

    public AiModel? Result { get; private set; }

    /// <summary>供 XAML 运行时加载器使用（实际均由带参构造创建）。</summary>
    public ModelEditWindow() : this("", null) { }

    public ModelEditWindow(string providerId, AiModel? existing)
    {
        InitializeComponent();
        _providerId = providerId;
        _existing = existing;

        TitleText.Text = existing is null ? "添加模型" : "编辑模型";
        Title = TitleText.Text;

        CurrencyCombo.ItemsSource = new List<string> { "CNY", "USD" };
        CurrencyCombo.SelectedIndex = 0;

        // 标签选择：思考强度 + 模型能力
        StrengthsPicker.Items = ThinkingStrengthOptions;
        CapsPicker.Items = ModelCapabilities.Options.Select(o => o.Label).ToList();

        // 两个三态下拉。默认都落在「未知」—— 未知的含义是「跟随服务商 / 不下发」，
        // 与「确定不支持」是两回事，所以不能默认选「不支持」。
        ArrayContentCombo.ItemsSource = TriStateLabels;
        TemperatureCombo.ItemsSource = TriStateLabels;
        ArrayContentCombo.SelectedIndex = 0;
        TemperatureCombo.SelectedIndex = 0;

        if (existing is not null)
        {
            DisplayNameBox.Text = existing.DisplayName;
            CallIdBox.Text = existing.CallId;
            StreamingCheck.IsChecked = existing.SupportsStreaming;
            ThinkingCheck.IsChecked = existing.SupportsThinking;
            StrengthsPicker.SelectedItems = existing.ThinkingStrengths;
            CapsPicker.SelectedItems = existing.Capabilities
                .Select(c => ModelCapabilities.Labels.TryGetValue(c, out var label) ? label : c)
                .ToList();
            CtxBox.Text = existing.ContextWindow.ToString();
            MaxOutBox.Text = existing.MaxOutputTokens.ToString();
            VendorBox.Text = existing.Vendor;
            ArrayContentCombo.SelectedIndex = IndexFromTriState(existing.SupportsArrayContent);
            TemperatureCombo.SelectedIndex = IndexFromTriState(existing.TemperatureSupported);
            InputBox.Text = existing.PriceInput?.ToString("0.####");
            CacheBox.Text = existing.PriceCacheHit?.ToString("0.####");
            OutputBox.Text = existing.PriceOutput?.ToString("0.####");
            CurrencyCombo.SelectedItem = existing.Currency switch
            {
                "USD" => "USD",
                _ => "CNY",
            };
        }
        else
        {
            StreamingCheck.IsChecked = true;
            ThinkingCheck.IsChecked = false;
            CtxBox.Text = "-1";
            MaxOutBox.Text = "-1";
            UpdateVendorHint();
        }
    }

    /// <summary>
    /// 开发商输入框的实时反馈。让人当场看到「这个词认不认得出来、有没有图标」，
    /// 而不是保存之后发现头像还是通用图标却不知道为什么。
    /// </summary>
    private void OnVendorChanged(object? sender, TextChangedEventArgs e) => UpdateVendorHint();

    private void UpdateVendorHint()
    {
        if (VendorHint is null) return;

        var id = VendorBox?.Text?.Trim() ?? "";
        if (id.Length == 0)
        {
            VendorHint.Text = "未设置：模型头像用通用图标。";
            return;
        }

        var name = VendorIcons.VendorName(id);
        var hasIcon = VendorIcons.Resolve(id) is not null;

        VendorHint.Text = name is null
            ? $"内置表里没有登记「{id}」，将按同名图标文件查找：" +
              (hasIcon ? "已找到图标。" : "没有找到图标，头像用通用图标。")
            : $"已识别为 {name}（{id}）" + (hasIcon ? "。" : "，但没有对应图标文件，头像用通用图标。");
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var displayName = DisplayNameBox.Text?.Trim();
        var callId = CallIdBox.Text?.Trim();
        if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(callId))
        {
            TitleText.Text = "模型备注名称与模型调用 id 不能为空";
            return;
        }

        var m = _existing ?? new AiModel();
        m.ProviderId = _providerId;
        m.DisplayName = displayName;
        m.CallId = callId;
        m.SupportsStreaming = StreamingCheck.IsChecked ?? true;
        m.SupportsThinking = ThinkingCheck.IsChecked ?? false;
        m.ThinkingStrengths = (StrengthsPicker.SelectedItems ?? Array.Empty<string>()).ToList();
        m.Capabilities = (CapsPicker.SelectedItems ?? Array.Empty<string>())
            .Select(label => LabelToWire.TryGetValue(label, out var wire) ? wire : label)
            .ToList();
        m.ContextWindow = ParseLong(CtxBox.Text, -1);
        m.MaxOutputTokens = ParseLong(MaxOutBox.Text, -1);
        m.Vendor = VendorBox.Text?.Trim() ?? "";
        m.SupportsArrayContent = TriStateFromIndex(ArrayContentCombo.SelectedIndex);
        m.TemperatureSupported = TriStateFromIndex(TemperatureCombo.SelectedIndex);

        // 用户亲手保存过，这一行就归他了：内置表不再对它提任何意见，
        // 黄叹号/灰问号也随之消失。这是「提示可被消除」的关键一步 ——
        // 一个点了也不会消失的警告，用户很快就会学会无视它。
        m.ConfigOrigin = ModelConfigOrigin.Manual;

        m.PriceInput = ParseDouble(InputBox.Text);
        m.PriceCacheHit = ParseDouble(CacheBox.Text);
        m.PriceOutput = ParseDouble(OutputBox.Text);
        m.Currency = CurrencyCombo.SelectedItem as string ?? "CNY";

        Result = m;
        Close();
    }

    private static long ParseLong(string? text, long fallback)
        => long.TryParse(text?.Trim(), out var v) ? v : fallback;

    // 三态下拉与 bool? 的互换。下标 0 是「未知」，与 bool? 的 null 一一对应；
    // 顺序反了的话「不知道」会被存成「不支持」，那是两种完全不同的语义。
    private static bool? TriStateFromIndex(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    private static int IndexFromTriState(bool? value) => value switch
    {
        true => 1,
        false => 2,
        _ => 0,
    };

    private static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), out var v) ? v : null;
    }
}
