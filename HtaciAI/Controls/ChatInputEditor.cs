using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace HtaciAI.Controls;

/// <summary>
/// 会话输入框的文本外壳：AvaloniaEdit 的 <see cref="TextEditor"/> + 一个把占位字符
/// 换成真控件的 <see cref="VisualLineElementGenerator"/>。
///
/// <b>为什么不用 TextBox</b>：TextBox 只能显示字符。附件要在输入框里显示成消息气泡里那种
/// 卡片（图标 + 文件名 + × ），而不只是 <c>@图片1</c> 这样的文字 —— 那需要「正文里的某个字符
/// 由真控件代替」，Avalonia 里只有 AvaloniaEdit 的 <c>VisualLineElementGenerator</c> 扩展点
/// 能做到，TextBox 没有对应的口子。
///
/// 分工：
/// <list type="bullet">
///   <item>AvaloniaEdit 负责文本缓冲区、光标、选区、IME、撤销、滚动；</item>
///   <item>正文里每个 <see cref="WidgetChar"/>（U+FFFC）按出现顺序对应一张卡片，
///         卡片控件由 <see cref="WidgetProvider"/> 提供；</item>
///   <item>本类只做「哪个偏移上有占位字符」和「换行/占位提示」这些外壳的事，
///         谁对应什么文件由调用方（<see cref="AttachmentDraft"/> 那一套）决定。</item>
/// </list>
///
/// <b>偏移表每次文本变化都整篇重扫</b>，不用增量维护：输入框正文只有几百字符，
/// 一次 O(n) 扫描的代价可以忽略，换来的是「用户怎么改都不会错位」——
/// 增量维护要做插入撑长、删除裁短、跨行移动三件事，任何一处漏了都是卡片贴错行。
/// </summary>
public sealed class ChatInputEditor : UserControl
{
    /// <summary>正文里代表一张卡片的占位字符（Unicode Object Replacement Character）。</summary>
    /// <remarks>
    /// 与 <c>AttachmentDraft.Token</c> 必须同值：那一侧负责「第几个占位字符对应哪个文件」，
    /// 这一侧负责「把它画成控件」。本类在 Controls 层、不引用 Services 层，所以常量各写一份。
    /// </remarks>
    public const char WidgetChar = '\uFFFC';

    private readonly TextEditor _editor;
    private readonly TextBlock _placeholder;
    private readonly InlineWidgetGenerator _generator;

    /// <summary>正文里每个占位字符的偏移，升序。</summary>
    private readonly List<int> _slots = new();

    private bool _renderersAttached;

    /// <summary>按序号取卡片控件；序号 = 该占位字符在正文里是第几个。</summary>
    private Func<int, Control?>? _widgetProvider;

    public ChatInputEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = false,
            WordWrap = true,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            // 底色透明、边框 0：外面那圈圆角与光晕由宿主（InputShell）画，
            // 这里再画一层会和它叠成双边框。
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // 底部留出的空档是给浮在输入框里的功能栏的（上传 / 模型 / 发送那一行）。
            Padding = new Thickness(16, 16, 16, 38),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _editor.Document = new TextDocument();
        _editor.Document.Changed += OnDocumentChanged;

        _generator = new InlineWidgetGenerator(this);

        _placeholder = new TextBlock
        {
            Text = "在这里输入你的问题，Enter 发送，Shift+Enter 换行",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid();
        grid.Children.Add(_editor);
        grid.Children.Add(_placeholder);
        Content = grid;

        UpdatePlaceholder();
    }

    /// <summary>底层编辑器。宿主要挂拖放/按键处理时用。</summary>
    public TextEditor Editor => _editor;

    /// <summary>正文。赋值走 Document.Text，会触发一次 <see cref="TextChanged"/>。</summary>
    public string Text
    {
        get => _editor.Document.Text;
        set => _editor.Document.Text = value ?? "";
    }

    /// <summary>光标位置（字符偏移）。</summary>
    public int CaretIndex
    {
        get => _editor.CaretOffset;
        set => _editor.CaretOffset = Math.Clamp(value, 0, _editor.Document.TextLength);
    }

    public int SelectionStart => _editor.SelectionStart;

    public int SelectionLength => _editor.SelectionLength;

    /// <summary>选区终点（<see cref="SelectionStart"/> 之后的字符数）。</summary>
    public int SelectionEnd => _editor.SelectionStart + _editor.SelectionLength;

    /// <summary>正文变化。整体替换与逐字输入都会触发。</summary>
    public event EventHandler? TextChanged;

    /// <summary>焦点是否在输入区（<see cref="TextEditor"/> 的焦点其实落在它内部的 TextArea 上）。</summary>
    public bool IsInputFocused => _editor.TextArea?.IsFocused == true || _editor.IsFocused;

    /// <summary>把焦点交给输入区（焦点真正落点是内部的 TextArea，不是外层的 TextEditor）。</summary>
    public bool FocusInput() => _editor.TextArea?.Focus() ?? _editor.Focus();

    /// <summary>按普通文本粘贴剪贴板内容。</summary>
    public void Paste() => _editor.Paste();

    /// <summary>
    /// 设置卡片来源：给一个序号，返回该位置要显示的控件（返回 null 则那个占位字符按普通
    /// 空字符显示）。序号 = 该占位字符在正文里是第几个。
    ///
    /// <b>同一序号必须返回同一个控件实例</b>：内联控件滚出视口会被摘出视觉树、
    /// 滚回来再挂回去，每次新建实例会让卡片里的悬停态、按钮状态反复丢失。
    /// </summary>
    public void SetWidgetProvider(Func<int, Control?>? provider)
    {
        _widgetProvider = provider;
        _editor.TextArea?.TextView.Redraw();
    }

    /// <summary>整体替换正文并把光标放到指定位置（删卡片、采纳标记这类整段改动用它）。</summary>
    public void ReplaceText(string text, int caret)
    {
        _editor.Document.Text = text ?? "";
        CaretIndex = caret;
    }

    /// <summary>重画一遍（卡片外观变了、字号变了时用）。</summary>
    public void Redraw() => _editor.TextArea?.TextView.Redraw();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        AttachRenderers();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachRenderers();
    }

    private void AttachRenderers()
    {
        if (_renderersAttached) return;

        // ⚠️ 必须等 TextView 建好：TextArea/TextView 是 TextEditor 模板里的部件，
        // 模板应用之前为 null。
        var view = _editor.TextArea?.TextView;
        if (view is null) return;

        view.ElementGenerators.Add(_generator);
        _renderersAttached = true;

        RebuildSlots();
        view.Redraw();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        RebuildSlots();

        // 占位字符的偏移全变了，卡片必须重画：不重画的话新插入的占位字符上还是旧的空白，
        // 直到下一次滚动才被重新构造出来。
        _editor.TextArea?.TextView.Redraw();

        UpdatePlaceholder();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildSlots()
    {
        _slots.Clear();

        var text = _editor.Document.Text;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == WidgetChar)
                _slots.Add(i);
    }

    private void UpdatePlaceholder()
    {
        // 有卡片时不算空 —— 纯附件也该发得出去，占位提示不能还挂着
        _placeholder.IsVisible = _editor.Document.TextLength == 0;
    }

    /// <summary>找从 <paramref name="startOffset"/> 起（含）第一个占位字符的偏移。</summary>
    private int FindNextSlot(int startOffset)
    {
        for (var i = 0; i < _slots.Count; i++)
            if (_slots[i] >= startOffset)
                return _slots[i];

        return -1;
    }

    private VisualLineElement? ConstructElement(int offset)
    {
        var index = _slots.IndexOf(offset);
        if (index < 0) return null;

        var control = _widgetProvider?.Invoke(index);
        return control is null ? null : new InlineObjectElement(1, control);
    }

    /// <summary>把正文里的 U+FFFC 换成控件的那一层。AvaloniaEdit 只认这一个扩展点。</summary>
    private sealed class InlineWidgetGenerator : VisualLineElementGenerator
    {
        private readonly ChatInputEditor _owner;

        public InlineWidgetGenerator(ChatInputEditor owner) => _owner = owner;

        public override int GetFirstInterestedOffset(int startOffset)
            => _owner.FindNextSlot(startOffset);

        public override VisualLineElement? ConstructElement(int offset)
            => _owner.ConstructElement(offset);
    }
}
