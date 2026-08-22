using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HtaciAI.Services.Skills;

namespace HtaciAI.Views;

/// <summary>
/// 技能页：左侧列出本地技能，右侧展示所选技能的描述与 SKILL.md 正文。
/// 数据来自 <see cref="SkillRegistry"/>（从 ~/.htaci/skills 加载）。
/// </summary>
public partial class SkillsPage : UserControl
{
    private readonly List<SkillDefinition> _skills = new();
    private SkillDefinition? _selected;

    public SkillsPage()
    {
        InitializeComponent();
        ReloadSkills();
    }

    private void ReloadSkills()
    {
        _skills.Clear();
        _skills.AddRange(SkillRegistry.Instance.GetAll());
        _selected = _skills.Count > 0 ? _skills[0] : null;
        RefreshList();
        ShowDetail();
    }

    // ---- 左侧：技能列表 ----

    private void RefreshList()
    {
        SkillList.Children.Clear();
        if (_skills.Count == 0)
        {
            SkillList.Children.Add(new TextBlock
            {
                Text = "暂无技能",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Avalonia.Thickness(12, 8, 0, 0)
            });
            return;
        }

        foreach (var s in _skills)
            SkillList.Children.Add(CreateSkillItem(s));
    }

    private Border CreateSkillItem(SkillDefinition s)
    {
        var isActive = ReferenceEquals(s, _selected);
        var nameBlock = new TextBlock
        {
            Text = s.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#1A1A2E" : "#4B5563")),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var descBlock = new TextBlock
        {
            Text = s.Description,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        var title = new StackPanel
        {
            Spacing = 2,
            Margin = new Avalonia.Thickness(0, 0, 12, 0),
            Children = { nameBlock, descBlock }
        };

        var item = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F0FA")) : Brushes.Transparent,
            Padding = new Avalonia.Thickness(12, 12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = title
        };

        item.PointerEntered += (_, _) =>
        {
            if (!isActive) item.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        };
        item.PointerExited += (_, _) =>
        {
            if (!isActive) item.Background = Brushes.Transparent;
        };
        item.PointerPressed += (_, e) =>
        {
            _selected = s;
            RefreshList();
            ShowDetail();
            e.Handled = true;
        };

        return item;
    }

    // ---- 右侧：详情 ----

    private void ShowDetail()
    {
        if (_selected is null)
        {
            DetailTitle.Text = "技能详情";
            DetailDesc.Text = "选择左侧技能查看描述与 SKILL.md";
            DetailBody.Text = "";
            return;
        }

        DetailTitle.Text = _selected.Name;
        DetailDesc.Text = _selected.Description;
        DetailBody.Text = _selected.Body;
    }
}
