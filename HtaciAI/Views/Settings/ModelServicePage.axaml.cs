using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.ModelTable;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 模型服务页。两层结构：
/// <list type="number">
///   <item><b>列表层</b>：每个服务商一张卡片（名称、模型数量、启用开关），点卡片进入详情；</item>
///   <item><b>详情层</b>：该服务商的模型表格，以及添加模型 / 获取模型 / 编辑 / 删除这些动作。</item>
/// </list>
///
/// 两层共用同一个宿主控件（<c>Host</c>），靠整体重建来切换与刷新 ——
/// 服务商与模型都是低频操作，重建的代价可以忽略，换来的是「界面永远和库一致」，
/// 不必手工同步表格行、计数、卡片上的数字。
/// </summary>
public partial class ModelServicePage : UserControl
{
    // ---- 配色（沿用本页既有的一套） ----

    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush TextAccent = new SolidColorBrush(Color.Parse("#546E7A"));
    private static readonly IBrush TextDanger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#F5F5F5"));

    /// <summary>头像底色候选，按服务商名稳定取一个（同一个名字每次都是同一个颜色）。</summary>
    private static readonly Color[] AvatarPalette =
    {
        Color.Parse("#546E7A"), Color.Parse("#2EAF7D"), Color.Parse("#7B5EA7"),
        Color.Parse("#D97706"), Color.Parse("#4A90D9"), Color.Parse("#DC2626"),
    };

    /// <summary>只在首次挂载时构建；之后切换设置页的导航项不应把用户从详情页弹回列表。</summary>
    private bool _built;

    public ModelServicePage()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (_built) return;
            _built = true;
            await ShowProviderListAsync();
        };
    }

    // ============================================================
    // 导航
    // ============================================================

    private async Task ShowProviderListAsync()
        => Host.Content = await BuildProviderListAsync();

    private async Task ShowProviderDetailAsync(AiProvider provider)
        => Host.Content = await BuildProviderDetailAsync(provider);

    // ============================================================
    // 列表层：服务商卡片
    // ============================================================

    private async Task<Control> BuildProviderListAsync()
    {
        var body = new StackPanel { Margin = new Thickness(20) };

        body.Children.Add(new TextBlock
        {
            Text = "模型管理",
            FontSize = 18,
            FontWeight = FontWeight.DemiBold,
            Foreground = TextPrimary,
        });
        body.Children.Add(new TextBlock
        {
            Text = "在此处管理和配置您的模型服务。您可以添加、删除或修改自定义模型服务的设置，以便更好地满足您的需求。",
            FontSize = 14,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 20),
        });

        // 「自定义模型」标题行：「+ 添加服务商」靠右
        var sectionHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sectionHeader.Children.Add(new TextBlock
        {
            Text = "自定义模型",
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var addProviderBtn = CreateTextButton("+ 添加服务商", () => _ = AddProviderAsync());
        Grid.SetColumn(addProviderBtn, 1);
        sectionHeader.Children.Add(addProviderBtn);
        body.Children.Add(sectionHeader);

        // 卡片网格：用 UniformGrid 而不是 WrapPanel —— 前者会把一行的宽度<b>均分</b>给各列，
        // 卡片因此随窗口自适应伸缩；WrapPanel 只会按固定宽度排列，右边必然留一大块空白。
        var providers = await ProviderRepository.GetAllAsync();
        UniformGrid? cards = null;

        if (providers.Count == 0)
        {
            body.Children.Add(BuildEmptyState());
        }
        else
        {
            cards = new UniformGrid { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var provider in providers)
                cards.Children.Add(await CreateProviderCardAsync(provider));
            body.Children.Add(cards);
        }

        var scroller = new SmoothScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };

        // 列数按可用宽度算：窗口宽就多排几列，窄了就退回一列。
        // 挂在 SizeChanged 上而不是一开始算一次：窗口尺寸要等首次布局后才知道。
        if (cards is not null)
        {
            var grid = cards;
            void Reflow() => ApplyCardColumns(grid, scroller.Bounds.Width);
            scroller.SizeChanged += (_, _) => Reflow();
            Reflow();
        }

        return scroller;
    }

    /// <summary>卡片最小宽度（再窄就换行）。约等于「头像 + 名称 + 开关 + 箭头」能排下的宽度。</summary>
    private const double MinCardWidth = 330;

    /// <summary>最多几列。太宽再切成更多列，卡片会扁得不成样子。</summary>
    private const int MaxCardColumns = 4;

    /// <summary>body 的左右 Margin 各 20，可用宽度要减掉这 40。</summary>
    private const double ContentPadding = 40;

    /// <summary>
    /// 按可用宽度决定列数，尽量铺满一行。宽度还没量出来（为 0）时不动，
    /// 等 SizeChanged 再来 —— 否则会把列数错误地锁在 1。
    /// </summary>
    private static void ApplyCardColumns(UniformGrid grid, double width)
    {
        if (width <= 0) return;

        var columns = (int)Math.Floor((width - ContentPadding) / MinCardWidth);
        columns = Math.Clamp(columns, 1, MaxCardColumns);

        if (grid.Columns != columns)
            grid.Columns = columns;
    }

    private Control BuildEmptyState()
    {
        var content = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 40),
        };
        content.Children.Add(new TextBlock
        {
            Text = "还没有配置任何模型服务",
            FontSize = 14,
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "添加一个服务商（如 DeepSeek、月之暗面），再从中选模型即可开始对话。",
            FontSize = 12,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var addBtn = CreateTextButton("+ 添加服务商", () => _ = AddProviderAsync());
        addBtn.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(addBtn);

        return new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 8, 0, 0),
            Child = content,
        };
    }

    private async Task<Control> CreateProviderCardAsync(AiProvider provider)
    {
        var modelCount = await ModelRepository.CountByProviderAsync(provider.Id);

        var name = new TextBlock
        {
            Text = provider.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var stats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        stats.Children.Add(new TextBlock
        {
            Text = $"模型数量：{modelCount}",
            FontSize = 12,
            Foreground = TextMuted,
        });
        if (BalanceFor(provider) is { } balance)
            stats.Children.Add(new TextBlock { Text = $"余额：{balance}", FontSize = 12, Foreground = TextMuted });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(CreateAvatar(provider.Name));

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, stats },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 启用开关：控制这个服务商是否参与模型选择器。
        // 停用后其名下模型不再被 ModelCatalog 列出（那里本来就会跳过 IsEnabled=false 的服务商）。
        var toggle = new ToggleSwitch
        {
            IsChecked = provider.IsEnabled,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,       // Fluent 的 ToggleSwitch 默认给文字留了宽度，不清掉会撑开卡片
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        // 右侧箭头：提示这张卡可以点进去（用字形而不是按钮，免得和卡片整体点击打架）
        var chevron = new TextBlock
        {
            Text = "\uF2B1",                 // Fluent System Icons：chevron_right
            FontFamily = AppFonts.Icons,
            FontSize = 14,
            Foreground = TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        var card = new Border
        {
            // 不设固定宽度：宽度由外层 UniformGrid 均分决定，卡片才能自适应
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(12),
            Background = CardBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 1 2 0 #14000000"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = new SolidColorBrush(Color.Parse("#C7D2DA"));
            card.BoxShadow = BoxShadows.Parse("0 3 10 0 #22000000");
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = LineBrush;
            card.BoxShadow = BoxShadows.Parse("0 1 2 0 #14000000");
        };
        card.PointerPressed += (_, e) =>
        {
            // 落在开关（或任何按钮）上的按下不算「点卡片」，否则拨一下开关就跳进详情页了
            if (IsInsideInteractive(e.Source as Visual, card)) return;

            e.Handled = true;
            _ = ShowProviderDetailAsync(provider);
        };

        // 拨开关即保存。这里不整页重建：重建会把开关的动画打断，视觉上像「点了一下没反应」。
        // 停用的卡片整体压暗，状态一眼可见。
        toggle.IsCheckedChanged += async (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            provider.IsEnabled = enabled;
            card.Opacity = enabled ? 1 : 0.6;
            ToolTip.SetTip(toggle, enabled ? "已启用，其模型会出现在模型选择器里" : "已停用，其模型不会出现在模型选择器里");

            try
            {
                await ProviderRepository.UpdateAsync(provider);
            }
            catch
            {
                // 落库失败就把开关拨回去，别让界面显示一个没生效的状态
                toggle.IsChecked = !enabled;
                provider.IsEnabled = !enabled;
            }
        };

        card.Opacity = provider.IsEnabled ? 1 : 0.6;

        return card;
    }

    /// <summary>
    /// 从 <paramref name="source"/> 往上找（到 <paramref name="boundary"/> 为止）有没有可交互控件
    /// （开关 / 按钮 / 勾选框）。卡片整体是可点的，这些控件上的操作必须排除在外。
    /// </summary>
    private static bool IsInsideInteractive(Visual? source, Visual boundary)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, boundary); v = v.GetVisualParent())
            if (v is ToggleSwitch or Button or CheckBox) return true;

        return false;
    }

    /// <summary>服务商头像：没有 logo 资源，用名称首字 + 一个稳定的底色代替。</summary>
    private static Control CreateAvatar(string providerName)
    {
        var initial = string.IsNullOrWhiteSpace(providerName)
            ? "?"
            : providerName.Trim()[..1].ToUpperInvariant();

        // 用名字的哈希选色：同名永远同色，重启也不变
        var index = (providerName ?? "").Aggregate(0, (acc, c) => acc + c) % AvatarPalette.Length;

        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(AvatarPalette[index]),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>
    /// 服务商余额。目前没有任何数据源（<c>ai_provider</c> 表没这一列，也没有查询接口），
    /// 所以一律返回 <c>null</c>，卡片上就不会出现「余额」那一行 —— 显示一个编造的数字比不显示更糟。
    /// 将来接入余额查询（部分服务商有类似 <c>/user/balance</c> 的接口）后，在这里返回真实值即可，
    /// 卡片的渲染分支已经就位。
    /// </summary>
    private static string? BalanceFor(AiProvider provider) => null;

    // ============================================================
    // 详情层：服务商下的模型
    // ============================================================

    private async Task<Control> BuildProviderDetailAsync(AiProvider provider)
    {
        var models = await ModelRepository.GetByProviderAsync(provider.Id);

        var body = new StackPanel { Margin = new Thickness(20) };

        // 第一行：返回。单独一行，不和标题挤在一起。
        var back = CreateTextButton("返回", () => _ = ShowProviderListAsync(), glyph: "\uF2AB");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(-8, 0, 0, 0);   // 抵掉按钮自身内边距，让文字与下方标题左对齐
        body.Children.Add(back);

        // 第二行：服务商名 + 模型数量
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = provider.Name,
            FontSize = 18,
            FontWeight = FontWeight.DemiBold,
            Foreground = TextPrimary,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = $"模型数量：{models.Count}",
            FontSize = 12,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            body.Children.Add(new TextBlock
            {
                Text = provider.Endpoint,
                FontSize = 12,
                Foreground = TextFaint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        // 动作行：紧贴表格上方右对齐。放这里而不是页面顶部 —— 它作用于下面这张表，
        // 摆在表上方因果一目了然，也消掉了原先头部到表格之间那一大块空白。
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        // 四个按钮同一套样式（都是无底色的文字按钮），「获取模型」配一个同步图标。
        actions.Children.Add(CreateTextButton("获取模型", () => _ = FetchModelsAsync(provider), glyph: "\uF191"));
        actions.Children.Add(CreateTextButton("+ 添加模型", () => _ = AddModelAsync(provider)));
        actions.Children.Add(CreateTextButton("编辑", () => _ = EditProviderAsync(provider)));
        actions.Children.Add(CreateTextButton("删除", () => _ = DeleteProviderAsync(provider), danger: true));
        body.Children.Add(actions);

        body.Children.Add(CreateModelTable(provider, models));

        return new SmoothScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
    }

    // ---- 模型表格 ----

    private Control CreateModelTable(AiProvider provider, List<AiModel> models)
    {
        var header = new Border
        {
            Background = HeaderBrush,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Height = 35,
            Child = CreateModelRowHeader(),
        };

        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (models.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "暂无模型，点击「+ 添加模型」或「获取模型」添加",
                FontSize = 12,
                Foreground = TextFaint,
                Margin = new Thickness(20, 12, 0, 12),
            });
        }
        else
        {
            foreach (var model in models)
                rows.Children.Add(CreateModelRow(provider, model));
        }

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 35,
            CornerRadius = new CornerRadius(8),
            BoxShadow = BoxShadows.Parse("0 0 1 0 #80808080"),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new StackPanel { Children = { header, rows } },
        };
    }

    // 注意：操作列必须是固定宽度而非 Auto —— 表头与内容行是两个独立 Grid，
    // 若为 Auto，表头（"操作"）与内容行（按钮）量出的宽度不同，会导致前方 Star 列错位。
    private static Grid CreateModelGrid()
        => new() { ColumnDefinitions = new ColumnDefinitions("2*,1*,1*,1*,0.8*,1.4*,140") };

    private Control CreateModelRowHeader()
    {
        var grid = CreateModelGrid();
        string[] headers = { "模型名称", "每百万输入", "缓存命中", "每百万输出", "上下文", "模型能力", "操作" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = headers[i],
                FontSize = 12,
                FontWeight = FontWeight.DemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
            };
            if (i == headers.Length - 1)
                cell.Margin = new Thickness(4, 0, 8, 0);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Control CreateModelRow(AiProvider provider, AiModel model)
    {
        var grid = CreateModelGrid();
        grid.Margin = new Thickness(0, 10, 0, 0);

        // 模型名称：调用 id 不单独列出，悬停时提示显示
        grid.Children.Add(CreateModelNameCell(provider, model));

        grid.Children.Add(CreateModelCell(1, FormatPriceCell(model.PriceInput, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(2, FormatPriceCell(model.PriceCacheHit, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(3, FormatPriceCell(model.PriceOutput, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(4, FormatContextWindow(model.ContextWindow), gray: true));
        grid.Children.Add(CreateModelCell(5, FormatCapabilities(model.Capabilities), gray: true));

        var ops = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 8, 0),
            Children =
            {
                CreateTextButton("编辑", () => _ = EditModelAsync(provider, model)),
                CreateTextButton("删除", () => _ = DeleteModelAsync(provider, model), danger: true),
            },
        };
        Grid.SetColumn(ops, 6);
        grid.Children.Add(ops);

        return grid;
    }

    /// <summary>
    /// 模型名称单元格：名称 + 配置来源提示 + 开发商。
    ///
    /// 提示图标是整个「表驱动配置」能不能自我维持的关键：配置缺失的模型必须一眼看得出来，
    /// 而且点一下就能去补。一个点不开、补不掉的提示，用户看几次就学会无视了，
    /// 那这套机制就白做了。
    /// </summary>
    private Control CreateModelNameCell(AiProvider provider, AiModel model)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(20, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (CreateConfigHint(provider, model) is { } hint)
            row.Children.Add(hint);

        // 开发商（决定头像的那个「谁的模型」）。没识别出来就不占位置。
        var vendorName = VendorIcons.VendorName(model.Vendor);
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            row.Children.Add(new TextBlock
            {
                Text = vendorName,
                FontSize = 11,
                Foreground = TextFaint,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var tip = new List<string> { $"调用 id：{model.CallId}" };
        if (model.MaxOutputTokens > 0) tip.Add($"最大输出：{model.MaxOutputTokens}");
        if (!string.IsNullOrWhiteSpace(model.Vendor)) tip.Add($"开发商：{model.Vendor}");
        ToolTip.SetTip(row, string.Join("\n", tip));

        Grid.SetColumn(row, 0);
        return row;
    }

    /// <summary>
    /// 配置来源提示图标。只有「表里没认全」的行才返回控件：
    /// 黄叹号 = 只反推出了开发商，配置要补；灰问号 = 内置表完全不认识。
    /// 点一下直接进编辑页，补完保存后提示即消失（保存会把来源改成 Manual）。
    /// </summary>
    private Control? CreateConfigHint(AiProvider provider, AiModel model)
    {
        var (glyph, color, text) = model.ConfigOrigin switch
        {
            ModelConfigOrigin.VendorOnly => ("\uF86A", "#D97706",
                "只认出了开发商，其余配置缺失，需要补充"),
            ModelConfigOrigin.Unmatched => ("\uF63E", "#9CA3AF",
                "内置模型表里没有这个模型，配置需要补充"),
            _ => ("", "", ""),
        };

        if (glyph.Length == 0) return null;

        var icon = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = AppFonts.Icons,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse(color)),
            },
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinWidth = 0,
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        ToolTip.SetTip(icon, text + "\n点击编辑该模型");
        icon.Click += (_, _) => _ = EditModelAsync(provider, model);
        return icon;
    }

    /// <summary>上下文窗口展示：≥1M 简写为 M，否则原样显示，未知返回"未知"。</summary>
    private static string FormatContextWindow(long window)
    {
        if (window <= 0) return "未知";
        return window >= 1_000_000 ? $"{window / 1_000_000.0:0.#}M" : window.ToString();
    }

    private static TextBlock CreateModelCell(int column, string text, bool isBold = false, bool gray = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = isBold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = gray ? TextMuted : TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(20, 0, 0, 0),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static string FormatCapabilities(List<string> caps)
    {
        if (caps.Count == 0) return "—";
        return string.Join("、", caps.Select(c => ModelCapabilities.Labels.TryGetValue(c, out var label) ? label : c));
    }

    private static string FormatPriceCell(double? value, string currency)
    {
        if (value is null) return "—";
        string cur = currency == "USD" ? "$" : "¥";
        return $"{cur}{value.Value.ToString("0.##")}";
    }

    // ---- 按钮 ----

    /// <summary>无底色的文字按钮（次级动作）。<paramref name="glyph"/> 给定时在最前面加一个图标字形。</summary>
    private static Button CreateTextButton(string text, Action onClick, bool danger = false, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (glyph is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = AppFonts.Icons,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = danger ? TextDanger : TextAccent,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = danger ? TextDanger : TextAccent,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            Height = 28,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }


    // ============================================================
    // 动作
    // ============================================================

    private async Task AddProviderAsync()
    {
        var dialog = new ProviderEditWindow();
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } provider)
        {
            await ProviderRepository.CreateAsync(provider);
            await ShowProviderListAsync();
        }
    }

    private async Task EditProviderAsync(AiProvider provider)
    {
        var dialog = new ProviderEditWindow(provider);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } updated)
        {
            await ProviderRepository.UpdateAsync(updated);
            // 就停在详情页：刚编辑完的服务商，用户多半还要继续配模型
            await ShowProviderDetailAsync(updated);
        }
    }

    private async Task DeleteProviderAsync(AiProvider provider)
    {
        // 删服务商会把它下面所有模型一起带走，且不可撤销 —— 先问一次
        var count = await ModelRepository.CountByProviderAsync(provider.Id);
        var message = count == 0
            ? $"确定删除服务商「{provider.Name}」？此操作不可撤销。"
            : $"确定删除服务商「{provider.Name}」？它下面的 {count} 个模型会一并删除，此操作不可撤销。";

        if (!await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, message, "删除服务商"))
            return;

        await ProviderRepository.DeleteAsync(provider.Id);
        await ShowProviderListAsync();
    }

    /// <summary>打开「获取模型」窗口；加了东西才需要刷新（一个没加就不必重绘）。</summary>
    private async Task FetchModelsAsync(AiProvider provider)
    {
        var existing = (await ModelRepository.GetByProviderAsync(provider.Id))
            .Select(m => m.CallId)
            .ToList();

        var dialog = new FetchModelsWindow(provider, existing);
        await ShowDialogAsync(dialog);

        if (dialog.AddedCount > 0)
            await ShowProviderDetailAsync(provider);
    }

    private async Task AddModelAsync(AiProvider provider)
    {
        var dialog = new ModelEditWindow(provider.Id, null);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } model)
        {
            await ModelRepository.CreateAsync(model);
            await ShowProviderDetailAsync(provider);
        }
    }

    private async Task EditModelAsync(AiProvider provider, AiModel model)
    {
        var dialog = new ModelEditWindow(provider.Id, model);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } updated)
        {
            await ModelRepository.UpdateAsync(updated);
            await ShowProviderDetailAsync(provider);
        }
    }

    private async Task DeleteModelAsync(AiProvider provider, AiModel model)
    {
        var message = $"确定删除模型「{model.DisplayName}」？此操作不可撤销。";
        if (!await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, message, "删除模型"))
            return;

        await ModelRepository.DeleteAsync(model.Id);
        await ShowProviderDetailAsync(provider);
    }

    private async Task ShowDialogAsync(Window dialog)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }
}
