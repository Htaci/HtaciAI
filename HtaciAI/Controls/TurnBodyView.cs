using System.Text;
using Avalonia.Controls;

namespace HtaciAI.Controls;

/// <summary>
/// 一轮回复中的「正文块」：渲染态由 <see cref="MarkdownStreamParser"/> 生成富文本，
/// 同时累积一份未经渲染的 Markdown 源码（供「显示全部原文」拼装用），两者只存不切换。
/// </summary>
public sealed class TurnBodyView : Grid
{
    private readonly StackPanel _rendered = new();
    private readonly StringBuilder _rawText = new();
    private readonly MarkdownStreamParser _parser;

    public TurnBodyView()
    {
        _parser = new MarkdownStreamParser(_rendered);
        Children.Add(_rendered);
    }

    /// <summary>追加流式分片：渲染与原文缓冲同步累积。</summary>
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
}
