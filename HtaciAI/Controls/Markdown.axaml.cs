using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HtaciAI.Models;
using HtaciAI.Services.Attachments;

namespace HtaciAI.Controls;

public partial class Markdown : UserControl
{
    private readonly StringBuilder _log = new();
    public string markdownText = "";

    // StackPanel 用于放置解析后的内容
    StackPanel stackPanel;

    // 流式解析状态
    private MarkdownStreamParser _parser;
    private bool needsRefresh = false;

    public Markdown()
    {
        InitializeComponent();
        // 在MarkdownContent中添加一个StackPanel用于放置解析后的内容
        stackPanel = new StackPanel();
        MarkdownContent.Content = stackPanel;   // 设置Content为stackPanel

        // 初始化解析器
        _parser = new MarkdownStreamParser(stackPanel);
    }

    // 初始化完成后调用
    protected override void OnInitialized()
    {
        base.OnInitialized();
    }

    // 流式更新方法
    public void UpdateMarkdown(string newLine)
    {
        // 在这里开始
        _parser.AppendText(newLine);
        needsRefresh = true;

        // 下面的是用于自定义容器的更新的，不要改这个
        // 通知 SelectableTextContainer 刷新 TextBlock 列表
        if (needsRefresh)
        {
            // 延迟刷新，确保布局完成
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MarkdownContent.RefreshTextBlocks();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    // 结束流式输入
    public void CompleteMarkdown()
    {
        _parser.Complete();

        // 最后调用，刷新一次容器
        // 最后刷新一次
        MarkdownContent.RefreshTextBlocks();
    }

    // 一次性更新方法
    public void SetMarkdown(string fullText)
    {
        // 清空现有内容
        stackPanel.Children.Clear();
        _parser = new MarkdownStreamParser(stackPanel);

        // 一次性解析
        _parser.AppendText(fullText);
        _parser.Complete();

        MarkdownContent.RefreshTextBlocks();
    }
}

// Markdown 流式解析器
public class MarkdownStreamParser
{
    private readonly StackPanel _container;
    private readonly StringBuilder _buffer = new();
    private ParserState _state = ParserState.Normal;

    // 默认字体
    private readonly FontFamily _defaultFont = new FontFamily("Microsoft YaHei UI");
    private readonly FontFamily _codeFont = new FontFamily("Consolas,Courier New,monospace");

    // 列表状态
    private Stack<ListContext> _listStack = new();

    // 代码块状态
    private StringBuilder _codeBlockContent = new();
    private string _codeBlockLanguage = "";
    private CodeBlockView? _currentCodeBlock;

    // 引用块状态
    private StackPanel? _quotePanel;
    private bool _inQuote = false;

    // 表格状态
    private List<string> _tableLines = new();
    private bool _inTable = false;
    private Grid? _currentTableGrid;
    private Border? _currentTableBorder;
    private int _tableRowCount = 0;
    private int _spacersBeforeTable = 0;

    // 当前行缓冲
    private StringBuilder _lineBuffer = new();

    /// <summary>当前段落 - 用于实时更新（单个富文本块，见 CreateInlineBlock）</summary>
    private CustomSelectableTextBlock? _currentParagraph;

    /// <summary>
    /// 上一次预览用过的文件卡片（路径 → 控件），供下一次重建时<b>原地复用</b>。
    ///
    /// 流式期间每来一个字符都会清空 Inlines 重建一遍；不复用的话，一段带卡片的回复
    /// 会按字符数创建几十上百个控件。实测（Avalonia 12.0.4）<c>Inlines.Clear()</c> 之后
    /// 复用同一个控件重新挂进新的 <see cref="InlineUIContainer"/> 是安全的。
    /// </summary>
    private readonly Dictionary<string, AttachmentChip> _previewChips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本遍预览正在用到的卡片。一遍画完才换给 <see cref="_previewChips"/>。</summary>
    private Dictionary<string, AttachmentChip>? _buildingChips;
    private StringBuilder _currentParagraphText = new();
    private bool _paragraphProcessed = false;

    public MarkdownStreamParser(StackPanel container)
    {
        _container = container;
    }

    public void AppendText(string text)
    {
        foreach (char c in text)
        {
            _buffer.Append(c);
            _lineBuffer.Append(c);

            // 实时更新当前块内容
            if (_state == ParserState.CodeBlock && _currentCodeBlock != null)
            {
                // 实时更新代码块
                if (c == '\n')
                {
                    string line = _lineBuffer.ToString().TrimEnd('\r', '\n');
                    if (line.StartsWith("```"))
                    {
                        FinishCodeBlock();
                        _state = ParserState.Normal;
                        _lineBuffer.Clear();
                        continue;
                    }
                    _codeBlockContent.AppendLine(line);
                    _currentCodeBlock.SetCode(_codeBlockContent.ToString().TrimEnd());
                    _lineBuffer.Clear();
                }
            }
            else if (c == '\n')
            {
                // 完成当前段落
                if (_currentParagraph != null)
                {
                    _paragraphProcessed = false;
                }

                ProcessLine(_lineBuffer.ToString());
                _lineBuffer.Clear();

                // 处理完后清理实时预览的段落引用
                _currentParagraph = null;
                _currentParagraphText.Clear();
                _paragraphProcessed = false;
            }
            else
            {
                // 实时更新当前段落
                UpdateCurrentParagraph();
            }
        }
    }

    /// <summary>
    /// 流式预览：把当前这一行画进一个临时段落。
    ///
    /// <b>文件卡片在预览期就渲染</b>：标记一闭合（<c>]</c> 到达）就变成卡片，
    /// 不必等这一轮结束。标记没闭合时 <see cref="AttachmentMarker.Scan"/> 找不到它，
    /// 自然显示成原文 —— 半截路径本来也没有可点的东西。
    ///
    /// 代价是每字符重建 Inlines，所以卡片<b>按路径复用</b>（见 <see cref="_previewChips"/>），
    /// 不重复创建控件。
    /// </summary>
    private void UpdateCurrentParagraph()
    {
        if (_state == ParserState.CodeBlock || _inTable)
            return;

        string currentLine = _lineBuffer.ToString();

        // 跳过特殊行的实时预览 - 扩展检测逻辑
        if (currentLine.TrimStart().StartsWith("#") ||
            currentLine.TrimStart().StartsWith(">") ||
            currentLine. TrimStart().StartsWith("```") ||
            IsUnorderedListItem(currentLine, out _, out _) ||  // 添加这行
            IsOrderedListItem(currentLine, out _, out _) ||    // 添加这行
            currentLine.Contains("|"))
        {
            if (_currentParagraph != null && ! _paragraphProcessed)
            {
                _container.Children.Remove(_currentParagraph);
                _currentParagraph = null;
                _currentParagraphText. Clear();
            }
            return;
        }

        // 创建或更新当前段落
        if (_currentParagraph == null && ! string.IsNullOrWhiteSpace(currentLine))
        {
            _currentParagraph = CreateInlineBlock(currentLine);
            _currentParagraph.Margin = new Thickness(0, 5, 0, 5);

            _container.Children.Add(_currentParagraph);
            _currentParagraphText. Clear();
        }

        if (_currentParagraph != null && !_paragraphProcessed)
        {
            _currentParagraphText.Clear();
            _currentParagraphText. Append(currentLine);

            // 清空并重新生成行内元素。清空之后旧卡片只是脱离了容器，可以原地复用。
            _currentParagraph.Inlines?.Clear();
            RenderPreviewInlines(_currentParagraph, currentLine);
        }
    }

    /// <summary>
    /// 预览段落的一遍重绘：清空 → 复用上一遍的卡片 → 重新挂。
    /// 复用池只在这一处（以及段落定稿）参与，别处的块每次都是新建 —— 它们不重绘。
    /// </summary>
    private void RenderPreviewInlines(CustomSelectableTextBlock block, string text)
    {
        _buildingChips = new Dictionary<string, AttachmentChip>(StringComparer.OrdinalIgnoreCase);

        try
        {
            AppendInlineRuns(block, text);
        }
        finally
        {
            _previewChips.Clear();
            foreach (var (path, chip) in _buildingChips)
                _previewChips[path] = chip;

            _buildingChips = null;
        }
    }

    public void Complete()
    {
        // 处理最后一行（如果没有换行符）
        if (_lineBuffer.Length > 0)
        {
            ProcessLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }

        // 完成所有未完成的块
        if (_state == ParserState.CodeBlock)
        {
            FinishCodeBlock();
        }

        if (_inQuote)
        {
            FinishQuote();
        }

        if (_inTable)
        {
            FinishTable();
        }

        // 关闭所有未关闭的列表
        while (_listStack.Count > 0)
        {
            _listStack.Pop();
        }

        _currentParagraph = null;
    }

    private void ProcessLine(string line)
    {
        string trimmedLine = line.TrimEnd('\r', '\n');

        // 代码块处理
        if (_state == ParserState.CodeBlock)
        {
            if (trimmedLine.StartsWith("```"))
            {
                FinishCodeBlock();
                _state = ParserState.Normal;
                return;
            }
            else
            {
                return;
            }
        }

        // 检测代码块开始
        if (trimmedLine.StartsWith("```"))
        {
            if (_currentParagraph != null && !_paragraphProcessed)
            {
                _container.Children.Remove(_currentParagraph);
                _currentParagraph = null;
                _currentParagraphText.Clear();
            }

            _state = ParserState.CodeBlock;
            _codeBlockLanguage = trimmedLine.Substring(3).Trim();
            _codeBlockContent.Clear();
            StartCodeBlock();
            return;
        }

        // 空行处理
        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            if (_inQuote)
            {
                FinishQuote();
            }

            bool endedTable = _inTable;
            if (_inTable)
            {
                FinishTable();
            }

            _listStack.Clear();

            // 表格自己带上下留白，紧跟其后的空行 spacer 会叠成一大条空隙
            if (!endedTable)
            {
                AddParagraphSpacing();
            }
            return;
        }

        // 水平线
        if (IsHorizontalRule(trimmedLine))
        {
            AddHorizontalRule();
            return;
        }

        // 引用块
        if (trimmedLine.StartsWith(">"))
        {
            if (!_inQuote)
            {
                StartQuote();
            }

            string quoteContent = trimmedLine.Substring(1).TrimStart();
            AddQuoteLine(quoteContent);
            return;
        }
        else if (_inQuote)
        {
            FinishQuote();
        }

        // 表格
        if (trimmedLine.Contains("|"))
        {
            ProcessTableLine(trimmedLine);
            return;
        }
        else if (_inTable)
        {
            FinishTable();
        }

        // 无序列表
        if (IsUnorderedListItem(trimmedLine, out int indent, out string content))
        {
            AddListItem(false, indent, content);
            return;
        }

        // 有序列表
        if (IsOrderedListItem(trimmedLine, out indent, out content))
        {
            AddListItem(true, indent, content);
            return;
        }

        // 标题
        if (trimmedLine.StartsWith("#"))
        {
            AddHeading(trimmedLine);
            return;
        }

        // 普通段落
        if (_currentParagraph != null && !_paragraphProcessed)
        {
            _paragraphProcessed = true;

            // 定稿重绘一次：预览期那一遍是按「可能会再来字符」画的，这里用最终文本收口
            // （行尾空白、未闭合标记等都在这时定型）。卡片同样按路径复用，不重建控件。
            _currentParagraph.Inlines?.Clear();
            RenderPreviewInlines(_currentParagraph, trimmedLine);
        }
        else
        {
            AddParagraph(trimmedLine);
        }
    }

    private CustomSelectableTextBlock CreateTextBlock()
    {
        return new CustomSelectableTextBlock
        {
            FontFamily = _defaultFont,
            TextWrapping = TextWrapping.Wrap
        };
    }

    /// <summary>
    /// 把一段可能含行内标记（粗体 / 斜体 / 行内代码 / 链接）的文本渲染成「单个」富文本块。
    ///
    /// 关键：行内格式必须用 Inlines 表达，不能用「横向 StackPanel + 多个 TextBlock」拼接——
    /// 横向 StackPanel 会以无限宽度测量子元素，子 TextBlock 的 TextWrapping 直接失效，
    /// 于是段落永不换行、长行溢出容器（超出屏幕右侧）。
    /// </summary>
    private CustomSelectableTextBlock CreateInlineBlock(
        string text, double? fontSize = null, bool bold = false, bool renderFileCards = true)
    {
        var block = new CustomSelectableTextBlock
        {
            FontFamily = _defaultFont,
            TextWrapping = TextWrapping.Wrap
        };

        if (fontSize.HasValue) block.FontSize = fontSize.Value;
        if (bold) block.FontWeight = FontWeight.Bold;

        AppendInlineRuns(block, text, renderFileCards);
        return block;
    }

    /// <summary>
    /// 把一段文本追加成行内元素（Run / 文件卡片）。
    ///
    /// <b>附件标记必须先切出去再做行内解析</b>：<see cref="ParseInlineSegments"/> 把
    /// <c>_</c> <c>*</c> <c>`</c> 当行内标记，而路径里合法地含这些字符
    /// （<c>D:\Project\HtaciAI\_scratch</c> 的下划线就会把后半段变成斜体），
    /// 解析先行的话路径会显示成一堆残缺的斜体 / 粗体。
    /// </summary>
    /// <param name="renderFileCards">
    /// 是否把 <c>[agent-file:///…]</c> 渲染成可点击卡片。流式预览期传 false，理由见
    /// <see cref="UpdateCurrentParagraph"/>。
    /// </param>
    private void AppendInlineRuns(CustomSelectableTextBlock block, string text, bool renderFileCards = true)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inlines = block.Inlines ??= new InlineCollection();

        if (renderFileCards && AttachmentMarker.Contains(text))
        {
            AppendInlineWithFileCards(inlines, text);
            return;
        }

        AppendSegments(inlines, text);
    }

    /// <summary>把文本切成「普通片段 + 文件卡片」并追加。</summary>
    private void AppendInlineWithFileCards(InlineCollection inlines, string text)
    {
        var spans = AttachmentMarker.Scan(text);
        if (spans.Count == 0)
        {
            AppendSegments(inlines, text);
            return;
        }

        var cursor = 0;

        foreach (var span in spans)
        {
            if (span.Start > cursor)
                AppendSegments(inlines, text[cursor..span.Start]);

            // 路径不存在（模型编的）时退回原始文本：一个点下去什么都没有的卡片，
            // 比直接显示路径更让人困惑。
            if (AttachmentStore.DescribeForDisplay(span.Path) is { } info)
                inlines.Add(new InlineUIContainer(GetFileChip(info)));
            else
                inlines.Add(new Run(text.Substring(span.Start, span.Length)));

            cursor = span.End;
        }

        if (cursor < text.Length)
            AppendSegments(inlines, text[cursor..]);
    }

    /// <summary>
    /// 取一个文件卡片控件：预览重绘时优先复用上一遍同路径的那个，否则新建。
    ///
    /// <b>同一路径在一行里出现两次必须建两个</b>：一个控件只能有一个父级，
    /// 复用第二个会把前一个容器里的实例抢走，前半句就空了。所以复用是「取走」语义。
    /// </summary>
    private AttachmentChip GetFileChip(ChatAttachment info)
    {
        AttachmentChip? chip = null;

        if (_previewChips.Remove(info.Path, out var reusable))
            chip = reusable;

        chip ??= new AttachmentChip(info, null, compact: true, openOnClick: true);

        _buildingChips?.TryAdd(info.Path, chip);
        return chip;
    }

    /// <summary>行内标记（粗体 / 斜体 / 行内代码 / 链接）→ Run。</summary>
    private void AppendSegments(InlineCollection inlines, string text)
    {
        foreach (var segment in ParseInlineSegments(text))
        {
            // 跳过空文本
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            var run = new Run(segment.Text);

            if (segment.IsBold)
                run.FontWeight = FontWeight.Bold;

            if (segment.IsItalic)
                run.FontStyle = FontStyle.Italic;

            if (segment.IsCode)
            {
                run.FontFamily = _codeFont;
                run.Foreground = new SolidColorBrush(Color.FromRgb(200, 50, 50));
            }

            if (segment.IsLink)
            {
                run.Foreground = new SolidColorBrush(Colors.Blue);
                run.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(run);
        }
    }

    // 解析行内元素为片段
    private List<InlineSegment> ParseInlineSegments(string text)
    {
        var segments = new List<InlineSegment>();

        if (string.IsNullOrEmpty(text))
            return segments;

        int pos = 0;

        while (pos < text.Length)
        {
            int nextSpecial = FindNextInlineMarker(text, pos);

            if (nextSpecial == -1)
            {
                if (pos < text.Length)
                {
                    segments.Add(new InlineSegment { Text = text.Substring(pos) });
                }
                break;
            }

            if (nextSpecial > pos)
            {
                segments.Add(new InlineSegment { Text = text.Substring(pos, nextSpecial - pos) });
            }

            int consumed = ProcessInlineMarker(text, nextSpecial, segments);
            pos = nextSpecial + consumed;
        }

        return segments;
    }

    private int FindNextInlineMarker(string text, int startPos)
    {
        int minPos = int.MaxValue;
        string[] markers = { "**", "*", "__", "_", "`", "[", "http://", "https://" };

        foreach (var marker in markers)
        {
            int pos = text.IndexOf(marker, startPos);
            if (pos != -1 && pos < minPos)
                minPos = pos;
        }

        return minPos == int.MaxValue ? -1 : minPos;
    }

    private int ProcessInlineMarker(string text, int pos, List<InlineSegment> segments)
    {
        // 粗体 **text**
        if (text.Substring(pos).StartsWith("**"))
        {
            int endPos = text.IndexOf("**", pos + 2);
            if (endPos != -1)
            {
                string content = text.Substring(pos + 2, endPos - pos - 2);
                segments.Add(new InlineSegment { Text = content, IsBold = true });
                return endPos - pos + 2;
            }
        }

        // 粗体 __text__
        if (text.Substring(pos).StartsWith("__"))
        {
            int endPos = text.IndexOf("__", pos + 2);
            if (endPos != -1)
            {
                string content = text.Substring(pos + 2, endPos - pos - 2);
                segments.Add(new InlineSegment { Text = content, IsBold = true });
                return endPos - pos + 2;
            }
        }

        // 斜体 *text*
        if (text[pos] == '*' && !text.Substring(pos).StartsWith("**"))
        {
            int endPos = text.IndexOf('*', pos + 1);
            if (endPos != -1)
            {
                string content = text.Substring(pos + 1, endPos - pos - 1);
                segments.Add(new InlineSegment { Text = content, IsItalic = true });
                return endPos - pos + 1;
            }
        }

        // 斜体 _text_
        if (text[pos] == '_' && !text.Substring(pos).StartsWith("__"))
        {
            int endPos = text.IndexOf('_', pos + 1);
            if (endPos != -1)
            {
                string content = text.Substring(pos + 1, endPos - pos - 1);
                segments.Add(new InlineSegment { Text = content, IsItalic = true });
                return endPos - pos + 1;
            }
        }

        // 行内代码 `code`
        if (text[pos] == '`')
        {
            int endPos = text.IndexOf('`', pos + 1);
            if (endPos != -1)
            {
                string content = text.Substring(pos + 1, endPos - pos - 1);
                segments.Add(new InlineSegment { Text = content, IsCode = true });
                return endPos - pos + 1;
            }
        }

        // 链接 [text](url)
        if (text[pos] == '[')
        {
            int textEnd = text.IndexOf(']', pos + 1);
            if (textEnd != -1 && textEnd + 1 < text.Length && text[textEnd + 1] == '(')
            {
                int urlEnd = text.IndexOf(')', textEnd + 2);
                if (urlEnd != -1)
                {
                    string linkText = text.Substring(pos + 1, textEnd - pos - 1);
                    segments.Add(new InlineSegment { Text = linkText, IsLink = true });
                    return urlEnd - pos + 1;
                }
            }
        }

        // 自动链接 http:// 或 https://
        if (text.Substring(pos).StartsWith("http://") || text.Substring(pos).StartsWith("https://"))
        {
            int endPos = pos;
            while (endPos < text.Length && !char.IsWhiteSpace(text[endPos]))
                endPos++;

            string url = text.Substring(pos, endPos - pos);
            segments.Add(new InlineSegment { Text = url, IsLink = true });
            return endPos - pos;
        }

        // 如果没有匹配，添加单个字符
        segments.Add(new InlineSegment { Text = text[pos].ToString() });
        return 1;
    }

    private void StartCodeBlock()
    {
        _currentCodeBlock = new CodeBlockView(_codeFont, _codeBlockLanguage)
        {
            Margin = new Thickness(0, 6, 0, 6)
        };
        _container.Children.Add(_currentCodeBlock);
    }

    private void FinishCodeBlock()
    {
        _currentCodeBlock = null;
        _codeBlockContent.Clear();
        _codeBlockLanguage = "";
    }

    private bool IsHorizontalRule(string line)
    {
        string trimmed = line.Trim();
        return (trimmed.All(c => c == '-') && trimmed.Length >= 3) ||
               (trimmed.All(c => c == '*') && trimmed.Length >= 3) ||
               (trimmed.All(c => c == '_') && trimmed.Length >= 3);
    }

    private bool IsUnorderedListItem(string line, out int indent, out string content)
    {
        indent = 0;
        content = "";

        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            if (line[i] == '\t')
                indent += 4;
            else
                indent += 1;
            i++;
        }

        if (i < line.Length && (line[i] == '-' || line[i] == '*' || line[i] == '+'))
        {
            if (i + 1 < line.Length && line[i + 1] == ' ')
            {
                content = line.Substring(i + 2).TrimStart();
                indent = indent / 2;
                return true;
            }
        }

        return false;
    }

    private bool IsOrderedListItem(string line, out int indent, out string content)
    {
        indent = 0;
        content = "";

        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            if (line[i] == '\t')
                indent += 4;
            else
                indent += 1;
            i++;
        }

        var match = Regex.Match(line.Substring(i), @"^(\d+)\.\s+(.*)");
        if (match.Success)
        {
            content = match.Groups[2].Value;
            indent = indent / 2;
            return true;
        }

        return false;
    }

    private void AddHeading(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#')
            level++;

        if (level > 6) level = 6;

        string content = line.Substring(level).TrimStart();

        double fontSize = level switch
        {
            1 => 32,
            2 => 24,
            3 => 20,
            4 => 18,
            5 => 16,
            6 => 14,
            _ => 16
        };

        var heading = CreateInlineBlock(content, fontSize, bold: true);
        heading.Margin = new Thickness(0, 10, 0, 5);
        _container.Children.Add(heading);
    }

    private void AddParagraph(string line)
    {
        var paragraph = CreateInlineBlock(line);
        paragraph.Margin = new Thickness(0, 5, 0, 5);
        _container.Children.Add(paragraph);
    }

    private void AddParagraphSpacing()
    {
        var spacer = new Border
        {
            Height = 10
        };
        _container.Children.Add(spacer);
    }

    private void AddHorizontalRule()
    {
        var border = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            Margin = new Thickness(0, 10, 0, 10)
        };
        _container.Children.Add(border);
    }

    /// <summary>
    /// 引用块：一张极淡的青粉渐变圆角卡片，左边描边加粗成一条竖条。
    ///
    /// <b>竖条是「描边的一部分」，不是额外画上去的方块</b>：之前用一个独立 Border 摆在左边，
    /// 它会带自己的圆角、还会跟外层的 1px 描边错开半像素 —— 看着像卡片外面插了根胶囊。
    /// 这里改成 <c>BorderThickness(3,1,1,1)</c>：同一条描边、同一个圆角，左边缘自然就粗了，
    /// 与网页上 <c>border-left: 3px</c> 是同一回事。
    ///
    /// <b>整体很淡</b>：底色 alpha 压到 0.5 / 0.22，压在白色气泡上只留一层很薄的青粉；
    /// 描边给到 0.45，靠那条线撑住存在感，而不是靠色块鲜艳。
    /// </summary>
    private void StartQuote()
    {
        _inQuote = true;
        _quotePanel = new StackPanel
        {
            Spacing = 2
        };

        var border = new Border
        {
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(13, 10, 14, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(3, 1, 1, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x6E, 0xCB, 0xFF)),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x80, 0xD7, 0xF8, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x38, 0xFF, 0xE4, 0xF7), 1),
                },
            },
            Child = _quotePanel,
        };

        _container.Children.Add(border);
    }

    private void AddQuoteLine(string content)
    {
        var quoteLine = CreateInlineBlock(content);
        quoteLine.Margin = new Thickness(0, 2, 0, 2);
        _quotePanel.Children.Add(quoteLine);
    }

    private void FinishQuote()
    {
        _inQuote = false;
        _quotePanel = null;
    }

    private void AddListItem(bool isOrdered, int indent, string content)
    {
        while (_listStack.Count > 0 && _listStack.Peek().Indent > indent)
        {
            _listStack.Pop();
        }

        StackPanel listPanel;
        ListContext currentList;

        if (_listStack.Count == 0 || _listStack.Peek().Indent < indent || _listStack.Peek().IsOrdered != isOrdered)
        {
            listPanel = new StackPanel
            {
                Margin = new Thickness(indent * 20, 0, 0, 0)
            };

            currentList = new ListContext
            {
                Panel = listPanel,
                IsOrdered = isOrdered,
                Indent = indent,
                Counter = 1
            };

            if (_listStack.Count == 0)
            {
                _container.Children.Add(listPanel);
            }
            else
            {
                _listStack.Peek().Panel.Children.Add(listPanel);
            }

            _listStack.Push(currentList);
        }
        else
        {
            currentList = _listStack.Peek();
        }

        // 用 Grid（Auto + *）而不是横向 StackPanel：正文列才能拿到有限的可用宽度，长条目才会换行
        var itemPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 2, 0, 2)
        };

        // 创建标记 TextBlock
        var bullet = CreateTextBlock();
        bullet.Margin = new Thickness(0, 0, 8, 0);  // 右边距
        bullet.VerticalAlignment = VerticalAlignment.Top;

        if (isOrdered)
        {
            bullet.Text = $"{currentList.Counter}. ";  // 只有数字和点，没有空格
            currentList.Counter++;
        }
        else
        {
            bullet.Text = "•";  // 只有符号，没有空格
        }

        Grid.SetColumn(bullet, 0);
        itemPanel.Children.Add(bullet);

        // 条目正文：单个富文本块
        var itemContent = CreateInlineBlock(content);
        itemContent.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(itemContent, 1);
        itemPanel.Children.Add(itemContent);

        currentList.Panel.Children.Add(itemPanel);
    }

    private void ProcessTableLine(string line)
    {
        if (!_inTable)
        {
            _inTable = true;
            _tableLines.Clear();
            _tableRowCount = 0;
            StartTable();
        }

        _tableLines.Add(line);

        // 实时添加表格行
        if (_tableLines.Count == 1)
        {
            // 第一行：表头
            var cells = ParseTableRow(line);
            AddTableRowToGrid(cells, true, 0);
        }
        else if (_tableLines.Count == 2)
        {
            // 第二行：分隔符
            if (IsTableSeparator(line))
            {
                _tableRowCount = 1;
            }
        }
        else
        {
            // 数据行
            var cells = ParseTableRow(line);
            AddTableRowToGrid(cells, false, _tableRowCount);
            _tableRowCount++;
        }
    }

    private void StartTable()
    {
        // 表格前的空行 spacer 与表格自带留白会叠成一大条空隙，这里先摘掉，表格无效时再补回
        _spacersBeforeTable = 0;
        while (_container.Children.Count > 0 && IsSpacer(_container.Children[^1]))
        {
            _container.Children.RemoveAt(_container.Children.Count - 1);
            _spacersBeforeTable++;
        }

        // 单元格不画边框：Grid 背景色当分隔线（RowSpacing/ColumnSpacing 留出 1px），
        // 避免外框和内部格子叠成「双线」
        _currentTableGrid = new Grid
        {
            RowSpacing = 1,
            ColumnSpacing = 1,
            Background = new SolidColorBrush(Color.FromRgb(232, 232, 234))
        };

        // 外框描边 + 一层内层圆角 Border 包住 Grid：直接让 Grid 当外框子元素时，
        // 它的矩形背景会盖掉右侧和下侧描边（圆角处描边也会被裁掉），必须靠这层隔开
        _currentTableBorder = new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 226, 229)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Colors.White),
            Child = new Border
            {
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Child = _currentTableGrid
            }
        };

        _container.Children.Add(_currentTableBorder);
    }

    /// <summary>空行留白 spacer（见 AddParagraphSpacing）：无子元素、无背景、只有高度。</summary>
    private static bool IsSpacer(Control control)
        => control is Border { Child: null, Background: null, Height: > 0 };

    private void AddTableRowToGrid(List<string> cells, bool isHeader, int rowIndex)
    {
        // 确保有足够的列定义
        while (_currentTableGrid.ColumnDefinitions.Count < cells.Count)
        {
            _currentTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // 确保有足够的行定义
        while (_currentTableGrid.RowDefinitions.Count <= rowIndex)
        {
            _currentTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int col = 0; col < cells.Count; col++)
        {
            var cellBorder = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                Background = new SolidColorBrush(isHeader
                    ? Color.FromRgb(246, 247, 249)                    // 表头：淡灰
                    : (rowIndex % 2 == 0
                        ? Colors.White
                        : Color.FromRgb(250, 250, 251)))             // 正文：斑马纹
            };

            // 单元格正文：单个富文本块（才能按列宽换行，横向 StackPanel 会溢出）
            var cellBlock = CreateInlineBlock(cells[col], bold: isHeader);
            cellBlock.VerticalAlignment = VerticalAlignment.Center;

            cellBorder.Child = cellBlock;

            Grid.SetRow(cellBorder, rowIndex);
            Grid.SetColumn(cellBorder, col);

            _currentTableGrid.Children.Add(cellBorder);
        }
    }

    private void FinishTable()
    {
        // 检查表格是否有效
        if (_tableLines.Count >= 2)
        {
            string secondLine = _tableLines[1];
            if (!IsTableSeparator(secondLine))
            {
                // 无效表格，重新渲染为普通段落
                _container.Children.Remove(_currentTableBorder);
                for (int i = 0; i < _spacersBeforeTable; i++)
                {
                    AddParagraphSpacing();
                }
                foreach (var line in _tableLines)
                {
                    AddParagraph(line);
                }
            }
        }

        _inTable = false;
        _tableLines.Clear();
        _currentTableGrid = null;
        _currentTableBorder = null;
        _tableRowCount = 0;
        _spacersBeforeTable = 0;
    }

    private bool IsTableSeparator(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("|") || !trimmed.EndsWith("|"))
            return false;

        var content = trimmed.Trim('|');
        var parts = content.Split('|');

        foreach (var part in parts)
        {
            var p = part.Trim();
            if (string.IsNullOrEmpty(p))
                continue;

            if (!p.All(c => c == '-' || c == ':' || c == ' '))
                return false;
        }

        return true;
    }

    private List<string> ParseTableRow(string line)
    {
        var cells = new List<string>();
        var trimmed = line.Trim();

        if (trimmed.StartsWith("|"))
            trimmed = trimmed.Substring(1);
        // 结尾的 | 若落在行内代码里（反引号未闭合）就不是列分隔符
        if (trimmed.EndsWith("|") && trimmed.Count(c => c == '`') % 2 == 0)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        // 行内代码里的 | 不是分隔符，否则 `a: "x" | "y"` 会把一个单元格劈成两个
        var cell = new StringBuilder();
        bool inCode = false;
        foreach (char c in trimmed)
        {
            if (c == '`')
                inCode = !inCode;

            if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private enum ParserState
    {
        Normal,
        CodeBlock
    }

    private class ListContext
    {
        public StackPanel Panel { get; set; }
        public bool IsOrdered { get; set; }
        public int Indent { get; set; }
        public int Counter { get; set; }
    }

    // 行内片段类
    private class InlineSegment
    {
        public string Text { get; set; } = "";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public bool IsCode { get; set; }
        public bool IsLink { get; set; }
    }
}