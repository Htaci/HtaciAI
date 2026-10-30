using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using HtaciAI.Controls;
using HtaciAI.Models;
using HtaciAI.Services;
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

    /// <summary>自动检测的提示配色：黄=缺配置，绿=认全了。黄与设置页的黄叹号同色。</summary>
    private static readonly string WarnColor = "#D97706";
    private static readonly string OkColor = "#059669";

    private readonly string _providerId;
    private readonly AiModel? _existing;

    /// <summary>
    /// 点过「自动检测」之后由内置表给出的来源。非 null 表示这一行不是用户凭空填的，
    /// 保存时据此还原 <see cref="AiModel.ConfigOrigin"/>，黄叹号 / 灰问号才不会因为顺手保存一下就被抹掉。
    /// </summary>
    private ModelConfigOrigin? _detectedOrigin;


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

        // 图标是最直观的反馈：认得出来就当场显示，认不出来就留空（而不是留个通用方块）
        VendorLogoHost?.Children.Clear();
        if (VendorAvatar.CreateLogo(id, 16) is { } logo && VendorLogoHost is not null)
            VendorLogoHost.Children.Add(logo);

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

    /// <summary>
    /// 按当前填写（或已存的）模型调用 id 重新查一次内置模型表，把配置整行刷新成表里的值。
    ///
    /// 存在的理由：模型是先「获取模型」批量加进来的，那时表里可能还没收录它，
    /// 或者当时收录的内容已经过时。没有这个按钮，唯一的更新途径是删掉重加，
    /// 用户填过的价格也就跟着丢了 —— 所以价格一栏这里刻意不碰。
    ///
    /// 三种命中结果与「获取模型」批添加完全一致，且走同一个落库函数
    /// （<see cref="BuiltinModelConfigs.ApplyModelInfo"/>），免得两边各算一套。
    /// </summary>
    private void OnDetectClick(object? sender, RoutedEventArgs e)
    {
        var callId = CallIdBox.Text?.Trim() ?? "";
        if (callId.Length == 0)
        {
            SetCallIdHint("先填模型调用 id，再点自动检测。", WarnColor);
            return;
        }

        var match = ModelTableIndex.Resolve(callId);
        _detectedOrigin = match.Kind switch
        {
            TableMatchKind.Model => ModelConfigOrigin.Builtin,
            TableMatchKind.VendorOnly => ModelConfigOrigin.VendorOnly,
            _ => ModelConfigOrigin.Unmatched,
        };

        // 开发商是两级共有的：只命中开发商时也该把头像摆对。
        if (match.Vendor is { } vendor) VendorBox.Text = vendor.Id;

        if (match.Model is not { } info)
        {
            SetCallIdHint(
                match.Vendor is { } known
                    ? $"只认出了开发商 {known.Name}，其余配置内置表里没有，仍需手动补充。"
                    : "内置模型表里没有这个模型，配置保持原样。",
                WarnColor);
            return;
        }

        var detected = new AiModel();
        BuiltinModelConfigs.ApplyModelInfo(detected, info, callId);
        WriteForm(detected);

        SetCallIdHint($"已识别为 {detected.DisplayName}：名称、能力、上下文、思考档位已按内置表更新。", OkColor);
    }

    /// <summary>
    /// 把检测结果写回表单。价格不在内置表里所以一概不碰；
    /// 表里没给数字（上下文 / 最大输出）或三态为未知时，也保留用户已经填好的值 ——
    /// 「表里没写」不等于「确定是零」，检测不该把已有信息抹成空白。
    /// </summary>
    private void WriteForm(AiModel m)
    {
        DisplayNameBox.Text = m.DisplayName;
        ThinkingCheck.IsChecked = m.SupportsThinking;
        StrengthsPicker.SelectedItems = m.ThinkingStrengths;
        CapsPicker.SelectedItems = m.Capabilities
            .Select(c => ModelCapabilities.Labels.TryGetValue(c, out var label) ? label : c)
            .ToList();

        if (m.ContextWindow > 0) CtxBox.Text = m.ContextWindow.ToString();
        if (m.MaxOutputTokens > 0) MaxOutBox.Text = m.MaxOutputTokens.ToString();
        if (m.SupportsArrayContent is { } arrayContent)
            ArrayContentCombo.SelectedIndex = IndexFromTriState(arrayContent);
        if (m.TemperatureSupported is { } temperature)
            TemperatureCombo.SelectedIndex = IndexFromTriState(temperature);
    }

    /// <summary>自动检测的结果就地反馈在 id 下方：认全了是绿的，缺配置是黄的。</summary>
    private void SetCallIdHint(string text, string color)
    {
        CallIdHint.Text = text;
        CallIdHint.Foreground = new SolidColorBrush(Color.Parse(color));
        CallIdHint.IsVisible = true;
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

        // 点过自动检测就沿用检测结果：配置确实是照内置表刷出来的，来源也该如实记成表里的那一档。
        // 没检测过则是用户亲手填的，保存过就归他了 —— 内置表不再提意见，黄叹号/灰问号随之消失。
        // 一个点了也不会消失的警告，用户很快就会学会无视它。
        m.ConfigOrigin = _detectedOrigin ?? ModelConfigOrigin.Manual;

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
