using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HtaciAI.Services.Skills;
using HtaciAI.Views.Skills;

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
        AddSkillButton.Click += async (_, _) => await OnCreateSkillAsync();
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

    /// <summary>创建新技能：对话框落盘后重载注册表，并选中刚建好的那个。</summary>
    private async Task OnCreateSkillAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new CreateSkillWindow();
        if (owner is not null)
            await dialog.ShowDialog(owner);
        if (dialog.Result is null) return;

        var createdId = dialog.Result.Id;
        ReloadSkills();
        if (_skills.FirstOrDefault(s => s.Id == createdId) is { } created)
        {
            _selected = created;
            RefreshList();
            ShowDetail();
        }
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
            Text = s.DisplayName,
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
        };
        title.Children.Add(nameBlock);
        // 有备注时补一行原始 id 小字：备注给人看，id 才是 SKILL.md 与 load_skill 用的名字
        if (!string.IsNullOrWhiteSpace(s.Alias))
            title.Children.Add(new TextBlock
            {
                Text = s.Name,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.Parse("#B0B7C3")),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        title.Children.Add(descBlock);

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

        DetailTitle.Text = _selected.DisplayName;
        DetailDesc.Text = string.IsNullOrWhiteSpace(_selected.Alias)
            ? _selected.Description
            : $"{_selected.Name} · {_selected.Description}";
        DetailBody.Text = _selected.Body;
    }
}
