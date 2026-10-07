using System;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using HtaciAI.Services.Skills;

namespace HtaciAI.Views.Skills;

/// <summary>
/// 创建技能对话框：填写 id / 名称 / 备注 / 描述 / 正文，写入
/// <c>&lt;数据根&gt;/skills/&lt;id&gt;/SKILL.md</c>（frontmatter + 正文），
/// 保存后重载 <see cref="SkillRegistry"/>，新技能立刻在技能页与选择器里可见。
/// </summary>
public partial class CreateSkillWindow : Window
{
    /// <summary>目录名白名单：字母数字开头，后接字母数字与 . _ -，顺带挡掉路径穿越。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();

    /// <summary>保存成功后的技能定义（供调用方读取）。</summary>
    public SkillDefinition? Result { get; private set; }

    public CreateSkillWindow()
    {
        InitializeComponent();

        // id 通常就等于 name，别让用户抄两遍：焦点离开 id 时若 name 还空着就补上
        IdBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(IdBox.Text))
                NameBox.Text = IdBox.Text.Trim();
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var id = IdBox.Text?.Trim() ?? "";
        if (!IdPattern().IsMatch(id))
        {
            SetError("技能 id 只能由字母、数字、. _ - 组成，且以字母或数字开头");
            return;
        }

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = id;

        try
        {
            Result = SkillRegistry.Instance.CreateOnDisk(
                id, name, AliasBox.Text ?? "", DescBox.Text ?? "", BodyBox.Text?.Trim() ?? "");
        }
        catch (Exception ex)
        {
            SetError("保存失败：" + ex.Message);
            return;
        }

        Close();
    }

    private void SetError(string text)
    {
        ErrorText.Foreground = new SolidColorBrush(Color.Parse("#DC2626"));
        ErrorText.Text = text;
    }
}
