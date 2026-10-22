using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Plugins;

/// <summary>
/// 插件开发文档：<b>按需</b>把几份 HTML 写进 <c>&lt;数据根&gt;/doc/</c>，然后在浏览器里打开首页。
///
/// <b>为什么写在数据根而不是程序目录</b>：与技能/工具/插件同一个原则 —— 数据跟着数据走，
/// 便携版拷走时文档一起过去；程序目录在 macOS 上还可能是只读的。
///
/// <b>为什么是「点一次写一次」而不是每次启动都写</b>：
/// 没人点的时候不该在用户的磁盘上多出几个文件；而每次点击重写又能保证文档与当前版本的
/// 宿主 API 一致（不用操心「文档过期了」）。
///
/// 内容全部内置在这里（不发网络请求、不依赖资源文件）：
/// 目标读者是「想给自己写个插件」的用户，所以从「在 VS 里建一个 dll 项目」讲起。
/// </summary>
public static class PluginDocs
{
    /// <summary>首页文件名。</summary>
    public const string IndexFileName = "index.html";

    /// <summary>
    /// 确保文档已写到磁盘，返回首页的绝对路径。写失败返回 <c>null</c>（调用方给用户提示即可）。
    /// </summary>
    public static string? EnsureWritten()
    {
        try
        {
            var root = AppPaths.Docs;
            Directory.CreateDirectory(root);

            foreach (var page in BuildPages())
                File.WriteAllText(Path.Combine(root, page.FileName), page.Html, new UTF8Encoding(false));

            return Path.Combine(root, IndexFileName);
        }
        catch (Exception ex)
        {
            PluginLog.Host("写入插件开发文档失败：" + ex.Message, ex);
            return null;
        }
    }

    private sealed record Page(string FileName, string Html);

    private static readonly (string FileName, string Title)[] Sections =
    {
        ("01-setup.html", "一、搭建开发环境"),
        ("02-manifest.html", "二、plugin.json 清单"),
        ("03-basics.html", "三、日志、存储与资源"),
        ("04-prompt-chat.html", "四、上下文贡献与会话事件"),
        ("05-sessions.html", "五、主动发起对话"),
        ("06-ui.html", "六、窗口与界面插槽"),
        ("07-tools.html", "七、注册工具"),
        ("08-debug.html", "八、调试、打包与排错"),
    };

    private static IEnumerable<Page> BuildPages()
    {
        yield return new Page(IndexFileName, Page_Index());
        yield return new Page("01-setup.html", Page_Setup());
        yield return new Page("02-manifest.html", Page_Manifest());
        yield return new Page("03-basics.html", Page_Basics());
        yield return new Page("04-prompt-chat.html", Page_PromptChat());
        yield return new Page("05-sessions.html", Page_Sessions());
        yield return new Page("06-ui.html", Page_Ui());
        yield return new Page("07-tools.html", Page_Tools());
        yield return new Page("08-debug.html", Page_Debug());
    }

    // ============================================================
    // 骨架
    // ============================================================

    private const string Style = """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 32px 24px 64px;
          font: 15px/1.75 -apple-system, "Segoe UI", "Microsoft YaHei", system-ui, sans-serif;
          color: #1f2430; background: #fbfcfe;
        }
        main { max-width: 860px; margin: 0 auto; }
        h1 { font-size: 26px; margin: 0 0 6px; }
        h2 { font-size: 19px; margin: 34px 0 10px; padding-bottom: 6px; border-bottom: 1px solid #e5e7eb; }
        h3 { font-size: 16px; margin: 22px 0 8px; }
        p, li { color: #333a49; }
        .lead { color: #5a6273; margin: 0 0 22px; }
        code { background: #eef1f6; border-radius: 4px; padding: 1px 5px; font-size: 13px;
               font-family: "Cascadia Code", Consolas, monospace; }
        pre { background: #1f2430; color: #e8eaf0; border-radius: 10px; padding: 14px 16px;
              overflow-x: auto; font-size: 13px; line-height: 1.6; }
        pre code { background: none; color: inherit; padding: 0; }
        table { width: 100%; border-collapse: collapse; margin: 12px 0; font-size: 14px; }
        th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #e8ebf1; vertical-align: top; }
        th { background: #f3f5f9; font-weight: 600; }
        .note { background: #eef6ff; border-left: 3px solid #4a90d9; padding: 10px 14px;
                border-radius: 6px; margin: 14px 0; }
        .warn { background: #fff7ed; border-left: 3px solid #d97706; padding: 10px 14px;
                border-radius: 6px; margin: 14px 0; }
        nav { margin-bottom: 26px; font-size: 14px; }
        nav a { color: #3a7bc8; text-decoration: none; margin-right: 14px; white-space: nowrap; }
        nav a:hover { text-decoration: underline; }
        .toc { list-style: none; padding: 0; }
        .toc li { margin: 6px 0; }
        .toc a { color: #3a7bc8; text-decoration: none; }
        .toc a:hover { text-decoration: underline; }
        footer { margin-top: 46px; color: #9aa1ae; font-size: 13px; }
        kbd { background: #fff; border: 1px solid #cfd5df; border-bottom-width: 2px;
              border-radius: 4px; padding: 0 5px; font-size: 12px; }
        """;

    /// <summary>包一层 html 骨架，带上跨页导航。</summary>
    private static string Layout(string title, string body)
    {
        var nav = new StringBuilder("<nav>");
        foreach (var (file, sectionTitle) in Sections)
            nav.Append($"<a href=\"{file}\">{sectionTitle}</a>");
        nav.Append("</nav>");

        return $"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title} · HtaciAI 插件开发文档</title>
            <style>{Style}</style>
            </head>
            <body>
            <main>
            {nav}
            <h1>{title}</h1>
            {body}
            <footer>HtaciAI 插件开发文档 · 由宿主按需生成，与当前版本的程序一致。重新点一次「插件开发文档」即可刷新。</footer>
            </main>
            </body>
            </html>
            """;
    }

    // ============================================================
    // 各页
    // ============================================================

    private static string Page_Index() => Layout("HtaciAI 插件开发文档", """
        <p class="lead">
        这份文档教你从零写一个 HtaciAI 插件：用 Visual Studio 2026 建一个类库（dll），
        引用宿主的契约程序集，然后按需要打开「上下文贡献」「会话事件」「主动发起对话」
        「窗口」「界面插槽」「自定义工具」这些能力。
        </p>

        <h2>先搞清楚三件事</h2>

        <p><b>1. 插件就是一个 dll + 一个 plugin.json。</b>
        放在 <code>&lt;数据根&gt;/plugins/&lt;插件id&gt;/</code> 里，宿主扫描到这个目录就会把它列出来。
        插件 id 就是目录名。</p>

        <p><b>2. 插件跑在宿主进程里，不是沙箱。</b>
        它能读写任意文件、能建窗口、能用网络。宿主能做的是「声明能力、启用前展示、出错隔离、
        崩溃归因」，做不到「拦住它做坏事」。所以：<b>只装你自己信任的插件</b>；
        写插件的人也应该只做用户预期之内的事。</p>

        <p><b>3. 能力靠清单声明。</b>
        <code>plugin.json</code> 里的 <code>capabilities</code> 决定 <code>IPluginContext</code> 上
        哪些门面不是 <code>null</code>。没声明的拿到的是 null —— 这是刻意的，
        让你在第一次使用时就发现「我忘了声明」，而不是运行到一半发现「插件就是不工作」。</p>

        <h2>目录</h2>
        <ul class="toc">
        """ + string.Join("\n", Sections.Select(s => $"<li><a href=\"{s.FileName}\">{s.Title}</a></li>")) + """
        </ul>

        <div class="note">
        想直接看效果？宿主自带的示例插件在程序目录的
        <code>samples/HtaciAI.Plugin.Sample/</code>（源码），把它编译出来的 dll 和 plugin.json
        放进 <code>plugins/htaci.sample/</code> 就能在插件页看到它。
        </div>
        """);

    private static string Page_Setup() => Layout("一、搭建开发环境", """
        <p class="lead">目标：一个能编译成 dll、能被宿主加载、能在插件页看到「运行中」的空插件。</p>

        <h2>1. 准备</h2>
        <ul>
          <li><b>Visual Studio 2026</b>（或 2022 17.8 以上），安装时勾选「.NET 桌面开发」。</li>
          <li><b>.NET 10 SDK</b>：命令行运行 <code>dotnet --list-sdks</code>，能看到 10.x 即可。</li>
          <li>宿主程序（HtaciAI）已经装好并能正常启动。</li>
        </ul>

        <h2>2. 在 VS 2026 里创建项目</h2>
        <ol>
          <li>「创建新项目」→ 搜索并选择 <b>类库（Class Library）</b> → 下一步。</li>
          <li>项目名建议与插件 id 一致，例如 <code>HtaciAI.Pet</code>。</li>
          <li>框架选 <b>.NET 10.0</b>，其余保持默认。</li>
          <li>创建后删掉模板生成的 <code>Class1.cs</code>。</li>
        </ol>

        <h3>改 csproj</h3>
        <p>右键项目 →「编辑项目文件」，把内容改成（版本号按你机器上的宿主来）：</p>
        <pre><code>&lt;Project Sdk="Microsoft.NET.Sdk"&gt;
          &lt;PropertyGroup&gt;
            &lt;TargetFramework&gt;net10.0&lt;/TargetFramework&gt;
            &lt;Nullable&gt;enable&lt;/Nullable&gt;
            &lt;LangVersion&gt;latest&lt;/LangVersion&gt;
          &lt;/PropertyGroup&gt;

          &lt;ItemGroup&gt;
            &lt;!-- 契约程序集：从宿主安装目录引用，不要复制到输出目录 --&gt;
            &lt;Reference Include="HtaciAI.Plugin.Abstractions"&gt;
              &lt;HintPath&gt;C:\Path\To\HtaciAI\HtaciAI.Plugin.Abstractions.dll&lt;/HintPath&gt;
              &lt;Private&gt;false&lt;/Private&gt;
            &lt;/Reference&gt;
          &lt;/ItemGroup&gt;

          &lt;ItemGroup&gt;
            &lt;!-- 契约里的窗口/控件类型引用 Avalonia；宿主会提供它，所以不复制 --&gt;
            &lt;PackageReference Include="Avalonia" Version="12.0.4"&gt;
              &lt;ExcludeAssets&gt;runtime&lt;/ExcludeAssets&gt;
            &lt;/PackageReference&gt;
          &lt;/ItemGroup&gt;
        &lt;/Project&gt;</code></pre>

        <div class="warn">
        <b>不要让 dll 或 Avalonia 的副本进入插件目录。</b>
        插件目录里出现 <code>Avalonia*.dll</code> / <code>HtaciAI*.dll</code> 会被宿主忽略并记警告：
        两套 <code>Control</code> 类型互相不认，症状是「控件建出来了但样式全丢」，
        或者 <code>Application.Current</code> 为 null。插件只用宿主那一份。
        </div>

        <h2>3. 写第一个插件</h2>
        <p>新建 <code>PetPlugin.cs</code>：</p>
        <pre><code>using System.Threading;
        using System.Threading.Tasks;
        using HtaciAI.Plugin.Abstractions;

        namespace HtaciAI.Pet;

        public sealed class PetPlugin : IPlugin
        {
            public Task InitializeAsync(IPluginContext context, CancellationToken ct)
            {
                context.Log.Info($"你好，我是 {context.PluginId}，宿主版本 {context.HostVersion}");
                return Task.CompletedTask;
            }

            public Task ShutdownAsync(CancellationToken ct) =&gt; Task.CompletedTask;
        }</code></pre>

        <h2>4. 写 plugin.json</h2>
        <p>在项目里新建一个文本文件 <code>plugin.json</code>，属性里设成「如果较新则复制」：</p>
        <pre><code>{
          "id": "htaci.pet",
          "name": "我的桌宠",
          "version": "0.1.0",
          "description": "一个最小的插件",
          "minHostVersion": "1.0.0",
          "entry": "HtaciAI.Pet.dll",
          "entryType": "HtaciAI.Pet.PetPlugin",
          "capabilities": []
        }</code></pre>

        <p>字段含义见 <a href="02-manifest.html">二、plugin.json 清单</a>。
        <b><code>id</code> 必须等于目录名</b>，<code>entryType</code> 要写<b>全名</b>（含命名空间）。</p>

        <h2>5. 部署到宿主</h2>
        <ol>
          <li>在宿主里打开「设置 → 数据管理」，那里能看到<b>数据根目录</b>的路径。</li>
          <li>在数据根下建 <code>plugins/htaci.pet/</code>。</li>
          <li>把编译产物 <code>HtaciAI.Pet.dll</code> 与 <code>plugin.json</code> 复制进去。</li>
          <li>回到宿主的「插件」页：<b>切到这一页就会自动重新扫描</b>，也可以点左上角的 ⟳ 手动刷新。
              列表里出现你的插件，状态「运行中」。<b>不需要重启程序。</b></li>
          <li>看日志：<code>&lt;数据根&gt;/logs/plugin.log</code>，应该有你写的那一行。</li>
        </ol>

        <div class="note">
        <b>插件页怎么用</b>：左侧是插件列表（按加载顺序，鼠标移上去会出现上下箭头调整顺序），
        右上角的房子图标回到<b>主页</b> —— 主页把每个插件显示成一张卡片，运行中的排在前面。
        「插件开关」是<b>总开关</b>：关掉会卸载全部插件，之后新插件也不会加载（默认关闭）。
        </div>

        <div class="note">
        <b>日常开发怎么快一点？</b>把项目的生成后事件加一行，
        让它编译完直接拷过去（路径换成你自己的数据根）：
        <pre><code>copy /Y "$(TargetPath)" "%APPDATA%\HtaciAI\plugins\htaci.pet\"</code></pre>
        然后每次改完代码：在宿主插件页把插件取消勾选再勾上（不用重启程序），新 dll 就生效了。
        </div>

        <h2>6. 调试</h2>
        <p>VS 里没法直接「启动调试」一个插件（它是被宿主加载的）。两种够用的办法：</p>
        <ul>
          <li><b>日志</b>：<code>context.Log.Info/Warn/Error</code> 写进 <code>plugin.log</code>，
              宿主不会因为你写日志出错而崩溃（写盘失败也是静默丢弃）。</li>
          <li><b>附加进程</b>：先启动宿主，再在 VS 里「调试 → 附加到进程」选择 HtaciAI，
              然后把插件取消勾选/重新勾上，断点就会命中。</li>
        </ul>
        """);

    private static string Page_Manifest() => Layout("二、plugin.json 清单", """
        <p class="lead">清单是宿主判断「这是个插件、它能用什么」的唯一依据。所有字段都<b>区分大小写</b>。</p>

        <h2>必填字段</h2>
        <table>
          <tr><th>字段</th><th>说明</th></tr>
          <tr><td><code>id</code></td><td>
            插件唯一标识，<b>必须等于目录名</b>。允许字母、数字与 <code>. _ -</code>，最长 64。
            状态文件、私有数据目录、工具 id 都按它索引，改了等于换一个插件。</td></tr>
          <tr><td><code>entry</code></td><td>入口程序集文件名（相对插件目录），如 <code>HtaciAI.Pet.dll</code>。</td></tr>
          <tr><td><code>entryType</code></td><td>实现 <code>IPlugin</code> 的类型<b>全名</b>，如 <code>HtaciAI.Pet.PetPlugin</code>。
            写全名而不是靠猜：一个程序集里可能还有别的类，猜错比报错难查。</td></tr>
        </table>

        <h2>展示与兼容</h2>
        <table>
          <tr><th>字段</th><th>说明</th></tr>
          <tr><td><code>name</code></td><td>给人看的名字。</td></tr>
          <tr><td><code>version</code></td><td>插件版本，如 <code>1.0.0</code>。</td></tr>
          <tr><td><code>description</code></td><td>一句话说明，显示在插件页。</td></tr>
          <tr><td><code>author</code></td><td>作者署名。</td></tr>
          <tr><td><code>homepage</code></td><td>项目主页 / 仓库地址。</td></tr>
          <tr><td><code>tags</code></td><td>分类标签数组，纯展示。</td></tr>
          <tr><td><code>minHostVersion</code></td><td>要求的最低宿主版本；高于当前版本时拒绝加载。</td></tr>
        </table>

        <h2>能力声明 <code>capabilities</code></h2>
        <p>数组，取值来自下表。声明了什么，<code>IPluginContext</code> 上对应的门面才不是 <code>null</code>。</p>
        <table>
          <tr><th>能力</th><th>拿到什么</th><th>能做什么</th></tr>
          <tr><td><code>prompt.contribute</code></td><td><code>Prompts</code></td>
              <td>每轮往 system 提示词末尾追加内容（有配额与来源标记）</td></tr>
          <tr><td><code>chat.observe</code></td><td><code>Chat</code></td>
              <td>订阅会话事件：轮次开始/增量/工具调用/完成/失败/活动变化</td></tr>
          <tr><td><code>chat.control</code></td><td><code>Sessions</code></td>
              <td>主动发起一轮、停止、改权限档位、压缩、读最近消息</td></tr>
          <tr><td><code>ui.window</code></td><td><code>Windows</code></td><td>建自己的窗口（透明/置顶/记忆位置由宿主配好）</td></tr>
          <tr><td><code>ui.slot</code></td><td><code>Slots</code></td><td>往宿主界面的插槽插控件、加新标签页入口卡片</td></tr>
          <tr><td><code>tools.provide</code></td><td><code>Tools</code></td><td>注册自己的工具，让模型能调用</td></tr>
        </table>

        <h2>平台与依赖</h2>
        <table>
          <tr><th>字段</th><th>说明</th></tr>
          <tr><td><code>platforms</code></td><td>
            只在哪些平台加载：<code>windows</code> / <code>linux</code> / <code>macos</code>。
            <b>空 = 所有平台</b>。用在「P/Invoke 了某个系统独有的库」这类情况。</td></tr>
          <tr><td><code>requires</code></td><td>
            前置插件 id 数组。宿主<b>先加载前置</b>；前置没装 → 本插件「无法加载」；
            前置被禁用或加载失败 → 本插件「因依赖未运行」而停下。
            前置停止时，依赖它的插件<b>一起停</b>。</td></tr>
          <tr><td><code>conflicts</code></td><td>
            与这些插件互斥：只要它们中有一个正在运行，本插件就不加载。
            适合「两个插件都想接管同一件事」的情形，让用户自己选留哪个。</td></tr>
        </table>

        <div class="warn">
        <code>requires</code> 成环（A 要 B、B 要 A）时，<b>整组都不会加载</b>，
        插件页会写明环路是哪几个。<code>requires</code> 写自己也算错。
        </div>

        <h2>完整示例</h2>
        <pre><code>{
          "id": "htaci.pet",
          "name": "桌面宠物",
          "version": "1.0.0",
          "description": "一个会看着你写代码的桌宠",
          "author": "你的名字",
          "homepage": "https://example.com/pet",
          "tags": ["桌宠", "界面"],
          "minHostVersion": "1.0.0",
          "entry": "HtaciAI.Pet.dll",
          "entryType": "HtaciAI.Pet.PetPlugin",
          "capabilities": ["ui.window", "chat.observe", "chat.control"],
          "platforms": ["windows", "macos"],
          "requires": ["htaci.core"],
          "conflicts": ["other.cat"]
        }</code></pre>
        """);

    private static string Page_Basics() => Layout("三、日志、存储与资源", """
        <p class="lead">这三样不需要任何能力声明，<code>IPluginContext</code> 上永远有。</p>

        <h2>日志</h2>
        <pre><code>context.Log.Info("启动完成");
        context.Log.Warn("配置缺失，使用默认值");
        context.Log.Error("抓取失败", ex);</code></pre>
        <p>写进 <code>&lt;数据根&gt;/logs/plugin.log</code>，每行带时间、级别与插件 id，单文件 2 MB 轮转。
        三个方法<b>都不会抛异常</b>（写盘失败静默丢弃），所以不必给日志调用套 try。</p>
        <div class="note">
        插件页有「查看崩溃日志」入口；排查「插件是不是崩过」先看
        <code>&lt;数据根&gt;/logs/crash-*.log</code> 与 <code>plugins/state.json</code>。
        </div>

        <h2>私有存储</h2>
        <pre><code>// 推荐：宿主只当字符串，插件可以干净卸载
        var raw = context.Storage.LoadText("config");        // 不存在返回 null
        context.Storage.SaveText("config", "{\"speed\":3}");   // 返回是否成功

        // 数据目录（自己管理文件时用）
        var dir = context.Storage.DataDirectory;</code></pre>
        <p>落在 <code>&lt;数据根&gt;/plugin-data/&lt;插件id&gt;/</code>，<b>与插件目录分开</b> ——
        插件升级时整个插件目录会被替换，数据放里面就会跟着消失。</p>
        <p>文件名主干只允许字母、数字与 <code>. _ -</code>（防路径穿越）；自己决定存 JSON 还是纯文本。</p>

        <div class="warn">
        <code>Load&lt;T&gt;</code> / <code>Save&lt;T&gt;</code> 这两个泛型重载<b>能让你的插件卸载不掉</b>。
        它们会用反射式 JSON 序列化处理<b>你自己定义的类型</b>，而运行时会把那些类型永久缓存进进程级结构 ——
        结果是禁用插件后提示「需重启」（内存没释放、dll 一直被占用）。
        要可卸载就用 <code>LoadText</code> / <code>SaveText</code> 自己序列化。
        </div>

        <h2>资源文件</h2>
        <pre><code>var iconPath = context.Resources.ResolvePath("assets/pet.png");
        if (iconPath is not null &amp;&amp; File.Exists(iconPath)) { /* 用它 */ }</code></pre>
        <p>把相对路径解析成绝对路径，<b>越出插件目录时返回 null</b>（<code>..</code> 一律拒绝）。
        它<b>不检查文件是否存在</b> —— 这样你能区分「路径非法」和「文件缺失」。要判断存在用
        <code>File.Exists</code>。</p>

        <h2>生命周期</h2>
        <table>
          <tr><th>时机</th><th>你要做什么</th></tr>
          <tr><td><code>InitializeAsync(context, ct)</code></td>
              <td>做初始化。抛异常 = 加载失败（宿主记下原因，不影响别的插件）。
              这时已经在 UI 线程上，可以直接建控件。</td></tr>
          <tr><td>运行中</td><td>干活。所有回调都要能随时被取消。</td></tr>
          <tr><td><code>ShutdownAsync(ct)</code></td>
              <td><b>停掉你自己起的一切</b>：定时器、动画、线程、未完成的 Task。
              漏一个，插件就会被判成「需重启」才能完全卸载。宿主会等你最多 3 秒。</td></tr>
        </table>
        <pre><code>private CancellationTokenSource? _cts;
        private Task? _worker;

        public Task InitializeAsync(IPluginContext context, CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _worker = Task.Run(async () =&gt;
            {
                try { while (!token.IsCancellationRequested) { await Task.Delay(5000, token); DoWork(); } }
                catch (OperationCanceledException) { }
            }, token);
            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(CancellationToken ct)
        {
            _cts?.Cancel();
            if (_worker is not null)
            {
                try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            }
            _worker = null;
            _cts?.Dispose();
            _cts = null;
        }</code></pre>
        """);

    private static string Page_PromptChat() => Layout("四、上下文贡献与会话事件", """
        <p class="lead">两个能力：<code>prompt.contribute</code>（往模型看到的上下文里加东西）
        与 <code>chat.observe</code>（看会话在发生什么）。都<b>不需要会话窗口打开</b>。</p>

        <h2>上下文贡献</h2>
        <pre><code>context.Prompts?.Add(new MyContributor());

        sealed class MyContributor : IPromptContributor
        {
            public string Id =&gt; "pet-mood";
            public int Order =&gt; 100;               // 小的先追加，同序按插件 id 排

            public ValueTask&lt;string?&gt; BuildAsync(PromptContext ctx, CancellationToken ct)
                =&gt; ValueTask.FromResult&lt;string?&gt;($"用户现在的心情：{CurrentMood()}");
        }</code></pre>

        <p>每轮发送前宿主问一次，你返回的文本会被追加到 system 提示词<b>末尾</b>，并且带一行来源标记：</p>
        <pre><code>&lt;!-- plugin: htaci.pet --&gt;
        用户现在的心情：平静</code></pre>

        <table>
          <tr><th>约束</th><th>值</th></tr>
          <tr><td>单插件每轮上限</td><td>4096 字符（超出截断并记警告）</td></tr>
          <tr><td>所有插件合计上限</td><td>8192 字符（后面来的被跳过或截断）</td></tr>
          <tr><td>单个贡献者超时</td><td>500 ms（超时按失败处理，跳过它）</td></tr>
          <tr><td>抛异常</td><td>只跳过你这一次，这一轮照常发送</td></tr>
        </table>
        <div class="note">
        这个入口<b>能悄悄改变模型行为</b>，所以插件页会显示每个插件最近一次实际贡献了多少字。
        别用它塞大段文本：那是 RAG/文档该干的事，塞进 system 会挤掉真正的对话历史。
        </div>

        <h2>会话事件</h2>
        <pre><code>context.Chat?.Subscribe(new MySink());

        sealed class MySink : IChatEventSink
        {
            public ValueTask OnEventAsync(ChatEvent e, CancellationToken ct)
            {
                switch (e.Kind)
                {
                    case ChatEventKind.RoundStarted:
                        // 一轮开始了
                        break;
                    case ChatEventKind.ContentDelta:
                        // e.Text = 这一轮到目前为止的完整正文；e.Delta = 本次新增
                        break;
                    case ChatEventKind.ThinkingDelta:
                        // e.Thinking = 到目前为止的全部思考
                        break;
                    case ChatEventKind.ToolCallStarted:
                        // e.ToolName
                        break;
                    case ChatEventKind.MessagePersisted:
                        // 一条消息落库了：e.Role / e.Text / e.Status
                        break;
                    case ChatEventKind.RoundCompleted:
                    case ChatEventKind.RoundFailed:
                        // e.Success / e.Status（completed / failed / stopped）/ e.Text（失败原因）
                        break;
                    case ChatEventKind.ActivityChanged:
                        // e.Status：Running / Idle，以及会话活动状态
                        break;
                }
                return ValueTask.CompletedTask;
            }
        }</code></pre>

        <h3>只订阅某个会话</h3>
        <pre><code>context.Chat?.Subscribe(sink, new ChatEventFilter { SessionId = "会话id" });
        // 也能按种类过滤：new ChatEventFilter { Kinds = new[] { ChatEventKind.ContentDelta } }</code></pre>

        <h3>几条必须知道的规则</h3>
        <ul>
          <li><b>投递不在 UI 线程上。</b>要更新控件请 <code>Dispatcher.UIThread.Post(...)</code>。</li>
          <li><b>增量已经合并过</b>（约 50 ms 一批），不要再自己做节流；
              <code>e.Text</code> 是累积正文，直接拿它显示即可。</li>
          <li><b>乱序不会发生</b>，但「一批里的最后一个增量」和「轮次结束」是分开的两条：
              宿主保证先发完增量再发结束事件。</li>
          <li><b>队列满了会丢最旧的增量</b>（<code>Text</code> 会在下一条里补全），
              所以别把增量当成「一个都不能少」的数据。</li>
          <li><b>回调抛异常只记日志</b>，不影响这一轮对话，也不影响别的插件。</li>
          <li>订阅返回的 <code>IDisposable</code> 用来退订；插件被禁用时宿主也会替你退订。</li>
        </ul>
        """);

    private static string Page_Sessions() => Layout("五、主动发起对话", """
        <p class="lead"><code>chat.control</code>：让插件在<b>会话没打开时</b>也能发起一轮对话、停止、改档位、压缩、读历史。</p>

        <h2>发起一轮</h2>
        <pre><code>var result = await context.Sessions!.SendAsync("会话id", "帮我看一下这个报错");
        switch (result.Status)
        {
            case SessionSendStatus.Sent:
                // result.Success == false 表示这一轮失败或被停止了
                break;
            case SessionSendStatus.Busy:        // 这个会话已经有一轮在跑
            case SessionSendStatus.NotFound:    // 会话不存在（可能刚被删）
            case SessionSendStatus.NoModel:     // 会话没模型，宿主也没有可回退的默认模型
            case SessionSendStatus.Failed:
                break;
        }</code></pre>

        <ul>
          <li>一轮要用「会话当前的模型 / 思考档位 / 工具 / 权限档位」，<b>与你打开窗口看到的一致</b>。</li>
          <li>会话窗口开着时，这一轮会实时出现在窗口里；没开时同样跑完并落库。</li>
          <li><b>没窗口时拿不到用户</b>：工具审批按「拒绝」处理、提问按「用户不在」回答，
              工具链会如实把这些回给模型 —— 这一轮照常跑完，不会卡住。</li>
          <li>同一会话已有任务在跑时返回 <code>Busy</code>，不排队也不插队。</li>
          <li>传进去的 <code>ct</code> 只作用于<b>发起之前</b>；要停掉已经在跑的那一轮用 <code>CancelAsync</code>。</li>
        </ul>

        <h2>其余出口</h2>
        <pre><code>await context.Sessions!.CancelAsync(sessionId);                 // 等价于点「停止」
        await context.Sessions.SetPermissionModeAsync(sessionId, PluginPermissionMode.Loose);
        await context.Sessions.CompressAsync(sessionId);               // 等价于点「压缩」
        var recent = await context.Sessions.GetRecentMessagesAsync(sessionId, 20);</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>SessionMessageSnapshot</code></td>
              <td>只读快照：<code>Role</code>（user/assistant/tool/error/agent）、
              <code>Content</code>、<code>Thinking</code>、<code>Status</code>、
              <code>SequenceNumber</code>、<code>CreatedAt</code>。</td></tr>
          <tr><td><code>PluginPermissionMode</code></td>
              <td><code>Strict</code> 严格 / <code>Normal</code> 普通 / <code>Loose</code> 宽松 / <code>Free</code> 自由。</td></tr>
        </table>

        <div class="warn">
        插件被禁用时，宿主会<b>停掉它发起、还没跑完的那一轮</b>，并拒绝之后的所有调用。
        所以「用户点了禁用，插件还在后台发消息」这件事不会发生。
        </div>

        <h2>典型用法：点一下就开口</h2>
        <pre><code>// 桌宠窗口上放一个按钮
        button.Click += async (_, _) =&gt;
        {
            var result = await context.Sessions!.SendAsync(_currentSessionId, "在吗？");
            if (result.Status != SessionSendStatus.Sent)
                context.Log.Warn($"发起失败：{result.Error}");
        };</code></pre>
        """);

    private static string Page_Ui() => Layout("六、窗口与界面插槽", """
        <p class="lead"><code>ui.window</code> 给你一个配好的窗口；<code>ui.slot</code> 让你往宿主界面里插内容。</p>

        <h2>建窗口</h2>
        <pre><code>var window = context.Windows!.CreateStandalone(new WindowOptions
        {
            Title = "我的桌宠",
            Width = 260,
            Height = 260,
            Topmost = true,          // 默认就是 true：不置顶会被主窗口盖住
            Transparent = true,      // 自绘气泡/立绘
            ShowInTaskbar = false,   // 默认 false：桌宠不该在任务栏多一个条目
            Borderless = true,       // 默认 true：无边框；按住空白处可拖动
            AllowDrag = true,
            RememberPosition = true, // 按「插件 id + 标题」记住位置
        });

        window.Content = new TextBlock { Text = "你好" };
        window.Show();</code></pre>

        <ul>
          <li>只能用一次 <code>Show()</code>；关闭用 <code>window.Close()</code>。</li>
          <li><b>不要调 <code>Shutdown()</code></b> —— 那会关掉整个程序。</li>
          <li>插件被禁用 / 程序退出时，宿主会统一关掉你所有窗口。</li>
          <li>位置存在 <code>plugin-data/&lt;id&gt;/window-state.json</code>；换了显示器导致位置跑到屏幕外时，
              宿主会自动回退到屏幕中央。</li>
          <li>只能在 UI 线程上建窗口。自己的后台线程要先 <code>Dispatcher.UIThread.Post</code>。</li>
        </ul>

        <h2>往界面里插控件</h2>
        <table>
          <tr><th>插槽 id</th><th>位置</th><th>适合放什么</th></tr>
          <tr><td><code>PluginSlots.NewTabCards</code>（<code>newtab.cards</code>）</td>
              <td>新标签页卡片区（内置卡片之后）</td><td>入口卡片（用 <code>AddLauncherCard</code>）</td></tr>
          <tr><td><code>PluginSlots.ConversationHeader</code>（<code>conversation.header</code>）</td>
              <td>会话消息列表上方，一行</td><td>状态条、提示条</td></tr>
          <tr><td><code>PluginSlots.ConversationInputSide</code>（<code>conversation.input.side</code>）</td>
              <td>输入框左侧，竖排</td><td>一排小按钮</td></tr>
          <tr><td><code>PluginSlots.SidebarBelow</code>（<code>sidebar.below</code>）</td>
              <td>左侧会话列表下方</td><td>状态面板</td></tr>
          <tr><td><code>PluginSlots.MainWindowTitlebarRight</code>（<code>mainwindow.titlebar.right</code>）</td>
              <td>标题栏右侧（「检查更新」左边）</td><td>图标按钮</td></tr>
        </table>

        <pre><code>context.Slots?.AddControl(PluginSlots.ConversationHeader, () =&gt; new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F3FF")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Child = new TextBlock { Text = "插件在看着你" },
        });</code></pre>

        <h2>新标签页入口卡片</h2>
        <pre><code>context.Slots?.AddLauncherCard(new LauncherCard
        {
            Title = "我的桌宠",
            Description = "打开桌宠窗口",
            Glyph = "\uE9EA",          // Fluent System Icons 字形
            AccentColor = "#7C3AED",   // 可选，卡片主色
            OnClick = () =&gt; OpenPetWindow(),
        });</code></pre>

        <h3>几条必须知道的规则</h3>
        <ul>
          <li><b>工厂会被反复调用。</b>每次界面重建（切走再切回会话、标签页关掉重开）都会重新要一份。
              <b>不要在工厂里存瞬时状态</b> —— 状态放 <code>Storage</code> 或插件自己的对象里，
              工厂只负责「把它画出来」。</li>
          <li><b>工厂抛异常不会毁掉整个页面。</b>那个位置会换成一行灰色的「插件 X 显示失败」，
              页面其余部分照常工作。异常栈在 <code>plugin.log</code> 里。</li>
          <li><b>插槽空了会自动隐藏</b>，不会留一个空白的边距。</li>
          <li>插槽 id 写错不会让插件加载失败，但会记一条警告（日志里有可用 id 列表），而且不会显示。</li>
          <li>撤销：<code>AddControl</code> 返回的 <code>IDisposable</code>；插件被禁用时宿主也会全部撤掉。</li>
        </ul>
        """);

    private static string Page_Tools() => Layout("七、注册工具", """
        <p class="lead"><code>tools.provide</code>：登记自己的工具，让模型能调用。走的是与内置工具<b>同一套</b>权限审批。</p>

        <h2>登记</h2>
        <pre><code>context.Tools?.Register(
            new PluginToolDefinition
            {
                Name = "pet_feed",                    // ^[a-z][a-z0-9_]{1,63}$
                Description = "给桌宠喂一次食，会让它心情变好。",
                InputSchemaJson =
                    &quot;{&quot;type&quot;:&quot;object&quot;,&quot;properties&quot;:{&quot;food&quot;:{&quot;type&quot;:&quot;string&quot;}},&quot;required&quot;:[&quot;food&quot;]}&quot;,
                DangerLevel = PluginToolDangerLevel.Safe,
            },
            async (call, ct) =&gt;
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                var food = doc.RootElement.GetProperty("food").GetString();
                await Pet.EatAsync(food, ct);
                return PluginToolResult.Ok($"已喂食：{food}");
            });</code></pre>

        <h2>登记之后发生了什么</h2>
        <ol>
          <li>工具出现在「工具」页（来源是插件），用户能看到它的危险等级并<b>手动调高</b>。</li>
          <li>会话里勾选这个工具后，模型就能调它。</li>
          <li>调用时按当前权限档位与危险等级决定要不要问用户 —— 与内置工具完全一样。</li>
          <li>插件被禁用时自动注销；会话里选着的 id 于是解析不到（模型看不见也调不到）。</li>
        </ol>

        <h2>规则与限制</h2>
        <table>
          <tr><th>项</th><th>说明</th></tr>
          <tr><td>名字</td><td><code>^[a-z][a-z0-9_]{1,63}$</code>。与内置工具、别的插件重名会被拒绝（名字是模型唯一的调用入口）。</td></tr>
          <tr><td>schema</td><td>必须是不带 BOM 的合法 JSON，根为 <code>type=object</code>；无参数给 <code>{"type":"object","properties":{}}</code>。</td></tr>
          <tr><td>默认危险等级</td><td><code>Safe</code>。会改动东西请声明 <code>Risk</code>，执行命令/动系统请声明 <code>Danger</code>。</td></tr>
          <tr><td>超时</td><td>单次默认 5 分钟封顶；想跑更久的任务，在 schema 里声明 <code>timeout</code> 参数让模型自己给。</td></tr>
          <tr><td>取消</td><td>传进来的 <code>ct</code> <b>请传下去</b>：用户点停止、超时、插件被禁用都会取消它。</td></tr>
          <tr><td>失败</td><td>返回 <code>PluginToolResult.Fail("原因")</code>，或直接抛异常（宿主会写成一条失败结果回给模型）。</td></tr>
          <tr><td>线程</td><td>执行体在后台线程上跑，<b>不要直接改控件</b>。</td></tr>
        </table>

        <div class="note">
        结果文本会作为一条 <code>tool</code> 消息进上下文，所以要当作「给模型看的输出」来写：
        长输出自己截断，别把整份文件塞回去。
        </div>
        """);

    private static string Page_Debug() => Layout("八、调试、打包与排错", """
        <h2>打包发布</h2>
        <ol>
          <li>VS 里把配置切到 <b>Release</b>，生成。</li>
          <li>取 <code>bin/Release/net10.0/HtaciAI.Pet.dll</code>，与 <code>plugin.json</code> 一起放进一个目录。</li>
          <li>插件目录名必须等于 <code>id</code>，把整个目录压成 zip 就是发布包。</li>
          <li>用户解压到 <code>&lt;数据根&gt;/plugins/</code> 下，在插件页点刷新即可。</li>
        </ol>
        <p><b>不要</b>把 <code>Avalonia*.dll</code> / <code>HtaciAI*.dll</code> 打进包里：
        会被忽略并记警告，带上只会造成两套类型。</p>

        <h2>日志在哪</h2>
        <table>
          <tr><th>文件</th><th>内容</th></tr>
          <tr><td><code>logs/plugin.log</code></td><td>插件的 Info/Warn/Error、宿主侧的加载记录</td></tr>
          <tr><td><code>logs/plugin.1.log</code></td><td>上一代日志（超过 2 MB 时轮转）</td></tr>
          <tr><td><code>logs/crash-*.log</code></td><td>崩溃现场：时间、宿主版本、插件 id、最后在做什么、异常栈</td></tr>
          <tr><td><code>plugins/state.json</code></td><td>启用状态、崩溃次数、崩溃日志路径（<b>宿主独占，插件不要碰</b>）</td></tr>
        </table>

        <h2>常见问题</h2>
        <table>
          <tr><th>现象</th><th>原因 / 处置</th></tr>
          <tr><td>插件页显示「无法加载」，原因写着 id 与目录名不一致</td>
              <td>把 <code>plugin.json</code> 的 <code>id</code> 改成目录名，或反过来。</td></tr>
          <tr><td>「无法加载」+ 只支持 xxx 平台</td><td>清单里的 <code>platforms</code> 不包含当前系统。</td></tr>
          <tr><td>「因依赖未运行」</td><td><code>requires</code> 里的前置没运行：去插件页把它启用，或看它自己为什么没起来。</td></tr>
          <tr><td>「加载失败」+ 找不到类型</td><td><code>entryType</code> 要写<b>全名</b>（含命名空间），且类要 <code>public</code>、有公共无参构造。</td></tr>
          <tr><td>「需重启才能完全更新启用状态」</td><td>
              有东西吊住了插件程序集。常见来源：没在 <code>ShutdownAsync</code> 里停掉定时器/线程；
              用了泛型 <code>Storage.Load&lt;T&gt;</code>；未撤销的 <code>DispatcherTimer</code> 或动画。
              先重启程序，把这些补上。</td></tr>
          <tr><td>插件控件建出来了但样式全丢 / <code>Application.Current</code> 是 null</td>
              <td>插件目录里带了 <code>Avalonia*.dll</code>，出现两套类型。删掉它们。</td></tr>
          <tr><td>「疑似导致上次崩溃」</td>
              <td>上次是非正常退出，且崩溃前正在跑这个插件。插件页可以看崩溃日志；
              确认后手动启用即可（也可以勾「崩溃后仍自动加载」）。</td></tr>
          <tr><td>插槽里的东西不显示</td><td>检查插槽 id 是否写错（<code>plugin.log</code> 里有警告），以及能力是否声明了 <code>ui.slot</code>。</td></tr>
          <tr><td>插件工具模型调不到</td><td>工具要在会话里勾选过才会发给模型；另外确认插件是「运行中」，且清单声明了 <code>tools.provide</code>。</td></tr>
        </table>

        <h2>卸载与更新</h2>
        <ul>
          <li>在插件页取消勾选 = 停止并卸载；此时插件 dll 可以删除或替换。</li>
          <li>替换后重新勾选即可加载新版本，通常不需要重启程序。</li>
          <li>处于「需重启」状态时必须重启：旧实例的程序集还在内存里，再加载会变成两份同时跑。</li>
        </ul>
        """);
}
