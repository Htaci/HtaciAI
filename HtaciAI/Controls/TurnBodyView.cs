using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HtaciAI.Controls;

/// <summary>
/// 一轮回复中的「正文块」：同时维护两种呈现——
/// 渲染态（<see cref="MarkdownStreamParser"/> 生成的富文本 StackPanel）与
/// 原文态（单个原生 <see cref="SelectableTextBlock"/>，显示未经渲染的 Markdown 源码）。
/// 两者文本增量同步写入，切换只是互换可见性，不做重建。
/// </summary>
public sealed class TurnBodyView : Grid
{
    private static readonly FontFamily BodyFont = new("Microsoft YaHei UI");

    private readonly StackPanel _rendered = new();
    private readonly SelectableTextBlock _raw;
    private readonly StringBuilder _rawText = new();
    private readonly MarkdownStreamParser _parser;

    public TurnBodyView()
    {
        _parser = new MarkdownStreamParser(_rendered);

        _raw = new SelectableTextBlock
        {
            Text = "",
            FontFamily = BodyFont,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            IsVisible = false,
        };

        Children.Add(_rendered);
        Children.Add(_raw);
    }

    /// <summary>追加流式分片：渲染态与原文态同步累积。</summary>
    public void AppendText(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _rawText.Append(delta);
        _parser.AppendText(delta);
    }

    /// <summary>正文结束，刷新渲染态收尾。</summary>
    public void Complete() => _parser.Complete();

    /// <summary>未经渲染的 Markdown 源码。</summary>
    public string RawText => _rawText.ToString();

    /// <summary>切换到原文态（true）或渲染态（false）。原文按需从缓冲赋值。</summary>
    public void SetRawMode(bool raw)
    {
        if (raw) _raw.Text = _rawText.ToString();
        _rendered.IsVisible = !raw;
        _raw.IsVisible = raw;
    }

    /// <summary>正文是否为空（用于判断该块是否值得参与选择模式）。</summary>
    public bool IsEmpty => _rawText.Length == 0;
}
