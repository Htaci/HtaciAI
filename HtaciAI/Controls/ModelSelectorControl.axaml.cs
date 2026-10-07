using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Services;

namespace HtaciAI.Controls;

/// <summary>
/// 可复用的模型选择器控件：按钮本体显示「模型图标 + 当前模型名 + · + 当前思考模式」，
/// 点击弹出菜单选择模型或思考模式。模型列表通过 <see cref="Models"/> 依赖属性注入
/// （数据库加载的 <see cref="ModelDetails"/>）。
///
/// <b>列表为空时 <see cref="SelectedModel"/> 就是 null</b>（不做任何内置兜底），
/// 按钮显示「未配置模型」，调用方必须自己处理这种状态。
/// 通过 <see cref="SelectedModel"/> / <see cref="ThinkingMode"/> 依赖属性读写当前值，
/// 并通过 <see cref="SelectedModelChanged"/> / <see cref="ThinkingModeChanged"/> 事件对外通知变化。
/// </summary>
public partial class ModelSelectorControl : UserControl
{
    /// <summary>思考模式 wire 值 → 枚举 的固定映射（菜单展示顺序）。</summary>
    private static readonly (string Label, ThinkingMode Mode)[] ThinkingOptions =
    {
        ("NoThink", ThinkingMode.NoThink),
        ("none", ThinkingMode.Default),
        ("low", ThinkingMode.Low),
        ("high", ThinkingMode.High),
        ("max", ThinkingMode.Max),
    };

    private List<ModelDetails> _models = new();

    /// <summary>
    /// 上次构建菜单时的模型列表指纹。菜单每次打开都会重新注入列表，
    /// 内容没变就跳过重建，避免菜单刚展开就被清空重填的闪动。
    /// </summary>
    private string _lastSignature = "";

    /// <summary>
    /// 模型列表指纹：服务商名 + 模型 id + 显示名。这三者决定了菜单结构与每一项的文案，
    /// 任一变化（服务商改名、模型改名、增删模型）都必须重建菜单。
    /// </summary>
    private static string Signature(List<ModelDetails> models)
        => string.Join('\n', models.Select(
            m => m.ProviderName + "\u0001" + m.ModelId + "\u0001" + m.DisplayName));

    public ModelSelectorControl()
    {
        InitializeComponent();
        // 不预设任何模型：等 Models 注入后由下面那个分支决定选中项，一个都没有就保持 null

        // 菜单每次打开都通知宿主重新拉一次模型列表（服务商改名/新增模型能立刻反映）。
        // 用 Opening 而不是 Click：Click 时 Flyout 已经开始展开，事件顺序不可控。
        if (ModelAndThinkBtn.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 菜单即将打开。宿主应在此重新加载 <see cref="Models"/> ——
    /// 列表不再只在创建时注入一次，否则改了服务商/模型名之后选择器会一直显示旧数据。
    /// </summary>
    public event EventHandler? RefreshRequested;

    // ============================================================
    // 依赖属性
    // ============================================================

    /// <summary>可选模型列表（数据库加载）。空列表时控件内部回退内置默认模型。</summary>
    public static readonly StyledProperty<IEnumerable<ModelDetails>> ModelsProperty =
        AvaloniaProperty.Register<ModelSelectorControl, IEnumerable<ModelDetails>>(
            nameof(Models),
            defaultValue: Array.Empty<ModelDetails>(),
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => v ?? Array.Empty<ModelDetails>());

    public IEnumerable<ModelDetails> Models
    {
        get => GetValue(ModelsProperty);
        set => SetValue(ModelsProperty, value);
    }

    /// <summary>当前选中的模型。</summary>
    public static readonly StyledProperty<ModelDetails?> SelectedModelProperty =
        AvaloniaProperty.Register<ModelSelectorControl, ModelDetails?>(
            nameof(SelectedModel),
            defaultBindingMode: BindingMode.TwoWay);

    public ModelDetails? SelectedModel
    {
        get => GetValue(SelectedModelProperty);
        set => SetValue(SelectedModelProperty, value);
    }

    /// <summary>当前思考模式。</summary>
    public static readonly StyledProperty<ThinkingMode> ThinkingModeProperty =
        AvaloniaProperty.Register<ModelSelectorControl, ThinkingMode>(
            nameof(ThinkingMode),
            defaultValue: ThinkingMode.Default,
            defaultBindingMode: BindingMode.TwoWay);

    public ThinkingMode ThinkingMode
    {
        get => GetValue(ThinkingModeProperty);
        set => SetValue(ThinkingModeProperty, value);
    }

    // ============================================================
    // 事件
    // ============================================================

    /// <summary>选中模型变化时触发（携带新模型，可为 null）。</summary>
    public event EventHandler<ModelDetails?>? SelectedModelChanged;

    /// <summary>思考模式变化时触发（携带新模式）。</summary>
    public event EventHandler<ThinkingMode>? ThinkingModeChanged;

    // ============================================================
    // 属性变化
    // ============================================================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelsProperty)
        {
            _models = (Models ?? Array.Empty<ModelDetails>()).ToList();

            // 每次打开菜单都会重新注入列表，内容没变就不重建菜单 ——
            // 否则菜单刚展开就被清空重填，会出现一次可见的闪动。
            var signature = Signature(_models);
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                RebuildMenu();
            }

            // 当前选中项仍在数据库模型列表则保留；否则选中第一个可用模型。
            // 一个可用模型都没有时置 null（调用方会看到「未配置模型」并拒绝发送），
            // 不再退到任何写死的模型上。
            var current = SelectedModel;
            if (current is null || _models.All(m => m.ModelId != current.ModelId))
            {
                SelectedModel = _models.Count > 0 ? _models[0] : null;
            }
            else if (!_models.Any(m => ReferenceEquals(m, current)))
            {
                // 同一个模型被重新装配过（改了显示名 / 换了服务商）：换成新实例，
                // 否则标签与绿点会一直停在旧名字上 —— 这正是「改名后选择器不更新」的表现。
                SelectedModel = _models.First(m => m.ModelId == current.ModelId);
            }

            // 上面赋的可能是 null 且原本就是 null（空列表反复注入），那样不会触发属性变更通知，
            // 标签会一直停在旧文案上；这里显式刷一次。
            UpdateModelLabel();
            UpdateSelectionMarkers();
        }
        else if (change.Property == SelectedModelProperty)
        {
            UpdateModelLabel();
            UpdateSelectionMarkers();
            SelectedModelChanged?.Invoke(this, SelectedModel);
        }
        else if (change.Property == ThinkingModeProperty)
        {
            UpdateThinkingLabel();
            UpdateSelectionMarkers();
            ThinkingModeChanged?.Invoke(this, ThinkingMode);
        }
    }

    // ============================================================
    // 菜单构建
    // ============================================================

    /// <summary>
    /// 模型菜单项 → 它代表的模型 id 与尾随绿点；服务商菜单项 → 服务商名与绿点；
    /// 思考模式菜单项 → 它代表的模式。用于选中变化时刷新标记。
    /// </summary>
    private readonly List<(MenuItem Item, string ModelId, Control Dot)> _modelItems = new();
    private readonly List<(MenuItem Item, string ProviderName, Control Dot)> _providerItems = new();
    private readonly List<(MenuItem Item, ThinkingMode Mode)> _thinkingItems = new();

    /// <summary>思考模式子菜单的对号（思考模式沿用对号，模型/服务商用绿点）。</summary>
    private static Control CheckIcon() => new TextBlock
    {
        Text = "\uE73E",
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#4A90D9")),
    };

    /// <summary>
    /// 菜单项标题：文字 + 尾随绿点。绿点始终留在控件树里、只切 <c>IsVisible</c>，
    /// 这样反复切换选中项只是改可见性，不用重建菜单项。
    /// </summary>
    private static (StackPanel Panel, Control Dot) TitleWithDot(string text)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.Parse("#2ED573")),
            IsVisible = false,
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                dot,
            },
        };

        return (panel, dot);
    }

    /// <summary>
    /// 重建菜单：服务商子菜单（各自展开该服务商下的模型）→ 分隔线 → 思考模式子菜单。
    /// 按服务商分组同时解决了「多个服务商提供同名模型」——同名模型分处不同子菜单，
    /// 不再被压成一条无法区分、点哪个都一样的条目。
    /// </summary>
    private void RebuildMenu()
    {
        if (ModelAndThinkBtn.Flyout is not MenuFlyout menu) return;
        var items = menu.Items;
        items.Clear();
        _modelItems.Clear();
        _providerItems.Clear();
        _thinkingItems.Clear();

        if (_models.Count == 0)
        {
            items.Add(new MenuItem { Header = "未配置模型", IsEnabled = false });
        }
        else
        {
            // GroupBy 按首次出现顺序分组，正好是 ModelCatalog 的服务商排序（sort_order, name）
            foreach (var group in _models.GroupBy(m => m.ProviderName))
            {
                var (panel, dot) = TitleWithDot(group.Key);
                var provider = new MenuItem { Header = panel };
                _providerItems.Add((provider, group.Key, dot));

                foreach (var model in group)
                {
                    var name = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelName : model.DisplayName;
                    var (modelPanel, modelDot) = TitleWithDot(name);
                    var mi = new MenuItem { Header = modelPanel };
                    var captured = model;
                    mi.Click += (_, _) => SelectedModel = captured;
                    _modelItems.Add((mi, model.ModelId, modelDot));
                    provider.Items.Add(mi);
                }

                items.Add(provider);
            }
        }

        items.Add(new Separator());
        items.Add(BuildThinkingMenu());
        UpdateSelectionMarkers();
    }

    /// <summary>构建思考模式子菜单。</summary>
    private MenuItem BuildThinkingMenu()
    {
        var parent = new MenuItem { Header = "思考模式" };
        foreach (var (label, mode) in ThinkingOptions)
        {
            var mi = new MenuItem { Header = label };
            var captured = mode;
            mi.Click += (_, _) => ThinkingMode = captured;
            _thinkingItems.Add((mi, captured));
            parent.Items.Add(mi);
        }
        return parent;
    }

    /// <summary>
    /// 把绿点挪到「当前模型」及其「所属服务商」上；思考模式仍用对号。
    /// 服务商那一层也标出来，是为了菜单收起时也能一眼看出当前模型属于哪个服务商。
    /// </summary>
    private void UpdateSelectionMarkers()
    {
        var currentModelId = SelectedModel?.ModelId;
        var currentProvider = SelectedModel?.ProviderName;

        foreach (var (_, modelId, dot) in _modelItems)
            dot.IsVisible = modelId == currentModelId;

        foreach (var (_, providerName, dot) in _providerItems)
            dot.IsVisible = providerName == currentProvider;

        foreach (var (item, mode) in _thinkingItems)
            item.Icon = mode == ThinkingMode ? CheckIcon() : null;
    }

    // ============================================================
    // 标签刷新
    // ============================================================

    private void UpdateModelLabel()
    {
        if (ModelLabel is not null)
            ModelLabel.Text = SelectedModel?.DisplayName is { Length: > 0 } name ? name : "未配置模型";
    }

    private void UpdateThinkingLabel()
    {
        if (ThinkingLabel is not null)
            ThinkingLabel.Text = ThinkingModeToLabel(ThinkingMode);
    }

    private static string ThinkingModeToLabel(ThinkingMode mode) => mode switch
    {
        ThinkingMode.NoThink => "NoThink",
        ThinkingMode.Default => "none",
        ThinkingMode.Low => "low",
        ThinkingMode.High => "high",
        ThinkingMode.Max => "max",
        _ => "none",
    };
}
