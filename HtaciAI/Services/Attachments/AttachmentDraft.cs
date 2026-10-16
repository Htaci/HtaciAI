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
/// 待发送附件的草稿：正文里放的是 <c>@图片1</c> 这样的<b>占位符</b>，
/// 真正的绝对路径存在这里，发送时才展开。
///
/// 为什么要占位符而不是直接把路径写进正文：路径往往是 <c>C:\Users\...\一组很长的东西.png</c>，
/// 写进输入框会把句子冲散，用户也没法自然地调整它的位置。占位符短、可读，
/// 而且「图片在句子的哪一段」这个信息得以保留（模型拿到的是路径，位置不变）。
///
/// 本类刻意<b>不依赖任何 Avalonia 类型</b>：它只做「给定正文和光标，算出新正文和新光标」。
/// 这样它可以被单元测试直接覆盖 —— 这层逻辑（token 碰撞、光标处整体删除、孤儿清理）
/// 是输入框里最容易出细微错误的部分，而通过界面点出来极难复现。
/// </summary>
public sealed class AttachmentDraft
{
    /// <summary>一条待发送附件：正文里的占位符 token ↔ 真实文件。</summary>
    public sealed record Item(string Token, ChatAttachment Info);

    private readonly List<Item> _items = new();

    /// <summary>token 序号，只增不减。保证同一份草稿里不会出现两个同名 token。</summary>
    private int _seq;

    public IReadOnlyList<Item> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>该路径是否已在草稿里（不区分大小写）。用于避免同一个文件被附加两次。</summary>
    public bool HasPath(string path)
        => _items.Any(i => string.Equals(i.Info.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 追加一个附件：正文里插入占位符，并登记映射。
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

        var token = NextToken(info.Label, text);
        edit = InsertToken(token, text, caret);
        _items.Add(new Item(token, info));
        return true;
    }

    /// <summary>
    /// 采纳正文里已有的附件标记：把每个标记换成占位符并登记为附件
    /// （用于「把一段带标记的文本粘进输入框」这条路径）。
    ///
    /// 同一个路径出现多次时复用同一个占位符 —— 语义上它们本来就是同一个附件。
    /// 路径无法解析（如粘贴来的相对路径）的标记<b>原样留在正文里</b>，
    /// 不猜、也不删掉用户的文字。
    /// </summary>
    /// <returns>true = 至少有一个标记被换成了占位符。</returns>
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

            var token = TokenFor(span.Path, text);

            if (token is null)
            {
                sb.Append(text, span.Start, span.Length);   // 认不出的路径：原样保留
            }
            else
            {
                // 占位符前后各要一个空白分隔，已有则不重复补。
                // 标记本身是紧凑的（`]` 收尾），换成 `@图片1` 后紧贴文字会读成一坨。
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');

                sb.Append(token);
                adopted = true;

                if (span.End < text.Length && !char.IsWhiteSpace(text[span.End])) sb.Append(' ');
            }

            cursor = span.End;
        }

        sb.Append(text, cursor, text.Length - cursor);

        var result = sb.ToString();
        edit = new TextEdit(result, result.Length);
        return adopted;
    }

    /// <summary>
    /// 取该路径对应的占位符：已登记过就复用，否则新建一个（并登记）。
    /// 路径非法时返回 null，表示「这个标记不该被采纳」。
    /// </summary>
    private string? TokenFor(string path, string text)
    {
        var existing = _items.FirstOrDefault(
            i => string.Equals(i.Info.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return existing.Token;

        if (AttachmentMarker.ToAttachment(path) is not { } info) return null;

        // 查重基准用「原文本」，因为它包含所有还没被替换的标记文本，
        // 能保证新占位符不会和正文里已有的字符串撞车。
        var token = NextToken(info.Label, text);
        _items.Add(new Item(token, info));
        return token;
    }

    /// <summary>移除一条附件：正文里的占位符与登记一起消失。token 不在正文里时只清登记。</summary>
    /// <param name="caret">当前光标，token 删除位置在光标之后时用它把光标往回带。</param>
    public TextEdit Remove(Item item, string text, int caret)
    {
        _items.Remove(item);

        var idx = text.IndexOf(item.Token, StringComparison.Ordinal);
        if (idx < 0) return new TextEdit(text, Math.Clamp(caret, 0, text.Length));

        var len = item.Token.Length;
        if (idx + len < text.Length && text[idx + len] == ' ') len++;

        var newText = text.Remove(idx, len);

        // 删的是光标前面的内容时，光标要跟着往前挪同样多的字符；否则停在原地。
        var newCaret = caret > idx ? Math.Max(idx, caret - len) : Math.Clamp(caret, 0, newText.Length);
        return new TextEdit(newText, newCaret);
    }

    /// <summary>
    /// 用户在输入框里手工改了正文（选中一段删掉、Ctrl+Z 等）之后，
    /// 把正文里已经不存在的 token 从草稿里清掉，保证 chip 栏与正文一致。
    /// </summary>
    /// <returns>true = 有附件被清掉，调用方需要刷新 chip 栏。</returns>
    public bool Prune(string text)
    {
        var removed = _items.RemoveAll(i => !text.Contains(i.Token, StringComparison.Ordinal));
        return removed > 0;
    }

    /// <summary>
    /// 把正文里的占位符全部换成<b>附件标记</b>（见 <see cref="AttachmentMarker"/>）——
    /// 这一步之后的内容才是落库、发给模型、并被气泡渲染的那一份。
    ///
    /// 之所以不再换成裸路径：带标记的正文是自解释的，渲染、复制、粘贴三件事读同一份文本。
    /// 裸路径则要靠 metadata 去正文里反查位置，附件一旦被删就再也认不出来，
    /// 而且复制出来是一串路径、粘回去变不成附件。
    /// </summary>
    public string Expand(string text)
    {
        foreach (var item in _items)
            text = text.Replace(item.Token, AttachmentMarker.Format(item.Info.Path), StringComparison.Ordinal);

        return text;
    }

    public void Clear()
    {
        _items.Clear();
        _seq = 0;
    }

    // ---- 光标处的整体删除 ----
    //
    // 占位符是一个不可分割的单元：光标紧邻它时按 Backspace/Delete 应当整个删掉，
    // 而不是删掉一个字符留下 `@图` 这种残渣（残渣会让附件变成永远清不掉的孤儿）。

    /// <summary>
    /// 光标前紧邻一个 token 时整体删掉它。有选区时返回 false，交给默认删除。
    ///
    /// 注意与 <see cref="TryDeleteAfter"/> 的不对称：这里只删 token 本身，
    /// 不吞掉它前面那个由 <c>InsertToken</c> 补出来的空格（结果是可能留下一个尾随空格），
    /// 而向后删会连尾随空格一起吞掉。这是<b>照搬既有行为</b>，不是新引入的 ——
    /// 重构期间刻意不改动可观察行为，免得把「重构」和「顺手改手感」混在一起难以定位。
    /// </summary>
    public bool TryDeleteBefore(string text, int caret, int selectionStart, int selectionEnd, out TextEdit edit)
    {
        edit = new TextEdit(text, caret);
        if (selectionStart != selectionEnd) return false;   // 有选区时交给默认删除

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret <= 0) return false;

        foreach (var item in _items)
        {
            var idx = text.LastIndexOf(item.Token, caret - 1, StringComparison.Ordinal);
            if (idx < 0 || idx + item.Token.Length != caret) continue;

            _items.Remove(item);
            var newText = text.Remove(idx, item.Token.Length);
            edit = new TextEdit(newText, idx);
            return true;
        }

        return false;
    }

    /// <summary>光标后紧邻一个 token 时整体删掉它（连同尾随的那个空格）。</summary>
    public bool TryDeleteAfter(string text, int caret, int selectionStart, int selectionEnd, out TextEdit edit)
    {
        edit = new TextEdit(text, caret);
        if (selectionStart != selectionEnd) return false;

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret >= text.Length) return false;

        foreach (var item in _items)
        {
            if (text.IndexOf(item.Token, caret, StringComparison.Ordinal) != caret) continue;

            var len = item.Token.Length;
            if (caret + len < text.Length && text[caret + len] == ' ') len++;

            _items.Remove(item);
            edit = new TextEdit(text.Remove(caret, len), caret);
            return true;
        }

        return false;
    }

    // ---- 草稿序列化 ----

    private sealed record Payload(string Text, List<PayloadItem> Attachments);

    private sealed record PayloadItem(string Token, string Path);

    /// <summary>
    /// 序列化草稿。没有附件时就是纯文本本身 —— 不带附件是绝大多数情况，
    /// 那种情况下库里存的就是用户敲的原话，出问题时打开数据库一眼能看懂。
    /// </summary>
    public string Serialize(string text)
    {
        if (_items.Count == 0) return text;

        return JsonSerializer.Serialize(new Payload(
            text,
            _items.Select(i => new PayloadItem(i.Token, i.Info.Path)).ToList()));
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
        _seq = 0;

        if (string.IsNullOrEmpty(raw))
            return new TextEdit("", 0);

        if (!raw.StartsWith('{'))
            return new TextEdit(raw, raw.Length);

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(raw);
            if (payload is null) return new TextEdit(raw, raw.Length);

            foreach (var dto in payload.Attachments)
            {
                if (string.IsNullOrEmpty(dto.Token)) continue;
                if (AttachmentStore.FromPath(dto.Path) is not { } info) continue;
                _items.Add(new Item(dto.Token, info));
            }

            // 序号从已有条数起算，够用即可 —— NextToken 本身还会查重。
            _seq = _items.Count;

            var text = payload.Text ?? "";
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

    /// <summary>生成 <c>@图片1</c> 这样的占位符，与正文或已有 token 重名时往后顺延。</summary>
    private string NextToken(string label, string text)
    {
        while (true)
        {
            _seq++;
            var token = $"@{label}{_seq}";
            if (!text.Contains(token, StringComparison.Ordinal) &&
                _items.All(i => i.Token != token))
                return token;
        }
    }

    /// <summary>
    /// 把 token 插到光标处。前面紧跟非空白时补一个空格，
    /// 免得出现「这是一张@图片1」这种粘在一起的读法。
    /// </summary>
    private static TextEdit InsertToken(string token, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var lead = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        var insertion = lead + token + " ";

        var newText = text.Insert(caret, insertion);
        return new TextEdit(newText, caret + insertion.Length);
    }
}
