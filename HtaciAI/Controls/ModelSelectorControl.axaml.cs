using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using HtaciAI.Services;

namespace HtaciAI.Controls;

/// <summary>
/// 可复用的模型选择器控件：按钮本体显示「模型图标 + 当前模型名 + · + 当前思考模式」，
/// 点击弹出菜单选择模型或思考模式。模型列表通过 <see cref="Models"/> 依赖属性注入
/// （数据库加载的 <see cref="ModelDetails"/>），无数据时回退内置默认模型。
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

    public ModelSelectorControl()
    {
        InitializeComponent();
        // 初始选中内置默认模型，列表为空时兜底展示
        SelectedModel = ModelDetails.Default;
    }

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
            RebuildMenu();

            // 当前选中项仍在数据库模型列表则保留；否则优先选中第一个数据库模型，
            // 无数据库模型时用内置默认模型兜底
            var current = SelectedModel;
            if (current is null || _models.All(m => m.ModelId != current.ModelId))
                SelectedModel = _models.Count > 0 ? _models[0] : ModelDetails.Default;
        }
        else if (change.Property == SelectedModelProperty)
        {
            UpdateModelLabel();
            SelectedModelChanged?.Invoke(this, SelectedModel);
        }
        else if (change.Property == ThinkingModeProperty)
        {
            UpdateThinkingLabel();
            ThinkingModeChanged?.Invoke(this, ThinkingMode);
        }
    }

    // ============================================================
    // 菜单构建
    // ============================================================

    /// <summary>
    /// 实际参与展示/选择的模型列表：数据库模型优先，内置默认模型始终保留在尾部兜底（同 id 不重复）。
    /// </summary>
    private List<ModelDetails> EffectiveModels()
    {
        var list = new List<ModelDetails>(_models);
        if (list.All(m => m.ModelId != ModelDetails.Default.ModelId))
            list.Add(ModelDetails.Default);
        return list;
    }

    /// <summary>重建菜单：模型项（动态）→ 分隔线 → 思考模式子菜单（固定）。</summary>
    private void RebuildMenu()
    {
        if (ModelAndThinkBtn.Flyout is not MenuFlyout menu) return;
        var items = menu.Items;
        items.Clear();

        var models = EffectiveModels();
        foreach (var model in models)
        {
            var mi = new MenuItem { Header = model.DisplayName };
            var captured = model;
            mi.Click += (_, _) => SelectedModel = captured;
            items.Add(mi);
        }

        if (models.Count > 0)
            items.Add(new Separator());

        items.Add(BuildThinkingMenu());
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
            parent.Items.Add(mi);
        }
        return parent;
    }

    // ============================================================
    // 标签刷新
    // ============================================================

    private void UpdateModelLabel()
    {
        if (ModelLabel is not null)
            ModelLabel.Text = SelectedModel?.DisplayName ?? "";
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
