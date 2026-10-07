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

    // 当前行缓冲
    private StringBuilder _lineBuffer = new();

    // 当前段落 - 用于实时更新（单个富文本块，见 CreateInlineBlock）
    private CustomSelectableTextBlock? _currentParagraph;
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

            // 清空并重新生成行内元素
            _currentParagraph.Inlines?.Clear();
            AppendInlineRuns(_currentParagraph, currentLine);
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

            if (_inTable)
            {
                FinishTable();
            }

            _listStack.Clear();
            AddParagraphSpacing();
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
    private CustomSelectableTextBlock CreateInlineBlock(string text, double? fontSize = null, bool bold = false)
    {
        var block = new CustomSelectableTextBlock
        {
            FontFamily = _defaultFont,
            TextWrapping = TextWrapping.Wrap
        };

        if (fontSize.HasValue) block.FontSize = fontSize.Value;
        if (bold) block.FontWeight = FontWeight.Bold;

        AppendInlineRuns(block, text);
        return block;
    }

    /// <summary>把行内片段追加为 Run。流式刷新段落时先清空 Inlines 再调用。</summary>
    private void AppendInlineRuns(CustomSelectableTextBlock block, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inlines = block.Inlines ??= new InlineCollection();

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

    private void StartQuote()
    {
        _inQuote = true;
        _quotePanel = new StackPanel
        {
            Margin = new Thickness(10, 5, 0, 5)
        };

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 5, 5, 5),
            Background = new SolidColorBrush(Color.FromArgb(20, 100, 100, 100)),
            Child = _quotePanel
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
        _currentTableGrid = new Grid
        {
            Margin = new Thickness(0, 5, 0, 5)
        };

        _currentTableBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            BorderThickness = new Thickness(1),
            Child = _currentTableGrid
        };

        _container.Children.Add(_currentTableBorder);
    }

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
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = isHeader
                    ? new SolidColorBrush(Color.FromRgb(245, 245, 245))
                    : new SolidColorBrush(Colors.White)
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
        if (trimmed.EndsWith("|"))
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var parts = trimmed.Split('|');
        foreach (var part in parts)
        {
            cells.Add(part.Trim());
        }

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