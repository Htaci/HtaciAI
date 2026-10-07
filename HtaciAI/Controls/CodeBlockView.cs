using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HtaciAI.Controls;

namespace HtaciAI.Controls;

/// <summary>
/// 代码块卡片：头部（语言标签 + 复制 / 保存）+ 行号槽 + 代码正文。
///
/// 配色跟随主题。本控件是解析过程中 new 出来的，用不了 XAML 的 DynamicResource，
/// 因此手动监听 <see cref="IThemeVariantHost.ActualThemeVariantChanged"/> 重刷画刷。
///
/// 代码正文用 NoWrap + 横向 ScrollViewer（而不是折行）：行号槽是另一个 TextBlock，
/// 折行会让「第 N 行」与实际行错位。这也是 Cherry / GitHub 的做法。
/// </summary>
public sealed class CodeBlockView : Border
{
    private const double CodeFontSize = 13;

    /// <summary>
    /// 行高必须写死：行号槽和代码是两个独立的 TextBlock，
    /// 只有各自设成同一个固定 LineHeight，第 N 行的行号才会和第 N 行代码在同一条基线上。
    /// </summary>
    private const double CodeLineHeight = 21;

    private const string GlyphCopy = "";
    private const string GlyphCheck = "";
    private const string GlyphSave = "";
    private const string GlyphCode = "";

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    private readonly string _language;
    private readonly FontFamily _codeFont;

    private readonly Border _header;
    private readonly TextBlock _langIcon;
    private readonly TextBlock _langLabel;
    private readonly TextBlock _gutter;
    private readonly Border _divider;
    private readonly CustomSelectableTextBlock _code;
    private readonly GlyphButton _copyBtn;
    private readonly GlyphButton _saveBtn;

    private string _text = "";
    private IThemeVariantHost? _themeHost;
    private DispatcherTimer? _copyFeedbackTimer;

    /// <param name="codeFont">与正文一致的等宽字体（由 Markdown 解析器传入）。</param>
    /// <param name="language">``` 后面那段语言标记，可为空（显示为 TEXT）。</param>
    public CodeBlockView(FontFamily codeFont, string language)
    {
        _codeFont = codeFont;
        _language = FirstToken(language);

        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;

        // —— 头部：左「图标 + 语言」，右「复制 / 保存」 ——
        _langIcon = new TextBlock
        {
            Text = GlyphCode,
            FontFamily = IconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _langLabel = new TextBlock
        {
            Text = DisplayLanguage,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var langGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        langGroup.Children.Add(_langIcon);
        langGroup.Children.Add(_langLabel);

        _copyBtn = new GlyphButton(GlyphCopy, "复制代码");
        _copyBtn.Clicked += (_, _) => CopyToClipboard();

        _saveBtn = new GlyphButton(GlyphSave, "保存为文件");
        _saveBtn.Clicked += (_, _) => SaveToFileAsync();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(_copyBtn);
        actions.Children.Add(_saveBtn);

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(langGroup, 0);
        Grid.SetColumn(actions, 2);
        headerGrid.Children.Add(langGroup);
        headerGrid.Children.Add(actions);

        _header = new Border
        {
            Padding = new Thickness(10, 4, 6, 4),
            Child = headerGrid,
        };
        // 头部（语言名、按钮文字）不属于正文，排除出跨块选择，否则复制整条消息会混进 "TEXT"
        SelectableTextContainer.SetIsSelectable(_header, false);

        // —— 正文：行号槽 | 分隔线 | 代码 ——
        _gutter = new TextBlock
        {
            FontFamily = _codeFont,
            FontSize = CodeFontSize,
            LineHeight = CodeLineHeight,
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(10, 8, 8, 8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        SelectableTextContainer.SetIsSelectable(_gutter, false);

        _divider = new Border { Width = 1 };

        _code = new CustomSelectableTextBlock
        {
            FontFamily = _codeFont,
            FontSize = CodeFontSize,
            LineHeight = CodeLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(10, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Top,
            // 代码块开原生选择：可拖选 / 双击选词 / Ctrl+C，不必先进蓝框模式
            NativeSelectionEnabled = true,
        };

        var scroll = new SmoothScrollViewer()
        {
            Content = _code,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        // 这里只需要横向滚动条。纵向滚轮必须继续冒泡给外层消息列表，
        // 否则鼠标停在代码块上就滚不动整个对话了。
        ScrollViewer.SetIsScrollChainingEnabled(scroll, true);

        var bodyGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(_gutter, 0);
        Grid.SetColumn(_divider, 1);
        Grid.SetColumn(scroll, 2);
        bodyGrid.Children.Add(_gutter);
        bodyGrid.Children.Add(_divider);
        bodyGrid.Children.Add(scroll);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_header, 0);
        Grid.SetRow(bodyGrid, 1);
        root.Children.Add(_header);
        root.Children.Add(bodyGrid);

        Child = root;

        SetCode("");
        ApplyTheme();
    }

    /// <summary>更新代码正文（流式期间每来一行调一次），行号随之重算。</summary>
    public void SetCode(string code)
    {
        _text = code ?? "";
        _code.Text = _text;
        _gutter.Text = BuildLineNumbers(_text);
    }

    // ---- 主题 ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _themeHost = TopLevel.GetTopLevel(this) as IThemeVariantHost;
        if (_themeHost is not null)
            _themeHost.ActualThemeVariantChanged += OnThemeVariantChanged;

        ApplyTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_themeHost is not null)
        {
            _themeHost.ActualThemeVariantChanged -= OnThemeVariantChanged;
            _themeHost = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;

        Background = Solid(dark ? 0x1E1E1E : 0xFFFFFF);
        BorderBrush = Solid(dark ? 0x3F3F46 : 0xE5E7EB);
        _header.Background = Solid(dark ? 0x2A2A2C : 0xF3F4F6);
        _divider.Background = Solid(dark ? 0x3F3F46 : 0xE5E7EB);
        _gutter.Foreground = Solid(dark ? 0x6B7280 : 0x9CA3AF);
        _code.Foreground = Solid(dark ? 0xE6E6E6 : 0x1F2328);
        _langLabel.Foreground = Solid(dark ? 0x9CA3AF : 0x6B7280);
        _langIcon.Foreground = Solid(dark ? 0x60A5FA : 0x3B82F6);

        var iconFg = Solid(dark ? 0xB4B4B8 : 0x4B5563);
        var hover = Solid(dark ? 0x3F3F46 : 0xE5E7EB);
        _copyBtn.SetPalette(iconFg, hover);
        _saveBtn.SetPalette(iconFg, hover);
    }

    private static SolidColorBrush Solid(int rgb) => new(Color.FromRgb(
        (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // ---- 行为 ----

    private async void CopyToClipboard()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_text));
        await clipboard.SetDataAsync(transfer);

        ShowCopiedFeedback();
    }

    private void ShowCopiedFeedback()
    {
        _copyBtn.Glyph.Text = GlyphCheck;

        _copyFeedbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Tick -= OnCopyFeedbackTick;
        _copyFeedbackTimer.Tick += OnCopyFeedbackTick;
        _copyFeedbackTimer.Start();
    }

    private void OnCopyFeedbackTick(object? sender, EventArgs e)
    {
        _copyFeedbackTimer?.Stop();
        _copyBtn.Glyph.Text = GlyphCopy;
    }

    private async void SaveToFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var extension = ExtensionFor(_language);
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存代码",
            SuggestedFileName = $"code{extension}",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("代码文件") { Patterns = new[] { "*" + extension } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            },
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(_text);
    }

    // ---- 文本工具 ----

    private string DisplayLanguage
        => string.IsNullOrEmpty(_language) ? "TEXT" : _language.ToUpperInvariant();

    /// <summary>语言标记可能带附加信息（```python title="x"），只取第一个词。</summary>
    private static string FirstToken(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        var trimmed = language.Trim();
        var space = trimmed.IndexOfAny(new[] { ' ', '\t', '{' });
        return space < 0 ? trimmed : trimmed.Substring(0, space);
    }

    /// <summary>
    /// 行号列。行数必须和代码块实际渲染的行数严格一致 —— 末尾多一个空行会让整个行号列错位一行。
    /// </summary>
    private static string BuildLineNumbers(string code)
    {
        var lines = 1;
        foreach (var c in code)
        {
            if (c == '\n') lines++;
        }

        var sb = new StringBuilder(lines * 3);
        for (var i = 1; i <= lines; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append(i);
        }
        return sb.ToString();
    }

    private static string ExtensionFor(string language) => language.ToLowerInvariant() switch
    {
        "c" => ".c",
        "cpp" or "c++" or "cxx" => ".cpp",
        "cs" or "csharp" => ".cs",
        "css" => ".css",
        "go" => ".go",
        "html" or "htm" => ".html",
        "java" => ".java",
        "js" or "javascript" or "node" => ".js",
        "json" => ".json",
        "kt" or "kotlin" => ".kt",
        "md" or "markdown" => ".md",
        "php" => ".php",
        "ps1" or "powershell" => ".ps1",
        "py" or "python" => ".py",
        "rb" or "ruby" => ".rb",
        "rs" or "rust" => ".rs",
        "sh" or "bash" or "shell" or "zsh" => ".sh",
        "sql" => ".sql",
        "swift" => ".swift",
        "ts" or "typescript" => ".ts",
        "xml" or "xaml" => ".xml",
        "yaml" or "yml" => ".yml",
        _ => ".txt",
    };
}

/// <summary>
/// 无边框图标按钮。刻意不用 <see cref="Button"/>：Fluent 的按钮主题把背景画在模板内层的
/// ContentPresenter 上，直接给 Button 设 Background 会被盖住，还得额外写一份 ControlTheme 才能改。
/// 用 Border + 指针事件反而更短、完全可控。
/// </summary>
internal sealed class GlyphButton : Border
{
    private IBrush _hover = Brushes.Transparent;

    public GlyphButton(string glyph, string tip)
    {
        Width = 24;
        Height = 24;
        CornerRadius = new CornerRadius(4);
        // 透明而不是 null：null 背景不参与命中测试，点在空白处收不到事件
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);

        Glyph = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = Glyph;

        ToolTip.SetTip(this, tip);

        PointerEntered += (_, _) => Background = _hover;
        PointerExited += (_, _) => Background = Brushes.Transparent;
    }

    public TextBlock Glyph { get; }

    public event EventHandler? Clicked;

    public void SetPalette(IBrush foreground, IBrush hover)
    {
        _hover = hover;
        Glyph.Foreground = foreground;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
