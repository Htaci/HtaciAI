using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HtaciAI.Models;

namespace HtaciAI.Views.Settings;

/// <summary>模型编辑对话框（添加/编辑），隶属于某个服务商。</summary>
public partial class ModelEditWindow : Window
{
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

        if (existing is not null)
        {
            DisplayNameBox.Text = existing.DisplayName;
            CallIdBox.Text = existing.CallId;
            StreamingCheck.IsChecked = existing.SupportsStreaming;
            ThinkingCheck.IsChecked = existing.SupportsThinking;
            StrengthsBox.Text = string.Join(", ", existing.ThinkingStrengths);
            CapsBox.Text = string.Join(", ", existing.Capabilities);
            CtxBox.Text = existing.ContextWindow.ToString();
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
        }
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
        m.ThinkingStrengths = SplitList(StrengthsBox.Text);
        m.Capabilities = SplitList(CapsBox.Text);
        m.ContextWindow = ParseLong(CtxBox.Text, -1);
        m.PriceInput = ParseDouble(InputBox.Text);
        m.PriceCacheHit = ParseDouble(CacheBox.Text);
        m.PriceOutput = ParseDouble(OutputBox.Text);
        m.Currency = CurrencyCombo.SelectedItem as string ?? "CNY";

        Result = m;
        Close();
    }

    private static List<string> SplitList(string? text)
        => (text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

    private static long ParseLong(string? text, long fallback)
        => long.TryParse(text?.Trim(), out var v) ? v : fallback;

    private static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), out var v) ? v : null;
    }
}
