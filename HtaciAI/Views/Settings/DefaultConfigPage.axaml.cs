using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Storage;
using HtaciAI.Services.Tools;
using HtaciAI.Controls;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 默认配置页：新建普通会话时的初始值（话题命名、默认模型、默认工具 / 技能 / MCP）。
/// 改动即时写回 <see cref="AppPaths.Settings"/>。
/// </summary>
public partial class DefaultConfigPage : UserControl
{
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush TextMono = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    private readonly AppSettingsData _data;

    /// <summary>装载数据源期间置位，避免「程序改控件」被当成「用户改设置」而反复写盘。</summary>
    private bool _suppressSave = true;

    private ComboBox? _topicModelCombo;
    private ComboBox? _defaultModelCombo;
    private StackPanel? _mcpHost;

    public DefaultConfigPage()
    {
        InitializeComponent();

        _data = AppSettingsStore.Current;
        Build();
        _suppressSave = false;

        _ = LoadAsync();
    }

    private void Build()
    {
        ContentPanel.Children.Add(CreateTopicNamingCard());
        ContentPanel.Children.Add(CreateDefaultModelCard());
        ContentPanel.Children.Add(CreateToolsCard());
        ContentPanel.Children.Add(CreateSkillsCard());
        ContentPanel.Children.Add(CreateMcpCard());

        FooterHint.Text = $"修改会自动保存到 {AppPaths.Settings}";
    }

    // ---- 卡片一：话题命名 ----

    private Control CreateTopicNamingCard()
    {
        var body = new StackPanel();

        body.Children.Add(CreateSwitch(
            "自动生成话题标题",
            "开启后：配了命名模型就让模型在首轮回复后总结一个短名；没配则取首条消息的前 24 个字。关闭则保持「新会话」，等你手动改名。",
            _data.AutoTopicNaming,
            value =>
            {
                _data.AutoTopicNaming = value;
                Persist();
                RefreshTopicModelEnabled();
            }));

        _topicModelCombo = CreateCombo(240);
        _topicModelCombo.SelectionChanged += (_, _) =>
        {
            _data.TopicNamingModelId = (_topicModelCombo.SelectedItem as ModelOption)?.Id;
            Persist();
        };

        body.Children.Add(CreateFieldRow("命名模型", _topicModelCombo));

        return CreateCard("话题命名", "会话标题怎么来的。只在首轮回复后触发一次，之后不再改动。", body);
    }

    private void RefreshTopicModelEnabled()
    {
        // 关掉自动命名后，命名模型就没有意义了
        if (_topicModelCombo is not null) _topicModelCombo.IsEnabled = _data.AutoTopicNaming;
    }

    // ---- 卡片二：新会话默认模型 ----

    private Control CreateDefaultModelCard()
    {
        _defaultModelCombo = CreateCombo(280);
        _defaultModelCombo.SelectionChanged += (_, _) =>
        {
            _data.DefaultModelId = (_defaultModelCombo.SelectedItem as ModelOption)?.Id;
            Persist();
        };

        var body = new StackPanel();
        body.Children.Add(CreateFieldRow("默认模型", _defaultModelCombo));

        return CreateCard("新会话默认模型", "新建会话时预选的模型。留空则沿用上次用过的模型。", body);
    }

    // ---- 卡片三：默认工具 ----

    private Control CreateToolsCard()
    {
        var tools = ToolRegistry.Instance.GetEnabled();
        var host = new StackPanel { Spacing = 2 };

        if (tools.Count == 0)
        {
            host.Children.Add(CreateEmptyHint("暂无可用工具"));
        }
        else
        {
            // 设置为 null 表示「全部内置工具」，此时全部勾上
            var selected = _data.DefaultToolIds is { } ids ? ids.ToHashSet() : null;

            foreach (var tool in tools)
            {
                var check = new CheckBox
                {
                    Content = tool.Name,
                    Tag = tool.Id,
                    FontSize = 13,
                    IsChecked = selected is null || selected.Contains(tool.Id)
                };
                check.IsCheckedChanged += (_, _) => SaveToolSelection(host);
                host.Children.Add(check);
            }
        }

        var selectAll = CreateActionButton("全选");
        var selectNone = CreateActionButton("全不选");
        selectAll.PointerPressed += (_, e) =>
        {
            SetAllChecked(host, true);
            e.Handled = true;
        };
        selectNone.PointerPressed += (_, e) =>
        {
            SetAllChecked(host, false);
            e.Handled = true;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(selectAll);
        actions.Children.Add(selectNone);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(CreateEmptyHint("新建普通会话时默认启用的工具。"));
        header.Children.Add(actions);
        Grid.SetColumn(actions, 1);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(CreateCheckListHost(host, 200));

        return CreateCard("默认工具", null, body);
    }

    /// <summary>
    /// 一个都不勾时写入空列表（而不是 null）——null 会被解释成「全部内置工具」，
    /// 两者语义相反，必须区分。
    /// </summary>
    private void SaveToolSelection(StackPanel host)
    {
        _data.DefaultToolIds = CollectChecked(host);
        Persist();
    }

    // ---- 卡片四：默认技能 ----

    private Control CreateSkillsCard()
    {
        var skills = SkillRegistry.Instance.GetEnabled();
        var host = new StackPanel { Spacing = 2 };

        if (skills.Count == 0)
        {
            host.Children.Add(CreateEmptyHint("暂无可用技能"));
        }
        else
        {
            var selected = (_data.DefaultSkills ?? new()).Select(s => s.Id).ToHashSet();

            foreach (var skill in skills)
            {
                var check = new CheckBox
                {
                    Content = skill.DisplayName,
                    Tag = skill.Id,
                    FontSize = 13,
                    IsChecked = selected.Contains(skill.Id)
                };
                check.IsCheckedChanged += (_, _) =>
                {
                    _data.DefaultSkills = CollectChecked(host)
                        .Select(id => new SessionSkill { Id = id, Status = "loaded" })
                        .ToList();
                    Persist();
                };
                host.Children.Add(check);
            }
        }

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(CreateEmptyHint("默认不开启技能，AI 仍可通过 load_skill 按需加载。勾选的会被预加载进 system 提示词。"));
        body.Children.Add(CreateCheckListHost(host, 200));

        return CreateCard("默认技能", null, body);
    }

    // ---- 卡片五：默认 MCP 服务 ----

    private Control CreateMcpCard()
    {
        _mcpHost = new StackPanel { Spacing = 2 };
        _mcpHost.Children.Add(CreateEmptyHint("正在加载…"));

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(CreateEmptyHint("新建会话时默认启用哪些 MCP 服务。它们的工具会自动并入对话请求。"));
        body.Children.Add(CreateCheckListHost(_mcpHost, 200));

        return CreateCard("默认 MCP 服务", null, body);
    }

    private async Task LoadAsync()
    {
        List<ModelOption> modelOptions;
        try
        {
            modelOptions = (await ModelCatalog.ListEnabledAsync())
                .Select(m => new ModelOption($"{m.DisplayName}（{m.ProviderName}）", m.ModelId))
                .ToList();
        }
        catch
        {
            modelOptions = new List<ModelOption>();
        }

        FillModelCombo(_topicModelCombo, modelOptions, "（不指定，用首条消息）", _data.TopicNamingModelId);
        FillModelCombo(_defaultModelCombo, modelOptions, "（上次使用的模型）", _data.DefaultModelId);

        RefreshTopicModelEnabled();

        await LoadMcpServersAsync();
    }

    private static void FillModelCombo(ComboBox? combo, List<ModelOption> models, string sentinelLabel, string? selectedId)
    {
        if (combo is null) return;

        var options = new List<ModelOption> { new(sentinelLabel, null) };
        options.AddRange(models);

        combo.ItemsSource = options;

        var index = selectedId is null
            ? 0
            : Math.Max(0, options.FindIndex(o => o.Id == selectedId));
        combo.SelectedIndex = index;
    }

    private async Task LoadMcpServersAsync()
    {
        if (_mcpHost is null) return;

        List<McpServerConfig> servers;
        try
        {
            servers = await McpServerRepository.GetAllAsync();
        }
        catch
        {
            servers = new List<McpServerConfig>();
        }

        _mcpHost.Children.Clear();

        if (servers.Count == 0)
        {
            _mcpHost.Children.Add(CreateEmptyHint("还没有 MCP 服务。到「MCP服务」页面新增一个。"));
            return;
        }

        var selected = (_data.DefaultMcpServerIds ?? new()).ToHashSet();

        foreach (var server in servers)
        {
            var check = new CheckBox
            {
                Content = server.Name,
                Tag = server.Id,
                FontSize = 13,
                IsChecked = selected.Contains(server.Id)
            };
            check.IsCheckedChanged += (_, _) =>
            {
                _data.DefaultMcpServerIds = CollectChecked(_mcpHost);
                Persist();
            };
            _mcpHost.Children.Add(check);
        }
    }

    // ---- 持久化 ----

    private void Persist()
    {
        if (_suppressSave) return;
        if (AppSettingsStore.Save(_data)) return;

        FooterHint.Text = $"设置保存失败，请检查数据目录权限：{AppPaths.Settings}";
        FooterHint.Foreground = Danger;
    }

    // ---- 通用小控件 ----

    private static Border CreateCard(string title, string? description, Control body)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary
        });

        if (!string.IsNullOrEmpty(description))
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap
            });

        content.Children.Add(new Border { Height = 4 });
        content.Children.Add(body);

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16),
            Child = content
        };
    }

    /// <summary>带说明文字的开关行。</summary>
    private static Control CreateSwitch(string label, string description, bool value, Action<bool> onChanged)
    {
        var check = new CheckBox
        {
            Content = label,
            FontSize = 13,
            IsChecked = value
        };
        check.IsCheckedChanged += (_, _) => onChanged(check.IsChecked == true);

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                check,
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    Foreground = TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 0, 0, 0)
                }
            }
        };
    }

    private static Control CreateFieldRow(string label, Control field)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = TextMono,
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(text);
        grid.Children.Add(field);
        Grid.SetColumn(field, 1);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        return grid;
    }

    /// <summary>勾选列表的容器：固定最大高度，条目多时内部滚动。</summary>
    private static Control CreateCheckListHost(Control host, double maxHeight) => new Border
    {
        CornerRadius = new CornerRadius(8),
        Background = Brushes.White,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8),
        Child = new SmoothScrollViewer()
        {
            MaxHeight = maxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = host
        }
    };

    private static ComboBox CreateCombo(double width) => new()
    {
        Width = width,
        Height = 34,
        CornerRadius = new CornerRadius(6),
        FontSize = 13,
        DisplayMemberBinding = new Binding(nameof(ModelOption.Label))
    };

    /// <summary>幽灵样式的小按钮，与「环境配置」页保持一致。</summary>
    private static Border CreateActionButton(string text)
    {
        var button = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = TextMono,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.PointerEntered += (_, _) => button.Background = Surface;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static TextBlock CreateEmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = TextFaint,
        TextWrapping = TextWrapping.Wrap
    };

    private static void SetAllChecked(StackPanel host, bool value)
    {
        foreach (var check in host.Children.OfType<CheckBox>())
            check.IsChecked = value;
    }

    private static List<string> CollectChecked(StackPanel host)
        => host.Children.OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag!)
            .ToList();

    /// <summary>下拉项：Label 用于显示，Id 为 null 表示哨兵项（不指定）。</summary>
    private sealed record ModelOption(string Label, string? Id);
}
