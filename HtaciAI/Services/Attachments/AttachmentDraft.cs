using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using HtaciAI.Models;

namespace HtaciAI.Services.Attachments;

/// <summary>
/// 一次文本编辑的结果：新正文 + 新光标位置。
///
/// 所有会改动正文的操作都返回它，而不是让调用方自己去算光标 ——
/// 光标算错的表现是「删掉附件后光标跳到别处」，这类毛病靠肉眼很难复现和描述。
/// </summary>
public readonly record struct TextEdit(string Text, int Caret);

/// <summary>
/// 待发送附件的草稿：正文里放的是 <see cref="Token"/>（U+FFFC 占位字符），
/// 真正的绝对路径存在这里，发送时才展开。
///
/// 为什么是占位字符而不是 <c>@图片1</c> 这样的文字：输入框是富文本的（AvaloniaEdit），
/// 正文里的占位字符会被渲染成一张真卡片（见 <c>Controls/ChatInputEditor.cs</c>），
/// 长得和消息气泡里的附件卡片一样。占位字符本身只有 1 个字符宽，
/// 卡片却参与文本布局 —— 用户能像改字一样把附件挪到句子的任何位置。
///
/// 本类刻意<b>不依赖任何 Avalonia 类型</b>：它只做「给定正文和光标，算出新正文和新光标」。
/// 这样它可以被单元测试直接覆盖 —— 这层逻辑（去重、光标处整体删除、孤儿清理）
/// 是输入框里最容易出细微错误的部分，而通过界面点出来极难复现。
/// </summary>
public sealed class AttachmentDraft
{
    /// <summary>
    /// 正文里代表一个附件的占位字符（Unicode Object Replacement Character）。
    ///
    /// 选它是因为它不可能是用户敲出来的字符，且 AvaloniaEdit 的
    /// <c>VisualLineElementGenerator</c> 正好可以把任意位置的这个字符换成一个控件。
    /// </summary>
    public const char Token = '\uFFFC';

    /// <summary>一条待发送附件。正文里的第 n 个占位字符就是第 n 条。</summary>
    public sealed record Item(ChatAttachment Info);

    private readonly List<Item> _items = new();

    /// <summary>
    /// 已经从草稿里删掉、但还可能被撤销（Ctrl+Z / Ctrl+Y）找回来的附件，最近删的在末尾。
    ///
    /// 正文的撤销栈在编辑器手里，附件登记表在我们手里：用户按 Ctrl+Z 把占位字符放回来时，
    /// 登记表里那条已经没了，于是正文里出现一个「谁也不是」的占位字符 ——
    /// 编辑器只能把它画成 <c>[OBJ]</c> 这样的字面量。留一份删除记录，
    /// 占位字符变多时按相反顺序接回去，撤销/重做才是完整的一次往返。
    /// </summary>
    private readonly List<Item> _detached = new();

    /// <summary>
    /// 删除记录的保留上限。撤到很久以前时那条附件接不回来（卡片位置会显示成失效卡片），
    /// 但正文与登记表依旧一致，不会错位到别的附件上。
    /// </summary>
    private const int MaxDetached = 64;

    public IReadOnlyList<Item> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>该路径是否已在草稿里（不区分大小写）。用于避免同一个文件被附加两次。</summary>
    public bool HasPath(string path)
        => _items.Any(i => string.Equals(i.Info.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 追加一个附件：正文里插入占位字符，并登记映射。
    ///
    /// 一次调用同时完成「改正文」和「登记」，是为了消除一个顺序陷阱：
    /// 早先的实现先赋文本、再登记，而赋文本会同步触发 TextChanged 里的孤儿清理，
    /// 顺序反过来的话这条刚登记的会被误判成孤儿而清掉。
    /// 现在调用方拿到结果后只需赋一次文本，先后关系由本方法保证。
    /// </summary>
    /// <returns>false = 该路径已在草稿里，什么都没做。</returns>
    public bool TryAdd(ChatAttachment info, string text, int caret, out TextEdit edit)
    {
        edit = new TextEdit(text, caret);
        if (HasPath(info.Path)) return false;

        edit = InsertToken(text, caret);
        _items.Add(new Item(info));
        return true;
    }

    /// <summary>
    /// 采纳正文里已有的附件标记：把每个标记换成占位字符并登记为附件
    /// （用于「把一段带标记的文本粘进输入框」这条路径）。
    ///
    /// 同一个路径出现多次时复用同一条附件 —— 语义上它们本来就是同一个附件。
    /// 路径无法解析（如粘贴来的相对路径）的标记<b>原样留在正文里</b>，
    /// 不猜、也不删掉用户的文字。
    /// </summary>
    /// <returns>true = 至少有一个标记被换成了占位字符。</returns>
    public bool TryAdoptMarkers(string text, out TextEdit edit)
    {
        var spans = AttachmentMarker.Scan(text);
        if (spans.Count == 0)
        {
            edit = new TextEdit(text, text.Length);
            return false;
        }

        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        var adopted = false;

        foreach (var span in spans)
        {
            // 标记之前的原文（上一次替换之后到这个标记开头）
            sb.Append(text, cursor, span.Start - cursor);

            if (TokenFor(span.Path) is null)
            {
                sb.Append(text, span.Start, span.Length);   // 认不出的路径：原样保留
            }
            else
            {
                sb.Append(Token);
                adopted = true;
            }

            cursor = span.End;
        }

        sb.Append(text, cursor, text.Length - cursor);

        var result = sb.ToString();
        edit = new TextEdit(result, result.Length);
        return adopted;
    }

    /// <summary>
    /// 取该路径对应的占位字符：已登记过就直接返回（正文里会再多一个占位字符，
    /// 但它指向同一条附件），否则新建一条并登记。
    /// 路径非法时返回 null，表示「这个标记不该被采纳」。
    /// </summary>
    private char? TokenFor(string path)
    {
        var existing = _items.FirstOrDefault(
            i => string.Equals(i.Info.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return Token;

        if (AttachmentMarker.ToAttachment(path) is not { } info) return null;

        _items.Add(new Item(info));
        return Token;
    }

    /// <summary>
    /// 移除一条附件：正文里<b>它那一个</b>占位字符与登记一起消失。
    ///
    /// 位置靠「这条在列表里是第几条」算 —— 正文里的第 n 个占位字符就是第 n 条，
    /// 所以删中间那一条时后面的条目会整体前移，各自仍对着自己的占位字符。
    /// </summary>
    /// <param name="caret">当前光标，占位字符删除位置在光标之前时用它把光标往回带。</param>
    public TextEdit Remove(Item item, string text, int caret)
    {
        var index = _items.IndexOf(item);
        if (index < 0) return new TextEdit(text, Math.Clamp(caret, 0, text.Length));

        var offset = OffsetOf(text, index);

        // 正文里已经找不到它那一个占位字符（不该发生）：登记表也不动，
        // 更不能记进删除记录 —— 否则下一次 Prune 会按「占位字符变多了」把它加回来。
        if (offset < 0) return new TextEdit(text, Math.Clamp(caret, 0, text.Length));

        Remember(item);
        _items.RemoveAt(index);

        var newText = text.Remove(offset, 1);

        // 删的是光标前面的内容时，光标要跟着往前挪；否则停在原地。
        var newCaret = caret > offset ? Math.Clamp(caret - 1, 0, newText.Length)
                                      : Math.Clamp(caret, 0, newText.Length);
        return new TextEdit(newText, newCaret);
    }

    /// <summary>
    /// 用户在输入框里手工改了正文（选中一段占位字符删掉、Ctrl+Z / Ctrl+Y 等）之后，
    /// 让登记表重新对上正文：正文里没有对应占位字符的条目清掉，
    /// 正文里重新出现的占位字符（撤销/重做放回来的）从删除记录里接回对应的条目。
    ///
    /// 对不上时<b>按末尾对齐</b>：正文里的第 n 个占位字符永远对应第 n 条，
    /// 少了的就是最后那几条凭空消失的。
    /// </summary>
    /// <returns>true = 有附件被清掉或接回，调用方需要刷新。</returns>
    public bool Prune(string text)
    {
        var live = text.Count(c => c == Token);

        // 占位字符比登记表多：撤销/重做把它们放了回来。按相反顺序接回，
        // 第 n 个占位字符仍然对第 n 条 —— 转一圈回到原样。
        if (live > _items.Count && _detached.Count > 0)
        {
            var restore = Math.Min(live - _items.Count, _detached.Count);
            for (var i = 0; i < restore; i++)
            {
                _items.Add(_detached[^1]);
                _detached.RemoveAt(_detached.Count - 1);
            }

            return true;
        }

        if (live >= _items.Count) return false;

        // 末尾那几条正文里已经没有了：先记进删除记录（撤销时还接得回来），再裁掉
        for (var i = live; i < _items.Count; i++)
            Remember(_items[i]);

        _items.RemoveRange(live, _items.Count - live);
        return true;
    }

    /// <summary>
    /// 把正文里的占位字符全部换成<b>附件标记</b>（见 <see cref="AttachmentMarker"/>）——
    /// 这一步之后的内容才是落库、发给模型、并被气泡渲染的那一份。
    ///
    /// 之所以不再换成裸路径：带标记的正文是自解释的，渲染、复制、粘贴三件事读同一份文本。
    /// 裸路径则要靠 metadata 去正文里反查位置，附件一旦被删就再也认不出来，
    /// 而且复制出来是一串路径、粘回去变不成附件。
    /// </summary>
    public string Expand(string text)
    {
        if (_items.Count == 0) return text;

        var sb = new StringBuilder(text.Length + 32);
        var index = 0;

        foreach (var ch in text)
        {
            if (ch != Token)
            {
                sb.Append(ch);
                continue;
            }

            if (index < _items.Count) sb.Append(AttachmentMarker.Format(_items[index].Info.Path));
            index++;
        }

        return sb.ToString();
    }

    public void Clear()
    {
        _items.Clear();
        _detached.Clear();
    }

    // ---- 光标处的整体删除 ----
    //
    // 占位字符是一个不可分割的单元：光标紧邻它时按 Backspace/Delete 应当整个删掉，
    // 而不是删掉一个字符留下 `@图` 这种残渣（残渣会让附件变成永远清不掉的孤儿）。
    // 现在占位符只有 1 个字符，这两件事和「删掉一个普通字符」等价，
    // 但删除的同时必须把草稿里对应的那一条也移除，否则卡片会留在栏里。

    /// <summary>光标前紧邻一个占位字符时删掉它（连同它的登记）。有选区时返回 false。</summary>
    public bool TryDeleteBefore(string text, int caret, int selectionStart, int selectionEnd, out TextEdit edit)
    {
        edit = new TextEdit(text, caret);
        if (selectionStart != selectionEnd) return false;   // 有选区时交给默认删除

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret <= 0 || text[caret - 1] != Token) return false;

        var index = text.Take(caret - 1).Count(c => c == Token);
        if (index < _items.Count)
        {
            Remember(_items[index]);
            _items.RemoveAt(index);
        }

        edit = new TextEdit(text.Remove(caret - 1, 1), caret - 1);
        return true;
    }

    /// <summary>光标后紧邻一个占位字符时删掉它（连同它的登记）。</summary>
    public bool TryDeleteAfter(string text, int caret, int selectionStart, int selectionEnd, out TextEdit edit)
    {
        edit = new TextEdit(text, caret);
        if (selectionStart != selectionEnd) return false;

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret >= text.Length || text[caret] != Token) return false;

        var index = text.Take(caret).Count(c => c == Token);
        if (index < _items.Count)
        {
            Remember(_items[index]);
            _items.RemoveAt(index);
        }

        edit = new TextEdit(text.Remove(caret, 1), caret);
        return true;
    }

    // ---- 草稿序列化 ----

    private sealed record Payload(string Text, List<PayloadItem> Attachments);

    /// <summary>
    /// 一条存档附件。<see cref="Token"/> 是旧版（<c>@图片1</c> 文字占位符）留下的字段，
    /// 只为读得懂旧草稿，新写入时恒为 null。
    /// </summary>
    private sealed record PayloadItem(string Path, string? Token);

    /// <summary>
    /// 序列化草稿。没有附件时就是纯文本本身 —— 不带附件是绝大多数情况，
    /// 那种情况下库里存的就是用户敲的原话，出问题时打开数据库一眼能看懂。
    /// </summary>
    public string Serialize(string text)
    {
        if (_items.Count == 0) return text;

        return JsonSerializer.Serialize(new Payload(
            text,
            _items.Select(i => new PayloadItem(i.Info.Path, null)).ToList()));
    }

    /// <summary>
    /// 从库里存的草稿恢复，返回应当放进输入框的正文。
    ///
    /// 带附件的草稿存成 JSON，不带的是纯文本，靠首字符是不是 <c>{</c> 区分。
    /// 解析失败一律退回「整段当纯文本」而不是丢弃：草稿里可能有用户写了很久的内容，
    /// 宁可少两个附件，也不能把正文弄丢。
    /// </summary>
    public TextEdit Restore(string? raw)
    {
        _items.Clear();
        _detached.Clear();   // 换了一份草稿，上一条草稿的删除记录没有意义了

        if (string.IsNullOrEmpty(raw))
            return new TextEdit("", 0);

        if (!raw.StartsWith('{'))
            return new TextEdit(raw, raw.Length);

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(raw);
            if (payload is null) return new TextEdit(raw, raw.Length);

            var text = payload.Text ?? "";

            foreach (var dto in payload.Attachments)
            {
                if (AttachmentStore.FromPath(dto.Path) is not { } info) continue;

                // 旧草稿：正文里是 `@图片1` 这种文字占位符，就地换成占位字符。
                // 找不到的话（用户手工改过）就退化成「贴在末尾」，附件不能因此丢掉。
                if (dto.Token is { Length: > 0 } old && text.Contains(old, StringComparison.Ordinal))
                {
                    var at = text.IndexOf(old, StringComparison.Ordinal);
                    text = text.Remove(at, old.Length).Insert(at, Token.ToString());
                }
                else if (text.Count(c => c == Token) < _items.Count + 1)
                {
                    text += Token;
                }

                _items.Add(new Item(info));
            }

            Prune(text);   // 库里存的和正文对不上时以正文为准
            return new TextEdit(text, text.Length);
        }
        catch (JsonException)
        {
            // 损坏的 JSON：当纯文本处理，正文优先。
            _items.Clear();
            return new TextEdit(raw, raw.Length);
        }
    }

    // ---- 内部 ----

    /// <summary>把一条刚被删掉的附件记进删除记录（撤销时接回来用）。</summary>
    private void Remember(Item item)
    {
        _detached.Add(item);
        if (_detached.Count > MaxDetached) _detached.RemoveAt(0);
    }

    /// <summary>正文里第 <paramref name="index"/> 个占位字符的偏移，没有则 -1。</summary>
    private static int OffsetOf(string text, int index)
    {
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Token) continue;
            if (seen == index) return i;
            seen++;
        }

        return -1;
    }

    /// <summary>
    /// 把占位字符插到光标处。
    ///
    /// 不补空格：卡片是控件，自身带外边距，视觉上已经隔开；
    /// 补出来的空格会在导出成纯文本时变成一串难看的空洞。
    /// </summary>
    private static TextEdit InsertToken(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var newText = text.Insert(caret, Token.ToString());
        return new TextEdit(newText, caret + 1);
    }
}
