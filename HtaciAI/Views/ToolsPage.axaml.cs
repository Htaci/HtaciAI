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
using HtaciAI.Data;
using HtaciAI.Services.Docs;
using HtaciAI.Services.ScriptRuntimes;
using HtaciAI.Services.Tools;
using HtaciAI.Views.Tools;
using HtaciAI.Controls;

using HtaciAI.Services;

namespace HtaciAI.Views;

/// <summary>
/// 工具页：管理脚本工具与工具集。数据来自全局 <see cref="ToolRegistry"/>，
/// "创建新工具"引导用户选择脚本 + --describe 自动生成 schema 并入库。
/// </summary>
public partial class ToolsPage : UserControl
{
    private List<Toolset> _toolsets = new();
    private Toolset? _selectedToolset;

    public ToolsPage()
    {
        InitializeComponent();
        SetupButtons();
        DevDocLink.PointerPressed += (_, e) =>
        {
            // 生成失败（数据目录不可写）时把原因写在原地：这一页没有别的提示位，
            // 而弹一个只有「确定」的对话框为这点事打断用户不值当。
            if (DevDocs.Open(DevDocs.ToolStartPage) is { } error) DevDocLink.Text = error;
            e.Handled = true;
        };
        ReloadFromRegistry();
    }

    /// <summary>从注册表重新拉取工具集列表并刷新两侧面板。传 <paramref name="select"/> 则切到该集合。</summary>
    private void ReloadFromRegistry(Toolset? select = null)
    {
        _toolsets = ToolRegistry.Instance.GetToolsets().ToList();

        if (select is not null)
            _selectedToolset = _toolsets.FirstOrDefault(t => t.Id == select.Id);

        if (_selectedToolset is null || _toolsets.All(t => t.Id != _selectedToolset.Id))
            _selectedToolset = _toolsets.FirstOrDefault();

        RefreshToolsetList();
        UpdateRightPanel();
    }

    private void SetupButtons()
    {
        SetupHoverAction(AddToolsetBtn, OnAddToolsetClick);            // 透明图标按钮：浅灰 hover
        SetupHoverAction(AddToolBtn, OnAddToolClick);
        SetupHoverAction(CreateToolBtn, OnCreateToolClick, "#2F2F3C"); // 深色填充按钮：深一档 hover
    }

    /// <summary>
    /// 统一的悬停反馈（hover 底色 + 手型光标）。移出时恢复到按钮的初始背景，
    /// 而非固定 Transparent——否则会覆盖掉像「创建新工具」这类深色填充按钮的原背景。
    /// </summary>
    private void SetupHoverAction(Border btn, Action onClick, string hoverBg = "#F1F3F5")
    {
        var normalBg = btn.Background;
        btn.PointerEntered += (s, e) =>
        {
            btn.Background = new SolidColorBrush(Color.Parse(hoverBg));
            btn.Cursor = new Cursor(StandardCursorType.Hand);
        };
        btn.PointerExited += (s, e) =>
        {
            btn.Background = normalBg;
            btn.Cursor = new Cursor(StandardCursorType.Arrow);
        };
        btn.PointerPressed += (s, e) =>
        {
            onClick();
            e.Handled = true;
        };
    }

    /// <summary>左上角加号：新建一个工具集（分组），建完自动切过去。</summary>
    private async void OnAddToolsetClick()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new CreateToolsetWindow();
        if (owner is not null)
            await dialog.ShowDialog(owner);

        if (dialog.Result is null) return;
        ReloadFromRegistry(dialog.Result);
    }

    /// <summary>创建工具：归属预选当前集合（自动集合不能当归属，交给用户自己勾）。</summary>
    private async void OnCreateToolClick()
    {
        var preset = _selectedToolset is { IsAuto: false } set ? set.Id : null;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new CreateToolWindow(preset);
        if (owner != null)
            await dialog.ShowDialog(owner);

        if (dialog.Result is null) return;
        ReloadFromRegistry();
    }

    /// <summary>把其他集合里的工具加进当前集合：工具仍在原来的集合里，只是多了一个归属。</summary>
    private async void OnAddToolClick()
    {
        if (_selectedToolset is not { IsAuto: false } target)
        {
            SetHint("「全部」「内置」是自动集合，成员由规则决定，不能手工添加");
            return;
        }

        var candidates = ToolRegistry.Instance.GetEnabled()
            .Where(t => !Toolset.Contains(target.Id, t))
            .ToList();
        if (candidates.Count == 0)
        {
            SetHint($"所有工具都已经在「{target.Name}」里了");
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new AddToolsWindow(target, candidates);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        if (dialog.SelectedToolIds.Count == 0) return;

        foreach (var id in dialog.SelectedToolIds)
        {
            try { await ToolRepository.AddToolLinkAsync(id, target.Id); } catch { /* 落库失败不阻塞内存更新 */ }
            ToolRegistry.Instance.AddToolToToolset(id, target.Id);
        }

        RefreshToolsetList();
        UpdateRightPanel();
    }

    // ---- 左侧：工具集列表 ----

    private void RefreshToolsetList()
    {
        ToolsetList.Children.Clear();

        if (_toolsets.Count == 0)
        {
            ToolsetList.Children.Add(new TextBlock
            {
                Text = "暂无工具集",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(12, 8, 0, 0)
            });
            return;
        }

        foreach (var ts in _toolsets)
            ToolsetList.Children.Add(CreateToolsetItem(ts));
    }

    private Border CreateToolsetItem(Toolset ts)
    {
        var isActive = ReferenceEquals(ts, _selectedToolset);
        var toolCount = ToolRegistry.Instance.GetByToolset(ts.Id).Count;

        var nameBlock = new TextBlock
        {
            Text = ts.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var infoBlock = new TextBlock
        {
            Text = $"{toolCount} 个工具",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF"))
        };
        var title = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { nameBlock, infoBlock }
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Thickness(12, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = title
        };

        item.PointerEntered += (s, e) =>
        {
            if (!isActive)
                item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (s, e) =>
        {
            if (!isActive)
                item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (s, e) =>
        {
            _selectedToolset = ts;
            RefreshToolsetList();
            UpdateRightPanel();
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧：工具列表 ----

    private void UpdateRightPanel()
    {
        UpdateHeader();

        if (_selectedToolset is null)
        {
            SetHint("选择左侧工具集查看其中的工具");
            return;
        }

        var tools = ToolRegistry.Instance.GetByToolset(_selectedToolset.Id);
        if (tools.Count == 0)
        {
            SetHint(_selectedToolset.IsAuto
                ? $"「{_selectedToolset.Name}」里还没有工具"
                : $"工具集「{_selectedToolset.Name}」还没有工具，点右上角「创建新工具」，或用「添加工具」把别的集合里的工具拉进来");
            return;
        }

        BuildToolList(tools);
    }

    /// <summary>标题栏跟着当前集合走，并同步「添加工具」的可用性（自动集合不能手工加）。</summary>
    private void UpdateHeader()
    {
        var canAdd = _selectedToolset is { IsAuto: false };
        AddToolBtn.IsEnabled = canAdd;
        AddToolBtn.Opacity = canAdd ? 1 : 0.45;

        PanelTitle.Text = _selectedToolset?.Name ?? "工具";
        PanelDesc.Text = _selectedToolset switch
        {
            null => "管理工具与工具集，为 AI 提供可调用的能力",

            // 「全部」「内置」：成员由规则算出来
            // 插件带的集合：成员由插件声明，用户改不了（插件卸载时一起消失）
            { IsBuiltin: true } ts => Join(ts.Description, "随插件提供，成员由插件决定"),
            { IsAuto: true } ts => Join(ts.Description, "自动集合，成员由规则决定"),
            var ts => string.IsNullOrWhiteSpace(ts.Description) ? "自建工具集" : ts.Description,
        };
    }

    /// <summary>把两段说明拼起来，前一段为空时不留一个孤零零的分隔点。</summary>
    private static string Join(string? head, string tail)
        => string.IsNullOrWhiteSpace(head) ? tail : $"{head} · {tail}";

    private void BuildToolList(IReadOnlyList<ToolDefinition> tools)
    {
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        foreach (var tool in tools)
            wrap.Children.Add(CreateToolCard(tool));

        ToolListHost.Content = new SmoothScrollViewer()
        {
            Content = wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private Border CreateToolCard(ToolDefinition tool)
    {
        var (sourceText, sourceBg, sourceFg) = SourceBadge(tool);

        var levelBadge = BuildEditableLevelBadge(tool);

        // 用 WrapPanel 而不是 StackPanel：工具名 + 一串标签很容易超过卡片宽度，
        // StackPanel 只会把它截断 / 顶出卡片，WrapPanel 会自动折到第二行。
        var nameRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = tool.Name,
                    FontSize = 14.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse(sourceBg)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = sourceText,
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(Color.Parse(sourceFg))
                    }
                },
                levelBadge,
            }
        };

        // AI 自举建的脚本单独标一下：这段代码不是用户自己写的，值不值得留存该由他一眼判断
        if (tool.IsAgentCreated)
            nameRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFF4E5")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "AI 创建",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.Parse("#B45309")),
                },
            });
        var descBlock = new TextBlock
        {
            Text = tool.Description,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var icon = new TextBlock
        {
            Text = "\uF35C", // Fluent System Icons：扳手（工具）
            FontFamily = AppFonts.Icons,
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.Parse("#3A7BC8")),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var card = new Border
        {
            Width = 240,
            MinHeight = 118,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 14),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.White,
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #0A000000, 0 1 2 0 #06000000"),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { icon, nameRow, descBlock, BuildTimeoutBadge(tool) }
            }
        };

        card.PointerEntered += (s, e) =>
        {
            card.Background = new SolidColorBrush(Color.Parse("#F8FAFC"));
        };
        card.PointerExited += (s, e) =>
        {
            card.Background = Brushes.White;
        };
        card.PointerPressed += (s, e) =>
        {
            // TODO: 打开工具详情/编辑/删除
            e.Handled = true;
        };

        return card;
    }

    private void SetHint(string text)
    {
        ToolListHost.Content = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>来源小标签：内置=蓝 / 脚本=青 / MCP=紫（与 MCP 服务卡片同色）。</summary>
    private static (string Text, string Bg, string Fg) SourceBadge(ToolDefinition tool) => tool.Source switch
    {
        ToolSource.Builtin => ("内置", "#EDF4FC", "#3A7BC8"),
        ToolSource.Mcp => ("MCP", "#F3EFF8", "#6A4F94"),
        _ => (tool.Runtime == ScriptRuntimeKind.Node ? "Node.js" : "Python", "#EAF7F7", "#0B8A8B"),
    };

    /// <summary>权限等级小标签：安全=绿 / 风险=橙 / 危险=红。</summary>
    private static Border BuildLevelBadge(ToolDangerLevel level)
    {
        var (color, text) = level switch
        {
            ToolDangerLevel.Safe => ("#16A34A", "安全"),
            ToolDangerLevel.Risk => ("#D97706", "风险"),
            _ => ("#DC2626", "危险"),
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(color)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            },
        };
    }

    /// <summary>
    /// 可点的权限等级徽标：点开一个三选菜单直接改等级。
    ///
    /// 存在的理由是 AI 自举：模型新建的工具一律记为「危险」，但用户看过脚本之后
    /// 完全可以判断它其实是只读的 —— 不给一个改的入口，那些工具就永远每次都要审批。
    /// 内置工具不给这个入口（它们的等级是程序行为的一部分，改了没有意义）。
    /// </summary>
    private Control BuildEditableLevelBadge(ToolDefinition tool)
    {
        var badge = BuildLevelBadge(tool.DangerLevel);
        if (tool.Source == ToolSource.Builtin)
        {
            ToolTip.SetTip(badge, "内置工具的权限等级固定");
            return badge;
        }

        badge.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(badge, "点击修改权限等级");

        var menu = new StackPanel { Spacing = 2, MinWidth = 140 };

        // 先声明再在行里捕获：菜单项点击后要能把它自己收起来
        Flyout? flyout = null;

        foreach (var level in new[] { ToolDangerLevel.Safe, ToolDangerLevel.Risk, ToolDangerLevel.Danger })
        {
            var (color, text) = level switch
            {
                ToolDangerLevel.Safe => ("#16A34A", "安全"),
                ToolDangerLevel.Risk => ("#D97706", "风险"),
                _ => ("#DC2626", "危险"),
            };
            var row = new Border
            {
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border
                        {
                            Width = 8,
                            Height = 8,
                            CornerRadius = new CornerRadius(4),
                            Background = new SolidColorBrush(Color.Parse(color)),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = text,
                            FontSize = 13,
                            Foreground = new SolidColorBrush(Color.Parse("#374151")),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse("#14000000"));
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            row.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                flyout?.Hide();
                if (level == tool.DangerLevel) return;

                var updated = new ToolDefinition
                {
                    Id = tool.Id,
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchemaJson = tool.InputSchemaJson,
                    Source = tool.Source,
                    Runtime = tool.Runtime,
                    Target = tool.Target,
                    ToolsetIds = tool.ToolsetIds,
                    Enabled = tool.Enabled,
                    DangerLevel = level,
                    CreatedBy = tool.CreatedBy,
                };

                try
                {
                    await ToolRepository.UpdateAsync(updated);
                }
                catch
                {
                    // 写库失败就保持原等级：界面上什么都不变，比显示一个没存下的值好
                    return;
                }

                ToolRegistry.Instance.Register(updated);
                ReloadFromRegistry();
            };
            menu.Children.Add(row);
        }

        flyout = new Flyout { Content = menu, Placement = PlacementMode.BottomEdgeAlignedRight };
        badge.PointerPressed += (_, e) =>
        {
            // 吃掉按下，避免冒泡到卡片上（卡片目前只是占位，将来接了详情页也不该被这一下点开）
            e.Handled = true;
            flyout!.ShowAt(badge);
        };

        return badge;
    }

    /// <summary>
    /// 超时徽标：显示这个工具最多等多久，点开可以改（默认 / 一直等 / 具体时长）。
    ///
    /// <b>为什么要能单独配</b>：5 分钟是按「本地读写」定的，可 bash 装个包、脚本跑一次
    /// 全量测试、插件或 MCP 服务跑一个远端任务，都轻易超过它。而且超时之后我们只是
    /// 不再等那个任务 —— 它若停在同步 IO 里还会继续跑，用户看到的是「报告失败，但东西被改了」。
    /// 所以对这类工具，正确答案往往不是「再等一会儿」，而是显式放宽或显式不限时。
    ///
    /// 内置工具也给这个入口：它的默认值是程序行为的一部分，但「我这条机器上装包要 8 分钟」
    /// 只有用户自己知道。
    /// </summary>
    private Control BuildTimeoutBadge(ToolDefinition tool)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F3F4F6")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "超时 " + FormatTimeout(tool.TimeoutSeconds),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse("#4B5563")),
            },
        };
        ToolTip.SetTip(badge, "点击设置这个工具最多等多久");

        // 先声明再在行里捕获：菜单项点击后要能把它自己收起来
        Flyout? flyout = null;
        var menu = new StackPanel { Spacing = 2, MinWidth = 190 };

        void AddChoice(string text, int? seconds)
        {
            var isCurrent = tool.TimeoutSeconds == seconds;
            var row = new Border
            {
                Padding = new Thickness(12, 7),
                CornerRadius = new CornerRadius(6),
                Background = isCurrent ? new SolidColorBrush(Color.Parse("#14000000")) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse(isCurrent ? "#1A1A2E" : "#374151")),
                },
            };
            row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse("#14000000"));
            row.PointerExited += (_, _) =>
                row.Background = isCurrent ? new SolidColorBrush(Color.Parse("#14000000")) : Brushes.Transparent;
            row.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                flyout?.Hide();
                if (isCurrent) return;
                await ApplyTimeoutAsync(tool, seconds);
            };
            menu.Children.Add(row);
        }

        AddChoice($"默认（{FormatTimeout(null)}）", null);
        AddChoice("一直等待（不设超时）", 0);
        AddChoice("10 分钟", 600);
        AddChoice("30 分钟", 1800);
        AddChoice("1 小时", 3600);

        // 任意秒数：预设只覆盖常用档，真正特殊的（90 秒、8 分钟）得能填
        var box = new TextBox
        {
            Watermark = "秒数（0 = 一直等待）",
            FontSize = 12,
            Height = 30,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Text = tool.TimeoutSeconds is { } current and > 0 ? current.ToString() : "",
        };
        var apply = new Border
        {
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#1A1A2E")),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = "应用",
                FontSize = 12,
                Foreground = Brushes.White,
            },
        };

        async void Commit()
        {
            // 空 = 恢复默认；非数字就当没填（不清空用户的输入，让他自己改）
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                flyout?.Hide();
                await ApplyTimeoutAsync(tool, null);
                return;
            }
            if (!int.TryParse(box.Text.Trim(), out var seconds))
            {
                box.Text = "";
                return;
            }
            flyout?.Hide();
            await ApplyTimeoutAsync(tool, Math.Clamp(seconds, 0, ToolExecutorDispatcher.MaxTimeoutSeconds));
        }

        apply.PointerPressed += (_, e) => { e.Handled = true; Commit(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };

        menu.Children.Add(new Border
        {
            Margin = new Thickness(0, 4, 0, 2),
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#E5E7EB")),
        });
        menu.Children.Add(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(8, 2, 8, 4),
            Children = { box, apply },
        });

        flyout = new Flyout { Content = menu, Placement = PlacementMode.BottomEdgeAlignedLeft };
        badge.PointerPressed += (_, e) =>
        {
            // 吃掉按下，避免冒泡到卡片上
            e.Handled = true;
            flyout!.ShowAt(badge);
        };

        return badge;
    }

    /// <summary>把配置值说成人话。null = 没配过，走分派器的 5 分钟默认。</summary>
    private static string FormatTimeout(int? seconds)
    {
        if (seconds is null) return "5 分钟";
        if (seconds.Value <= 0) return "不限时";
        var s = seconds.Value;
        if (s % 3600 == 0) return $"{s / 3600} 小时";
        if (s % 60 == 0) return $"{s / 60} 分钟";
        return $"{s} 秒";
    }

    /// <summary>写库并立刻生效（注册表里的定义就地改，不必重启）。</summary>
    private async Task ApplyTimeoutAsync(ToolDefinition tool, int? seconds)
    {
        try
        {
            await ToolRegistry.Instance.SetTimeoutSecondsAsync(tool.Id, seconds);
        }
        catch
        {
            // 写库失败就保持原值：界面上什么都不变，比显示一个没存下的值好
            return;
        }

        ReloadFromRegistry(_selectedToolset);
    }
}
