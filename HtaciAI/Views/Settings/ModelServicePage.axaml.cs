using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
///   <item><b>列表层</b>：每个服务商一张卡片（名称、模型数量）；卡片右侧「···」菜单里是启用/禁用、设置、删除。</item>
///   <item><b>详情层</b>：服务商名称与设置入口、API 密钥、端点列表，以及该服务商的模型表格。</item>
/// </list>
///
/// 两层共用同一个宿主控件（<c>Host</c>），靠整体重建来切换与刷新 ——
/// 服务商与模型都是低频操作，重建的代价可以忽略，换来的是「界面永远和库一致」。
///
/// 为什么把密钥与端点留在详情页：配服务商这件事天然是「一边看着模型列表一边调地址」的，
/// 就地编辑省掉往返，也让「当前这页在改谁」不再有歧义。名称与那些低频开关收进设置窗。
/// 留在弹窗里的只有真正低频的服务商级开关（见 <see cref="ProviderSettingsWindow"/>）。
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

    /// <summary>停用的卡片：整块灰底、无阴影，与启用的白底卡片一眼分得开。</summary>
    private static readonly IBrush CardDisabledBrush = new SolidColorBrush(Color.Parse("#F1F2F4"));

    /// <summary>就地编辑输入框的描边色。与「添加端点」那种虚线框按钮用同一个值 —— 两者外观必须一致。</summary>
    private static readonly IBrush InputBorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));

    /// <summary>输入框底色：半透明白。默认的实白在这个浅灰页面上显得很「重」。</summary>
    private static readonly IBrush InputBrush = new SolidColorBrush(Color.Parse("#80FFFFFF"));

    /// <summary>默认标记的胶囊底色。</summary>
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#F0F1F3"));

    /// <summary>只在首次挂载时构建；之后切换设置页的导航项不应把用户从详情页弹回列表。</summary>
    private bool _built;

    /// <summary>当前显示的是哪个服务商的详情；null = 在列表层。用于「设置」保存后决定重建哪一层。</summary>
    private string? _detailProviderId;

    public ModelServicePage()
    {
        InitializeComponent();

        // 点空白处让输入框失焦。没有这一步，地址 / 密钥框只能靠「再点另一个输入框」
        // 才能退出编辑态，而退出编辑态就是触发保存的时机，用户会以为改的东西没存。
        TextBoxFocusCleanup.Attach(this);

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
    {
        _detailProviderId = null;
        Host.Content = await BuildProviderListAsync();
    }

    private async Task ShowProviderDetailAsync(AiProvider provider)
    {
        _detailProviderId = provider.Id;
        Host.Content = await BuildProviderDetailAsync(provider);
    }

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

        // 启用的排在前面：停用的服务商留在列表里是为了「还记得它存在」，不该抢占视线。
        providers = providers.OrderByDescending(p => p.IsEnabled).ToList();

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

    /// <summary>卡片最小宽度（再窄就换行）。约等于「头像 + 名称 + 菜单按钮」能排下的宽度。</summary>
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
        var enabled = provider.IsEnabled;

        // 停用的卡片整体褪色：名称与数字都用弱化色，头像也压暗，
        // 这样「灰着一张」本身就是状态，不需要再挂一个开关来解释。
        var name = new TextBlock
        {
            Text = provider.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = enabled ? TextPrimary : TextMuted,
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
            Foreground = enabled ? TextMuted : TextFaint,
        });
        if (BalanceFor(provider) is { } balance)
            stats.Children.Add(new TextBlock
            {
                Text = $"余额：{balance}",
                FontSize = 12,
                Foreground = enabled ? TextMuted : TextFaint,
            });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var avatar = ProviderAvatar.Create(provider.Name, provider.Icon, 40);
        if (!enabled) avatar.Opacity = 0.5;
        grid.Children.Add(avatar);

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, stats },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 「···」菜单：启用/禁用、设置、删除。
        // 启用开关原先直接摆在卡片上，但它和「点卡片进详情」争夺同一块点击区域（要靠
        // IsInsideInteractive 才能分开），现在收进菜单，卡片就只有「进去」一个含义。
        // 平时不显示（Opacity 0），鼠标进卡片才浮出来 —— 它是个次要入口，不该和内容抢位置。
        var more = CreateCardMenuButton(provider);
        more.Opacity = 0;
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        var card = new Border
        {
            // 不设固定宽度：宽度由外层 UniformGrid 均分决定，卡片才能自适应
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(12),
            // 启用的卡片是白底 —— 透明底在浅色页面上会把卡片和背景糊成一片，看不出边界
            Background = enabled ? CardBrush : CardDisabledBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            // 停用的卡片不要投影：投影会把它重新「抬」到前景
            BoxShadow = enabled ? BoxShadows.Parse("0 1 2 0 #14000000") : default,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        if (enabled)
        {
            card.PointerEntered += (_, _) =>
            {
                card.BorderBrush = new SolidColorBrush(Color.Parse("#C7D2DA"));
                card.BoxShadow = BoxShadows.Parse("0 3 10 0 #22000000");
                more.Opacity = 1;
            };
            card.PointerExited += (_, _) =>
            {
                card.BorderBrush = LineBrush;
                card.BoxShadow = BoxShadows.Parse("0 1 2 0 #14000000");
                more.Opacity = 0;
            };
        }
        else
        {
            // 停用的卡片也要能进菜单（启用/设置/删除都在里面），所以照样悬停显示
            card.PointerEntered += (_, _) => more.Opacity = 1;
            card.PointerExited += (_, _) => more.Opacity = 0;
        }

        card.PointerPressed += (_, e) =>
        {
            // 落在按钮上的按下不算「点卡片」，否则点一下「···」就跳进详情页了
            if (IsInsideInteractive(e.Source as Visual, card)) return;

            e.Handled = true;
            _ = ShowProviderDetailAsync(provider);
        };

        return card;
    }

    /// <summary>卡片右侧的「···」按钮，点开是启用/禁用、设置、删除。</summary>
    private Control CreateCardMenuButton(AiProvider provider)
    {
        var menu = new MenuFlyout();
        MenuTheme.Apply(menu);

        var toggleItem = new MenuItem { Header = provider.IsEnabled ? "禁用" : "启用" };
        toggleItem.Click += (_, _) => _ = SetProviderEnabledAsync(provider, !provider.IsEnabled);
        menu.Items.Add(toggleItem);

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => _ = OpenProviderSettingsAsync(provider);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "删除" };
        deleteItem.Click += (_, _) => _ = DeleteProviderAsync(provider);
        menu.Items.Add(deleteItem);

        var glyph = new TextBlock
        {
            Text = "\uE825",                 // Fluent System Icons：more_horizontal
            FontFamily = AppFonts.Icons,
            FontSize = 14,
            Foreground = TextFaint,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = glyph,
        };

        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(Color.Parse("#14000000"));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) =>
        {
            // 先吃掉事件：否则会冒泡到整张卡片，变成「点菜单却先进了详情页」
            e.Handled = true;
            menu.ShowAt(button);
        };

        return button;
    }

    /// <summary>
    /// 从 <paramref name="source"/> 往上找（到 <paramref name="boundary"/> 为止）有没有可交互控件
    /// （按钮 / 勾选框）。卡片整体是可点的，这些控件上的操作必须排除在外。
    /// </summary>
    private static bool IsInsideInteractive(Visual? source, Visual boundary)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, boundary); v = v.GetVisualParent())
            if (v is Button or CheckBox) return true;

        return false;
    }

    /// <summary>
    /// 服务商余额。目前没有任何数据源（<c>ai_provider</c> 表没这一列，也没有查询接口），
    /// 所以一律返回 <c>null</c>，卡片上就不会出现「余额」那一行 —— 显示一个编造的数字比不显示更糟。
    /// 将来接入余额查询（部分服务商有类似 <c>/user/balance</c> 的接口）后，在这里返回真实值即可。
    /// </summary>
    private static string? BalanceFor(AiProvider provider) => null;

    // ============================================================
    // 详情层：服务商配置 + 模型
    // ============================================================

    private async Task<Control> BuildProviderDetailAsync(AiProvider provider)
    {
        var models = await ModelRepository.GetByProviderAsync(provider.Id);

        var body = new StackPanel { Margin = new Thickness(20) };

        // ---- 第一行：返回 ----
        var back = CreateTextButton("返回", () => _ = ShowProviderListAsync(), glyph: "\uF2AB");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(-8, 0, 0, 0);   // 抵掉按钮自身内边距，让文字与下方标题左对齐
        body.Children.Add(back);

        // ---- 第二行：名称（就地编辑）+ 模型数量 + 设置齿轮 ----
        body.Children.Add(CreateNameRow(provider, models.Count));

        // ---- API 密钥 ----
        body.Children.Add(CreateSectionLabel("API 密钥"));
        body.Children.Add(CreateApiKeyRow(provider, models));

        // ---- API 地址（端点列表）----
        body.Children.Add(CreateSectionLabel("API 地址", "每个端点一种协议；请求只走标为「默认」的那个"));
        body.Children.Add(CreateEndpointList(provider));

        // ---- 模型 ----
        var modelsHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 22, 0, 0),
        };
        modelsHeader.Children.Add(new TextBlock
        {
            Text = "模型",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var syncAdd = CreateSyncAddButton(provider);
        Grid.SetColumn(syncAdd, 1);
        modelsHeader.Children.Add(syncAdd);
        body.Children.Add(modelsHeader);

        body.Children.Add(CreateModelTable(provider, models));

        return new SmoothScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
    }

    /// <summary>
    /// 标题行：名称（只读，改名在设置窗里）+ 紧贴的设置齿轮 + 模型数量。
    ///
    /// 名称原先是个透明无边框的 TextBox：看起来像标签、点进去却能改，既容易误触，
    /// 又让名称与齿轮之间空出一大截。现在名称是纯文本，齿轮紧挨着它，改名统一走设置窗。
    /// </summary>
    private Control CreateNameRow(AiProvider provider, int modelCount)
    {
        var name = new TextBlock
        {
            Text = provider.Name,
            FontSize = 18,
            FontWeight = FontWeight.DemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };

        var settings = CreateIconButton("\uF6AA", "服务商设置", () => _ = OpenProviderSettingsAsync(provider));
        settings.Margin = new Thickness(0);

        var count = new TextBlock
        {
            Text = $"模型数量：{modelCount}",
            FontSize = 12,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 6, 0, 14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(name);
        row.Children.Add(settings);
        row.Children.Add(count);

        return row;
    }

    /// <summary>小节标题；<paramref name="hint"/> 是紧随其后的灰色小字说明。</summary>
    private static Control CreateSectionLabel(string text, string? hint = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
        };
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (hint is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = TextFaint,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return row;
    }

    // ---- 端点列表 ----

    /// <summary>
    /// 端点列表：每个端点上方一行「协议名 + 默认标记」，下面一个地址输入框。
    /// 标签写在输入框<i>上方</i>而不是塞进 ComboBox，是因为协议一旦选定就不该随手改掉 ——
    /// 它决定了请求体长什么样，改错的表现是「模型能连上但说什么都不对」。
    /// </summary>

    // ---- API 密钥 ----

    /// <summary>
    /// 密钥行：遮罩显示的输入框 + 眼睛（临时看明文）+ 连接测试按钮。
    ///
    /// 默认<b>只显示首尾</b>（<c>sk-****CW6R</c>）而不是掩成一片星号：掩成一片就看不出
    /// 「填的是哪个 key」，多份密钥来回切时很容易把 A 站的 key 填到 B 站上。
    /// 点进输入框即切明文可编辑，离开就存回并重新遮上 —— 想改的人点进去就行，
    /// 只想确认的人则不会一直看到整串密钥。
    /// </summary>
    private Control CreateApiKeyRow(AiProvider provider, List<AiModel> models)
    {
        var box = new TextBox
        {
            Height = 34,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            FontSize = 12,
            Classes = { "center-text" },
            PlaceholderText = "sk-...（留空 = 不发送鉴权头）",
            Background = InputBrush,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var eyeGlyph = new TextBlock
        {
            Text = "\uE5F3",   // eye
            FontFamily = AppFonts.Icons,
            FontSize = 13,
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var revealed = false;

        void ShowMasked()
        {
            revealed = false;
            box.IsReadOnly = true;
            box.Text = MaskSecret(provider.ApiKey);
            eyeGlyph.Text = "\uE5F3";
        }

        void ShowPlain()
        {
            revealed = true;
            box.IsReadOnly = false;
            box.Text = provider.ApiKey ?? "";
            box.CaretIndex = box.Text.Length;
            eyeGlyph.Text = "\uE5F6";   // eye_off：当前显示的是明文
        }

        ShowMasked();

        // 点进去就是为了改：当场切明文，省掉「先按眼睛才能编辑」这一步
        box.GotFocus += (_, _) =>
        {
            if (!revealed) ShowPlain();
        };

        // 失焦即保存，然后重新遮上：就地编辑没有「保存」按钮，改完点别处就该生效。
        box.LostFocus += async (_, _) =>
        {
            if (!revealed) return;

            var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();
            if (!string.Equals(value, provider.ApiKey, StringComparison.Ordinal))
            {
                provider.ApiKey = value;
                await SaveProviderAsync(provider);
            }

            ShowMasked();
        };

        var eye = new Button
        {
            Content = eyeGlyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(eye, "显示 / 隐藏完整密钥");
        eye.Click += (_, _) =>
        {
            if (revealed)
            {
                ShowMasked();
            }
            else
            {
                ShowPlain();
                box.Focus();   // 一并给焦点：看完顺手就能改
            }
        };

        var test = CreateOutlineButton("检测", () => _ = OpenConnectivityTestAsync(provider, models));
        test.Margin = new Thickness(6, 0, 0, 0);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        row.Children.Add(box);
        Grid.SetColumn(eye, 1);
        row.Children.Add(eye);
        Grid.SetColumn(test, 2);
        row.Children.Add(test);

        return row;
    }

    /// <summary>
    /// 把密钥折成「首三 + **** + 尾四」。短到折了等于没折（≤ 8 位）就整串打码 ——
    /// 那种长度下露首尾等于露全部。
    /// </summary>
    private static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (secret.Length <= 8) return new string('*', secret.Length);
        return secret[..3] + "****" + secret[^4..];
    }

    /// <summary>描边小按钮（无底色、1px 边框），用于「检测」这类与输入框同排的动作。</summary>
    private Control CreateOutlineButton(string text, Action onClick, string? glyph = null)
    {
        var content = new TextBlock
        {
            FontSize = 12,
            Foreground = TextAccent,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inlines = content.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        if (glyph is not null)
            inlines.Add(new Avalonia.Controls.Documents.Run(glyph)
            {
                FontFamily = AppFonts.Icons,
                FontSize = 12,
            });
        inlines.Add(new Avalonia.Controls.Documents.Run(glyph is null ? text : " " + text));

        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            Height = 34,
            Padding = new Thickness(12, 0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>打开连接测试窗：模型列表取当前服务商已配置的模型。</summary>
    private async Task OpenConnectivityTestAsync(AiProvider provider, List<AiModel> models)
    {
        // 重新读一次端点与密钥：页面上那几个输入框是失焦保存的，
        // 直接拿内存对象可能落后于刚敲进去、还没失焦的内容。
        if (await ProviderRepository.GetAsync(provider.Id) is { } latest)
        {
            provider.Endpoints = latest.Endpoints;
            provider.ApiKey = latest.ApiKey;
            provider.ThinkingField = latest.ThinkingField;
        }

        var dialog = new ConnectivityTestWindow(provider, models);
        await ShowDialogAsync(dialog);
    }

    private Control CreateEndpointList(AiProvider provider)
    {
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };

        if (provider.Endpoints.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "还没有端点。先添加一个，填上地址即可开始使用。",
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var endpoint in provider.Endpoints)
                list.Children.Add(CreateEndpointEditor(provider, endpoint));
        }

        list.Children.Add(CreateAddEndpointButton(provider));
        return list;
    }

    private Control CreateEndpointEditor(AiProvider provider, AiProviderEndpoint endpoint)
    {
        var label = new TextBlock
        {
            Text = ProtocolLabel(endpoint.Protocol),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var remove = CreateIconButton("\uF36A", "删除这个端点", () => _ = RemoveEndpointAsync(provider, endpoint));
        remove.Margin = new Thickness(0, 0, 0, 0);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        header.Children.Add(label);
        var badge = CreateDefaultBadge(provider, endpoint);
        badge.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);

        var box = new TextBox
        {
            Height = 34,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            FontSize = 12,
            Classes = { "center-text" },
            Text = endpoint.Endpoint,
            PlaceholderText = "https://api.example.com（填到域名或完整接口地址都可以）",
            Background = InputBrush,
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        box.LostFocus += async (_, _) =>
        {
            var value = box.Text?.Trim() ?? "";
            if (string.Equals(value, endpoint.Endpoint, StringComparison.Ordinal)) return;

            endpoint.Endpoint = value;
            await SaveProviderAsync(provider);
        };

        return new StackPanel { Spacing = 0, Children = { header, box } };
    }

    /// <summary>
    /// 默认标记：默认的那个是实心灰胶囊（只是个状态标签，不可点）；
    /// 非默认的描边胶囊可点，点了就把这个端点切成默认。
    ///
    /// 用胶囊而不是「【默认】」这种括号文字，是因为括号读起来像正文的一部分，
    /// 而这其实是个控件/状态，形状要能一眼区分出来。
    /// </summary>
    private Control CreateDefaultBadge(AiProvider provider, AiProviderEndpoint endpoint)
    {
        if (endpoint.IsDefault)
        {
            return new Border
            {
                Background = BadgeBrush,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "默认",
                    FontSize = 11,
                    Foreground = TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        var text = new TextBlock
        {
            Text = "设为默认",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#374151")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var pill = new Border
        {
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
        };

        pill.PointerEntered += (_, _) => pill.Background = new SolidColorBrush(Color.Parse("#0A000000"));
        pill.PointerExited += (_, _) => pill.Background = Brushes.Transparent;
        pill.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            foreach (var item in provider.Endpoints)
                item.IsDefault = ReferenceEquals(item, endpoint);

            await SaveProviderAsync(provider);
            await ShowProviderDetailAsync(provider);
        };

        return pill;
    }

    /// <summary>
    /// 「+ 添加端点」：虚线框按钮，点开是协议菜单（不新开窗口 —— 这里只是选一个协议名）。
    /// 协议不做成默认值预先创建，是因为「先有端点再填地址」比「先选协议再填地址」更容易改错。
    /// </summary>
    private Control CreateAddEndpointButton(AiProvider provider)
    {
        var menu = new MenuFlyout();
        MenuTheme.Apply(menu);
        foreach (var protocol in new[]
                 {
                     ModelProtocol.OpenAI, ModelProtocol.OpenAIResponses, ModelProtocol.LMStudio,
                     ModelProtocol.Anthropic,
                 })
        {
            var item = new MenuItem { Header = ProtocolLabel(protocol) };
            var chosen = protocol;
            item.Click += async (_, _) =>
            {
                provider.Endpoints.Add(new AiProviderEndpoint
                {
                    ProviderId = provider.Id,
                    Protocol = chosen,
                    // 第一个端点天然是默认端点：多端点里必须有一个在生效，否则配了也用不上。
                    IsDefault = provider.Endpoints.Count == 0,
                });
                await SaveProviderAsync(provider);
                await ShowProviderDetailAsync(provider);
            };
            menu.Items.Add(item);
        }

        var button = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(8),
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = IconLabel("\uF10A", "添加端点", centered: true),
        };

        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(Color.Parse("#0A000000"));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            menu.ShowAt(button);
        };

        return button;
    }

    private async Task RemoveEndpointAsync(AiProvider provider, AiProviderEndpoint endpoint)
    {
        provider.Endpoints.Remove(endpoint);

        // 删掉的若是默认端点，顺位让第一个接任 —— 不能留下「一个端点都没有默认」的状态。
        if (endpoint.IsDefault && provider.Endpoints.Count > 0)
            provider.Endpoints[0].IsDefault = true;

        await SaveProviderAsync(provider);
        await ShowProviderDetailAsync(provider);
    }

    private static string ProtocolLabel(ModelProtocol protocol) => protocol switch
    {
        ModelProtocol.OpenAIResponses => "OpenAI Responses",
        ModelProtocol.LMStudio => "LM Studio",
        ModelProtocol.Anthropic => "Anthropic",
        _ => "OpenAI",
    };

    /// <summary>
    /// 「同步模型 | +」双段按钮。两件事同属「往这个服务商里加模型」，
    /// 合成一个控件比并排两个文字按钮更清楚地表达「一边是批量、一边是单个」。
    /// </summary>
    private Control CreateSyncAddButton(AiProvider provider)
    {
        var hasEndpoint = provider.Endpoints.Count > 0;

        var sync = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 0),
            Height = 30,
            MinWidth = 0,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            IsEnabled = hasEndpoint,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = hasEndpoint ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow),
            Content = IconLabel("\uF191", "同步模型"),
        };
        if (!hasEndpoint) ToolTip.SetTip(sync, "先添加一个端点再同步模型");
        sync.Click += (_, _) => _ = FetchModelsAsync(provider);

        var add = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 0),
            Height = 30,
            MinWidth = 0,
            CornerRadius = new CornerRadius(0, 7, 7, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = "\uF10A",
                FontFamily = AppFonts.Icons,
                FontSize = 12,
                Foreground = TextAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(add, "添加模型");
        add.Click += (_, _) => _ = AddModelAsync(provider);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto") };
        row.Children.Add(sync);
        var divider = new Border
        {
            Width = 1,
            Background = LineBrush,
            Margin = new Thickness(0, 6, 0, 6),
        };
        Grid.SetColumn(divider, 1);
        row.Children.Add(divider);
        Grid.SetColumn(add, 2);
        row.Children.Add(add);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = InputBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Child = row,
        };
    }

    /// <summary>
    /// 图标 + 文字的单个 TextBlock。用 Inlines 而不是两个 TextBlock 并排：
    /// 同一段落共基线，图标不会相对文字偏高/偏低；分开排版时这个偏差在不同字号下都很难消除。
    /// </summary>
    private static TextBlock IconLabel(string glyph, string text, bool centered = false)
        => new()
        {
            FontSize = 12,
            Foreground = TextAccent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run(glyph)
                {
                    FontFamily = AppFonts.Icons,
                    FontSize = 12,
                },
                new Avalonia.Controls.Documents.Run(" " + text),
            },
        };

    /// <summary>图标按钮（齿轮 / 编辑 / 删除之类），无底色，悬停才浮出一块浅灰。</summary>
    private static Control CreateIconButton(string glyph, string tip, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = AppFonts.Icons,
                FontSize = 13,
                Foreground = danger ? TextDanger : TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>落库的唯一出口：任一处就地编辑都走这里，失败统一提示一次。</summary>
    private async Task SaveProviderAsync(AiProvider provider)
    {
        try
        {
            await ProviderRepository.UpdateAsync(provider);
        }
        catch (Exception ex)
        {
            await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window,
                $"保存服务商「{provider.Name}」失败：{ex.Message}", "保存失败");
        }
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
                Text = "暂无模型，点右上角「同步模型」或「+」添加",
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
            Spacing = 0,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateIconButton("\uF3DE", "编辑模型", () => _ = EditModelAsync(provider, model)),
                CreateIconButton("\uF34D", "删除模型", () => _ = DeleteModelAsync(provider, model), danger: true),
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
    /// 而且点一下就能去补。一个点不开、补不掉的提示，用户看几次就学会无视它了，
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

        // 开发商 logo。没有图标时整块不出现 —— 留个空方块比没有更难看。
        if (VendorAvatar.CreateLogo(model.Vendor, 16) is { } logo)
        {
            logo.Margin = new Thickness(0, 0, 1, 0);
            row.Children.Add(logo);
        }

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

    /// <summary>
    /// 无底色的文字按钮（次级动作）。图标与文字写进同一个 TextBlock 的 Inlines ——
    /// 两个独立 TextBlock 各自居中时，字体度量不同会让图标比文字高一点点，
    /// 同一段落里的 Run 则共用一条基线，天然对齐。
    /// </summary>
    private static Button CreateTextButton(string text, Action onClick, bool danger = false, string? glyph = null)
    {
        var brush = danger ? TextDanger : TextAccent;
        var content = new TextBlock
        {
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 图标与文字塞进同一段落：前面的字形排一个字，后面接正文（用空格隔开）。
        var inlines = content.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        if (glyph is not null)
            inlines.Add(new Avalonia.Controls.Documents.Run(glyph)
            {
                FontFamily = AppFonts.Icons,
                FontSize = 11,
            });
        inlines.Add(new Avalonia.Controls.Documents.Run(glyph is null ? text : " " + text));

        var btn = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Height = 28,
            Padding = new Thickness(8, 0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ============================================================
    // 动作
    // ============================================================

    /// <summary>
    /// 添加服务商：先开预设清单（头像、地址、协议都配好了，填个密钥就能用）。
    /// 清单右上角的「自定义服务商…」才会走老路 —— 建一条空记录、进详情页、顺手打开设置窗让人改名。
    /// 自定义那条不做确认弹窗：密钥与端点都在详情页里能改，多一步确认只是多一次点击。
    /// </summary>
    private async Task AddProviderAsync()
    {
        var dialog = new PresetProviderWindow();
        await ShowDialogAsync(dialog);

        if (dialog.CustomRequested)
        {
            // 名字不能重复（设置窗保存时会校验），所以默认名也要避开已存在的 ——
            // 否则新建完第一件事就是被自己刚加的校验拦住。
            var provider = new AiProvider { Name = await NextDefaultProviderNameAsync() };
            await ProviderRepository.CreateAsync(provider);
            await ShowProviderDetailAsync(provider);
            await OpenProviderSettingsAsync(provider);
            return;
        }

        // 加了一条就回列表：用户第一眼要看的是「它进来了」，细节留在详情页里点进去再看
        if (dialog.Added) await ShowProviderListAsync();
    }

    /// <summary>「新服务商」用掉了就往后排：新服务商 2、3…（设置窗里保存时会拦重名）。</summary>
    private static async Task<string> NextDefaultProviderNameAsync()
    {
        const string baseName = "新服务商";

        var taken = (await ProviderRepository.GetAllAsync())
            .Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private async Task SetProviderEnabledAsync(AiProvider provider, bool enabled)
    {
        provider.IsEnabled = enabled;
        await SaveProviderAsync(provider);
        await ShowProviderListAsync();
    }

    /// <summary>齿轮入口：低频的服务商级开关。</summary>
    private async Task OpenProviderSettingsAsync(AiProvider provider)
    {
        var dialog = new ProviderSettingsWindow(provider);
        await ShowDialogAsync(dialog);
        if (!dialog.Changed) return;

        await SaveProviderAsync(provider);

        // 从卡片菜单进来的话当前在列表层，就不必硬跳详情页
        if (_detailProviderId == provider.Id)
            await ShowProviderDetailAsync(provider);
        else
            await ShowProviderListAsync();
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
