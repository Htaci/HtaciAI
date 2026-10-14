using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

/// <summary>
/// 「新建会话」落地页：选目标分类（普通会话 / 工作空间）、挑模型与工具、写下第一句话。
///
/// 输入区（附件栏 + 输入框 + 工具栏）用的是与对话页同一个 <see cref="ChatInputControl"/>。
/// 本页<b>不发请求</b> —— 首条消息交给 <see cref="ConversationPage"/> 去建会话并发送，
/// 所以这里不碰控件的运行态，那几处会话相关的功能也就自然不出现：
/// 不设 DraftStore（还没有会话可挂草稿）、不调 SetRunning（不会运行）、
/// 不注入压缩菜单（没有历史可压）、不设用量环（没有历史可算）。
/// </summary>
public partial class NewChatView : UserControl
{
    /// <summary>
    /// 用户提交了第一条消息。参数是正文（附件占位符已展开成真实路径）+ 附件列表。
    /// 宿主建好会话后把附件一起交给那一轮发送。
    /// </summary>
    public event EventHandler<InputSubmit>? SendRequested;

    public NewChatView()
    {
        InitializeComponent();

        this.Focusable = true;

        // 只接「提交」这一件事，其余（回车、附件、粘贴、拖放）都由控件自己处理。
        Input.SendRequested += (_, submit) => SendRequested?.Invoke(this, submit);

        WorkspacePicker.CreateRequested += OnCreateWorkspaceRequested;
        // 换了工作空间：工作空间配了默认模型就跟着切过去
        WorkspacePicker.SelectionChanged += (_, _) => ApplyWorkspaceDefaultModel();
        // 选中项变了要重算发送按钮能不能点（模型可能被停用后列表重建）
        Input.ModelSelector.SelectedModelChanged += (_, _) => RefreshSendAvailability();

        // 每次打开模型菜单都重拉一次列表，让改动立刻可见
        Input.ModelSelector.RefreshRequested += async (_, _) => await RefreshModelsAsync();

        InitTargetMode();

        _ = LoadModelsAsync();
        _ = LoadWorkspacesAsync();
        LoadSkills();
        _ = LoadMcpServersAsync();
        InitPermissionMode();
    }

    // 注意这两处的顺序：选择器必须先在「默认配置」里预选，而预选要等选项列表灌进去之后
    // 才留得住 —— 所以每次装载选项之后都要再回填一次默认值。这与改动前的行为一致。

    private void LoadSkills()
    {
        Input.LoadSkills();
        ApplyModeDefaults();
    }

    private async Task LoadMcpServersAsync()
    {
        await Input.LoadMcpServersAsync();
        ApplyModeDefaults();
    }

    // ---- 对外暴露的选择结果（新建会话时随消息传递给会话创建方） ----

    /// <summary>当前选中模型。</summary>
    public ModelDetails? SelectedModel => Input.ModelSelector.SelectedModel;

    /// <summary>当前思考模式。</summary>
    public ThinkingMode ThinkingMode => Input.ModelSelector.ThinkingMode;

    /// <summary>当前激活的工具 id 集合。</summary>
    public IReadOnlyList<string> SelectedToolIds => Input.ToolSelector.SelectedToolIds;

    /// <summary>当前启用的技能记录。</summary>
    public IReadOnlyList<SessionSkill> SelectedSkills => Input.SkillSelector.SelectedSkills;

    /// <summary>当前启用的 MCP 服务 id。</summary>
    public IReadOnlyList<string> SelectedMcpServerIds => Input.McpSelector.SelectedServerIds;

    /// <summary>当前权限审批档位。菜单在输入控件里，这里读它的当前值。</summary>
    public PermissionMode SelectedPermissionMode => Input.PermissionMode;

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
        ApplyWorkspaceDefaultModel();
    }

    /// <summary>
    /// 按目标分类回填工具/技能选择器：普通会话用「默认配置」里的预选值；
    /// 工作空间交给自己那一套配置，这里清空，让宿主走「工作空间继承」分支。
    /// </summary>
    private void ApplyModeDefaults()
    {
        if (_workspaceMode)
        {
            Input.ToolSelector.SelectedToolIds = Array.Empty<string>();
            Input.SkillSelector.SelectedSkills = Array.Empty<SessionSkill>();
            Input.McpSelector.SelectedServerIds = Array.Empty<string>();
            return;
        }

        Input.ToolSelector.SelectedToolIds = AppDefaults.ResolveToolIds();
        Input.SkillSelector.SelectedSkills = AppDefaults.ResolveSkills();
        Input.McpSelector.SelectedServerIds = AppDefaults.ResolveMcpServerIds();
    }

    /// <summary>加载工作空间列表与各自的会话统计；一个都没有时禁用「工作空间」选项并说明原因。</summary>
    private async Task LoadWorkspacesAsync()
    {
        try
        {
            var list = await WorkspaceRepository.GetAllAsync();
            var stats = await WorkspaceSessionRepository.GetStatsAsync();
            WorkspacePicker.SetData(list, stats);
            ApplyWorkspaceDefaultModel();

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
        ApplyWorkspaceDefaultModel();
    }

    // ---- 权限模式 ----
    //
    // 菜单、图标配色、四种档位的文案都在 ChatInputControl 里（两页完全一样）。
    // 本页没有会话可落库，所以只要把「默认配置」里的档位装进去即可。

    private void InitPermissionMode() => Input.PermissionMode = PermissionMode.Normal;

    /// <summary>
    /// 加载已启用的模型列表到选择器。一个都没有时选择器没有选中项，
    /// 这时必须禁用发送并说明原因 —— 不再有兜底模型，否则就是「点了没反应」。
    /// </summary>
    private async Task LoadModelsAsync()
    {
        try
        {
            Input.ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
        }
        catch
        {
            // 数据库未就绪时保持空列表，选择器显示「未配置模型」
        }

        // 预选「默认配置」里的模型；没配则用上次用过的。解析不出来就保持空
        try
        {
            if (await AppDefaults.ResolveDefaultModelAsync() is { } details)
                Input.ModelSelector.SelectedModel = details;
        }
        catch
        {
            // 忽略：保持选择器当前的选中项
        }

        // 工作空间模式下，工作空间的默认模型优先于「上次使用的模型」
        ApplyWorkspaceDefaultModel();
        RefreshSendAvailability();
    }

    /// <summary>
    /// 工作空间配了默认模型就把它设为当前选中项（仅工作空间模式下生效）。
    /// 没配就保持现状 —— 即 <see cref="LoadModelsAsync"/> 选出的「上次使用的模型」。
    /// </summary>
    private void ApplyWorkspaceDefaultModel()
    {
        if (!_workspaceMode) return;

        var id = WorkspacePicker.SelectedWorkspace?.DefaultModelId;
        if (string.IsNullOrWhiteSpace(id)) return;

        var match = Input.ModelSelector.Models.FirstOrDefault(m => m.ModelId == id || m.ModelName == id);
        if (match is not null) Input.ModelSelector.SelectedModel = match;
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
            Input.ModelSelector.Models = await ModelCatalog.ListEnabledAsync();
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
        => Input.RefreshSendAvailability("请先到「设置 → 模型服务」添加并启用模型");

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点击输入框内部时不抢焦点
        if (e.Source is Visual src && src.GetSelfAndVisualAncestors().Contains(Input))
            return;

        this.Focus();
    }
}
