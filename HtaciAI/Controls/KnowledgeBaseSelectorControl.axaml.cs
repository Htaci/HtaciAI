using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HtaciAI.Models;

namespace HtaciAI.Controls;

/// <summary>
/// 知识库选择器：按钮上直接写当前知识库名（与模型选择器同一形态）。
///
/// 选中值用 <see cref="SelectedLibraryId"/> 暴露，空字符串表示「不指定」——
/// 不用 null 是为了让「还没加载完」与「用户明确选择不指定」区分得开。
/// </summary>
public partial class KnowledgeBaseSelectorControl : UserControl
{
    private List<KnowledgeBase> _libraries = new();

    /// <summary>菜单内容指纹，避免每帧重建（与技能选择器同一个理由）。</summary>
    private string _lastSignature = "";

    public KnowledgeBaseSelectorControl()
    {
        InitializeComponent();
        MenuTheme.Apply(KbBtn.Flyout as MenuFlyout);
        RebuildMenu();

        // 每次打开菜单都通知宿主重新注入列表：知识库页刚新建的库，切回会话就能看到。
        if (KbBtn.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>可选的知识库列表。</summary>
    public IReadOnlyList<KnowledgeBase> Libraries
    {
        get => _libraries;
        set
        {
            _libraries = value?.ToList() ?? new List<KnowledgeBase>();
            RebuildMenuIfChanged();
            UpdateLabel();
        }
    }

    /// <summary>当前选中的知识库 id；空字符串 = 不指定。</summary>
    public string SelectedLibraryId { get; private set; } = "";

    /// <summary>用户在选择器里改了选择。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>菜单即将展开，宿主应重新注入 <see cref="Libraries"/>。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>由宿主设置当前值（不触发 <see cref="SelectionChanged"/>）。</summary>
    public void SetSelected(string? libraryId)
    {
        SelectedLibraryId = libraryId ?? "";
        RebuildMenuIfChanged();
        UpdateLabel();
    }

    private void Select(string libraryId)
    {
        if (SelectedLibraryId == libraryId) return;
        SelectedLibraryId = libraryId;
        UpdateLabel();
        RebuildMenu();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Signature() => string.Join('\u0001', _libraries.Select(l => l.Id + "=" + l.Name));

    private void RebuildMenuIfChanged()
    {
        if (Signature() == _lastSignature) return;
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _lastSignature = Signature();
        if (KbBtn.Flyout is not MenuFlyout menu) return;

        var items = menu.Items;
        items.Clear();

        var none = new MenuItem
        {
            Header = "不指定",
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = SelectedLibraryId.Length == 0,
            GroupName = "kb",
        };
        none.Click += (_, _) => Select("");
        items.Add(none);

        if (_libraries.Count == 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = "还没有知识库，到「知识库」页新建", IsEnabled = false });
            return;
        }

        items.Add(new Separator());
        foreach (var library in _libraries)
        {
            var id = library.Id;
            var item = new MenuItem
            {
                Header = library.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = SelectedLibraryId == id,
                GroupName = "kb",
            };
            item.Click += (_, _) => Select(id);
            items.Add(item);
        }
    }

    private void UpdateLabel()
    {
        var current = _libraries.FirstOrDefault(l => l.Id == SelectedLibraryId);
        LabelText.Text = current?.Name ?? "知识库";
        ToolTip.SetTip(KbBtn, current is null
            ? "这个会话不查知识库"
            : $"知识库：{current.Name}");
    }
}
