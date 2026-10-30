using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件自己的窗口（需要声明 <see cref="PluginCapabilities.UiWindow"/>）。
///
/// 插件当然可以自己 <c>new Window()</c>，但这个出口替你做对了几件容易做错的事：
/// <list type="bullet">
///   <item><b>属性配好</b>：透明、无边框、置顶、不进任务栏、可拖动，四个属性配错一个，
///         桌宠就会变成「任务栏里多一个条目」「点不到」「跑到屏幕外」；</item>
///   <item><b>位置记忆</b>：按插件 id 记住上次位置，下次启动自动还原（同一插件多个窗口按标题区分）；</item>
///   <item><b>归属主窗口</b>：owner 设成宿主主窗口，最小化主窗口时它跟着走；</item>
///   <item><b>退出时有人收</b>：宿主在禁用插件 / 退出程序时统一关闭，不需要你自己记着。</item>
/// </list>
///
/// <b>只能在 UI 线程上调用</b>（<see cref="IPlugin.InitializeAsync"/> 就在 UI 线程上，
/// 从窗口按钮或插件事件里回来时也在 UI 线程上；自己的后台线程要建窗口请先
/// <c>Dispatcher.UIThread.Post</c>）。
///
/// <b>不要调 <c>Shutdown()</c></b>：那会关掉整个程序。关自己的窗口用 <see cref="Window.Close"/>。
/// </summary>
public interface IPluginWindowHost
{
    /// <summary>
    /// 按 <paramref name="options"/> 建一个配好的窗口（还不显示，自己调 <see cref="Window.Show"/>）。
    /// </summary>
    Window CreateStandalone(WindowOptions options);

    /// <summary>当前还开着的窗口（宿主退出时按它统一关闭）。只读快照。</summary>
    IReadOnlyList<Window> OpenWindows { get; }
}

/// <summary>建窗口的参数。默认值就是桌宠那种形态，只需要改尺寸。</summary>
public sealed class WindowOptions
{
    /// <summary>窗口标题（也用于区分同一插件的多个窗口的记忆位置）。</summary>
    public string Title { get; init; } = "";

    public double Width { get; init; } = 320;

    public double Height { get; init; } = 320;

    /// <summary>置顶。<b>默认 true</b>：桌宠不置顶就会被主窗口盖住。</summary>
    public bool Topmost { get; init; } = true;

    /// <summary>透明背景（配合自绘的气泡/立绘）。</summary>
    public bool Transparent { get; init; }

    /// <summary>是否出现在任务栏。桌宠类应保持 false，否则任务栏会多出一堆条目。</summary>
    public bool ShowInTaskbar { get; init; }

    /// <summary>按住窗口任意空白处可拖动。默认 true。</summary>
    public bool AllowDrag { get; init; } = true;

    /// <summary>记住并还原位置。按「插件 id + 标题」存。</summary>
    public bool RememberPosition { get; init; } = true;

    /// <summary>
    /// 无边框。默认 true（自绘窗口）；<c>false</c> 时用系统标题栏，可缩放。
    /// </summary>
    public bool Borderless { get; init; } = true;
}

/// <summary>
/// 往宿主界面里插内容（需要声明 <see cref="PluginCapabilities.UiSlot"/>）。
///
/// 两个出口：
/// <list type="bullet">
///   <item><see cref="AddControl"/>：往某个插槽里塞一个控件（插槽 id 见 <see cref="PluginSlots"/>）；</item>
///   <item><see cref="AddLauncherCard"/>：在「新标签页」加一张自己的入口卡片。</item>
/// </list>
///
/// <b>控件工厂会被反复调用</b>：每次界面重建（切走再切回会话、标签页关掉重开）都会重新
/// ask 一次工厂。所以 <b>工厂里不要持有瞬时状态</b> —— 状态放 <see cref="IPluginStorage"/>
/// 或插件自己的对象里，工厂只负责「把它画出来」。
///
/// <b>工厂抛异常不会连着整个页面一起坏</b>：那个位置会被换成一行灰色的
/// 「插件 X 显示失败」，页面其余部分照常工作。所以放心写，但别指望它静默。
///
/// <b>只能在 UI 线程上调用</b>。
/// </summary>
public interface IUiExtensionRegistry
{
    /// <summary>
    /// 往 <paramref name="slotId"/> 插入一个控件。<paramref name="order"/> 小的排前面，
    /// 相同则按注册顺序。返回的 <see cref="IDisposable"/> 撤销（插件被禁用时宿主也会撤销）。
    /// </summary>
    /// <param name="factory">每次界面重建时调用一次，返回要显示的控件。不要在闭包里存瞬时状态。</param>
    IDisposable AddControl(string slotId, Func<Control> factory, int order = 0);

    /// <summary>
    /// 在「新标签页」加一张入口卡片。返回的 <see cref="IDisposable"/> 撤销。
    /// </summary>
    IDisposable AddLauncherCard(LauncherCard card);
}

/// <summary>
/// 「新标签页」上的一张入口卡片。<b>只描述内容</b>，长什么样由宿主决定
/// （与内置的「智能对话」「工具」那些卡片保持同一套外观）。
/// </summary>
public sealed class LauncherCard
{
    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Fluent System Icons 字形码位（如 <c>"\uE9EA"</c>）。与 <see cref="IconPath"/> 二选一。</summary>
    public string? Glyph { get; init; }

    /// <summary>相对插件目录的图标路径，用 <see cref="IResourceLocator.ResolvePath"/> 解析后交给宿主。</summary>
    public string? IconPath { get; init; }

    /// <summary>卡片主色（十六进制，如 <c>#4A90D9</c>）。空 = 用宿主的灰蓝色。</summary>
    public string? AccentColor { get; init; }

    /// <summary>点击卡片要做什么。<b>在 UI 线程上调用</b>。</summary>
    public Action? OnClick { get; init; }
}

/// <summary>
/// 宿主提供的插槽 id。<b>用常量而不是自由字符串</b>：
/// 拼错一个字符就永远插不进去，而且没有任何报错能提示你。
/// </summary>
public static class PluginSlots
{
    /// <summary>「新标签页」的卡片区（已经有内置卡片，插件卡片排在它们后面）。</summary>
    public const string NewTabCards = "newtab.cards";

    /// <summary>会话窗口的顶部（消息列表上方，没有内容时整行隐藏）。</summary>
    public const string ConversationHeader = "conversation.header";

    /// <summary>输入框左侧的侧边栏（竖排，适合放一排小按钮）。</summary>
    public const string ConversationInputSide = "conversation.input.side";

    /// <summary>会话列表下方的剩余空间（左侧导航底部）。</summary>
    public const string SidebarBelow = "sidebar.below";

    /// <summary>主窗口标题栏右侧（在「检查更新」左边）。</summary>
    public const string MainWindowTitlebarRight = "mainwindow.titlebar.right";

    /// <summary>
    /// 会话页右侧（消息区右边）的侧栏，宽度由内容决定，空时整列隐藏。
    /// 适合放「与当前会话并排看」的东西：网页预览、笔记、图片。
    /// </summary>
    public const string ConversationSidebar = "conversation.sidebar";

    /// <summary>全部插槽 id，供插件页展示与文档引用。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        NewTabCards, ConversationHeader, ConversationInputSide, SidebarBelow, MainWindowTitlebarRight,
        ConversationSidebar,
    };
}
