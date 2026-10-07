using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using HtaciAI.Data;
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

        WorkspacePicker.CreateRequested += OnCreateWorkspaceRequested;
        // 选中项变了要重算发送按钮能不能点（模型可能被停用后列表重建）
        ModelSelector.SelectedModelChanged += (_, _) => RefreshSendAvailability();

        // 每次打开模型菜单都重拉一次列表，让改动立刻可见
        ModelSelector.RefreshRequested += async (_, _) => await RefreshModelsAsync();

        InitTargetMode();

        _ = LoadModelsAsync();
        _ = LoadWorkspacesAsync();
        LoadTools();
        LoadSkills();
        _ = LoadMcpServersAsync();
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

    /// <summary>当前启用的 MCP 服务 id（新建会话时随消息传递给会话创建方）。</summary>
    public IReadOnlyList<string> SelectedMcpServerIds => McpSelector.SelectedServerIds;

    /// <summary>当前权限审批档位（新建会话时随消息传递给会话创建方）。</summary>
    public PermissionMode SelectedPermissionMode => _permissionMode;

    /// <summary>true = 目标为工作空间：发起后在所选工作空间里新建会话并直接请求。</summary>
    public bool IsWorkspaceMode => _workspaceMode;

    /// <summary>工作空间模式下选中的工作空间；未选择时为 null。</summary>
    public WorkspaceConfig? SelectedWorkspace => WorkspacePicker.SelectedWorkspace;

    // ---- 目标分类：普通会话 / 工作空间 ----

    private bool _workspaceMode;

    private void InitTargetMode()
    {
        ModePlainBtn.PointerPressed += (_, e) => { SetWorkspaceMode(false); e.Handled = true; };
        ModeWorkspaceBtn.PointerPressed += (_, e) => { SetWorkspaceMode(true); e.Handled = true; };
        SetWorkspaceMode(false);
    }

    private void SetWorkspaceMode(bool workspace)
    {
        _workspaceMode = workspace;

        // 选中项用白色胶囊，未选中透明；轨道本身是半透明灰
        ModePlainBtn.Background = workspace ? Brushes.Transparent : Brushes.White;
        ModeWorkspaceBtn.Background = workspace ? Brushes.White : Brushes.Transparent;
        ModePlainText.Foreground = new SolidColorBrush(Color.Parse(workspace ? "#6B7280" : "#1A1A2E"));
        ModeWorkspaceText.Foreground = new SolidColorBrush(Color.Parse(workspace ? "#1A1A2E" : "#6B7280"));

        // 不用 IsVisible：保留占位，否则切换模式时整块内容会重新居中、其他控件跟着挪
        WorkspaceSection.Opacity = workspace ? 1 : 0;
        WorkspaceSection.IsHitTestVisible = workspace;

        ApplyModeDefaults();
    }

    /// <summary>
    /// 按目标分类回填工具/技能选择器：普通会话用「默认配置」里的预选值；
    /// 工作空间交给自己那一套配置，这里清空，让宿主走「工作空间继承」分支。
    /// </summary>
    private void ApplyModeDefaults()
    {
        if (_workspaceMode)
        {
            ToolSelector.SelectedToolIds = Array.Empty<string>();
            SkillSelector.SelectedSkills = Array.Empty<SessionSkill>();
            McpSelector.SelectedServerIds = Array.Empty<string>();
            return;
        }

        ToolSelector.SelectedToolIds = AppDefaults.ResolveToolIds();
        SkillSelector.SelectedSkills = AppDefaults.ResolveSkills();
        McpSelector.SelectedServerIds = AppDefaults.ResolveMcpServerIds();
    }

    /// <summary>加载工作空间列表与各自的会话统计；一个都没有时禁用「工作空间」选项并说明原因。</summary>
    private async Task LoadWorkspacesAsync()
    {
        try
        {
            var list = await WorkspaceRepository.GetAllAsync();
            var stats = await WorkspaceSessionRepository.GetStatsAsync();
            WorkspacePicker.SetData(list, stats);

            if (list.Count == 0)
            {
                ModeWorkspaceBtn.IsEnabled = false;
                ModeWorkspaceBtn.Opacity = 0.45;   // Border 禁用不会自动变灰
                ToolTip.SetTip(ModeWorkspaceBtn, "还没有工作空间");
            }
        }
        catch
        {
            ModeWorkspaceBtn.IsEnabled = false;
            ModeWorkspaceBtn.Opacity = 0.45;
            ToolTip.SetTip(ModeWorkspaceBtn, "工作空间列表加载失败");
        }
    }

    /// <summary>选择器面板里点了「+ 新建」：走和侧栏一致的新建工作空间对话框，建完刷新并选中。</summary>
    private async void OnCreateWorkspaceRequested(object? sender, EventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dlg = new CreateWorkspaceWindow();
        if (owner is not null)
            await dlg.ShowDialog(owner);
        if (dlg.Result is null) return;

        try { await WorkspaceRepository.CreateAsync(dlg.Result); } catch { /* 忽略 */ }

        await LoadWorkspacesAsync();
        WorkspacePicker.SelectWorkspace(dlg.Result.Id);
    }

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

    /// <summary>
    /// 加载已启用的模型列表到选择器。一个都没有时选择器没有选中项，
    /// 这时必须禁用发送并说明原因 —— 不再有兜底模型，否则就是「点了没反应」。
    /// </summary>
    private async Task LoadModelsAsync()
    {
        try
        {
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
        }
        catch
        {
            // 数据库未就绪时保持空列表，选择器显示「未配置模型」
        }

        // 预选「默认配置」里的模型；没配则用上次用过的。解析不出来就保持空
        try
        {
            if (await AppDefaults.ResolveDefaultModelAsync() is { } details)
                ModelSelector.SelectedModel = details;
        }
        catch
        {
            // 忽略：保持选择器当前的选中项
        }

        RefreshSendAvailability();
    }

    /// <summary>
    /// 重新拉取模型列表（每次打开模型菜单时调用）。
    /// 不走 <see cref="LoadModelsAsync"/>：那个还会按「默认模型」重选一次，
    /// 把用户刚手动挑的模型覆盖掉。
    /// </summary>
    private async Task RefreshModelsAsync()
    {
        try
        {
            ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
            RefreshSendAvailability();
        }
        catch
        {
            // 读取失败就保持现有列表，不要清空选择器
        }
    }

    /// <summary>
    /// 没有可用模型就禁用发送按钮并挂上原因。
    /// Opacity 得自己给：Button 禁用不会自动变灰，看起来还是能点的样子。
    /// </summary>
    private void RefreshSendAvailability()
    {
        var ready = ModelSelector.SelectedModel is not null;
        SendBtn.IsEnabled = ready;
        SendBtn.Opacity = ready ? 1 : 0.45;
        ToolTip.SetTip(SendBtn, ready ? null : "请先到「设置 → 模型服务」添加并启用模型");
    }

    /// <summary>把注册表中的工具集与已启用工具注入工具选择器，再按默认配置预选。</summary>
    private void LoadTools()
    {
        var registry = ToolRegistry.Instance;
        ToolSelector.Toolsets = registry.GetToolsets();
        ToolSelector.Tools = registry.GetEnabled();

        ApplyModeDefaults();
    }

    /// <summary>把已启用的技能注入技能选择器，再按默认配置预选（默认通常一个都不勾）。</summary>
    private void LoadSkills()
    {
        SkillSelector.Skills = SkillRegistry.Instance.GetEnabled();

        ApplyModeDefaults();
    }

    /// <summary>
    /// 把全部 MCP 服务注入选择器（含未启动的：勾选后发送时自动启动），再按默认配置预选。
    /// 数据库未就绪时保持空列表，选择器显示「暂无 MCP 服务」。
    /// </summary>
    private async Task LoadMcpServersAsync()
    {
        try
        {
            McpSelector.Servers = await McpServerRepository.GetAllAsync();
        }
        catch
        {
            // 忽略：保持空列表
        }

        ApplyModeDefaults();
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
        if (string.IsNullOrEmpty(text)) return;

        // 工作空间模式但没选工作空间：不发，输入内容留着别丢
        if (_workspaceMode && SelectedWorkspace is null) return;

        // 没有可用模型：发送按钮此时是禁用的，但回车走的是这条路，得在这儿兜住。
        // 输入内容保留，配好模型回来还能接着发。
        if (ModelSelector.SelectedModel is null) return;

        SendRequested?.Invoke(this, text);
        InputBox.Text = "";
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点击输入框内部时不抢焦点
        if (e.Source is Visual src && src.GetSelfAndVisualAncestors().Contains(InputBox))
            return;

        this.Focus();
    }
}
