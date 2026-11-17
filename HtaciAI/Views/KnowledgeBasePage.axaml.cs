using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HtaciAI.Controls;
using HtaciAI.Data;
using HtaciAI.Models;
using HtaciAI.Services;
using HtaciAI.Services.KnowledgeBase;
using HtaciAI.Services.Storage;

namespace HtaciAI.Views;

/// <summary>
/// 知识库页：左侧库列表，右侧内容。
///
/// 一个库下分两块，彼此独立：
/// <list type="bullet">
///   <item><b>笔记</b>：Markdown 正文，随手写、随手改，不需要嵌入模型。</item>
///   <item><b>向量库</b>：导入文件 → 切块 → 嵌入 → 检索；没有配置嵌入模型时这块不可用，
///         但不会影响笔记，也不会影响已经导入的文件（它们仍然在目录里）。</item>
/// </list>
///
/// 「导入」只复制文件并登记，「建立索引」才真正花额度调嵌入接口 ——
/// 两件事分开，用户才有机会先看清自己要索引些什么。
/// </summary>
public partial class KnowledgeBasePage : UserControl
{
    // ---- 配色与页面里其它管理页保持一致 ----
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#1A1A2E"));
    private static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush TextFaint = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#F1F3F5"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4A90D9"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#D97706"));

    private List<KnowledgeBase> _libraries = new();

    /// <summary>
    /// 保存前确认标题没被别的笔记占着。
    ///
    /// 同库内标题唯一（数据库上有索引），而 UpsertNoteAsync 是「按标题 upsert」：
    /// 把 A 的标题改成已有的 B，冲突会落在 B 那一行上，结果 A 的内容搬进 B、A 自己消失，
    /// 而 B 在界面上毫无动静 —— 用户看到的是「我的另一篇笔记不见了」。
    /// 与其让它静默合并，不如在这里拦下来让用户换个标题。
    /// </summary>
    private async Task<bool> EnsureTitleFreeAsync(KbNote note, string title, TextBlock? status)
    {
        if (title.Length == 0)
        {
            if (status is not null)
            {
                status.Text = "标题不能为空";
                status.Foreground = Danger;
            }
            return false;
        }

        var all = await KnowledgeBaseRepository.GetNotesAsync(note.LibraryId);
        var taken = all.Any(n => n.Id != note.Id && string.Equals(n.Title, title, StringComparison.Ordinal));
        if (taken)
        {
            if (status is not null)
            {
                status.Text = $"这个库里已经有「{title}」了，换一个标题";
                status.Foreground = Danger;
            }
            return false;
        }

        return true;
    }

    /// <summary>当前展示的库；null = 在主页（或新建表单）。</summary>
    private KnowledgeBase? _current;

    /// <summary>库详情页当前分页。</summary>
    private LibraryTab _tab = LibraryTab.Notes;

    /// <summary>正在编辑的笔记；null = 笔记列表。</summary>
    private KbNote? _editingNote;

    private bool _indexing;
    private CancellationTokenSource? _indexCts;

    private enum LibraryTab
    {
        Notes,
        Vectors,
    }

    public KnowledgeBasePage()
    {
        InitializeComponent();

        HomeButton.Click += (_, _) => ShowHome();
        AddButton.Click += (_, _) => ShowCreateForm();
        OpenDirectoryButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_current is not null) OpenDirectory(AppPaths.KnowledgeBaseFiles(_current.Id));
        };

        _ = LoadAsync();
    }

    // ============================================================
    // 数据
    // ============================================================

    private async Task LoadAsync()
    {
        try
        {
            _libraries = await KnowledgeBaseRepository.GetAllAsync();
        }
        catch
        {
            _libraries = new List<KnowledgeBase>();
        }

        BuildLibraryList();

        // 刷新后停在原处：库还在就重画它，不在了回主页
        if (_current is not null && _libraries.FirstOrDefault(l => l.Id == _current.Id) is { } still)
        {
            _current = still;
            await RenderCurrentAsync();
        }
        else if (_current is not null)
        {
            ShowHome();
        }
        else if (DetailPanel.Children.Count == 0)
        {
            ShowHome();
        }
    }

    private void BuildLibraryList()
    {
        LibraryList.Children.Clear();

        if (_libraries.Count == 0)
        {
            LibraryList.Children.Add(new TextBlock
            {
                Text = "还没有知识库，点右上角 ＋ 新建一个。",
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 4),
            });
            return;
        }

        foreach (var library in _libraries)
            LibraryList.Children.Add(CreateLibraryRow(library));
    }

    private Control CreateLibraryRow(KnowledgeBase library)
    {
        var isActive = _current?.Id == library.Id;

        var name = new TextBlock
        {
            Text = library.Name,
            FontSize = 13,
            Foreground = isActive ? TextPrimary : TextMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var count = new TextBlock
        {
            Text = $"{library.NoteCount} 篇笔记 · {library.DocumentCount} 个文件",
            FontSize = 11,
            Foreground = TextFaint,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(count);

        var icon = new TextBlock
        {
            Text = "\uF8FE",
            FontFamily = AppFonts.Icons,
            FontSize = 14,
            Foreground = isActive ? Accent : TextFaint,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);

        var row = new Border
        {
            Height = 52,
            CornerRadius = new CornerRadius(10),
            Background = isActive ? new SolidColorBrush(Color.Parse("#E8F1FB")) : Brushes.Transparent,
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        row.PointerEntered += (_, _) =>
        {
            if (!isActive) row.Background = Surface;
        };
        row.PointerExited += (_, _) =>
        {
            if (!isActive) row.Background = Brushes.Transparent;
        };
        row.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            _current = library;
            _editingNote = null;
            _tab = LibraryTab.Notes;
            BuildLibraryList();
            await RenderCurrentAsync();
        };

        return row;
    }

    // ============================================================
    // 主页 / 新建
    // ============================================================

    private void ShowHome()
    {
        _current = null;
        _editingNote = null;
        BuildLibraryList();

        HeaderTitle.Text = "知识库";
        HeaderDesc.Text = "笔记随时可写；向量库需要先配嵌入模型";
        DetailTabs.IsVisible = false;
        DetailTabs.Children.Clear();
        DetailActions.IsVisible = false;
        OpenDirectoryButton.IsVisible = false;

        DetailPanel.Children.Clear();

        if (_libraries.Count == 0)
        {
            DetailPanel.Children.Add(CreateEmptyCard(
                "还没有知识库",
                "知识库用来放两类东西：随手写的笔记，和导入后可供 AI 检索的文件。"
                + "点左上角的 ＋ 新建一个。"));
            return;
        }

        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var library in _libraries)
        {
            var card = CreateLibraryCard(library);
            card.Margin = new Thickness(0, 0, 12, 12);
            grid.Children.Add(card);
        }
        DetailPanel.Children.Add(grid);
    }

    private Control CreateLibraryCard(KnowledgeBase library)
    {
        var body = new StackPanel { Spacing = 6, Width = 240 };

        body.Children.Add(new TextBlock
        {
            Text = library.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(library.Description) ? "（没有描述）" : library.Description,
            FontSize = 12,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
        });

        body.Children.Add(new TextBlock
        {
            Text = $"{library.NoteCount} 篇笔记 · {library.DocumentCount} 个文件",
            FontSize = 11.5,
            Foreground = TextMuted,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = body,
        };

        card.PointerEntered += (_, _) => card.BorderBrush = new SolidColorBrush(Color.Parse("#7EB3E8"));
        card.PointerExited += (_, _) => card.BorderBrush = LineBrush;
        card.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            _current = library;
            _editingNote = null;
            _tab = LibraryTab.Notes;
            BuildLibraryList();
            await RenderCurrentAsync();
        };

        return card;
    }

    private void ShowCreateForm()
    {
        _current = null;
        _editingNote = null;
        BuildLibraryList();

        HeaderTitle.Text = "新建知识库";
        HeaderDesc.Text = "一个库对应一组笔记与一组导入文件";
        DetailTabs.IsVisible = false;
        DetailTabs.Children.Clear();
        DetailActions.IsVisible = false;
        OpenDirectoryButton.IsVisible = false;

        DetailPanel.Children.Clear();

        var nameBox = CreateTextBox("名称", "");
        var descBox = CreateTextBox("描述（可空）", "");

        var status = new TextBlock { FontSize = 12, Foreground = Danger, IsVisible = false };

        var create = CreateButton("创建", primary: true);
        var cancel = CreateButton("取消");
        create.Click += async (_, _) =>
        {
            var name = nameBox.Text?.Trim() ?? "";
            if (name.Length == 0)
            {
                status.Text = "名称不能为空。";
                status.IsVisible = true;
                return;
            }

            var library = new KnowledgeBase
            {
                Id = KnowledgeBaseRepository.NewId(),
                Name = name,
                Description = descBox.Text?.Trim() ?? "",
            };
            await KnowledgeBaseRepository.CreateAsync(library);

            _current = library;
            _tab = LibraryTab.Notes;
            await LoadAsync();
        };
        cancel.Click += (_, _) => ShowHome();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { create, cancel },
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(nameBox);
        body.Children.Add(descBox);
        body.Children.Add(status);
        body.Children.Add(actions);

        DetailPanel.Children.Add(CreateCard("新建知识库", null, body));
    }

    // ============================================================
    // 库详情
    // ============================================================

    private async Task RenderCurrentAsync()
    {
        if (_current is null)
        {
            ShowHome();
            return;
        }

        HeaderTitle.Text = _current.Name;
        HeaderDesc.Text = string.IsNullOrWhiteSpace(_current.Description)
            ? "笔记随时可写；向量库需要先配嵌入模型"
            : _current.Description;
        OpenDirectoryButton.IsVisible = true;

        BuildDetailTabs();
        BuildDetailActions();

        if (_editingNote is not null && _tab == LibraryTab.Notes)
        {
            BuildNoteEditor(_editingNote);
            return;
        }

        switch (_tab)
        {
            case LibraryTab.Notes:
                await BuildNotesTabAsync();
                break;
            default:
                await BuildVectorsTabAsync();
                break;
        }
    }

    private void BuildDetailTabs()
    {
        DetailTabs.IsVisible = true;
        DetailTabs.Children.Clear();

        DetailTabs.Children.Add(CreateTab("笔记", LibraryTab.Notes));
        DetailTabs.Children.Add(CreateTab("向量库", LibraryTab.Vectors));
    }

    private Control CreateTab(string label, LibraryTab tab)
    {
        var isActive = _tab == tab;

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = isActive ? Accent : TextMuted,
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var underline = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = isActive ? Accent : Brushes.Transparent,
            Margin = new Thickness(0, 5, 0, 0),
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(text);
        stack.Children.Add(underline);

        stack.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            if (_tab == tab) return;

            _tab = tab;
            _editingNote = null;
            BuildDetailTabs();
            await RenderCurrentAsync();
        };

        return stack;
    }

    private void BuildDetailActions()
    {
        DetailActions.IsVisible = true;
        DetailActions.Children.Clear();

        var rename = CreateGhostButton("重命名");
        rename.Click += async (_, _) => await RenameCurrentAsync();

        var delete = CreateGhostButton("删除", Danger);
        delete.Click += async (_, _) => await DeleteCurrentAsync();

        DetailActions.Children.Add(rename);
        DetailActions.Children.Add(delete);
    }

    private async Task RenameCurrentAsync()
    {
        if (_current is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var (result, text) = await MessageEditDialog.ShowAsync(owner, "重命名知识库", _current.Name);
        if (result == MessageEditResult.Cancel) return;

        var name = text.Trim();
        if (name.Length == 0) return;

        await KnowledgeBaseRepository.RenameAsync(_current.Id, name, _current.Description);
        _current.Name = name;
        await LoadAsync();
    }

    private async Task DeleteCurrentAsync()
    {
        if (_current is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var ok = await ConfirmDialog.ShowAsync(owner,
            $"确定删除知识库「{_current.Name}」吗？\n\n里面的笔记、已索引的内容和导入的文件都会一起删掉，无法恢复。"
            + "\n正在使用它的会话会被改回「不指定」。",
            "删除知识库");
        if (!ok) return;

        var library = _current;
        await KnowledgeBaseRepository.DeleteAsync(library.Id);
        await KnowledgeBaseRepository.ClearReferencesAsync(library.Id);

        // 导入的文件目录一起清掉：库里已经没有这些行的记录了，留着只会变成谁都认不出的垃圾
        try
        {
            var dir = Path.Combine(AppPaths.KnowledgeBases, library.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 文件被占用时删不掉，库里已经没记录，这里不必让整个删除失败
        }

        _current = null;
        ShowHome();
        await LoadAsync();
    }

    // ============================================================
    // 笔记
    // ============================================================

    private async Task BuildNotesTabAsync()
    {
        DetailPanel.Children.Clear();
        if (_current is null) return;

        List<KbNote> notes;
        try
        {
            notes = await KnowledgeBaseRepository.GetNotesAsync(_current.Id);
        }
        catch
        {
            notes = new List<KbNote>();
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "笔记",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var newNote = CreateButton("新建笔记", primary: true);
        newNote.Click += (_, _) => _ = CreateNoteAsync();
        header.Children.Add(newNote);
        Grid.SetColumn(newNote, 1);

        DetailPanel.Children.Add(header);

        if (notes.Count == 0)
        {
            DetailPanel.Children.Add(CreateEmptyCard(
                "还没有笔记",
                "笔记是随手写的东西：约定、踩过的坑、待办的思考。"
                + "正文用 Markdown 写，预览里就是排好版的样子。"));
            return;
        }

        foreach (var note in notes)
            DetailPanel.Children.Add(CreateNoteRow(note));
    }

    private Control CreateNoteRow(KbNote note)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? "（无标题）" : note.Title,
            FontSize = 13.5,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var excerpt = new TextBlock
        {
            Text = FirstLine(note.Content),
            FontSize = 12,
            Foreground = TextFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(excerpt);

        var time = new TextBlock
        {
            Text = FormatTime(note.UpdatedAt),
            FontSize = 11,
            Foreground = TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        grid.Children.Add(time);
        Grid.SetColumn(time, 1);

        var row = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        row.PointerEntered += (_, _) => row.BorderBrush = new SolidColorBrush(Color.Parse("#7EB3E8"));
        row.PointerExited += (_, _) => row.BorderBrush = LineBrush;
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _editingNote = note;
            BuildNoteEditor(note);
        };

        return row;
    }

    private async Task CreateNoteAsync()
    {
        if (_current is null) return;

        var note = new KbNote
        {
            Id = KnowledgeBaseRepository.NewId(),
            LibraryId = _current.Id,
            Title = "",
            Content = "",
        };

        _editingNote = note;
        BuildNoteEditor(note);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 笔记编辑：标题框 + 编辑/预览切换。
    /// 默认进预览（打开笔记多半是想看内容），要改再点「编辑」。
    /// </summary>
    private void BuildNoteEditor(KbNote note)
    {
        DetailPanel.Children.Clear();

        var titleBox = new TextBox
        {
            Text = note.Title,
            PlaceholderText = "标题",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2),
        };

        var editor = new TextBox
        {
            Text = note.Content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 320,
            FontSize = 13.5,
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            IsVisible = false,
        };

        var preview = new Markdown
        {
            MinHeight = 320,
        };
        preview.SetMarkdown(note.Content);

        var status = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TextFaint,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var editLabel = new TextBlock
        {
            Text = "编辑",
            FontSize = 12.5,
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editToggle = new Button
        {
            Content = editLabel,
            Height = 28,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        editToggle.PointerEntered += (_, _) => editToggle.Background = Surface;
        editToggle.PointerExited += (_, _) => editToggle.Background = Brushes.Transparent;

        var save = CreateButton("保存", primary: true);
        var back = CreateGhostButton("返回列表");
        var delete = CreateGhostButton("删除", Danger);

        editToggle.Click += (_, _) =>
        {
            var editing = !editor.IsVisible;
            editor.IsVisible = editing;
            preview.IsVisible = !editing;
            editLabel.Text = editing ? "预览" : "编辑";
            if (!editing)
            {
                // 切回预览时把编辑框里的内容推给渲染器 —— 不落库，落库只在「保存」
                preview.SetMarkdown(editor.Text ?? "");
            }
        };

        save.Click += async (_, _) =>
        {
            var title = titleBox.Text?.Trim() ?? "";
            if (!await EnsureTitleFreeAsync(note, title, status)) return;

            note.Title = title;
            note.Content = editor.Text ?? "";
            await KnowledgeBaseRepository.UpsertNoteAsync(note);
            preview.SetMarkdown(note.Content);
            status.Text = $"已保存 · {FormatTime(note.UpdatedAt)}";
            status.Foreground = OkBrush;
        };

        back.Click += async (_, _) =>
        {
            // 返回前把改动存一下：用户点了「返回」通常以为已经写完了
            var title = titleBox.Text?.Trim() ?? "";
            if (note.Title != title || note.Content != (editor.Text ?? ""))
            {
                if (!await EnsureTitleFreeAsync(note, title, status)) return;

                note.Title = title;
                note.Content = editor.Text ?? "";
                await KnowledgeBaseRepository.UpsertNoteAsync(note);
            }

            _editingNote = null;
            await LoadAsync();
        };

        delete.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (!await ConfirmDialog.ShowAsync(owner, $"确定删除笔记「{note.Title}」吗？", "删除笔记")) return;

            await KnowledgeBaseRepository.DeleteNoteAsync(note.Id);
            _editingNote = null;
            await LoadAsync();
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tools.Children.Add(editToggle);
        tools.Children.Add(save);
        tools.Children.Add(delete);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        toolbar.Children.Add(status);
        toolbar.Children.Add(tools);
        Grid.SetColumn(tools, 1);

        DetailPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { back },
        });
        DetailPanel.Children.Add(titleBox);
        DetailPanel.Children.Add(toolbar);
        DetailPanel.Children.Add(editor);
        DetailPanel.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12),
            Child = preview,
        });
    }

    // ============================================================
    // 向量库
    // ============================================================

    private async Task BuildVectorsTabAsync()
    {
        DetailPanel.Children.Clear();
        if (_current is null) return;

        var libraryId = _current.Id;
        var embedModel = await AppDefaults.ResolveEmbeddingModelAsync();
        KbLibraryStats stats;
        List<KbDocument> documents;
        try
        {
            stats = await KnowledgeBaseRepository.GetStatsAsync(libraryId);
            documents = await KnowledgeBaseRepository.GetDocumentsAsync(libraryId);
        }
        catch
        {
            stats = new KbLibraryStats();
            documents = new List<KbDocument>();
        }

        // ---- 嵌入模型状态：这一块决定下面所有操作能不能做 ----
        var statusPanel = new StackPanel { Spacing = 4 };
        if (embedModel is null)
        {
            statusPanel.Children.Add(new TextBlock
            {
                Text = "未配置嵌入模型，向量库不可用。",
                FontSize = 13,
                Foreground = WarnBrush,
            });
            statusPanel.Children.Add(new TextBlock
            {
                Text = "到「设置 → 默认配置 → 向量库嵌入模型」选一个支持 embedding 的服务商模型。"
                       + "笔记不受影响，仍然可以正常写。",
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            statusPanel.Children.Add(new TextBlock
            {
                Text = $"嵌入模型：{embedModel.DisplayName}（{embedModel.ProviderName}）",
                FontSize = 13,
                Foreground = TextPrimary,
            });

            var changedModel = stats.ModelId.Length > 0 && stats.ModelId != embedModel.ModelName;
            if (changedModel)
            {
                statusPanel.Children.Add(new TextBlock
                {
                    Text = $"索引是用「{stats.ModelId}」建的，与当前模型不一致。换模型后向量空间变了，"
                           + "旧索引的相似度不再可信，建议重建索引。",
                    FontSize = 12,
                    Foreground = WarnBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            statusPanel.Children.Add(new TextBlock
            {
                Text = $"{stats.Documents} 个文件 · {stats.Chunks} 个切片"
                       + (stats.Pending > 0 ? $" · {stats.Pending} 个待索引" : "")
                       + (stats.Failed > 0 ? $" · {stats.Failed} 个失败" : ""),
                FontSize = 12,
                Foreground = TextFaint,
            });
        }

        // ---- 操作 ----
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var import = CreateButton("导入文件", primary: true);
        import.IsEnabled = true;
        import.Click += async (_, _) => await ImportFilesAsync();

        var scan = CreateGhostButton("扫描目录");
        scan.Click += async (_, _) =>
        {
            var added = await KnowledgeBaseIndexer.ScanDirectoryAsync(libraryId);
            if (added == 0) await InfoAsync("导入目录里没有新文件。", "扫描目录");
            await LoadAsync();
        };

        var index = CreateButton("建立索引");
        index.IsEnabled = embedModel is not null && !_indexing;
        index.Click += async (_, _) => await IndexAllAsync();

        actions.Children.Add(import);
        actions.Children.Add(scan);
        actions.Children.Add(index);

        if (_indexing)
        {
            var stop = CreateGhostButton("停止索引", Danger);
            stop.Click += (_, _) => _indexCts?.Cancel();
            actions.Children.Add(stop);
        }

        DetailPanel.Children.Add(CreateCard("向量库", null, statusPanel, actions));

        if (documents.Count == 0)
        {
            DetailPanel.Children.Add(CreateEmptyCard(
                "还没有导入文件",
                "支持的格式：Markdown、纯文本、JSON、YAML、INI、CSV、日志、XML。"
                + "导入只是把文件复制进来，点「建立索引」才花钱调嵌入接口。"));
            return;
        }

        foreach (var doc in documents)
            DetailPanel.Children.Add(CreateDocumentRow(doc));
    }

    private Control CreateDocumentRow(KbDocument doc)
    {
        var title = new TextBlock
        {
            Text = doc.Title,
            FontSize = 13.5,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var (statusText, statusBrush) = doc.Status switch
        {
            "ok" => ($"{doc.ChunkCount} 个切片 · {FormatTime(doc.IndexedAt)}", TextFaint),
            "failed" => (doc.Error ?? "索引失败", Danger),
            _ => ("待索引", WarnBrush),
        };

        var detail = new TextBlock
        {
            Text = $"{doc.Path} · {statusText}",
            FontSize = 11.5,
            Foreground = statusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(detail);

        var remove = CreateGhostButton("移除", Danger);
        remove.Click += async (_, _) =>
        {
            // 与 kb_remove 走同一条路：登记、切片、知识库目录里那份副本一起清掉。
            // 只删登记不删副本的话，下次「扫描目录」会把这个文件重新登记回来。
            var result = await KnowledgeBaseIndexer.RemoveDocumentAsync(doc);
            if (!result.Ok)
            {
                await InfoAsync(result.Error ?? "删除失败", "移除失败");
                return;
            }
            await LoadAsync();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        grid.Children.Add(remove);
        Grid.SetColumn(remove, 1);

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10),
            Child = grid,
        };
    }

    private async Task ImportFilesAsync()
    {
        if (_current is null) return;

        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入知识库的文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("可索引的文本文件")
                {
                    Patterns = new[] { "*.md", "*.markdown", "*.txt", "*.json", "*.yaml", "*.yml", "*.ini", "*.csv", "*.log", "*.xml" },
                },
                FilePickerFileTypes.All,
            },
        });

        if (files.Count == 0) return;

        var libraryId = _current.Id;
        var failed = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null) continue;

            if (!KnowledgeBaseIndexer.IsSupported(path))
            {
                failed.Add($"{Path.GetFileName(path)}（格式不支持）");
                continue;
            }

            try
            {
                await KnowledgeBaseIndexer.ImportAsync(libraryId, path);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}（{ex.Message}）");
            }
        }

        if (failed.Count > 0)
            await InfoAsync("这些文件没有导入成功：\n\n" + string.Join("\n", failed), "导入文件");

        await LoadAsync();
    }

    /// <summary>
    /// 给这个库里所有待索引 / 内容变过的文件建索引。
    /// 一个文件失败不中断整批：失败原因写进文档行，界面上能直接看到是哪个、为什么。
    /// </summary>
    private async Task IndexAllAsync()
    {
        if (_current is null || _indexing) return;

        var embedModel = await AppDefaults.ResolveEmbeddingModelAsync();
        if (embedModel is null)
        {
            await InfoAsync("还没有配置嵌入模型，无法建立索引。", "建立索引");
            return;
        }

        var libraryId = _current.Id;
        var documents = await KnowledgeBaseRepository.GetDocumentsAsync(libraryId);
        if (documents.Count == 0)
        {
            await InfoAsync("这个库里还没有文件。先「导入文件」或把文件拷进导入目录再「扫描目录」。", "建立索引");
            return;
        }

        _indexing = true;
        _indexCts = new CancellationTokenSource();
        await BuildVectorsTabAsync();

        var okCount = 0;
        var failCount = 0;
        try
        {
            foreach (var doc in documents)
            {
                if (_indexCts.IsCancellationRequested) break;

                var result = await KnowledgeBaseIndexer.IndexDocumentAsync(doc, embedModel, _indexCts.Token);
                if (result.Ok) okCount++;
                else failCount++;
            }
        }
        finally
        {
            _indexing = false;
            _indexCts.Dispose();
            _indexCts = null;
        }

        await LoadAsync();

        if (failCount > 0)
            await InfoAsync($"索引完成：成功 {okCount} 个，失败 {failCount} 个。失败原因见列表。", "建立索引");
    }

    // ============================================================
    // 小工具
    // ============================================================

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（空）";

        var line = text.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        line = line.TrimStart('#', ' ', '-', '>', '*');
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    private static string FormatTime(long millis)
    {
        if (millis <= 0) return "";

        var time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
        return time.Date == DateTimeOffset.Now.Date
            ? time.ToString("HH:mm")
            : time.ToString("yyyy-MM-dd HH:mm");
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // 打不开就用不了，但不算错误
        }
    }

    private async Task InfoAsync(string message, string title)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        await ConfirmDialog.ShowAsync(owner, message, title);
    }

    private TextBox CreateTextBox(string watermark, string text) => new()
    {
        Text = text,
        PlaceholderText = watermark,
        FontSize = 13,
        Height = 36,
        Padding = new Thickness(10, 0),
        CornerRadius = new CornerRadius(8),
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
    };

    private Button CreateButton(string label, bool primary = false) => new()
    {
        Content = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = primary ? Brushes.White : TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
        Height = 30,
        Padding = new Thickness(14, 0),
        CornerRadius = new CornerRadius(8),
        Background = primary ? Accent : Brushes.Transparent,
        BorderBrush = primary ? Accent : LineBrush,
        BorderThickness = new Thickness(1),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private Button CreateGhostButton(string label, IBrush? foreground = null)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = foreground ?? TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Height = 28,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.PointerEntered += (_, _) => button.Background = Surface;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static Control CreateCard(string title, string? description, params Control[] body)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
        });

        if (!string.IsNullOrEmpty(description))
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = TextFaint,
                TextWrapping = TextWrapping.Wrap,
            });

        foreach (var child in body) content.Children.Add(child);

        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            Child = content,
        };
    }

    private static Control CreateEmptyCard(string title, string description)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextMuted,
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12.5,
            Foreground = TextFaint,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = Surface,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16),
            Child = content,
        };
    }
}
