using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Skills;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

public partial class NewChatView : UserControl
{
    public event EventHandler<string>? SendRequested;

    public NewChatView()
    {
        InitializeComponent();
        this.Focusable = true;
        // 回车发送、Shift+Enter 换行：TextBox 内部处理 Enter（AcceptsReturn）时事件已被标记 handled，
        // 需注册 handledEventsToo:true 才能可靠拦截回车（否则 Enter 只在输入框内换行而不发送）。
        InputBox.AddHandler(KeyDownEvent, (EventHandler<KeyEventArgs>)OnInputKeyDown, handledEventsToo: true);
        _ = LoadModelsAsync();
        LoadTools();
        LoadSkills();
        InitPermissionMode();
    }

    /// <summary>当前选中模型（新建会话时随消息传递给会话创建方）。</summary>
    public ModelDetails? SelectedModel => ModelSelector.SelectedModel;

    /// <summary>当前思考模式（新建会话时随消息传递给会话创建方）。</summary>
    public ThinkingMode ThinkingMode => ModelSelector.ThinkingMode;

    /// <summary>当前激活的工具 id 集合（新建会话时随消息传递给会话创建方）。</summary>
    public IReadOnlyList<string> SelectedToolIds => ToolSelector.SelectedToolIds;

    /// <summary>当前启用的技能记录（新建会话时随消息传递给会话创建方）。</summary>
    public IReadOnlyList<SessionSkill> SelectedSkills => SkillSelector.SelectedSkills;

    /// <summary>当前权限审批档位（新建会话时随消息传递给会话创建方）。</summary>
    public PermissionMode SelectedPermissionMode => _permissionMode;

    // ---- 权限模式（输入框盾牌图标） ----

    private PermissionMode _permissionMode = PermissionMode.Normal;

    /// <summary>取得权限菜单（通过按钮 Flyout 访问，规避 x:Name 在 Flyout 上的编译歧义）。</summary>
    private MenuFlyout? PermMenu => PermModeBtn.Flyout as MenuFlyout;

    private void InitPermissionMode()
    {
        if (PermMenu is not { } menu) return;
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Tag is not string tag) continue;
            mi.ToggleType = MenuItemToggleType.CheckBox;
            mi.Click += (_, _) => SetPermissionMode(ParseMode(tag));
        }
        ApplyPermissionMode();
    }

    private void SetPermissionMode(PermissionMode mode)
    {
        _permissionMode = mode;
        ApplyPermissionMode();
    }

    private void ApplyPermissionMode()
    {
        if (PermMenu is { } menu)
            foreach (var item in menu.Items)
                if (item is MenuItem mi && mi.Tag is string tag)
                    mi.IsChecked = ParseMode(tag) == _permissionMode;

        var color = _permissionMode switch
        {
            PermissionMode.Strict => "#DC2626", // 红
            PermissionMode.Normal => "#4A90D9", // 蓝
            PermissionMode.Loose => "#D97706",  // 橙
            PermissionMode.Free => "#16A34A",   // 绿
            _ => "#9CA3AF",
        };
        PermModeIcon.Foreground = new SolidColorBrush(Color.Parse(color));
        ToolTip.SetTip(PermModeBtn, ModeTip(_permissionMode));
    }

    private static string ModeTip(PermissionMode mode) => mode switch
    {
        PermissionMode.Strict => "权限模式：严格（所有工具需确认）",
        PermissionMode.Normal => "权限模式：普通（安全工具自动通过）",
        PermissionMode.Loose => "权限模式：宽松（安全/风险工具自动通过）",
        PermissionMode.Free => "权限模式：自由（所有工具免确认）",
        _ => "",
    };

    private static PermissionMode ParseMode(string tag) => tag switch
    {
        "Strict" => PermissionMode.Strict,
        "Normal" => PermissionMode.Normal,
        "Loose" => PermissionMode.Loose,
        "Free" => PermissionMode.Free,
        _ => PermissionMode.Normal,
    };

    /// <summary>加载已启用的模型列表到选择器；列表为空时控件内部回退内置默认模型。</summary>
    private async Task LoadModelsAsync()
    {
        try
        {
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
        }
        catch
        {
            // 数据库未就绪时保持内置默认模型
        }
    }

    /// <summary>把注册表中的工具集与已启用工具注入工具选择器。</summary>
    private void LoadTools()
    {
        var registry = ToolRegistry.Instance;
        ToolSelector.Toolsets = registry.GetToolsets();
        ToolSelector.Tools = registry.GetEnabled();
    }

    /// <summary>把已启用的技能注入技能选择器（新建会话默认不勾选任何技能）。</summary>
    private void LoadSkills()
    {
        SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();
    }

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Send();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send()
    {
        var text = InputBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            SendRequested?.Invoke(this, text);
            InputBox.Text = "";
        }
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点击输入框内部时不抢焦点
        if (e.Source is Visual src && src.GetSelfAndVisualAncestors().Contains(InputBox))
            return;

        this.Focus();
    }
}
