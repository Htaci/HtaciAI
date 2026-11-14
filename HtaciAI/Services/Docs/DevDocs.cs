using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HtaciAI.Services.Plugins;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services.Docs;

/// <summary>
/// 开发文档：<b>按需</b>把几份 HTML 写进 <c>&lt;数据根&gt;/doc/</c>，然后在浏览器里打开。
///
/// <b>为什么写在数据根而不是程序目录</b>：与技能/工具/插件同一个原则 —— 数据跟着数据走，
/// 便携版拷走时文档一起过去；程序目录在 macOS 上还可能是只读的。
///
/// <b>为什么是「点一次写一次」而不是每次启动都写</b>：
/// 没人点的时候不该在用户的磁盘上多出几个文件；而每次点击重写又能保证文档与当前版本的
/// 宿主 API 一致（不用操心「文档过期了」）。
///
/// 内容全部内置在这里（不发网络请求、不依赖资源文件）。
/// 分三类，对应三种「用户自己能扩展的东西」：
/// <list type="bullet">
///   <item><b>插件</b> —— 写一个 dll，拿到上下文贡献、窗口、界面插槽这些宿主动作；</item>
///   <item><b>工具</b> —— 写一个 .py / .js，让模型多一个能调用的函数；</item>
///   <item><b>技能</b> —— 写一份 SKILL.md，往 system 里注入「这类任务该怎么做」。</item>
/// </list>
/// 三者互不替代：要动手改宿主选插件，要一个明确能力选工具，要一套做事方法选技能。
///
/// 内容全部内置在这里（不发网络请求、不依赖资源文件），示例脚本与技能格式都取自宿主的
/// 真实实现 —— 文档里贴的代码是能直接跑的，不是另维护一份「差不多」的版本。
/// </summary>
public static class DevDocs
{
    /// <summary>首页文件名。</summary>
    public const string IndexFileName = "index.html";

    /// <summary>
    /// 生成并打开某一页（交给系统的默认浏览器/编辑器）。成功返回 <c>null</c>，失败返回给用户看的原因。
    ///
    /// 页面里只要一行调用：「生成到哪、失败怎么说」都由文档自己负责，
    /// 插件 / 工具 / 技能三个入口不必各写一遍。
    /// </summary>
    public static string? Open(string page)
    {
        if (EnsureWritten(page) is not { } path)
            return "生成开发文档失败：数据目录不可写（" + AppPaths.Docs + "）";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return "打开文档失败：" + ex.Message;
        }
    }

    /// <summary>插件类起始页（插件页的「开发文档」直接落到这里）。</summary>
    public const string PluginStartPage = "plugin-01-setup.html";

    /// <summary>工具类起始页（工具页的「开发文档」直接落到这里）。</summary>
    public const string ToolStartPage = "tool-01-script.html";

    /// <summary>技能类起始页（技能页的「开发文档」直接落到这里）。</summary>
    public const string SkillStartPage = "skill-01-format.html";

    /// <summary>
    /// 确保文档已写到磁盘，返回<b>要打开那一页</b>的绝对路径。
    /// <paramref name="page"/> 为空或不在目录里时回落到首页。写失败返回 <c>null</c>（调用方给用户提示即可）。
    /// </summary>
    public static string? EnsureWritten(string? page = null)
    {
        try
        {
            var root = AppPaths.Docs;
            Directory.CreateDirectory(root);

            // 逐页写盘。文件名常量与 Sections 共用，改一处不会漏掉另一处。
            foreach (var doc in BuildPages())
                File.WriteAllText(Path.Combine(root, doc.FileName), Flatten(doc.Html), new UTF8Encoding(false));

            // 早期只有插件一类时的页名，不删就会留下 8 份没人链接的旧副本
            RemoveLegacyPages(root);

            var target = page is not null
                         && Sections.Any(s => string.Equals(s.File, page, StringComparison.OrdinalIgnoreCase))
                ? page
                : IndexFileName;
            return Path.Combine(root, target);
        }
        catch (Exception ex)
        {
            PluginLog.Host("写入开发文档失败：" + ex.Message, ex);
            return null;
        }
    }

    /// <summary>
    /// 早期只有插件一类时的页名。改名之后不删，doc 目录里就会留下 8 份没人链接的旧副本，
    /// 用户翻到它们只会困惑「哪份是新的」。<b>只删这几个名字</b>：目录里的其他文件一律不碰。
    /// </summary>
    private static readonly string[] LegacyPages =
    {
        "01-setup.html", "02-manifest.html", "03-basics.html", "04-prompt-chat.html",
        "05-sessions.html", "06-ui.html", "07-tools.html", "08-debug.html",
    };

    private static void RemoveLegacyPages(string root)
    {
        foreach (var name in LegacyPages)
        {
            try
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉不算错误：多一份旧副本不影响新文档的使用
            }
        }
    }

    private sealed record Page(string FileName, string Html);

    /// <summary>
    /// 把源码里「每行之间留一个空行」的排版压回单换行。
    /// <b>必须做</b>：不压的话 &lt;pre&gt; 里的代码每两行就会多出一行空白，复制出去也是坏的。
    /// </summary>
    private static string Flatten(string html)
        => html.Replace("\r\r\n", "\n", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>文档里的一页。<see cref="Category"/> 为空表示它排在分类之外（只有首页）。</summary>
    private sealed record Section(string File, string Title, string Category);

    private const string CategoryPlugin = "插件";
    private const string CategoryTool = "工具";
    private const string CategorySkill = "技能";

    private static readonly Section[] Sections =
    {
        new(IndexFileName, "概览", ""),

        new(PluginStartPage, "一、搭建开发环境", CategoryPlugin),
        new("plugin-02-manifest.html", "二、plugin.json 清单", CategoryPlugin),
        new("plugin-03-basics.html", "三、日志、存储与资源", CategoryPlugin),
        new("plugin-04-prompt-chat.html", "四、上下文贡献与会话事件", CategoryPlugin),
        new("plugin-05-sessions.html", "五、主动发起对话", CategoryPlugin),
        new("plugin-06-ui.html", "六、窗口、标签页与界面插槽", CategoryPlugin),
        new("plugin-07-tools.html", "七、注册工具", CategoryPlugin),
        new("plugin-08-debug.html", "八、调试、打包与排错", CategoryPlugin),

        new(ToolStartPage, "一、脚本工具", CategoryTool),
        new("tool-02-schema.html", "二、入参、权限、超时与工具集", CategoryTool),

        new(SkillStartPage, "一、SKILL.md 格式", CategorySkill),
        new("skill-02-injection.html", "二、注入、启用与自建", CategorySkill),
    };

    private static IEnumerable<Page> BuildPages()

    {

        var pages = new (string File, string Body)[]
        {
            (IndexFileName, Page_Index()),

            (PluginStartPage, Page_Setup()),
            ("plugin-02-manifest.html", Page_Manifest()),
            ("plugin-03-basics.html", Page_Basics()),
            ("plugin-04-prompt-chat.html", Page_PromptChat()),
            ("plugin-05-sessions.html", Page_Sessions()),
            ("plugin-06-ui.html", Page_Ui()),
            ("plugin-07-tools.html", Page_Tools()),
            ("plugin-08-debug.html", Page_Debug()),

            (ToolStartPage, Page_ToolScript()),
            ("tool-02-schema.html", Page_ToolSchema()),

            (SkillStartPage, Page_SkillFormat()),
            ("skill-02-injection.html", Page_SkillInjection()),
        };



        // 两趟：先把每页正文处理好（加锚点、收集标题），再统一套外壳。

        // 侧栏搜索要一份跨所有页面的索引，逐页渲染时拿不到别人那一份。

        var prepared = pages

            .Select(page =>

            {

                var (html, headings) = AddAnchors(page.Body);

                return (page.File, Title: TitleOf(page.File), Html: html, Headings: headings);

            })

            .ToList();



        var index = BuildSearchIndex(prepared.Select(p => (p.File, p.Title, p.Headings)).ToList());



        foreach (var page in prepared)

            yield return new Page(page.File, Layout(page.File, page.Title, page.Html, page.Headings, index));

    }
    // ============================================================
    // 骨架
    // ============================================================

    private const string Style = """

        :root {

          --accent: #2563eb;

          --accent-soft: #eff6ff;

          --accent-line: #bfdbfe;

          --brand-cyan: #d7f8ff;

          --brand-pink: #ffe4f7;

          --text: #0f172a;

          --text-2: #475569;

          --muted: #64748b;

          --faint: #94a3b8;

          --line: #eaeaef;

          --line-soft: #f1f3f7;

          --code-bg: #10151c;

          --radius: 12px;

          --topbar: 60px;

        }



        * { box-sizing: border-box; }



        html { scroll-behavior: smooth; }



        body {

          margin: 0;

          color: var(--text);

          font: 15px/1.75 Inter, "Segoe UI", "PingFang SC", "Microsoft YaHei", system-ui, -apple-system, sans-serif;

          -webkit-font-smoothing: antialiased;

          background:

            radial-gradient(1100px 560px at 10% -12%, rgba(215, 248, 255, .8) 0%, rgba(215, 248, 255, 0) 62%),

            radial-gradient(900px 500px at 92% -6%, rgba(255, 228, 247, .78) 0%, rgba(255, 228, 247, 0) 58%),

            #fbfcfe;

          background-attachment: fixed;

        }



        a { color: var(--accent); text-decoration: none; }

        a:hover { text-decoration: underline; }



        /* ---------- 顶栏 ---------- */

        .topbar {

          position: sticky; top: 0; z-index: 30; height: var(--topbar);

          display: flex; align-items: center; justify-content: space-between; gap: 24px;

          padding: 0 28px;

          background: rgba(255, 255, 255, .72);

          -webkit-backdrop-filter: blur(16px) saturate(180%);

          backdrop-filter: blur(16px) saturate(180%);

          border-bottom: 1px solid var(--line);

        }



        .brand { display: flex; align-items: center; gap: 10px; color: var(--text); }

        .brand:hover { text-decoration: none; }

        .brand-mark {

          width: 30px; height: 30px; border-radius: 9px;

          display: grid; place-items: center;

          font-size: 15px; font-weight: 800; color: #0f172a;

          background: linear-gradient(135deg, var(--brand-cyan) 0%, var(--brand-pink) 100%);

          border: 1px solid rgba(110, 203, 255, .55);

        }

        .brand-text { font-size: 16px; font-weight: 700; letter-spacing: -.3px; }

        .brand-sub {

          margin-left: 2px; padding-left: 11px; font-size: 12.5px; color: var(--muted);

          border-left: 1px solid var(--line);

        }



        .topbar-links { display: flex; align-items: center; gap: 20px; font-size: 13.5px; }

        .topbar-links a { color: var(--text-2); }

        .topbar-links a:hover { color: var(--accent); text-decoration: none; }



        /* ---------- 三栏 ---------- */

        .layout {

          display: grid; grid-template-columns: 268px minmax(0, 1fr) 208px;

          max-width: 1420px; margin: 0 auto; align-items: start;

        }



        .sidebar {

          position: sticky; top: var(--topbar); height: calc(100vh - var(--topbar));

          overflow-y: auto; padding: 20px 14px 48px; border-right: 1px solid var(--line-soft);

        }



        .search { position: relative; margin: 0 4px 18px; }

        .search input {

          width: 100%; height: 34px; padding: 0 30px 0 31px;

          font: inherit; font-size: 13px; color: var(--text);

          background: #fff; border: 1px solid var(--line); border-radius: 9px; outline: none;

          transition: border-color .2s, box-shadow .2s;

        }

        .search input:focus { border-color: var(--accent-line); box-shadow: 0 0 0 3px rgba(37, 99, 235, .1); }

        .search input::placeholder { color: var(--faint); }

        .search-icon { position: absolute; left: 10px; top: 10px; color: var(--faint); pointer-events: none; }

        .search kbd { position: absolute; right: 8px; top: 8px; }



        .tree-title {

          margin: 0 0 8px; padding: 0 11px; font-size: 11.5px; font-weight: 600;

          letter-spacing: .09em; text-transform: uppercase; color: var(--faint);

        }

        .tree-group {
          margin: 16px 0 5px; padding: 0 11px; font-size: 11.5px; font-weight: 600;
          letter-spacing: .06em; color: var(--text-2);
        }



        .item {

          display: block; margin-bottom: 2px; padding: 7px 11px; border-radius: 8px;

          font-size: 13.5px; color: var(--text-2); transition: background .15s, color .15s;

        }

        .item:hover { background: #f4f6fb; color: var(--text); text-decoration: none; }

        .item.active {

          background: var(--accent-soft); color: var(--accent); font-weight: 600;

          box-shadow: inset 3px 0 0 var(--accent);

        }



        .results { display: grid; gap: 2px; }

        .result { display: block; padding: 7px 11px; border-radius: 8px; }

        .result:hover { background: #f4f6fb; text-decoration: none; }

        .result-page { display: block; font-size: 11.5px; color: var(--faint); }

        .result-title { display: block; font-size: 13.5px; color: var(--text); }

        .empty { margin: 0; padding: 6px 11px; font-size: 13px; color: var(--faint); }



        /* ---------- 正文 ---------- */

        .content { min-width: 0; max-width: 900px; padding: 30px 46px 90px; }



        .breadcrumb {

          display: flex; align-items: center; gap: 8px; margin-bottom: 14px;

          font-size: 12.5px; color: var(--faint);

        }

        .breadcrumb a { color: var(--muted); }

        .breadcrumb .sep { color: #d6dae2; }



        h1 { margin: 0 0 10px; font-size: 32px; font-weight: 700; line-height: 1.25; letter-spacing: -.03em; }

        h2 {

          margin: 40px 0 12px; padding-bottom: 9px; font-size: 21px; font-weight: 650;

          letter-spacing: -.01em; border-bottom: 1px solid var(--line);

        }

        h3 { margin: 26px 0 8px; font-size: 16.5px; font-weight: 650; }



        .anchor { margin-left: 8px; font-size: 14px; color: var(--accent-line); opacity: 0; transition: opacity .15s; }

        h2:hover .anchor, h3:hover .anchor { opacity: 1; }



        p, li { color: var(--text-2); }

        p { margin: 12px 0; }

        ul, ol { margin: 12px 0; padding-left: 22px; }

        li { margin: 5px 0; }

        .lead { margin: 0 0 24px; font-size: 16px; line-height: 1.8; }

        b, strong { color: var(--text); font-weight: 650; }



        code {

          padding: 1px 6px; font-size: 13px; color: #0f2b46; background: #f1f5f9;

          border: 1px solid #e6ecf3; border-radius: 5px;

          font-family: "Cascadia Code", Consolas, "SF Mono", monospace;

        }

        pre {

          margin: 16px 0; padding: 16px 18px; overflow-x: auto;

          background: var(--code-bg); border: 1px solid #1e2733; border-radius: var(--radius);

          font-size: 12.8px; line-height: 1.7;

        }

        pre code { padding: 0; border: 0; background: none; color: #e6eaf2; font-size: inherit; }



        table {

          width: 100%; margin: 16px 0; font-size: 14px;

          border: 1px solid var(--line); border-collapse: separate; border-spacing: 0;

          border-radius: var(--radius); overflow: hidden;

        }

        th, td { padding: 9px 13px; text-align: left; vertical-align: top; border-bottom: 1px solid var(--line-soft); }

        tr:last-child td { border-bottom: 0; }

        th { background: #f7f9fc; color: var(--text); font-weight: 600; }

        td { color: var(--text-2); }



        .note, .warn { margin: 16px 0; padding: 13px 16px; border-radius: 10px; font-size: 14px; }

        .note {

          color: #1f4859;

          background: linear-gradient(135deg, rgba(215, 248, 255, .85) 0%, rgba(255, 228, 247, .35) 100%);

          border: 1px solid rgba(110, 203, 255, .45); border-left: 3px solid #6ecbff;

        }

        .warn { color: #7c4a12; background: #fffbeb; border: 1px solid #fde68a; border-left: 3px solid #d97706; }

        .note code, .warn code { background: rgba(255, 255, 255, .75); }



        kbd {

          padding: 0 5px; font-size: 11.5px; color: var(--muted); background: #fff;

          border: 1px solid #dfe4ec; border-bottom-width: 2px; border-radius: 4px;

        }



        footer {

          margin-top: 60px; padding-top: 18px; font-size: 12.5px; color: var(--faint);

          border-top: 1px solid var(--line-soft);

        }



        /* ---------- 右侧目录 ---------- */

        .toc-aside {

          position: sticky; top: var(--topbar); max-height: calc(100vh - var(--topbar));

          overflow-y: auto; padding: 34px 18px 48px; font-size: 13px;

        }

        .toc-title {

          margin-bottom: 10px; font-size: 11.5px; font-weight: 600;

          letter-spacing: .09em; text-transform: uppercase; color: var(--faint);

        }

        .toc-aside ul { margin: 0; padding: 0; list-style: none; }

        .toc-aside li { margin: 7px 0; }

        .toc-aside a {

          display: block; padding-left: 11px; line-height: 1.5; color: var(--muted);

          border-left: 1px solid var(--line);

        }

        .toc-aside a:hover { color: var(--accent); border-left-color: var(--accent-line); text-decoration: none; }

        .toc-aside a.active { color: var(--accent); border-left-color: var(--accent); font-weight: 600; }



        @media (max-width: 1180px) {

          .layout { grid-template-columns: 252px minmax(0, 1fr); }

          .toc-aside { display: none; }

        }



        @media (max-width: 820px) {

          .layout { grid-template-columns: minmax(0, 1fr); }

          .sidebar { display: none; }

          .content { padding: 22px 20px 70px; }

          h1 { font-size: 26px; }

        }

        """;



    /// <summary>侧栏搜索 + 右栏目录高亮。够小、够独立，直接内联进页面，不引外部资源。</summary>

    private const string Script = """

        (function () {

          var input = document.getElementById('q');

          var tree = document.getElementById('tree');

          var box = document.getElementById('results');

          if (!input || !tree || !box) return;



          var items = window.__DOC_INDEX__ || [];



          function render(list) {

            box.textContent = '';



            if (!list.length) {

              var none = document.createElement('p');

              none.className = 'empty';

              none.textContent = '没有匹配的章节';

              box.appendChild(none);

              return;

            }



            for (var i = 0; i < list.length; i++) {

              var it = list[i];

              var link = document.createElement('a');

              link.className = 'result';

              link.href = it.p + (it.a ? '#' + it.a : '');



              var page = document.createElement('span');

              page.className = 'result-page';

              page.textContent = it.t;



              var title = document.createElement('span');

              title.className = 'result-title';

              title.textContent = it.h;



              link.appendChild(page);

              link.appendChild(title);

              box.appendChild(link);

            }

          }



          function update() {

            var kw = input.value.trim().toLowerCase();



            if (!kw) {

              tree.hidden = false;

              box.hidden = true;

              box.textContent = '';

              return;

            }



            var hits = [];

            for (var i = 0; i < items.length; i++) {

              var it = items[i];

              if ((it.h + ' ' + it.t).toLowerCase().indexOf(kw) < 0) continue;

              hits.push(it);

              if (hits.length >= 40) break;

            }



            tree.hidden = true;

            box.hidden = false;

            render(hits);

          }



          input.addEventListener('input', update);

          input.addEventListener('keydown', function (e) {

            if (e.key !== 'Escape') return;

            input.value = '';

            update();

            input.blur();

          });

          document.addEventListener('keydown', function (e) {

            if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'k') return;

            e.preventDefault();

            input.focus();

            input.select();

          });

        })();



        (function () {

          var links = [].slice.call(document.querySelectorAll('.toc-aside a'));

          if (!links.length || typeof IntersectionObserver === 'undefined') return;



          var byId = {};

          links.forEach(function (a) { byId[a.getAttribute('href').slice(1)] = a; });



          var observer = new IntersectionObserver(function (entries) {

            entries.forEach(function (entry) {

              if (!entry.isIntersecting) return;

              links.forEach(function (a) { a.classList.remove('active'); });

              var link = byId[entry.target.id];

              if (link) link.classList.add('active');

            });

          }, { rootMargin: '0px 0px -72% 0px' });



          Object.keys(byId).forEach(function (id) {

            var el = document.getElementById(id);

            if (el) observer.observe(el);

          });

        })();

        """;



    /// <summary>正文里的一处标题。右栏「本页内容」与侧栏搜索都靠它。</summary>

    private sealed record Heading(int Level, string Id, string Text);



    private static string TitleOf(string fileName)
        => Sections.FirstOrDefault(s => string.Equals(s.File, fileName, StringComparison.OrdinalIgnoreCase))?.Title
           ?? "开发文档";

    /// <summary>这一页属于哪一类（首页返回空）。</summary>
    private static string CategoryOf(string fileName)
        => Sections.FirstOrDefault(s => string.Equals(s.File, fileName, StringComparison.OrdinalIgnoreCase))?.Category
           ?? "";



    /// <summary>

    /// 给正文里的 h2 / h3 加锚点 id，顺便把标题收集出来。

    ///

    /// <b>为什么不在每页手写目录</b>：目录与正文是两份东西，手写就一定会有一份忘记同步 ——

    /// 改个标题，右边还是旧的，点过去跳到错的地方。这里只认「正文里确实有的标题」。

    /// </summary>

    private static (string Html, List<Heading> Headings) AddAnchors(string body)

    {

        var headings = new List<Heading>();

        var builder = new StringBuilder(body.Length + 512);

        var cursor = 0;



        while (true)

        {

            var open2 = body.IndexOf("<h2>", cursor, StringComparison.Ordinal);

            var open3 = body.IndexOf("<h3>", cursor, StringComparison.Ordinal);



            var open = open2 < 0 ? open3 : open3 < 0 ? open2 : Math.Min(open2, open3);

            if (open < 0) break;



            var level = open == open2 ? 2 : 3;

            var closeTag = level == 2 ? "</h2>" : "</h3>";

            var close = body.IndexOf(closeTag, open, StringComparison.Ordinal);

            if (close < 0) break;



            var inner = body[(open + 4)..close];

            var id = $"s{headings.Count + 1}";



            builder.Append(body, cursor, open - cursor);

            builder.Append($"<h{level} id=\"{id}\">{inner}<a class=\"anchor\" href=\"#{id}\">#</a></h{level}>");



            headings.Add(new Heading(level, id, StripTags(inner).Trim()));

            cursor = close + closeTag.Length;

        }



        builder.Append(body, cursor, body.Length - cursor);

        return (builder.ToString(), headings);

    }



    private static string StripTags(string html)

    {

        var builder = new StringBuilder(html.Length);

        var inside = false;



        foreach (var ch in html)

        {

            if (ch == '<') { inside = true; continue; }

            if (ch == '>') { inside = false; continue; }

            if (!inside) builder.Append(ch);

        }



        return builder.ToString();

    }



    /// <summary>

    /// 侧栏搜索用的索引：页面本身 + 每页的 h2。

    /// <b>只收到 h2</b>：连 h3 一起收，搜一个常用词会返回几十条同页结果，反而更难找。

    /// </summary>

    private static string BuildSearchIndex(IReadOnlyList<(string File, string Title, List<Heading> Headings)> pages)

    {

        var items = new List<string>();



        foreach (var page in pages)

        {

            items.Add(Entry(page.File, page.Title, "", page.Title));



            foreach (var heading in page.Headings)

                if (heading.Level == 2)

                    items.Add(Entry(page.File, page.Title, heading.Id, heading.Text));

        }



        return "[" + string.Join(",", items) + "]";

    }



    /// <summary>搜索索引里的一条：<c>{"p":页, "t":页标题, "a":锚点, "h":标题}</c>。</summary>

    private static string Entry(string page, string title, string anchor, string text)

        => "{\"p\":" + Quote(page)

           + ",\"t\":" + Quote(title)

           + ",\"a\":" + Quote(anchor)

           + ",\"h\":" + Quote(text) + "}";
    /// <summary>把一段文本塞进 JSON / HTML 里的双引号字符串。内容全来自本文件，只需挡住这三种字符。</summary>
    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("<", "\\u003c") + "\"";

    /// <summary>包一层外壳：顶栏 + 左侧目录 + 正文 + 右侧「本页内容」。</summary>
    private static string Layout(
        string fileName, string title, string body,
        IReadOnlyList<Heading> headings, string searchIndex)
    {
        var tree = new StringBuilder();

        // 首页单独一行，其余按分类分组：三类扩展在侧栏里就是三块，
        // 不必先点开某一类才知道这一类的目录有多长。
        var indexActive = string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase) ? " active" : "";
        tree.Append($"<a class=\"item{indexActive}\" href=\"{IndexFileName}\">概览</a>");

        var currentCategory = "";
        foreach (var section in Sections)
        {
            if (section.Category.Length == 0) continue;

            if (!string.Equals(section.Category, currentCategory, StringComparison.Ordinal))
            {
                currentCategory = section.Category;
                tree.Append($"<p class=\"tree-group\">{currentCategory}</p>");
            }

            var active = string.Equals(section.File, fileName, StringComparison.OrdinalIgnoreCase)
                ? " active"
                : "";
            tree.Append($"<a class=\"item{active}\" href=\"{section.File}\">{section.Title}</a>");
        }

        var toc = new StringBuilder();

        foreach (var heading in headings)
            if (heading.Level == 2)
                toc.Append($"<li><a href=\"#{heading.Id}\">{heading.Text}</a></li>");

        var tocAside = toc.Length == 0
            ? ""
            : $"<nav class=\"toc-aside\"><p class=\"toc-title\">本页内容</p><ul>{toc}</ul></nav>";

        var category = CategoryOf(fileName);
        var crumb = string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase)
            ? "<span>文档</span>"
            : category.Length == 0
                ? $"<a href=\"{IndexFileName}\">文档</a><span class=\"sep\">/</span><span>{title}</span>"
                : $"<a href=\"{IndexFileName}\">文档</a><span class=\"sep\">/</span><span>{category}</span>"
                  + $"<span class=\"sep\">/</span><span>{title}</span>";

        return $"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title} · HtaciAI 开发文档</title>
            <style>{Style}</style>
            </head>
            <body>
            <header class="topbar">
              <a class="brand" href="{IndexFileName}">
                <span class="brand-mark">H</span>
                <span class="brand-text">HtaciAI</span>
                <span class="brand-sub">开发文档</span>
              </a>
              <nav class="topbar-links">
                <a href="https://htaci.com" target="_blank" rel="noreferrer">官网</a>
                <a href="https://app.htaci.com" target="_blank" rel="noreferrer">网页版</a>
              </nav>
            </header>

            <div class="layout">
              <aside class="sidebar">
                <div class="search">
                  <svg class="search-icon" width="13" height="13" viewBox="0 0 16 16" fill="none"
                       stroke="currentColor" stroke-width="1.7" stroke-linecap="round">
                    <circle cx="7" cy="7" r="4.6"/>
                    <path d="M10.6 10.6 14 14"/>
                  </svg>
                  <input id="q" type="search" placeholder="搜索章节" autocomplete="off" spellcheck="false">
                  <kbd>Ctrl K</kbd>
                </div>
                <nav class="tree" id="tree">{tree}</nav>
                <div class="results" id="results" hidden></div>
              </aside>

              <main class="content">
                <div class="breadcrumb">{crumb}</div>
                <h1>{title}</h1>
                {body}
                <footer>HtaciAI 开发文档 · 由宿主按需生成，与当前版本的程序一致。重新点一次「开发文档」即可刷新。</footer>
              </main>

              {tocAside}
            </div>

            <script>window.__DOC_INDEX__ = {searchIndex};</script>
            <script>{Script}</script>
            </body>
            </html>
            """;
    }

    // ============================================================
    // 各页
    // ============================================================

    private static string Page_Index() => """

        <p class="lead">
        这份文档讲的是<b>你自己能给 HtaciAI 加什么</b>。一共有三条路，各自解决不同的事。
        </p>

        <h2>三类扩展</h2>

        <p><b>1. 插件</b> —— 用 C# 写一个 dll，拿到宿主自己的能力：贡献上下文、订阅会话事件、
        主动发起对话、开窗口、往界面插插槽、注册工具。
        想改「程序本身怎么工作」，走这条。<a href="plugin-01-setup.html">从搭建环境开始 →</a></p>

        <p><b>2. 工具</b> —— 写一个 .py / .js，让模型多一个能调用的函数。
        想要一个明确的能力（查天气、算汇率、读你们的内部接口），走这条。
        不用编译，改完即用。<a href="tool-01-script.html">看脚本工具怎么写 →</a></p>

        <p><b>3. 技能</b> —— 写一份 SKILL.md，往 system 里注入「这类任务该怎么做」。
        想教模型一套做事方法（审查规范、写作口径、排查顺序），走这条。
        <a href="skill-01-format.html">看 SKILL.md 的格式 →</a></p>

        <h2>怎么选</h2>

        <ul>
          <li>要一个<b>能调用的函数</b> → 工具</li>
          <li>要一套<b>做事的方法</b> → 技能</li>
          <li>要和<b>宿主本身</b>交互（窗口、事件、界面） → 插件</li>
          <li>既要能力又要方法 → 工具 + 技能，在技能正文里写清「什么时候调那个工具」</li>
        </ul>

        <h2>这三份文档从哪来</h2>

        <p>全部内置在程序里，点一次生成一次，所以永远与当前版本一致 ——
        不会出现「照着文档写的代码在新版本里编译不过」。生成位置是数据根下的 <code>doc</code> 目录。</p>

        <p>工具与技能这两类里的示例，取自宿主的真实实现：脚本的输入输出约定、
        SKILL.md 的 frontmatter 字段、技能名与工具名的合法字符，都与程序实际走的是同一套代码，
        贴出去的例子可以直接跑。</p>

        <div class="note">
        插件跑在宿主进程里，不是沙箱 —— 它能读写任意文件、能建窗口、能用网络。
        宿主只做「声明能力、启用前展示、出错隔离、崩溃归因」，做不到「拦住它做坏事」。
        所以：<b>只装你自己信任的插件</b>。工具与技能则是数据，不引入可执行代码（脚本由独立进程跑）。
        </div>

        """;


    // ---- 工具 ----

    private static string Page_ToolScript() => """
        <p class="lead">
        目标：写一个脚本文件，让模型多出一个能调用的工具。宿主只负责「把参数递进去、把结果读出来」，
        其余全由脚本自己决定 —— 所以任何语言、任何库都能用。
        </p>

        <h2>契约：参数从 stdin 进，结果从 stdout 出</h2>

        <p>一次工具调用就是<b>启动一个子进程</b>：</p>

        <ul>
          <li>宿主把这次调用的参数整体序列化成<b>一个 JSON 对象</b>，写进子进程的 <code>stdin</code>，然后关掉它；</li>
          <li>脚本自己干活，把结果打印到 <code>stdout</code> —— 这段文字原样成为模型的工具结果；</li>
          <li><b>退出码 0 = 成功</b>；非 0 = 失败，此时 <code>stderr</code> 的内容会作为失败原因回给模型。</li>
        </ul>

        <div class="note">
        不要从命令行参数读入参。参数里常有空格、引号、中文路径，拼进命令行一定会被二次解析；
        stdin 是唯一不用操心转义的通道。也不要把结果写成 JSON 再让模型自己解析 —— 
        stdout 的原文就是给模型看的，写得越直白它越不容易读错。
        </div>

        <h2>示例一：Python</h2>

        <p>文件放在 <code>&lt;数据根&gt;/tools/add.py</code>：</p>

        <pre><code># 两数相加。参数经 stdin 传入 JSON，结果输出到 stdout。
        import json, sys

        def run(a, b):
            return a + b

        if __name__ == "__main__":
            print(json.dumps(run(**json.loads(sys.stdin.read()))))</code></pre>

        <p>对应的入参 schema（在工具页的「创建新工具」里填）：</p>

        <pre><code>{"type":"object","properties":{"a":{"type":"integer","description":"加数"},"b":{"type":"integer","description":"加数"}},"required":["a","b"]}</code></pre>

        <h2>示例二：Node.js</h2>

        <p>文件放在 <code>&lt;数据根&gt;/tools/uppercase.js</code>：</p>

        <pre><code>// 字符串转大写。参数经 stdin 传入 JSON，结果输出到 stdout。
        const fs = require('fs');
        const input = fs.readFileSync(0, 'utf-8');
        const args = JSON.parse(input);
        console.log(args.text.toUpperCase());</code></pre>

        <pre><code>{"type":"object","properties":{"text":{"type":"string","description":"要转换的文本"}},"required":["text"]}</code></pre>

        <h2>宿主替你做的四件事</h2>

        <ul>
          <li><b>解释器探测</b>：启动前按「设置 → 环境配置」里配的路径或系统 PATH 找 Python / Node。
              找不到就直接报「未检测到运行时」，不会去猜一个别的。</li>
          <li><b>超时</b>：默认 300 秒，可在工具页给这个工具单独改（含「一直等待」）。
              schema 里声明一个 <code>timeout</code>（整数，秒）之后，
              模型还能为单次调用要求更长的时长 —— 跑批量任务时有用。
              详见<a href="tool-02-schema.html#s5">超时</a>。</li>
          <li><b>取消</b>：用户点「停止」会连<b>整个进程树</b>一起杀掉，不会留下后台还在跑的脚本。</li>
          <li><b>落库</b>：参数与结果照常进 <code>chat_message</code>，与内置工具完全一样，
              重开会话还看得见。</li>
        </ul>

        <h2>bash 工具的执行器</h2>

        <p>内置的 <code>bash</code> 工具不读脚本文件，它把一条 <code>command</code> 交给<b>某个 shell</b> 去执行。
        这个 shell 是哪一个，由「设置 → 环境配置 → bash 工具」决定，可选
        cmd、Windows PowerShell、PowerShell 7、Git Bash、bash（PATH 里那个），
        默认「自动」= Windows 用 <code>cmd.exe</code>、其他平台用 <code>/bin/bash</code>。</p>

        <p>命令语法随执行器变：cmd 里写 <code>%VAR%</code>、PowerShell 里写 <code>$env:VAR</code>、
        bash 里写 <code>$VAR</code>，换一个就全不认。<b>选中的执行器会写进工具描述</b>，
        模型据它决定命令怎么写 —— 所以换执行器不需要改提示词，也不需要重启，
        改完下一次对话就按新的那个走。</p>

        <div class="note">
        在 cmd 里要跑 PowerShell 仍可以直接在命令开头写 <code>powershell</code> / <code>pwsh</code>：
        工具会认出这个前缀，单独起一个 PowerShell 进程执行整条命令，不会套两层引号。
        同理，Git Bash 里要跑 cmd 内建命令，得显式写 <code>cmd /c …</code>。
        </div>

        <h2>为什么命令会卡住不返回</h2>

        <p>子进程是<b>没有窗口、没有人守着</b>的。凡是要等人点一下、敲一下的东西，都会把整个工具
        永远吊在那里 —— 用户看到的就是「AI 不动了」，日志里连超时都没有。已经处理掉的几类：</p>

        <ul>
        <li><b>弹系统窗口的命令</b>：Windows 上 <code>start</code> 只是 ShellExecute 的桥，目标不对时
        弹「Windows 找不到 … 文件」并一直等用户点确定。<code>start</code>、<code>explorer</code>、
        <code>rundll32</code>、<code>mshta</code>、<code>control</code>、<code>mmc</code>、
        <code>Start-Process</code> 现在<b>直接拒绝</b>，并把原因写回给模型。
        只认每段命令的第一个词，<code>npm start</code> 这类正常写法不受影响。</li>
        <li><b>等输入的命令</b>：<code>pause</code>、<code>choice</code>、<code>Read-Host</code>、
        要账号的 git。子进程的 stdin 一启动就关掉，它们拿到 EOF 立刻失败（<code>pause</code> 实测 30ms 返回），
        PowerShell 另外加了 <code>-NonInteractive</code>。</li>
        <li><b>带引号的路径被撕掉</b>：cmd 的 <code>/c</code> 自己剥引号，而 .NET 的
        <code>ArgumentList</code> 按 C 规则把 <code>"</code> 转成 <code>\"</code>，
        cmd 不认反斜杠转义 —— <code>dir "C:\Program Files"</code> 会变成
        <code>dir \"C:\Program Files\</code> 并报「文件名、目录名或卷标语法不正确」。
        现在 cmd 走整串 <code>Arguments</code>（<code>/d /s /c "…"</code>），PowerShell 走
        <code>-EncodedCommand</code>（base64，绕开所有引号转义），bash 仍用 <code>-c</code>。</li>
        <li><b>超时以后还是卡着</b>：进程树杀掉后要等两条管道读到结尾，孙进程还攥着管道时那一步永远不完成。
        现在只等 2 秒，拿不到就放弃把结果交回去。</li>
        </ul>

        <p>另外，输出解码改成「先按严格 UTF-8 试，不合法再按系统 OEM 代码页解」：
        cmd 的内建命令在中文系统上输出 GBK，以前统一按 UTF-8 解会整片变成 <code>�</code>，
        模型看到的是乱码路径，接着按乱码拼命令。PowerShell 则在命令前垫一句把
        <code>[Console]::OutputEncoding</code> 设成 UTF-8，并关掉进度条（进度会以 CLIXML 灌进 stderr）。</p>

        <h2>什么时候不该写脚本工具</h2>

        <p>脚本拿到的是一个<b>已经序列化好的参数对象</b>：没有会话、没有历史、没有界面，
        也不能反过来问用户问题。需要「按当前对话内容判断」的活留给技能；
        需要窗口、事件订阅、界面插槽的活去写<a href="plugin-01-setup.html">插件</a>。</p>
        """;

    private static string Page_ToolSchema() => """
        <p class="lead">
        对模型来说，一个工具只有三样东西看得见：<b>名字</b>、<b>描述</b>、<b>入参 schema</b>。
        脚本写错顶多是执行失败；这三样写错，工具要么永远不被用，要么被用错。
        </p>

        <h2>名字</h2>

        <p>发给模型的调用名用 snake_case（如 <code>get_weather</code>）。
        内部另有一份 id 作为库表主键与路由键，二者是分开的 —— 改显示名不会丢掉已有记录。</p>

        <p>AI 自建工具时，名字必须匹配 <code>^[a-z][a-z0-9_]{1,63}$</code>，重名会被直接拒绝。</p>

        <h2>描述</h2>

        <p>写「做什么、什么时候该用、什么时候不该用」，不要写「这是一个工具」。
        模型是按描述在多个工具之间挑的，描述里省掉的边界就是它将来踩的坑。</p>

        <h2>入参 schema</h2>

        <p>标准 JSON Schema，根必须是 <code>{"type":"object"}</code>，参数放 <code>properties</code>，
        必填项列进 <code>required</code>。每个参数都要有 <code>description</code> ——
        那是模型唯一能看到的参数说明。</p>

        <h2>危险等级与审批</h2>

        <p>每个工具有一个危险等级，它决定「要不要先问你一声」：</p>

        <ul>
          <li><b>安全</b>：只读、无副作用（<code>read</code> / <code>glob</code> / <code>grep</code> / <code>webfetch</code>）；</li>
          <li><b>风险</b>：会改动内容（<code>edit</code> / <code>write</code>）；</li>
          <li><b>危险</b>：能执行命令、影响系统（<code>bash</code>）。<b>脚本工具默认就是这一档</b>，
              因为宿主看不出脚本里到底写了什么。</li>
        </ul>

        <p>是否真的弹确认框，取决于当前会话的权限档位：</p>

        <ul>
          <li><b>严格</b>：全部审批；</li>
          <li><b>普通</b>（默认）：除「安全」外都审批；</li>
          <li><b>宽松</b>：只有「危险」审批；</li>
          <li><b>自由</b>：都不审批。</li>
        </ul>

        <p>等级可以在工具页调低，但 AI 自建工具时等级由程序指定，不接受模型自评 ——
        让将被执行的一方给自己定风险，没有意义。</p>

        <h2>超时</h2>

        <p>单次工具执行默认最多等 <b>5 分钟</b>。这个值按「本地读文件、跑一条命令」估的，
        四类工具（内置 / 脚本 / MCP / 插件）都受它约束，也都能单独改：
        在工具页找到那个工具，点卡片下方的「超时 5 分钟」徽标，选一个档位或直接填秒数，
        改完立刻生效，不用重启。</p>

        <ul>
          <li><b>默认</b>：没有单独配过，走 5 分钟的兜底；</li>
          <li><b>一直等待</b>：不设上限，只有用户点「停止」才会中断；</li>
          <li><b>具体秒数</b>：上限 24 小时。</li>
        </ul>

        <p>配置存在 <code>tool_timeouts</code> 表里（一行 = 一个工具），不认工具来源。
        内置工具的定义在代码里、插件与 MCP 工具是运行时注册的，它们都不在 <code>tool_tools</code> 表里 ——
        所以超时单独一张表，而不是给工具表加列。</p>

        <h3>入参 timeout 与配置的关系</h3>

        <p>schema 里声明了 <code>timeout</code>（整数，秒）的工具，模型可以为<b>这一次调用</b>申请更长的时长。
        两者都有时<b>取大的那个</b>：入参只能把单次调用抬高，不会把已经放宽的工具压回默认值。
        但如果工具被显式设成「一直等待」，入参就不再有意义。</p>

        <p>插件工具还多一条来源：插件在 <code>PluginToolDefinition.TimeoutSeconds</code> 里声明自己的默认时长
        （<code>null</code> = 5 分钟、<code>0</code> = 一直等、<code>&gt;0</code> = 秒数）。
        优先级是「工具页里用户配的」 &gt; 「插件声明的」 &gt; 「默认 5 分钟」；入参 <code>timeout</code>
        与插件声明的值取大者。插件声明的负数按默认处理、超过 24 小时按上限处理，两种情况都会记一条警告。

        <div class="note">
        两个内置工具设为「一直等待」且不接受修改：<code>ask_user_question</code> 与
        <code>ask_user_continue</code> 等的是人，人要多慢有多慢，超时等于把一张还在作答的卡片变成失败。
        <code>task</code> 默认 30 分钟 —— 它跑的是整个子 Agent 的一轮任务，按 5 分钟算必然被腰斩。
        </div>

        <h3>超时之后会发生什么</h3>

        <p>宿主<b>停止等待</b>并把「超时」作为该 tool_call 的结果回给模型，同时取消指令已经发出去了。
        但要有心理准备：<b>取消只是请求，不是保证</b>。</p>

        <ul>
          <li><code>bash</code>、脚本工具：进程树会被杀掉，副作用到此为止；</li>
          <li>纯 C# 的工具（插件、MCP 客户端）：只有它自己响应了取消令牌才会真的停下。
              停在同步 IO 或原生调用里的，会继续跑到自己结束，结果被丢弃。</li>
        </ul>

        <p>所以真正危险的不是「等到超时」，而是「超时了但活还是干完了」：
        模型拿到的是失败，磁盘上却是已改过的文件，它接着往下做就建立在错误前提上。
        对装包、编译、全量测试这类明知要很久的工具，与其让它撞 5 分钟，
        不如在工具页显式放宽或设成一直等待。</p>

        <h2>工具集</h2>

        <p>工具集只是给人看的分类，不改变工具能不能被用到：</p>

        <ul>
          <li><b>全部</b>与<b>内置</b>是自动集合，成员是算出来的，不能手工增删；</li>
          <li>自建集合与工具是多对多：一个工具可以同时属于多个集合，也可以一个都不属于
              （它仍然出现在「全部」里）。</li>
        </ul>

        <p>删掉一个工具时，它与集合的关联会一起消失，集合本身不受影响。</p>

        <h2>让 AI 自己建工具</h2>

        <p>「设置 → 允许 AI 创建工具与技能」打开后，模型可以用 <code>create_tool</code>
        写一份脚本并登记成工具。新建出来的工具<b>默认是停用的</b> —— 需要到工具页勾选启用，
        这样「模型自己写的代码」不会在你没看过之前就跑起来。它会被标上「AI 创建」。</p>
        """;

    // ---- 技能 ----

    private static string Page_SkillFormat() => """
        <p class="lead">
        技能是「这类任务该怎么做」的知识包，不是可调用的函数。它只有一份文件：
        <code>&lt;数据根&gt;/skills/&lt;技能id&gt;/SKILL.md</code>，目录名就是技能 id。
        </p>

        <h2>文件长什么样</h2>

        <pre><code>---
        name: review-sql
        alias: SQL 审查
        description: 检查一条 SQL 的索引使用、隐式转换与全表扫描风险。用户贴出 SQL 或问「这条语句为什么慢」时使用。
        ---

        ## 什么时候用

        用户给出一条 SQL 并关心性能时。

        ## 怎么审

        1. 先看 WHERE 里出现的列，逐个确认有没有可用的索引；
        2. 再看有没有对列做函数运算或隐式类型转换，那会让索引失效；
        3. 最后看执行计划里有没有全表扫描，有的话说明具体是哪一段导致的。

        ## 输出要求

        按「问题 / 原因 / 改法」三段写，每条都要给出 SQL 片段作为依据。</code></pre>

        <h2>frontmatter 的四项</h2>

        <ul>
          <li><code>name</code>：技能名，同时也是 <code>load_skill</code> 的调用名。缺省时用目录名。</li>
          <li><code>alias</code>：给人看的显示名。只影响界面，不参与提示词注入。</li>
          <li><code>description</code>：<b>最重要的一项</b>。模型靠它判断「这件事该不该用这个技能」，
              所以写「做什么 + 什么时候用」，而不是「这是一个关于 X 的技能」。</li>
          <li><code>origin</code>：只有 AI 自建时才写 <code>agent</code>；缺省按人工创建处理。</li>
        </ul>

        <div class="note">
        元数据必须是<b>单行</b>。frontmatter 是按行解析的，描述里换行会把后面的内容整段吞掉。
        宿主新建技能时会把多行描述压成一行，手写时也要守同样的规矩。
        </div>

        <h2>正文</h2>

        <p><code>---</code> 之后的全部内容就是正文，会被原样注入 system 提示词。
        它是给模型的说明书，不是给人看的文档站：写清步骤、判断标准和输出格式，比写得长更有用。
        正文越大，注入的 token 成本越会跟着每一轮请求一起付。</p>

        <h2>没有 frontmatter 也行</h2>

        <p>整份文件直接写正文，宿主会把全文当成技能内容、用目录名当技能名。
        适合先跑起来再补元数据，但那样模型只有在它被加载之后才知道这是什么技能。</p>

        <h2>名字的规则</h2>

        <p>目录名（技能 id）必须匹配 <code>^[a-z0-9][a-z0-9._-]{0,63}$</code>：
        小写字母或数字开头，只含小写字母、数字、<code>.</code> <code>_</code> <code>-</code>。
        它同时是 <code>load_skill</code> 的调用名，所以不能用中文或大写。</p>
        """;

    private static string Page_SkillInjection() => """
        <p class="lead">技能有两种进入上下文的方式，区别只有一个：注入多少。</p>

        <h2>allowed：只给元数据</h2>

        <p>把 <code>name: description</code> 放进 system 的「可用技能」清单。
        模型看到清单，判断这次用得上，就调用 <code>load_skill</code> 把全文拉进来。
        成本低，适合「装了但不一定每次都用」的技能。</p>

        <h2>loaded：直接注入全文</h2>

        <p>SKILL.md 正文每一轮都躺在 system 里。适合「这个会话就是干这件事」的场景
        （整个会话都在做代码审查）。代价是每一轮都要为它付一次 token。</p>

        <p>同一个技能可以同时以两种状态出现，互不影响。会话里的这套状态记在
        <code>chat_sessions.enabled_skills</code>，每项是 <code>{ id, status }</code>，
        status 取 <code>allowed</code> / <code>loaded</code>。</p>

        <h2>技能与子 Agent</h2>

        <p>派发子 Agent 时只继承 <b>allowed</b>，<b>loaded 不继承</b>：全文注入属于发起派发的那个会话，
        抄进子会话等于同一段话被计费两次。子 Agent 需要时会自己 <code>load_skill</code>。</p>

        <h2>让 AI 自己写技能</h2>

        <p>打开「设置 → 允许 AI 创建工具与技能」后，模型可以用 <code>create_skill</code>
        建一份 SKILL.md（正文上限 24 KB，名字规则同上）。
        这样建出来的技能在 frontmatter 里带 <code>origin: agent</code>，界面上会标「AI 创建」——
        技能是全文注入的，用户该知道哪几段不是自己写的。</p>

        <h2>技能不是工具</h2>

        <p>技能不会出现在工具调用列表里，模型没法「调用」它：它改变的是模型「怎么做」，
        不是「能做什么」。要给它一个新的可执行能力，去写<a href="tool-01-script.html">脚本工具</a>。</p>
        """;

    private static string Page_Setup() => """

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
        """;

    private static string Page_Manifest() => """
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
              <td>每轮往 system 提示词末尾追加内容（不限长度，带来源标记）</td></tr>
          <tr><td><code>chat.observe</code></td><td><code>Chat</code></td>
              <td>订阅会话事件：轮次开始/增量/工具调用/完成/失败/活动变化</td></tr>
          <tr><td><code>chat.control</code></td><td><code>Sessions</code></td>
              <td>主动发起一轮、停止、改权限档位、压缩、读最近消息、列会话、开自己的会话区、
                  查用量与模型能力、读写自己会话的工具集、登记交互桥</td></tr>
          <tr><td><code>notice.control</code></td><td><code>Notifications</code></td>
              <td>决定自己派出去的任务跑完时，那条提醒弹不弹、留多久（默认自动收起）</td></tr>
          <tr><td><code>workspace.delegate</code></td><td><code>Workspaces</code></td>
              <td>查看工作空间、列工作空间会话、在工作空间里新建会话（把任务派到用户真实的开发目录）</td></tr>
          <tr><td><code>ui.window</code></td><td><code>Windows</code></td><td>建自己的窗口（透明/置顶/记忆位置由宿主配好）</td></tr>
          <tr><td><code>ui.slot</code></td><td><code>Slots</code></td><td>往宿主界面的插槽插控件、加新标签页入口卡片</td></tr>
          <tr><td><code>ui.tab</code></td><td><code>Tabs</code></td>
              <td>在主窗口里开自己的标签页（与会话标签并排，不另开窗口）</td></tr>
          <tr><td><code>ui.settings</code></td><td>实现 <code>IPluginSettingsProvider</code></td>
              <td>在插件页里提供一页自己的设置界面（开关、清理、概览这类轻量内容）</td></tr>
          <tr><td><code>tools.provide</code></td><td><code>Tools</code></td><td>注册自己的工具，让模型能调用</td></tr>
          <tr><td><code>conversation.dock</code></td><td><code>Dock</code></td>
              <td>把某个会话的对话视图借到自己的界面里显示（内置浏览器那种「网页 + 对话」并排）</td></tr>
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
          "capabilities": ["ui.window", "ui.tab", "chat.observe", "chat.control"],
          "platforms": ["windows", "macos"],
          "requires": ["htaci.core"],
          "conflicts": ["other.cat"]
        }</code></pre>
        """;

    private static string Page_Basics() => """
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
        """;

    private static string Page_PromptChat() => """
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

        <div class="note">
        这就是插件<b>给模型追加 system 提示词的唯一入口</b> —— 宿主没有「直接改档案提示词」的口子，
        也不会让你覆盖它，你只能往后追加，并且被来源标记约束。
        <b>按会话区分</b>：<code>BuildAsync</code> 拿到的 <code>PromptContext</code> 里有
        <code>SessionId</code> 与 <code>ModelId</code>，可以「只在这个会话里加」「换了模型就不加」；
        返回 <code>null</code> 表示这一轮什么都不加。
        </div>

        <table>
          <tr><th>约束</th><th>值</th></tr>
          <tr><td>贡献长度</td><td>不限，写多少拼多少（截断已移除）</td></tr>
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
                        // e.TurnId 认领这次派发：同一会话上可能还有用户自己聊的一轮；
                        // e.IsPluginInitiated 直接标出「这一轮是我经 Sessions 派的」
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
        """;

    private static string Page_Sessions() => """
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
              工具链会如实把这些回给模型 —— 这一轮照常跑完，不会卡住。
              想让机器人在手机上问一句，用下面的<b>交互桥</b>（<code>SetInteractionAsync</code>）。</li>
          <li>同一会话已有任务在跑时返回 <code>Busy</code>，不排队也不插队。</li>
          <li>传进去的 <code>ct</code> 只作用于<b>发起之前</b>；要停掉已经在跑的那一轮用 <code>CancelAsync</code>。</li>
        </ul>

        <h2>任务提醒：留多久、要不要弹</h2>
        <p>插件派出去的那一轮跑完，宿主会在屏幕右下角弹一条提醒（「任务××已完成 / 出错」）。
        默认是<b>弹一会儿自动收起</b>：完成 8 秒、出错 10 秒；等你动手的审批与提问给 30 秒，
        鼠标停在上面时倒计时暂停。</p>
        <p>要改成常驻、或者干脆不弹，清单里声明 <code>notice.control</code>，然后在
        <code>InitializeAsync</code> 里声明一次：</p>
        <pre><code>        // Auto（默认）：弹出后过一会儿自动收起
        // Persistent：常驻，直到用户自己点关闭
        // Off：完全不弹 —— 由插件自己把结果送到用户眼前
        context.Notifications?.SetMode(PluginNoticeMode.Persistent);</code></pre>
        <ul>
          <li>只管<b>你这个插件派的那几轮</b>：用户自己在窗口里聊的一轮、别的插件派的任务都不受影响。</li>
          <li><b>不写盘</b>：这是「这次运行想怎么提醒」。插件自己的用户设置该由插件存
              （<code>context.Storage</code> / <code>GetDataDirectory()</code>），下次启动再声明一次。</li>
          <li>只影响之后弹的提醒，已经弹在屏上的那条不会中途改主意。</li>
          <li>不声明 <code>notice.control</code> 也能正常用 —— 默认行为本来就生效，这个能力只是「允许你改」。
              <code>Off</code> 也不等于「没发生过」：会话里照常有未读提示，只是不往桌面上弹浮窗。</li>
        </ul>

        <h2>其余出口</h2>
        <pre><code>await context.Sessions!.CancelAsync(sessionId);                 // 等价于点「停止」
        await context.Sessions.SetPermissionModeAsync(sessionId, PluginPermissionMode.Loose);
        await context.Sessions.CompressAsync(sessionId);               // 等价于点「压缩」（全压）
        await context.Sessions.CompressAsync(sessionId, 3);            // 末尾保留 3 轮原文，只压更早的
        var recent = await context.Sessions.GetRecentMessagesAsync(sessionId, 20);</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>SessionMessageSnapshot</code></td>
              <td>只读快照：<code>Role</code>（user/assistant/tool/error/agent）、
              <code>Content</code>、<code>Thinking</code>、<code>Status</code>、
              <code>SequenceNumber</code>、<code>CreatedAt</code>。</td></tr>
          <tr><td><code>CompressAsync(sessionId, keepRecentTurns)</code></td>
              <td>压缩上下文。<code>keepRecentTurns = 0</code>（默认）全压；给 N 就<b>末尾保留 N 轮原文</b>，
              只把更早的部分压成摘要 —— 最近几轮的工具调用、报错原文、文件路径都还在。
              切点落在第 N 条用户消息上，不会切出半轮；可压内容不足 N 轮时返回 <code>false</code> 且什么都不压。
              返回 <code>false</code> 还可能是「已经在压了」或「没配模型」。</td></tr>
          <tr><td><code>PluginPermissionMode</code></td>
              <td><code>Strict</code> 严格 / <code>Normal</code> 普通 / <code>Loose</code> 宽松 / <code>Free</code> 自由。</td></tr>
        </table>

        <div class="warn">
        插件被禁用时，宿主会<b>停掉它发起、还没跑完的那一轮</b>，并拒绝之后的所有调用。
        所以「用户点了禁用，插件还在后台发消息」这件事不会发生。
        </div>

        <h2>插件自己的会话区</h2>
        <p>需要「自己的对话，不挤进用户的会话列表」时，让宿主给你开一个插件会话。
        它是一行 <code>plugin_sessions</code>，<b>只属于你这一个插件</b>：别的插件看不到，用户也看不到。</p>
        <pre><code>var id = await context.Sessions!.CreateSessionAsync("我的面板") ?? return;
        await context.Sessions.SendAsync(id, "先看一下当前状态");

        foreach (var s in await context.Sessions.ListMySessionsAsync())
            context.Log.Info($"{s.SessionId} {s.Title}");

        await context.Sessions.DeleteSessionAsync(id);   // 只能删自己建的</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>CreateSessionAsync</code></td>
              <td>新建属于<b>本插件</b>的会话，返回会话 id（失败返回 <code>null</code>）。默认不给任何工具与 MCP、权限档位为普通。</td></tr>
          <tr><td><code>ListMySessionsAsync</code></td>
              <td>列出本插件自己建的会话（最近活动在前）。别的插件建的会话看不到。</td></tr>
          <tr><td><code>DeleteSessionAsync</code></td>
              <td>软删除自己的会话，正在跑的一轮会先被停掉。<b>传别人的会话 id 一律返回 false</b>，不会误删用户会话。</td></tr>
        </table>

        <h2>用量、模型能力与工具集</h2>
        <pre><code>var usage = await context.Sessions!.GetUsageAsync(id);      // 一次查库，不加载历史
        if (usage is { } u)
            context.Log.Info($"输入 {u.InputTokens} / 输出 {u.OutputTokens} / 往返 {u.RequestCount} 次");

        var model = await context.Sessions.GetModelInfoAsync(id);
        var directImage = model?.SupportsVision == true;         // false 时图片只能落成路径交给 view_image
        var percent = model is { ContextWindow: > 0 } && usage is { } s
            ? s.ContextUsed * 100.0 / model.ContextWindow
            : double.NaN;                                       // 窗口未知就显示「未知」，别算成 0%

        var all = await context.Sessions.ListAvailableToolsAsync();
        var mine = await context.Sessions.GetSessionToolsAsync(id);          // null = 会话不存在
        await context.Sessions.SetSessionToolsAsync(id, new[] { "内置工具id", "plugin:xxx:yyy" });</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>GetUsageAsync</code></td>
              <td>累计输入/缓存命中/输出、API 往返次数、<b>当前上下文占用与窗口</b>。
                  <code>ContextWindow &lt;= 0</code> = 模型没声明上限（按「未知」显示）。
                  占用口径与界面上的用量环是同一份实现，两边不会对不上。</td></tr>
          <tr><td><code>GetModelInfoAsync</code></td>
              <td>会话用的模型与能力（窗口、最大输出、是否支持视觉/思考）。
                  会话没选模型或模型已被删时回退宿主默认模型，<code>IsFallbackToHostDefault</code> 会告诉你发生了回退。</td></tr>
          <tr><td><code>ListAvailableToolsAsync</code></td>
              <td>宿主当前可登记的全部工具（内部哨兵工具不在里面），带危险等级与来源
                  （<code>builtin</code> / <code>script</code> / <code>mcp</code> / <code>plugin</code>）。</td></tr>
          <tr><td><code>GetSessionToolsAsync</code> / <code>SetSessionToolsAsync</code></td>
              <td>读写会话勾选的工具 id。<b>只能改你自己的插件会话</b>：普通会话与工作空间会话的工具是
                  用户在界面上选的，改它们一律返回 <code>false</code>。写入后下一轮请求就用新工具集。</td></tr>
        </table>

        <h2>交互桥：没窗口时也能问用户</h2>
        <p>宿主原来的规则是「没有窗口 → 审批一律拒绝、提问返回用户不在」，所以机器人只能跑只读工具。
        登记一个交互桥之后，这些交互改成<b>由你发给用户等回复</b>：</p>
        <pre><code>await context.Sessions!.SetInteractionAsync(id, new MyBridge());   // 传 null 撤销

        sealed class MyBridge : IPluginInteractionHost
        {
            public async Task&lt;bool&gt; ApproveAsync(PluginToolApprovalRequest r, CancellationToken ct)
                =&gt; await AskByChat($"要执行 {r.ToolName}（{r.DangerLevel}），允许吗？", ct);   // 超时返回 false

            public async Task&lt;IReadOnlyList&lt;PluginAskAnswer&gt;?&gt; AskAsync(
                IReadOnlyList&lt;PluginAskQuestion&gt; questions, CancellationToken ct)
                =&gt; await ShowQuestionCard(questions, ct);       // 超时返回 null = 没答

            public Task&lt;bool?&gt; WaitForUserAsync(string title, string instructions, CancellationToken ct)
                =&gt; WaitForScanAsync(title, instructions, ct);    // 用户取消返回 null
        }</code></pre>
        <ul>
          <li><b>窗口优先</b>：会话窗口开着时交互仍然交给界面，你的桥不会被调用（不会两处同时弹卡）。</li>
          <li><b>超时自己管</b>：用户可能永远不回。等不到就返回 <code>false</code> / <code>null</code>，
              这一轮照常跑完而不是卡死；<code>ct</code> 是「用户按了停止」，实现了就能立刻退出。</li>
          <li><b>别忘了摘桥</b>：插件被禁用时宿主会清掉，但你自己在会话不再需要时传 <code>null</code> 更干净。</li>
        </ul>

        <h2>在工作空间里派任务</h2>
        <p>需要目录、工具、MCP、模型都在的「真活」，派到用户的工作空间里跑（需声明
        <code>workspace.delegate</code>）：</p>
        <pre><code>var ws = await context.Workspaces!.ListWorkspacesAsync();          // 含会话数、最近活动
        var sessionId = await context.Workspaces.CreateWorkspaceSessionAsync(ws[0].Id, "修一下这个报错");
        await context.Sessions!.SendAsync(sessionId, "跑一下测试，把失败的用例修好");

        var runs = await context.Workspaces.ListWorkspaceSessionsAsync(ws[0].Id, take: 5);</code></pre>
        <ul>
          <li>新建的会话<b>继承工作空间的配置</b>（模型、工具、技能、MCP、权限档位），
              与用户在界面上「在工作空间里新建会话」完全一样，所以它也会出现在用户的会话列表里。</li>
          <li>工作空间的配置、删会话都不开放：那是用户在界面上的选择。</li>
          <li>这一轮的事件带 <code>TurnId</code> 与 <code>IsPluginInitiated</code>，
              派发多个任务时靠它们认领结果（见「观察会话」那页）。</li>
        </ul>

        <h3>数据存在哪：会话一行、消息走 chat_message</h3>
        <table>
          <tr><th>表</th><th>存什么</th></tr>
          <tr><td><code>plugin_sessions</code></td>
              <td>会话本身：<code>id</code>、<code>plugin_id</code>（归属）、标题、模型、思考档位、
                  会话提示词、工具与 MCP 列表、权限档位、最近活动时间、删除标记。</td></tr>
          <tr><td><code>chat_message</code></td>
              <td>这条会话的全部消息，<code>session_id</code> 就是插件会话 id。
                  <b>与普通会话、工作空间会话、子 Agent 共用一张消息表</b>，所以发一轮、读历史、压缩、
                  工具调用落库的写法与普通会话完全一样。</td></tr>
        </table>

        <ul>
          <li>会话 id 自带 <code>plugin-</code> 前缀，与另外三种会话共用一套 id 空间但不会撞车；
              <b>不要假设它的格式</b>，原样存下来用即可。</li>
          <li>不写 <code>chat_sessions</code>：用户的会话列表、默认模型、「已删除会话」恢复页都是整表扫描，
              所以插件会话<b>不会冒进用户界面</b>。</li>
          <li>宿主按 id 前缀判断去查哪张表，你不用关心解析过程，<code>SendAsync</code> 传 id 就行。</li>
        </ul>

        <h3>默认值与生命周期</h3>
        <ul>
          <li><b>标题</b>：留空就是「插件会话」；传了标题会去空白并截到 100 字。
              插件的会话标题<b>不会被自动话题命名改写</b>（自动命名只认「新会话」这个占位标题）。</li>
          <li><b>模型</b>：新建时为空，第一轮发送时用宿主的默认模型，跑完后把实际用的模型记在这条会话上，
              之后一直用它；模型被删了会再次回退到默认模型，都没有就返回 <code>NoModel</code>。</li>
          <li><b>工具与 MCP</b>：默认都是空的 —— 空的会话等于模型没有任何工具，什么都干不了。
              要给自己这条会话配工具用 <code>SetSessionToolsAsync</code>（只能改自己的会话）；
              MCP 服务与提示词仍然只有用户能在宿主界面里改。</li>
          <li><b>工具基准目录</b>：本插件的数据目录（<code>context.Storage.DataDirectory</code>，
              即 <code>plugin-data/&lt;插件id&gt;</code>），不会掉到程序目录里去写文件。</li>
          <li><b>system 提示词</b>：宿主自动生成，比普通会话短（身份 + 安全底线 + 工具规矩 + 环境），
              其中会写明「这条会话是插件 X 开的独立会话」。</li>
          <li><b>删除</b>：<code>DeleteSessionAsync</code> 是软删除，会话行标删除、消息保留；
              只有会话行被物理删除时，宿主才连带清掉它的消息（插件侧没有物理删除接口）。</li>
          <li><b>插件被禁用 / 卸载不删库里的会话</b>：数据一直留着，重新启用后
              <code>ListMySessionsAsync</code> 照样能拿回来 —— 所以别在会话里存密钥之类的东西。</li>
        </ul>

        <div class="note">
        <b>用户看不到，所以入口要你自己给。</b>插件会话不出现在会话页，用户没法点开它。
        要把这个会话区显示出来，用 <code>Dock</code>（需声明 <code>conversation.dock</code>）：
        <pre><code>var dock = await context.Dock!.AttachAsync(id, "我的面板");
        if (dock.Success) myPanel.Content = dock.View;</code></pre>
        借来的就是<b>真正那一份</b>会话视图（和普通会话同一套），同一条会话同一时刻只能被一个插件占着。
        只想读文字、不需要对话界面时，用 <code>GetRecentMessagesAsync</code> 自己画。
        </div>

        <h2>典型用法：点一下就开口</h2>
        <pre><code>// 桌宠窗口上放一个按钮
        button.Click += async (_, _) =&gt;
        {
            var result = await context.Sessions!.SendAsync(_currentSessionId, "在吗？");
            if (result.Status != SessionSendStatus.Sent)
                context.Log.Warn($"发起失败：{result.Error}");
        };</code></pre>
        """;

    private static string Page_Ui() => """
        <p class="lead"><code>ui.window</code> 给你一个配好的窗口；<code>ui.tab</code> 让你在主窗口里开标签页；<code>ui.slot</code> 让你往宿主界面里插内容。</p>

        <div class="note">
        <b>先选形态：</b>桌宠、悬浮球那类「属于桌面、只在旁边待着」的东西用<b>独立窗口</b>（下面第一节）；
        浏览器、编辑器、看板那类「用户会来回切」的工作区用<b>标签页</b>（第二节）——
        它与会话标签并排，共享同一个窗口尺寸与切换习惯，开成独立窗口反而多一层 Alt+Tab。
        </div>

        <h2>建窗口（<code>ui.window</code>）</h2>
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

        <h2>在主窗口里开标签页（<code>ui.tab</code>）</h2>
        <pre><code>var tab = context.Tabs!.OpenTab("浏览器", new BrowserControl());

        tab.SetTitle("浏览器 · 已登录");   // 标签栏上的文字立刻更新
        tab.Activate();                   // 切过去，等价于用户点了一下
        tab.Closed += () =&gt; _current = null;   // 用户点 × 也会触发</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>OpenTab(title, content)</code></td>
              <td>开一个标签页，标题为空时用插件 id。返回的句柄用来改标题 / 切过去 / 关掉。</td></tr>
          <tr><td><code>OpenTabs</code></td>
              <td>当前还开着、属于<b>本插件</b>的标签页快照。别的插件的看不到。</td></tr>
          <tr><td><code>SetTitle</code> / <code>Activate</code> / <code>Close</code></td>
              <td>标题随内容变就自己调；<code>Close</code> 重复调用无副作用。</td></tr>
          <tr><td><code>Closed</code> 事件</td>
              <td>标签页关掉时触发（用户点 × 也算）。<b>在 UI 线程上触发。</b></td></tr>
        </table>

        <ul>
          <li><b>同一个 <code>Control</code> 实例不能同时挂在两个标签页上</b>——要开第二个就再建一份。</li>
          <li>只能在 UI 线程调用；主窗口还没建好时 <code>OpenTab</code> 会抛
              <code>InvalidOperationException</code>（别在 <code>InitializeAsync</code> 里抢先开，那时候主窗口可能还没显示）。</li>
          <li>插件被禁用时，宿主先关掉你的全部标签页、再卸载程序集，所以不需要自己记着关。</li>
          <li>标签页关掉后句柄就失效了，再用它改标题不会有任何效果 —— 该监听的信号是 <code>Closed</code>。</li>
          <li>会话切换、窗口布局这些宿主内部约定不开放：你能做的只有上面这四件事。</li>
        </ul>

        <h2>在插件页里放一页设置（<code>ui.settings</code>）</h2>

        <p><b>为什么用它，而不是另开一个标签页</b>：插件页是用户管理插件的固定入口。
        「清缓存 / 重置数据 / 开关」这类操作，本质上是「管理这个插件」，应该和启用、停用、版本号放在一起。
        另开标签页的代价是入口会飘 —— 标签页一关，用户就得重新找一遍。</p>

        <pre><code>public sealed class MyPlugin : IPlugin, IPluginSettingsProvider
        {
            private IPluginContext? _context;
        
            public Control CreateSettingsView()
            {
        // 每次进这个分页都会调用一次，别在这里攒状态
        return new MySettingsView(_context!);
            }
        }</code></pre>

        <p>清单里同时声明能力：</p>
        <pre><code>"capabilities": ["tools.provide", "ui.settings"]</code></pre>

        <table>
          <tr><th>约定</th><th>说明</th></tr>
          <tr><td>只做一页</td><td>宿主只给一个分页，不做多级导航。条目多的管理界面
              （逐条的历史、站点列表）仍应放在插件自己的标签页里，这一页只留概览与入口按钮。</td></tr>
          <tr><td>宽度受限</td><td>它住在插件详情里，横向空间比整个窗口小得多。
              内容长了用换行或省略号，不要指望横向滚动。</td></tr>
          <tr><td>会被反复创建</td><td>每次翻到这个分页都会重新要一份控件。
              持久状态放 <code>Storage</code>，瞬时状态（「正在计算…」）留在控件里即可。</td></tr>
          <tr><td>不要内嵌 WebView 类控件</td><td>它们在宿主的滚动容器里会被裁切，还会盖住页面上其他内容
              （Same Airspace 限制）。</td></tr>
          <tr><td>不要弹窗确认</td><td>插件页里弹窗会挡住整页，而且拿不到宿主的弹窗样式。
              危险操作用「点一次变红、再点一次执行」的两段式按钮。</td></tr>
          <tr><td>抛异常不会连累插件</td><td>这一页会显示「设置界面显示失败：原因」，插件其余功能照常。</td></tr>
          <tr><td>插件没运行时不会调用</td><td>宿主显示「插件未运行」。你的设置界面不需要自己处理这个状态。</td></tr>
        </table>

        <h2>往界面里插控件（<code>ui.slot</code>）</h2>
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
          <tr><td><code>PluginSlots.ConversationSidebar</code>（<code>conversation.sidebar</code>）</td>
              <td>会话页右侧（消息区右边），空时整列隐藏</td><td>网页预览、笔记那类「与对话并排看」的内容</td></tr>
        </table>

        <pre><code>context.Slots?.AddControl(PluginSlots.ConversationHeader, () =&gt; new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F3FF")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Child = new TextBlock { Text = "插件在看着你" },
        });</code></pre>

        <h2>输入框工具栏 + 自己的菜单（<code>conversation.input.toolbar</code>）</h2>
        <p>这个插槽在内建按钮（附件 / 工具 / 技能 / MCP / 权限 / 更多）<b>右边，中间有一条竖线</b>，
        用户一眼就能分出「左边是宿主自带的，右边是插件给的」。横排，适合放一张图标按钮，
        点开是插件自己的菜单 —— <b>菜单长什么样由你决定</b>（<code>Flyout</code> 里放的是你给的控件）。</p>

        <pre><code>context.Slots?.AddControl(PluginSlots.ConversationInputToolbar, () =&gt;
        {
            var button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Content = new TextBlock
                {
                    Text = "\uE825",                        // more_horizontal_24_regular
                    FontFamily = PluginFonts.IconFont,      // 宿主的图标字体
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            // 菜单就是任意控件：这里是「本会话开了哪些网页 + 点一下就切过去」
            var menu = new StackPanel { Spacing = 2, Width = 240 };
            foreach (var page in BrowserSession.OpenPages)
            {
                var item = new Button
                {
                    Content = new TextBlock { Text = page.Title, FontSize = 12 },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                item.Click += (_, _) =&gt; BrowserSession.Activate(page);
                menu.Children.Add(item);
            }

            button.Flyout = new Flyout { Content = menu };
            return button;
        }, order: 10);</code></pre>

        <ul>
          <li><b>只能在 UI 线程上用</b>（<code>InitializeAsync</code> 就在 UI 线程上）。</li>
          <li><b>工厂会被反复调用</b>（切走再切回会话就重建一次）。菜单内容要在工厂里现读当前状态，
              别在闭包里缓存「第一次那份列表」。</li>
          <li>图标字体用 <code>PluginFonts.IconFont</code>（见下一节），别自己 new 字体族。</li>
          <li>没有插件往这个插槽里放东西时，整块（含那条竖线）自动隐藏，不必自己处理空状态。</li>
        </ul>

        <h3>图标与图标字体（<code>PluginFonts.IconFont</code>）</h3>

        <p>宿主的图标不是图片，是一套打包进来的图标字体（Fluent System Icons，MIT）。
        <b>插件可以直接用宿主这份，不用自己带字体文件</b>：插件和宿主跑在同一个进程里，
        <code>PluginFonts.IconFont</code> 拿到的就是宿主界面上用的那个 <code>FontFamily</code>。</p>

        <pre><code>new TextBlock
        {
            Text = "\uF10A",                     // add_24_regular
            FontFamily = PluginFonts.IconFont,   // 宿主的图标字体
            FontSize = 14,
        }</code></pre>

        <table>
          <tr><th>要点</th><th>说明</th></tr>
          <tr><td>别用字面符号</td><td><code>← → ⚙ ✕</code> 是「字符」而不是图标：字重、基线、宽度全由文本字体决定，
              和旁边宿主的按钮放一起就像打错字。要图标就用这套字体。</td></tr>
          <tr><td>码位不能猜</td><td>Fluent 的私用区码位是算出来的，跟语义顺序无关；<b>也不能照抄
              <code>Segoe Fluent Icons</code> 的码位</b>——两套字体毫无关系，抄错会显示成完全不相干的图标或方框。
              已用到的码位见仓库 <code>docs/icon-glyphs.md</code>；要新的就查字体文件的 cmap。</td></tr>
          <tr><td>写转义，别粘贴字形</td><td>私用区字符在多数编辑器里不可见。C# 写 <code>"\uF10A"</code>，
              XAML 写 <code>&amp;#xF10A;</code>，否则后来没人看得出那是什么图标。</td></tr>
          <tr><td>入口卡片只需码位</td><td><code>LauncherCard.Glyph</code> 只填码位字符串（如 <code>"\uE9EA"</code>），
              字体由宿主负责，不要把 <code>FontFamily</code> 塞进去。</td></tr>
          <tr><td>取不到会退化成方框</td><td>宿主没有这个资源、或字体文件读不出来时返回默认字体，
              图标显示成方框但<b>界面照常画出来</b>——不会因为一个字形炸掉整页。</td></tr>
          <tr><td>只在 UI 线程用</td><td>它要读 <code>Application.Current</code> 的资源，别在后台线程上取。</td></tr>
          <tr><td>自己打包的字体也照旧</td><td>要用别的字体（手写体、衬线体…）就把字体文件放进插件目录、
              用 <code>Resources.ResolvePath</code> 解析后自建 <code>FontFamily</code>；
              <code>PluginFonts</code> 只解决「和宿主一样的图标」。</td></tr>
        </table>

        <h3>按会话区分内容（<code>PluginSlotContext</code>）</h3>
        <p>同一个插槽会被<b>每个会话各创建一份</b>。插件要是不知道「我这一份是谁的」，
        就只能把跟会话相关的按钮做成全局的 —— 用户点开菜单也看不出哪个是当前会话的、点了给了谁。</p>

        <pre><code>var context = PluginSlotContext.Find(this);   // 从我的控件往上找
        if (context is null)
        {
            return new TextBlock { Text = "拿不到插槽上下文" };
        }
        
        var sessionId = context.SessionId;   // 可能为 null（新建会话页、与会话无关的插槽）
        if (sessionId is null)
        {
            // 需要会话的功能一律禁用并说明原因，<b>不要</b>猜一个会话
            button.IsEnabled = false;
        }</code></pre>

        <ul>
          <li><code>Find</code> 要在<b>控件已经挂上可视树之后</b>调用（例如 <code>AttachedToVisualTree</code>
              或菜单打开时）。工厂刚被调用时控件还没进树，那时拿到的是 <code>null</code>。</li>
          <li>宿主把它放在插槽外面那层壳的 <code>DataContext</code> 上，所以不覆盖自己的
              <code>DataContext</code> 也能直接读；<code>Find</code> 两种情况都能找到。</li>
          <li><code>SessionId</code> 为 <code>null</code> 表示「此刻没有会话」：新建会话页上就是这种状态。
              这时把依赖会话的入口置灰，别假装有会话 —— 那等于把权限交给一个用户没指定的地方。</li>
        </ul>

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

        <h2>把对话借到自己界面里（<code>conversation.dock</code>）</h2>

        <p><b>什么时候用</b>：你的界面需要「一边看自己的东西、一边和 AI 聊」，
        而用户已经在这个会话里聊到一半 —— 内置浏览器是最典型的例子。
        <b>不要</b>自己照着 <code>Sessions</code> 接口再搭一份对话界面：一个会话只能有一份视图，
        复制出来的那份是快照，用户点它会像丢消息。</p>

        <pre><code>var result = await context.Dock!.AttachAsync(sessionId, "内置浏览器");
        if (!result.Success)
        {
            statusText.Text = result.Error;   // 已经是给人看的一句话，可直接显示
            return;
        }

        sidebar.Child = result.View;          // 这一份就是用户的会话窗口，原样搬过来的

        // 用户在我的面板里关掉了它
        context.Dock.Release(sessionId);</code></pre>

        <table>
          <tr><th>成员</th><th>说明</th></tr>
          <tr><td><code>AttachAsync(sessionId, hostLabel, ct)</code></td>
              <td>把视图从原宿主摘下来交给你。成功时 <code>View</code> 已经是「没有父级」的控件，
                  可以直接放进你的容器（<code>ContentControl.Content</code>、<code>Panel.Children</code> 都行）。</td></tr>
          <tr><td><code>hostLabel</code></td>
              <td>「谁占着它」。会话页会显示「这个会话正在『内置浏览器』中显示」。
                  写用户看得懂的名字，不要写插件 id。</td></tr>
          <tr><td><code>Release(sessionId)</code></td>
              <td>还回去：视图从你的容器里摘下来，交回会话页 / 缓存。重复调用无副作用。</td></tr>
          <tr><td><code>Released</code> 事件</td>
              <td>视图被<b>宿主</b>收回时触发（用户在会话页点了「召回」）。收到后把你那边的空容器收掉。</td></tr>
        </table>

        <div class="warn">
        <b>占位由宿主画，你不用管。</b>借走期间会话页会自己显示「正在 X 中显示」和召回按钮。
        你只负责说清楚自己叫什么 —— 这样所有插件的占位与召回体验是一致的，
        你也不必知道会话页长什么样。<b>要出占位提示就去调 <code>AttachAsync</code>，别自己建对话框。</b>
        </div>

        <ul>
          <li><b>一个会话同一时刻只能被一个插件占着。</b>已经被别人占了时返回失败，
              <code>Error</code> 里写着是谁（如「这个会话正在「内置浏览器」中显示。」）。</li>
          <li><b>同一个会话再借一次是幂等的</b>：拿回来的是同一个视图（面板重建时直接复用）。</li>
          <li><b>只能在 UI 线程上调用</b>；<code>AttachAsync</code> 会等你到能拿到视图为止（会话可能要先读库）。</li>
          <li>插件被禁用时宿主<b>自动收回</b>全部借出的视图，不需要自己记得 <code>Release</code>。</li>
          <li>借走的是<b>用户那一份真窗口</b>：滚动位置、正在跑的一轮、输入框草稿都在，因此界面宽度、
              尺寸要给它留够地方（它在会话页里通常是主区域那么宽）。</li>
          <li>侧栏还不够用时，可以再往 <code>conversation.sidebar</code> 里放自己的控件，
              两者搭配就是「左对话右网页」或「左网页右对话」。</li>
        </ul>
        """;

    private static string Page_Tools() => """
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
                // TimeoutSeconds = 600,               // 可选：null = 5 分钟、0 = 一直等
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
          <li>工具出现在「工具」页（来源是插件），用户能看到它的危险等级并<b>手动调高</b>；
              插件页 → 这个插件 → <b>「工具」分页</b>还能一次看全它登记了哪些工具。</li>
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
          <tr><td>超时</td><td>单次默认 5 分钟。跑得久的工具在定义里写 <code>TimeoutSeconds</code>：<code>null</code>（默认）= 5 分钟、<code>0</code> = 一直等、<code>&gt;0</code> = 秒数（上限 24 小时）。用户可在工具页给这个工具单独放宽，<b>那里的配置优先</b>；想让模型为单次调用申请更久，在 schema 里声明 <code>timeout</code> 参数。
              <b>等人别用工具</b>：工具是被模型调用的，「等用户回一句话」请用交互桥的 <code>WaitForUserAsync</code>，
              否则超时到了模型只会看到一条假的「失败」。</td></tr>
          <tr><td>取消</td><td>传进来的 <code>ct</code> <b>请传下去</b>：用户点停止、超时、插件被禁用都会取消它。<b>不响应它就等于超时失败后还在后台跑</b>。</td></tr>
          <tr><td>失败</td><td>返回 <code>PluginToolResult.Fail("原因")</code>，或直接抛异常（宿主会写成一条失败结果回给模型）。</td></tr>
          <tr><td>线程</td><td>执行体在后台线程上跑，<b>不要直接改控件</b>。</td></tr>
        </table>

        <div class="note">
        结果文本会作为一条 <code>tool</code> 消息进上下文，所以要当作「给模型看的输出」来写：
        长输出自己截断，别把整份文件塞回去。
        </div>
        """;

    private static string Page_Debug() => """
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
          <tr><td>开标签页时抛「主窗口还没建好」</td>
              <td>主窗口尚未初始化。别在插件加载的最早期就开标签页 —— 放到用户触发的路径上
              （新标签页入口卡片的 <code>OnClick</code>、插槽工厂里、或等宿主第一次调用你之后）。</td></tr>
          <tr><td>插件工具模型调不到</td><td>工具要在会话里勾选过才会发给模型；另外确认插件是「运行中」，且清单声明了 <code>tools.provide</code>。</td></tr>
        </table>

        <h2>卸载与更新</h2>
        <ul>
          <li><b>插件运行中也能直接覆盖 dll</b>：宿主把托管程序集读成字节流再加载（不走
              <code>LoadFromAssemblyPath</code>），文件句柄立刻关闭，所以把新版复制过去不会被拒。
              覆盖后插件页会出现「插件文件已更新」（加载时记下的文件哈希与现在不一致），
              点「重新加载」即可切到新版。</li>
          <li>在插件页取消勾选 = 停止并卸载；卸载干净后 dll 可以删除或替换。</li>
          <li>替换后重新勾选即可加载新版本，通常不需要重启程序。</li>
          <li>处于「需重启」状态时必须重启：旧实例的程序集还在内存里，再加载会变成两份同时跑。</li>
        </ul>

        <div class="warn">
        <b>原生 dll 是唯一的例外。</b>原生库一旦被 <code>NativeLibrary.Load</code> 映射成映像节，
        句柄就跟着进程活到退出 —— 那个文件在插件运行期间<b>无法覆盖</b>，插件页会点名提示
        「文件被占用：xxx.dll」。想让原生库也能随插件更新，就从副本加载：
        </div>

        <pre><code>// 把原生库复制到临时目录，再加载副本；插件目录里的原件随时可替换
        var copy = Path.Combine(Path.GetTempPath(), "你的插件", "loader.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(Path.Combine(context.PluginDirectory, "loader.dll"), copy, overwrite: true);

        NativeLibrary.SetDllImportResolver(typeof(YourApi).Assembly, (name, asm, path) =&gt;
            name.Contains("loader", StringComparison.OrdinalIgnoreCase) &amp;&amp;
            NativeLibrary.TryLoad(copy, out var handle) ? handle : IntPtr.Zero);</code></pre>

        <p>内置浏览器插件就是这样加载 <code>WebView2Loader.dll</code> 的。</p>
        """;
}
