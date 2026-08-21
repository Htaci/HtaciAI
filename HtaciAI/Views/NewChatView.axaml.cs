using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HtaciAI.Services;
using HtaciAI.Services.Tools;

namespace HtaciAI.Views;

public partial class NewChatView : UserControl
{
    public event EventHandler<string>? SendRequested;

    public NewChatView()
    {
        InitializeComponent();
        this.Focusable = true;
        _ = LoadModelsAsync();
        LoadTools();
    }

    /// <summary>当前选中模型（新建会话时随消息传递给会话创建方）。</summary>
    public ModelDetails? SelectedModel => ModelSelector.SelectedModel;

    /// <summary>当前思考模式（新建会话时随消息传递给会话创建方）。</summary>
    public ThinkingMode ThinkingMode => ModelSelector.ThinkingMode;

    /// <summary>当前激活的工具 id 集合（新建会话时随消息传递给会话创建方）。</summary>
    public IReadOnlyList<string> SelectedToolIds => ToolSelector.SelectedToolIds;

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
