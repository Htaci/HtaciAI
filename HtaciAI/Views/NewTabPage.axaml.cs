using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HtaciAI.Views;

public partial class NewTabPage : UserControl
{
    public event EventHandler? SmartChatSelected;
    public event EventHandler? WorkspaceSelected;

    public NewTabPage()
    {
        InitializeComponent();
        CreateCards();
    }

    private void CreateCards()
    {
        //CardsPanel.Children.Add(CreateCard(new CardData
        //{
        //    Title = "智能对话",
        //    Description = "基础对话和网络搜索，聚焦于回答你想知道的问题。",
        //    Tags = new[] { "基础对话", "网络搜索" },
        //    MainColor = Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9),
        //    LightBg = Color.FromArgb(0xFF, 0xEE, 0xF4, 0xFB),
        //    GlowAlpha = 0x26,
        //    TagBg = Color.FromArgb(0xFF, 0xED, 0xF4, 0xFC),
        //    TagFg = Color.FromArgb(0xFF, 0x3A, 0x7B, 0xC8),
        //    OnClick = () => SmartChatSelected?.Invoke(this, EventArgs.Empty)
        //}));

        CardsPanel.Children.Add(CreateCard(new CardData
        {
            Title = "工作空间",
            Description = "管理 AI 的工作空间",

            MainColor = Color.FromArgb(0xFF, 0x54, 0x6E, 0x7A),
            LightBg = Color.FromArgb(0xFF, 0xEC, 0xEF, 0xF1),
            GlowAlpha = 0x1A,
            TagBg = Color.FromArgb(0xFF, 0xEC, 0xEF, 0xF1),
            TagFg = Color.FromArgb(0xFF, 0x45, 0x5A, 0x64),
            OnClick = () => WorkspaceSelected?.Invoke(this, EventArgs.Empty)
        }));

        //CardsPanel.Children.Add(CreateCard(new CardData
        //{
        //    Title = "通用智能体",
        //    Description = "综合型 AI 助手，可以直接操作/访问你的设备帮你完成各种任务。",
        //    Tags = new[] { "代理操作", "能力综合" },
        //    MainColor = Color.FromArgb(0xFF, 0x54, 0x6E, 0x7A),
        //    LightBg = Color.FromArgb(0xFF, 0xEC, 0xEF, 0xF1),
        //    GlowAlpha = 0x1A,
        //    TagBg = Color.FromArgb(0xFF, 0xEC, 0xEF, 0xF1),
        //    TagFg = Color.FromArgb(0xFF, 0x45, 0x5A, 0x64),
        //    OnClick = null
        //}));

        CardsPanel.Children.Add(CreateCard(new CardData
        {
            Title = "插件与技能",
            Description = "管理已安装的插件与技能",
            Tags = new[] { "Plugin", "Skills" },
            MainColor = Color.FromArgb(0xFF, 0x2E, 0xAF, 0x7D),
            LightBg = Color.FromArgb(0xFF, 0xED, 0xF8, 0xF3),
            GlowAlpha = 0x26,
            TagBg = Color.FromArgb(0xFF, 0xE8, 0xF7, 0xF1),
            TagFg = Color.FromArgb(0xFF, 0x25, 0x9A, 0x6A),
            OnClick = null
        }));

        //CardsPanel.Children.Add(CreateCard(new CardData
        //{
        //    Title = "浏览器智能体",
        //    Description = "自主浏览网页、采集信息、模拟操作，在浏览器中完成端到端的复杂任务。",
        //    Tags = new[] { "Web Agent", "自动化操作" },
        //    MainColor = Color.FromArgb(0xFF, 0x2E, 0xAF, 0x7D),
        //    LightBg = Color.FromArgb(0xFF, 0xED, 0xF8, 0xF3),
        //    GlowAlpha = 0x26,
        //    TagBg = Color.FromArgb(0xFF, 0xE8, 0xF7, 0xF1),
        //    TagFg = Color.FromArgb(0xFF, 0x25, 0x9A, 0x6A),
        //    OnClick = null
        //}));

        CardsPanel.Children.Add(CreateCard(new CardData
        {
            Title = "插件与技能市场",
            Description = "管理和发现新的插件与技能，扩展 AI 的功能。",
            MainColor = Color.FromArgb(0xFF, 0x7B, 0x5E, 0xA7),
            LightBg = Color.FromArgb(0xFF, 0xF3, 0xEF, 0xF8),
            GlowAlpha = 0x26,
            TagBg = Color.FromArgb(0xFF, 0xF1, 0xED, 0xF6),
            TagFg = Color.FromArgb(0xFF, 0x6A, 0x4F, 0x94),
            OnClick = null
        }));

        //CardsPanel.Children.Add(CreateCard(new CardData
        //{
        //    Title = "代码智能体",
        //    Description = "智能编写、审查与重构代码，覆盖主流语言，提升开发效率。",
        //    Tags = new[] { "Code Agent", "项目开发" },
        //    MainColor = Color.FromArgb(0xFF, 0x7B, 0x5E, 0xA7),
        //    LightBg = Color.FromArgb(0xFF, 0xF3, 0xEF, 0xF8),
        //    GlowAlpha = 0x26,
        //    TagBg = Color.FromArgb(0xFF, 0xF1, 0xED, 0xF6),
        //    TagFg = Color.FromArgb(0xFF, 0x6A, 0x4F, 0x94),
        //    OnClick = null
        //}));

        CardsPanel.Children.Add(CreateCard(new CardData
        {
            Title = "自动化",
            Description = "规划定时任务和自动化流程",
            Tags = new[] { "Automation", "Scheduling" },
            MainColor = Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23),
            LightBg = Color.FromArgb(0xFF, 0xFF, 0xF9, 0xF0),
            GlowAlpha = 0x26,
            TagBg = Color.FromArgb(0xFF, 0xFE, 0xF7, 0xEC),
            TagFg = Color.FromArgb(0xFF, 0xD4, 0x89, 0x1A),
            OnClick = null
        }));

        //CardsPanel.Children.Add(CreateCard(new CardData
        //{
        //    Title = "闲聊",
        //    Description = "轻松随意的日常交流伙伴，温暖、有趣、随时在线陪伴。",
        //    Tags = new[] { "情感陪伴", "随时在线" },
        //    MainColor = Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23),
        //    LightBg = Color.FromArgb(0xFF, 0xFF, 0xF9, 0xF0),
        //    GlowAlpha = 0x26,
        //    TagBg = Color.FromArgb(0xFF, 0xFE, 0xF7, 0xEC),
        //    TagFg = Color.FromArgb(0xFF, 0xD4, 0x89, 0x1A),
        //    OnClick = null
        //}));

    }

    private Border CreateCard(CardData data)
    {
        // ---- 图标容器（左上角）----
        var iconWrapper = new Border
        {
            Width = 52,
            Height = 52,
            
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(data.LightBg),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 4, Blur = 14, Spread = 0,
                Color = Color.FromArgb(46, data.MainColor.R, data.MainColor.G, data.MainColor.B)
            })
        };

        // ---- 标题 ----
        var titleBlock = new TextBlock
        {
            Text = data.Title,
            FontSize = 17.6,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x2E)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Thickness(0,10,0,0)
        };

        // ---- 描述 ----
        var descBlock = new TextBlock
        {
            Text = data.Description,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x5A, 0x72)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };

        // ---- 标签 ----
        var tagsPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        foreach (var tag in data.Tags)
        {
            tagsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(data.TagBg),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(10, 4),
                Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 11.2,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(data.TagFg)
                }
            });
        }

        // ---- 上半部分 ----
        var topContent = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        topContent.Children.Add(iconWrapper);
        topContent.Children.Add(titleBlock);
        topContent.Children.Add(descBlock);

        // ---- 卡片内部布局（上内容 + 标签固定在底部）----
        var cardInner = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        cardInner.Children.Add(topContent);
        cardInner.Children.Add(tagsPanel);
        Grid.SetRow(topContent, 0);
        Grid.SetRow(tagsPanel, 1);
        // 标签底部间距
        tagsPanel.Margin = new Thickness(0, 10, 0, 0);

        // ---- 光晕（大圆，RadialGradientBrush，模糊边缘）----
        var glowBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(
                    Color.FromArgb(data.GlowAlpha, data.MainColor.R, data.MainColor.G, data.MainColor.B), 0),
                new GradientStop(
                    Color.FromArgb(0, data.MainColor.R, data.MainColor.G, data.MainColor.B), 1)
            }
        };

        var glowCircle = new Border
        {
            Width = 200,
            Height = 200,
            CornerRadius = new CornerRadius(100),
            Background = glowBrush,
            Opacity = 0,
            IsHitTestVisible = false
        };

        Canvas.SetRight(glowCircle, -100);
        Canvas.SetTop(glowCircle, -100);

        var glowCanvas = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = false,
            Children = { glowCircle }
        };

        // ---- 卡片主体 ----
        var card = new Border
        {
            Width = 260,
            Margin = new Thickness(10),
            Padding = new Thickness(24, 28, 24, 24),
            CornerRadius = new CornerRadius(24),
            Background = Brushes.White,
            Cursor = new Cursor(StandardCursorType.Hand),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #14000000, 0 2 6 0 #0A000000"),
            Child = new Grid
            {
                Children = { cardInner, glowCanvas }
            }
        };

        // 悬停过渡动画（用 Margin 上移模拟抬起）
        card.Transitions = new Transitions
        {
            new ThicknessTransition
            {
                Property = Border.MarginProperty,
                Duration = TimeSpan.FromMilliseconds(400),
                Easing = new CubicEaseOut()
            }
        };

        var glowTransition = new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(350),
            Easing = new CubicEaseOut()
        };
        glowCircle.Transitions = new Transitions { glowTransition };

        card.PointerEntered += (s, e) =>
        {
            card.Margin = new Thickness(10, 2, 10, 18);
            card.BoxShadow = BoxShadows.Parse("0 12 32 0 #1E000000, 0 4 10 0 #0C000000");
            glowCircle.Opacity = 1;
            iconWrapper.CornerRadius = new CornerRadius(18);
        };

        card.PointerExited += (s, e) =>
        {
            card.Margin = new Thickness(10);
            card.BoxShadow = BoxShadows.Parse("0 4 16 0 #14000000, 0 2 6 0 #0A000000");
            glowCircle.Opacity = 0;
            iconWrapper.CornerRadius = new CornerRadius(16);
        };

        if (data.OnClick != null)
        {
            card.PointerPressed += (s, e) =>
            {
                data.OnClick();
                e.Handled = true;
            };
        }

        return card;
    }

    private sealed class CardData
    {
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string[] Tags { get; init; } = [];
        public Color MainColor { get; init; }
        public Color LightBg { get; init; }
        public byte GlowAlpha { get; init; }
        public Color TagBg { get; init; }
        public Color TagFg { get; init; }
        public Action? OnClick { get; init; }
    }
}
