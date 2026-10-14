using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using HtaciAI.Controls;
using HtaciAI.Services.Conversations;
using Xunit;

namespace HtaciAI.Tests;

/// <summary>
/// 草稿存档的可选性。
///
/// 这是「新建页复用同一个输入控件」得以成立的前提：新建页还没有会话，没有地方挂草稿，
/// 于是它干脆不给 <see cref="ChatInputControl.DraftStore"/>。
/// 如果控件在这种情况下会抛异常或偷偷落库，两页就不可能共用一个控件。
/// </summary>
public class InputDraftStoreTests
{
    /// <summary>记录所有落库内容的假存档。</summary>
    private sealed class FakeDraftStore : IDraftStore
    {
        public string? Initial { get; set; }
        public List<string> Saved { get; } = new();

        public string? Load() => Initial;

        public Task SaveAsync(string draft)
        {
            Saved.Add(draft);
            return Task.CompletedTask;
        }
    }

    private static ChatInputControl Mount()
    {
        var input = new ChatInputControl();
        new Window { Content = input }.Show();
        return input;
    }

    [AvaloniaFact]
    public async Task 没有存档时保存草稿不抛异常也不落库()
    {
        var input = Mount();
        Assert.Null(input.DraftStore);

        input.Text = "新建页上随手写的一句话";

        // 新建页走的就是这条路：没有会话可挂草稿，保存是空操作而不是错误
        await input.SaveDraftAsync();
    }

    [AvaloniaFact]
    public void 没有存档时恢复草稿得到空输入()
    {
        var input = Mount();

        input.RestoreDraft();

        Assert.Equal("", input.Text);
        Assert.Equal(0, input.AttachmentCount);
    }

    [AvaloniaFact]
    public async Task 设置存档后草稿能往返()
    {
        var store = new FakeDraftStore();
        var input = Mount();
        input.DraftStore = store;

        input.Text = "没发出去的一半";
        await input.SaveDraftAsync();

        var saved = Assert.Single(store.Saved);
        Assert.Equal("没发出去的一半", saved);

        // 换一个控件模拟「重新打开会话」
        var reopened = Mount();
        reopened.DraftStore = new FakeDraftStore { Initial = saved };
        reopened.RestoreDraft();

        Assert.Equal("没发出去的一半", reopened.Text);
    }

    [AvaloniaFact]
    public async Task 内容没变时不会重复落库()
    {
        var store = new FakeDraftStore();
        var input = Mount();
        input.DraftStore = store;

        input.Text = "写一次";

        // 打开会话、焦点变化等都会触发一次保存；内容没变就不该产生新的写入，
        // 否则光是点开一个会话就会往数据库写一次草稿。
        await input.SaveDraftAsync();
        await input.SaveDraftAsync();
        await input.SaveDraftAsync();

        Assert.Single(store.Saved);
    }
}
