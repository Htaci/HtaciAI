using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Models;
using HtaciAI.Services;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 连接测试窗：选一个模型 + 一个端点，跑两步探测（列模型 → 最小对话），把每步结果就地列出来。
///
/// 为什么要让用户选模型：只测端点的可达性说明不了「这个模型名在这家站里存不存在」，
/// 而后者恰恰是配好之后最常见的坑（模型 id 拼错、站点没开这个模型）。
/// 为什么让用户选端点：多端点时两个端点的 URL 与协议都可能不同，测哪个必须明说。
/// </summary>
public partial class ConnectivityTestWindow : Window
{
    private readonly AiProvider _provider;

    /// <summary>下拉里一行「模型」的可读文本。ComboBox 直接显示对象的 ToString，所以单独包一层。</summary>
    private sealed record ModelChoice(AiModel Model)
    {
        public override string ToString()
            => string.Equals(Model.DisplayName, Model.CallId, StringComparison.Ordinal)
                ? Model.CallId
                : $"{Model.DisplayName}（{Model.CallId}）";
    }

    private sealed record EndpointChoice(AiProviderEndpoint Endpoint)
    {
        public override string ToString()
            => (Endpoint.IsDefault ? "默认 · " : "") + ProtocolLabel(Endpoint.Protocol) + " · " + Endpoint.Endpoint;
    }

    /// <summary>供 XAML 加载器使用（实际均由带参构造创建）。</summary>
    public ConnectivityTestWindow() : this(new AiProvider(), new List<AiModel>()) { }

    public ConnectivityTestWindow(AiProvider provider, IReadOnlyList<AiModel> models)
    {
        InitializeComponent();
        _provider = provider;

        Title = $"连接测试 · {provider.Name}";
        SubtitleText.Text = provider.Name;

        ModelCombo.ItemsSource = models.Select(m => new ModelChoice(m)).ToList();
        if (ModelCombo.ItemCount > 0) ModelCombo.SelectedIndex = 0;

        EndpointCombo.ItemsSource = provider.Endpoints.Select(e => new EndpointChoice(e)).ToList();
        // 默认端点排在最前（provider.Endpoints 里 IsDefault 的那个不一定排第一，这里显式挑出来）
        var defaultIndex = provider.Endpoints
            .Select((e, i) => (e, i))
            .FirstOrDefault(t => t.e.IsDefault, (null!, 0)).i;
        if (EndpointCombo.ItemCount > 0) EndpointCombo.SelectedIndex = defaultIndex;

        SyncRunButtonState();
    }

    private static string ProtocolLabel(ModelProtocol protocol) => protocol switch
    {
        ModelProtocol.OpenAIResponses => "OpenAI Responses",
        ModelProtocol.LMStudio => "LM Studio",
        _ => "OpenAI",
    };

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>没有模型或没有端点时不让点：那种情况下测出来的必然是「配置缺失」，不是连接问题。</summary>
    private void SyncRunButtonState()
    {
        var ready = ModelCombo.SelectedItem is ModelChoice && EndpointCombo.SelectedItem is EndpointChoice;
        RunButton.IsEnabled = ready;
        if (!ready)
        {
            StatusText.Text = EndpointCombo.ItemCount == 0
                ? "还没有端点，先去页面上添加一个端点。"
                : "还没有模型，先同步或添加一个模型。";
        }
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not ModelChoice modelChoice
            || EndpointCombo.SelectedItem is not EndpointChoice endpointChoice)
            return;

        var model = modelChoice.Model;
        var endpoint = endpointChoice.Endpoint;

        RunButton.IsEnabled = false;
        ResultPanel.Children.Clear();
        StatusText.Text = "正在测试…";

        AddStepPlaceholder("列出模型（GET /models）", "正在请求…");
        var modelsStep = await ConnectionProbe.CheckModelsAsync(endpoint, _provider.ApiKey);
        ReplaceLastStep(modelsStep);

        // 第一步都失败就不必再发消息了：那一定会以同一个原因再失败一次，
        // 白白多等一轮超时，还让「到底哪里错」变得更含糊。
        if (!modelsStep.Ok)
        {
            StatusText.Text = "测试未通过：地址或密钥有问题。";
            AddConclusion("地址或密钥不可用，先修好这两处再看别的。");
            RunButton.IsEnabled = true;
            SyncRunButtonState();
            return;
        }

        AddStepPlaceholder("发送测试消息", "正在请求…");
        var completionStep = await ConnectionProbe.CheckCompletionAsync(_provider, endpoint, model);
        ReplaceLastStep(completionStep);

        StatusText.Text = completionStep.Ok
            ? "测试通过：这个模型在当前端点上可用。"
            : "测试未通过：地址与密钥正常，问题在模型或协议上。";
        AddConclusion(completionStep.Ok
            ? "这条模型配置可以直接用。"
            : "端点本身是通的，检查模型调用 id 是否与站点提供的名字一致，以及端点的协议是否选对。");

        RunButton.IsEnabled = true;
        SyncRunButtonState();
    }

    /// <summary>先占一行「进行中」，实测结果回来后原地替换，避免结果列表边跑边长。</summary>
    private void AddStepPlaceholder(string name, string detail)
        => ResultPanel.Children.Add(CreateStepRow(name, detail, null));

    private void ReplaceLastStep(ConnectionProbe.StepResult result)
    {
        if (ResultPanel.Children.Count == 0) return;
        ResultPanel.Children[^1] = CreateStepRow(result.Name, result.Detail, result.Ok, result.ElapsedMs);
    }

    private void AddConclusion(string text)
    {
        ResultPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TextMutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
    }

    private static readonly IBrush TextPrimaryBrush = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMutedBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));

    /// <summary>一步结果：状态图标 + 名称 + 明细（+ 耗时）。<paramref name="ok"/> 为 null 表示进行中。</summary>
    private static Control CreateStepRow(string name, string detail, bool? ok, long elapsedMs = 0)
    {
        var (glyph, color) = ok switch
        {
            true => ("\uF295", OkBrush),      // checkmark
            false => ("\uF36A", FailBrush),   // dismiss
            _ => ("\uF2DE", PendingBrush),    // clock（进行中）
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = glyph,
                    FontFamily = AppFonts.Icons,
                    FontSize = 13,
                    Foreground = color,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = name,
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextPrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        if (ok is not null && elapsedMs > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = $"{elapsedMs} ms",
                FontSize = 11,
                Foreground = PendingBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    header,
                    new TextBlock
                    {
                        Text = detail,
                        FontSize = 12,
                        Foreground = ok == false ? FailBrush : TextMutedBrush,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
    }
}
