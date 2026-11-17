using System;
using System.Collections.Generic;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 内置工具清单：C# 进程内工具（<see cref="ToolSource.Builtin"/>），启动时注册进注册表（不入库）。
/// <c>invalid</c> 为内部辅助工具（<see cref="ToolDefinition.IsInternal"/>），不出现在 AI 工具列表与工具页。
/// </summary>
public static class BuiltinTools
{
    public const string InvalidId = "builtin-invalid";

    /// <summary>ask_user_question 的工具名。会话视图要按它把工具卡渲染成问答卡片。</summary>
    public const string AskUserName = "ask_user_question";

    /// <summary>
    /// ask_user_continue 的工具名。
    /// 工具分派器要按它免掉统一超时：这个工具等的是「人做完一件事」，长度由人决定。
    /// </summary>
    public const string AskContinueName = "ask_user_continue";

    /// <summary>
    /// task 的工具名（派发子 Agent）。<b>按名字判定</b>：子会话配置要在这一处把它剔掉，
    /// 否则子 Agent 也能派发子 Agent，深度就不再是 1。
    /// </summary>
    public const string TaskToolName = "task";

    /// <summary>
    /// 知识库工具名。笔记与向量库分成两组：<b>笔记靠标题寻址，向量库靠内容召回</b>。
    ///
    /// 为什么不是一个大而全的 kb_search：这两件事的失败方式完全不同 ——
    /// 笔记读不到是「标题写错了」（重试换个标题就行），召回召不到是「库里没有」
    /// 或者「没配嵌入模型」（要告诉用户去配置）。合成一个工具，模型就分不清
    /// 「查过但库里没有」和「关键词不对」，只会反复换词重试。
    /// </summary>
    public const string KbNoteListName = "kb_note_list";
    public const string KbNoteReadName = "kb_note_read";
    public const string KbNoteWriteName = "kb_note_write";
    public const string KbNoteEditName = "kb_note_edit";
    public const string KbNoteDeleteName = "kb_note_delete";
    public const string KbRecallName = "kb_recall";
    public const string KbAddFileName = "kb_add_file";
    public const string KbAddTextName = "kb_add_text";
    public const string KbRemoveName = "kb_remove";

    private const string IdBash = "builtin-bash";
    private const string IdRead = "builtin-read";
    private const string IdEdit = "builtin-edit";
    private const string IdWrite = "builtin-write";
    private const string IdGlob = "builtin-glob";
    private const string IdGrep = "builtin-grep";
    private const string IdWebfetch = "builtin-webfetch";
    private const string IdLoadSkill = "builtin-load-skill";
    private const string IdUnloadSkill = "builtin-unload-skill";
    private const string IdAskUser = "builtin-ask-user";
    private const string IdAskContinue = "builtin-ask-continue";
    private const string IdViewImage = "builtin-view-image";
    private const string IdViewVideo = "builtin-view-video";
    private const string IdViewVideoFrames = "builtin-view-video-frames";
    private const string IdCreateTool = "builtin-create-tool";
    private const string IdCreateSkill = "builtin-create-skill";
    private const string IdTask = "builtin-task";
    private const string IdKbNoteList = "builtin-kb-note-list";
    private const string IdKbNoteRead = "builtin-kb-note-read";
    private const string IdKbNoteWrite = "builtin-kb-note-write";
    private const string IdKbNoteEdit = "builtin-kb-note-edit";
    private const string IdKbNoteDelete = "builtin-kb-note-delete";
    private const string IdKbRecall = "builtin-kb-recall";
    private const string IdKbAddFile = "builtin-kb-add-file";
    private const string IdKbAddText = "builtin-kb-add-text";
    private const string IdKbRemove = "builtin-kb-remove";

    /// <summary>自举工具名。<see cref="ToolDefinition.IsSelfExtension"/> 按它判定，界面与网关也认这两个常量。</summary>
    public const string CreateToolName = "create_tool";
    public const string CreateSkillName = "create_skill";

    public static IReadOnlyList<ToolDefinition> GetAll() => new[]
    {
        new ToolDefinition
        {
            Id = IdBash,
            Name = "bash",
            DisplayName = "执行命令",
            Icon = "\uEE6F",
            Description = ShellEnvironment.BashToolDescription(),
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Danger,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"要执行的命令行\"},\"timeout\":{\"type\":\"integer\",\"description\":\"超时秒数（默认 60）\"},\"workdir\":{\"type\":\"string\",\"description\":\"工作目录\"},\"description\":{\"type\":\"string\",\"description\":\"该命令用途说明\"}},\"required\":[\"command\"]}",
        },
        new ToolDefinition
        {
            Id = IdRead,
            Name = "read",
            DisplayName = "读取文件",
            Icon = "\uF379",
            Description = "读取文件内容或目录列表。安全：只读。编辑/覆盖写入前必须先读取过该文件。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"filePath\":{\"type\":\"string\",\"description\":\"文件或目录路径\"},\"offset\":{\"type\":\"integer\",\"description\":\"起始行号（从 0 开始）\"},\"limit\":{\"type\":\"integer\",\"description\":\"读取行数上限\"}},\"required\":[\"filePath\"]}",
        },
        new ToolDefinition
        {
            Id = IdEdit,
            Name = "edit",
            DisplayName = "编辑文件",
            Icon = "\uF3DE",
            Description = "精确字符串替换编辑文件。风险：会改动内容，执行前必须先读取过目标文件。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"filePath\":{\"type\":\"string\",\"description\":\"目标文件路径\"},\"oldString\":{\"type\":\"string\",\"description\":\"要替换的旧文本\"},\"newString\":{\"type\":\"string\",\"description\":\"替换成的新文本\"},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"是否替换所有匹配（默认只替换第一次）\"}},\"required\":[\"filePath\",\"oldString\",\"newString\"]}",
        },
        new ToolDefinition
        {
            Id = IdWrite,
            Name = "write",
            DisplayName = "写入文件",
            Icon = "\uF680",
            Description = "写文件（创建或覆盖）。风险：覆盖已存在文件前必须先读取过。新建文件则无需读取。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"content\":{\"type\":\"string\",\"description\":\"要写入的内容\"},\"filePath\":{\"type\":\"string\",\"description\":\"文件路径\"}},\"required\":[\"content\",\"filePath\"]}",
        },
        new ToolDefinition
        {
            Id = IdGlob,
            Name = "glob",
            DisplayName = "查找文件",
            Icon = "\uF419",
            Description = "文件模式匹配搜索（如 **/*.cs）。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"搜索模式（支持 ** 通配）\"},\"path\":{\"type\":\"string\",\"description\":\"搜索根目录（默认当前目录）\"}},\"required\":[\"pattern\"]}",
        },
        new ToolDefinition
        {
            Id = IdGrep,
            Name = "grep",
            DisplayName = "搜索内容",
            Icon = "\uF690",
            Description = "正则搜索文件内容，输出 文件:行号。支持按文件名或相对路径过滤（如 *.cs、src/**/*.cs）。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"正则表达式\"},\"path\":{\"type\":\"string\",\"description\":\"搜索根目录（默认当前目录）\"},\"include\":{\"type\":\"string\",\"description\":\"文件过滤，支持文件名或相对路径 glob（如 *.cs、src/**/*.cs）\"},\"context\":{\"type\":\"integer\",\"description\":\"每个匹配额外显示的上下文行数（0-10，默认 0）\"}},\"required\":[\"pattern\"]}",
        },
        new ToolDefinition
        {
            Id = IdWebfetch,
            Name = "webfetch",
            DisplayName = "抓取网页",
            Icon = "\uF45B",
            Description = "抓取指定 URL 的网页内容（非搜索，只能取已知网址）。HTML 会按 format 转成纯文本或 Markdown。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\",\"description\":\"要抓取的 URL\"},\"format\":{\"type\":\"string\",\"enum\":[\"text\",\"markdown\",\"html\"],\"description\":\"输出格式：text=纯文本（默认）、markdown=转成 Markdown、html=原始 HTML\"},\"timeout\":{\"type\":\"integer\",\"description\":\"超时秒数（默认 30）\"}},\"required\":[\"url\"]}",
        },
        new ToolDefinition
        {
            Id = IdLoadSkill,
            Name = "load_skill",
            DisplayName = "加载技能",
            Icon = "\uF8FE",
            Description = "加载技能：把该技能的 SKILL.md 全文注入当前会话 system，始终可用。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"技能名\"}},\"required\":[\"name\"]}",
        },
        new ToolDefinition
        {
            Id = IdUnloadSkill,
            Name = "unload_skill",
            DisplayName = "卸载技能",
            Icon = "\uE179",
            Description = "卸载已加载技能：从当前会话移除该技能的全文注入。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"技能名\"}},\"required\":[\"name\"]}",
        },
        new ToolDefinition
        {
            Id = IdCreateTool,
            Name = CreateToolName,
            DisplayName = "创建工具",
            Icon = "\uF8C1",
            Description = """
                创建一个新的脚本工具并登记到工具库（Python / Node.js）。危险：会写入可执行代码。
                建好之后<b>不会自动启用</b>，需要用户到「工具」页勾选后才能在本会话调用。
                写之前先 load_skill 加载 tool-authoring 了解脚本协议与参数规范。
                """,
            Source = ToolSource.Builtin,
            // 固定「危险」：等级必须由程序决定，不能交给模型自评 —— 它会把自己写的工具标成「安全」从而绕开审批。
            // 用户可以在工具页把它改成别的等级，那是知情之后的选择。
            DangerLevel = ToolDangerLevel.Danger,
            InputSchemaJson = """
                {"type":"object","properties":{"name":{"type":"string","description":"工具名，小写字母开头，只含小写字母/数字/下划线（如 fetch_weather）；会作为模型的调用名，全局唯一"},"description":{"type":"string","description":"一句话说明这个工具做什么、什么时候该用它；这是模型判断是否调用的唯一依据"},"runtime":{"type":"string","enum":["python","node"],"description":"脚本运行时：python 或 node"},"script":{"type":"string","description":"脚本源码全文。从 stdin 读入 JSON 参数，把结果打到 stdout；失败时用非 0 退出码并在 stderr 写原因"},"input_schema":{"type":"string","description":"入参 JSON Schema 字符串，根必须是 {\"type\":\"object\"}；无参数时传 {}"}},"required":["name","description","runtime","script","input_schema"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdCreateSkill,
            Name = CreateSkillName,
            DisplayName = "创建技能",
            Icon = "\uE161",
            Description = """
                创建一个新技能（SKILL.md 知识包），写入技能目录。风险：会写盘。
                建好之后不会自动加载，需要用户启用或你自己调用 load_skill。
                写之前先 load_skill 加载 skill-authoring 了解格式与内容规范。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson = """
                {"type":"object","properties":{"name":{"type":"string","description":"技能名，小写字母开头，只含小写字母/数字/-/._（如 review-sql）；同时是目录名与 load_skill 的调用名，全局唯一"},"alias":{"type":"string","description":"给人看的中文别名（可选，仅影响界面显示）"},"description":{"type":"string","description":"一句话说明这个技能覆盖什么任务、何时该加载它"},"body":{"type":"string","description":"SKILL.md 正文（Markdown，不含 frontmatter）。写清楚触发条件、步骤、输出格式与禁止事项"}},"required":["name","description","body"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdViewImage,
            Name = "view_image",
            DisplayName = "查看图片",
            Icon = "\uF489",
            Description = "查看本地图片文件的内容。用户消息里给出的图片路径就是让你用这个工具去看的，看完再回答。安全：只读。需要当前模型支持视觉。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"图片文件的绝对路径\"}},\"required\":[\"path\"]}",
        },
        new ToolDefinition
        {
            Id = IdViewVideo,
            Name = "view_video",
            DisplayName = "查看视频",
            Icon = "\uF84D",
            Description = "查看本地视频文件的内容（原样交给支持视频输入的模型，本地不做解码或抽帧）。用户消息里给出的视频路径就是让你用这个工具去看的，看完再回答。安全：只读。需要当前模型支持视频。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"视频文件的绝对路径\"}},\"required\":[\"path\"]}",
        },
        new ToolDefinition
        {
            Id = IdViewVideoFrames,
            Name = "view_video_frames",
            DisplayName = "视频抽帧",
            Icon = "\uEEFC",
            Description = """
                查看本地视频文件的内容：用本机的 ffmpeg 把视频按固定间隔抽成若干张图片，随工具结果一起交给你。
                需要当前模型支持视觉（不需要支持视频输入）——支持视频输入的模型可以直接用 view_video，看不了视频时用这个。
                适合：视频里讲了什么、某段时间画面是什么、有没有出现某个东西。默认 12 帧、每帧压到长边 768 像素。
                安全：只读，只是把解码出的帧写到数据目录的 frames 下。需要本机装了 ffmpeg（设置 → 环境配置）。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson = """
                {"type":"object","properties":{"path":{"type":"string","description":"视频文件的绝对路径"},"start":{"type":"number","description":"从第几秒开始抽（默认 0）"},"duration":{"type":"number","description":"抽多长的片段，单位秒（默认到视频结尾）"},"interval":{"type":"number","description":"每几秒抽一帧（默认按 max_frames 在片段内均分）；帧数因此超过 max_frames 时会自动放宽"},"max_frames":{"type":"integer","description":"最多抽几帧，默认 12，上限 24"}},"required":["path"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdAskUser,
            Name = AskUserName,
            DisplayName = "询问用户",
            Icon = "\uF63E",
            Description = "向用户提问并等待回答：可一次问多个问题，每题可单选或多选，用户也能用自定义文本作答。意图含糊、信息不足或需要用户在多个方案间拍板时使用。安全：只读，无副作用。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            SkipApproval = true,
            // 等的是人：不设上限（分派器另有同名兜底，这里写出来是为了工具页显示「一直等待」）
            TimeoutSeconds = 0,
            InputSchemaJson = """
                {"type":"object","properties":{"questions":{"type":"array","description":"要询问的问题列表（1-4 个）","items":{"type":"object","properties":{"question":{"type":"string","description":"问题本身，要具体、聚焦决策，不要问「我该怎么做」这类空泛问题；也不要写「（可多选）」之类的提示，单选还是多选由界面标注"},"header":{"type":"string","description":"极短标签，不超过 12 字"},"multiSelect":{"type":"boolean","description":"是否多选，默认 false（单选）。界面会在题干旁自动标注「单选 / 多选」；多选时用户可勾多个选项并同时补充自定义回答"},"options":{"type":"array","description":"候选项（1-6 个）。用户始终可以改用自定义输入作答，因此不必把「其他」写成选项。","items":{"type":"object","properties":{"label":{"type":"string","description":"选项文字"},"description":{"type":"string","description":"该选项意味着什么"}},"required":["label"]}}},"required":["question","options"]}}},"required":["questions"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdAskContinue,
            Name = AskContinueName,
            DisplayName = "请用户操作",
            Icon = "\uF5BE",
            Description =
                "请用户去做一件你做不了的事（扫码、输验证码、在手机上确认、手动登录等），" +
                "并在会话里显示一张卡片说明要做什么；用户点「我已做完，继续」后你才继续。" +
                "适合需要人实际操作才能推进的步骤。安全：只读，不改变任何状态。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            SkipApproval = true,
            // 同上：等的是人。它自己按 timeout_seconds 管时间，这一层不能再套硬超时
            TimeoutSeconds = 0,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"一行标题，说明这一步要做什么（如「请在手机上确认登录」）"},"instructions":{"type":"string","description":"要用户怎么做。按行写具体步骤，用户会照着做，所以要写清在哪、点什么、做完什么算完成"},"timeout_seconds":{"type":"integer","description":"最多等多久（秒）。默认 900，上限 3600。用户做现实操作常常要好几分钟，别给太小"}},"required":["title","instructions"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdTask,
            Name = TaskToolName,
            DisplayName = "子代理",
            Icon = "\uF5B9",
            Description = """
                派发一个子 Agent 去做一件完整的活，你只收到它最后的报告。
                子 Agent 有自己的会话（独立上下文、可继续追问），继承本会话的权限档位、工具、技能与 MCP。
                适合：需要独立上下文的长任务（项目分析、代码审查、批量调研），或需要同时换一种角色/视角看问题。
                不适合：一两步就能做完的事 —— 派发本身有开销，直接自己做更快。
                报告里会给出会话 id，传 session 即可在下次继续用同一个子 Agent（保留它的历史）。
                风险：会额外消耗模型额度与工具调用。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            // 跑的是一整个子 Agent 的一轮任务（可能几十次工具调用），5 分钟必然腰斩
            TimeoutSeconds = 30 * 60,
            InputSchemaJson = """
                {"type":"object","properties":{"agent":{"type":"string","description":"子 Agent 名称（见「子Agent」页，如「通用」「项目分析专家」）。不存在的名字会返回可用清单"},"prompt":{"type":"string","description":"任务描述。写清目标、范围、验收标准与输出要求；这是子 Agent 唯一的上下文来源，它看不到本会话的历史"},"session":{"type":"string","description":"要续用的子会话 id（上一次报告里给过）。不传就是新开一个；传了会保留它此前跑过的历史"},"max_turns":{"type":"integer","description":"本轮工具循环上限，默认 20，最大 100。撞上限就停并如实报告不完整"},"timeout":{"type":"integer","description":"整个子任务的最长等待秒数，默认 1800"}},"required":["agent","prompt"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbNoteList,
            Name = KbNoteListName,
            DisplayName = "列笔记",
            Icon = "\uF4EE",
            Description = """
                列出当前知识库里所有笔记的标题、简介与更新时间（不含正文）。
                这是进知识库的第一件事：<b>别凭印象猜标题</b>，先看这里有哪些。
                返回的标题就是后续读、改、删要用的名字 —— 笔记不用 id 寻址。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson = """
                {"type":"object","properties":{}}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbNoteRead,
            Name = KbNoteReadName,
            DisplayName = "读笔记",
            Icon = "\uE179",
            Description = """
                按标题读一篇笔记的全文（标题、简介、正文）。
                标题必须来自 kb_note_list 的结果或用户原话；对不上时会返回相近的标题供你挑，
                不要凭猜测反复试。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"笔记标题（同库内唯一）"}},"required":["title"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbNoteWrite,
            Name = KbNoteWriteName,
            DisplayName = "写笔记",
            Icon = "\uF56F",
            Description = """
                写入一篇笔记：标题不存在就新建，已存在就<b>整篇覆盖</b>。
                写之前先用 kb_note_list 看一眼有没有同名笔记 —— 覆盖会把原来的内容全部丢掉。
                只想改其中一处时用 kb_note_edit，不要整篇重写。
                正文用 Markdown。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"笔记标题，同库内唯一"},"summary":{"type":"string","description":"一句话简介，列笔记时显示。留空则自动取正文前几十字"},"content":{"type":"string","description":"Markdown 正文"}},"required":["title","content"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbNoteEdit,
            Name = KbNoteEditName,
            DisplayName = "改笔记",
            Icon = "\uF3DE",
            Description = """
                按标题精确修改一篇笔记里的某处文字：把 old_text 换成 new_text。
                old_text 要带上足够的上下文，保证在整篇里唯一 —— 匹配到多处时会报错而不是随便改一处。
                这是改笔记的默认做法：比整篇覆盖安全，也不会把没打算动的段落一起丢掉。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"笔记标题"},"old_text":{"type":"string","description":"要被替换掉的原文，需在整篇笔记里唯一"},"new_text":{"type":"string","description":"替换成的新内容"}},"required":["title","old_text","new_text"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbNoteDelete,
            Name = KbNoteDeleteName,
            DisplayName = "删笔记",
            Icon = "\uF34D",
            Description = """
                按标题删除一篇笔记。<b>不可恢复</b>：删掉就是没了，没有回收站。
                只在用户明确说要删的时候用；用户只是说「这笔记过时了」时，先问清楚是改还是删。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Danger,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"笔记标题"}},"required":["title"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbRecall,
            Name = KbRecallName,
            DisplayName = "召回知识库",
            Icon = "\uF690",
            Description = """
                在当前会话选定的知识库里按内容召回相关片段：向量相似度 + 关键词两条路合并。
                返回命中的片段（含来源标题与标题链），拿到片段后可再用 kb_note_read 读笔记全文。
                用途：查项目里已有的约定、规范、旧决策，或者用户导入过的资料。
                不适合：查这个项目的代码（那用 glob / grep / read，知识库不索引代码）。
                关于 min_score：低于这个相似度的结果直接丢掉，哪怕因此凑不满 top_k ——
                宁可少给几条，也不要拿一条不相干的当依据。放宽它之前先想想是不是该换关键词。
                没有选定知识库时直接返回提示，不要把查询改写成别的工具去猜。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            // 一次向量检索要现调一次嵌入接口，比纯本地工具慢，但远没到要让人等的地步
            TimeoutSeconds = 120,
            InputSchemaJson = """
                {"type":"object","properties":{"query":{"type":"string","description":"要查的内容。写具体一点更容易命中：错误码、函数名、配置项名这类关键词向量检索不擅长，交给关键词那一路"},"top_k":{"type":"integer","description":"最多返回几条，默认 6，最多 20。低于 min_score 的结果会被丢掉，实际可能更少"},"min_score":{"type":"number","description":"相似度下限，0~1，默认 0.35。调高更严格（只拿高度相关的），调低更宽松"}},"required":["query"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbAddFile,
            Name = KbAddFileName,
            DisplayName = "文件入库",
            Icon = "\uF41D",
            Description = """
                把一个文件导入知识库并建立索引，之后 kb_recall 就能召回它的内容。
                支持能读成文本的格式：md / txt / json / yaml / ini / csv / log / xml。
                <b>会消耗嵌入额度</b>（按切块数量），一次一两个文件可以，不要循环批量导几百个。
                用途：用户让你「把这个文件记下来」「把这份规范存进知识库」。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            TimeoutSeconds = 300,
            InputSchemaJson = """
                {"type":"object","properties":{"path":{"type":"string","description":"要导入的文件路径（绝对路径）"}},"required":["path"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbAddText,
            Name = KbAddTextName,
            DisplayName = "文字入库",
            Icon = "\uE4DC",
            Description = """
                把一段文字存进知识库的向量库并建立索引，之后 kb_recall 能召回它。
                适合：一席对话里得出的结论、一段需求、一条命令、用户口述的要点 —— 没有文件也不该先造一个文件。
                <b>会消耗嵌入额度</b>。
                标题要能让人一眼认出这是什么（「Nginx 反代的超时配置」而不是「笔记 1」）；
                同标题再插一次会新增一条并列的条目，不做覆盖。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            TimeoutSeconds = 300,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"这段内容的标题，用于在召回结果里标明来源"},"text":{"type":"string","description":"要存的正文，Markdown 也可以"}},"required":["title","text"]}
                """,
        },
        new ToolDefinition
        {
            Id = IdKbRemove,
            Name = KbRemoveName,
            DisplayName = "移除条目",
            Icon = "\uE47F",
            Description = """
                从向量库里删掉一条条目（按标题定位），文字条目和文件导入的都行。
                文件导入的那种，删的是导入时复制进知识库目录的那份副本与它的索引 ——
                用户自己的源文件不会被碰到。副本已经不在了也照删。
                用途：插错了、内容过时了。删之前先用 kb_recall 确认是哪一条，标题要精确。
                """,
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Risk,
            InputSchemaJson = """
                {"type":"object","properties":{"title":{"type":"string","description":"要删掉的那条条目的标题，必须与现有标题完全一致"}},"required":["title"]}
                """,
        },
        new ToolDefinition
        {
            Id = InvalidId,
            Name = "invalid",
            DisplayName = "无效工具",
            Description = "无效工具：返回调用工具与错误信息，供内部在工具执行失败时辅助反馈。不出现在 AI 工具列表与工具页。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Danger,
            IsInternal = true,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"tool\":{\"type\":\"string\",\"description\":\"发生错误的工具名\"},\"error\":{\"type\":\"string\",\"description\":\"错误信息\"}},\"required\":[\"tool\",\"error\"]}",
        },
    };

    /// <summary>
    /// 排除内部辅助工具后的清单。用于「默认开启的工具 = 全部内置工具」这个默认值——
    /// <c>invalid</c> 不该出现在给用户看的勾选列表里。
    /// </summary>
    public static IReadOnlyList<ToolDefinition> GetVisible()
    {
        var list = new List<ToolDefinition>();
        foreach (var tool in GetAll())
            if (!tool.IsInternal) list.Add(tool);
        return list;
    }

    /// <summary>调用名 → 内置工具定义。用于只有名字可查的地方（如历史消息里的工具卡）。</summary>
    private static readonly Dictionary<string, ToolDefinition> ByName = BuildIndex();

    private static Dictionary<string, ToolDefinition> BuildIndex()
    {
        var map = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        foreach (var tool in GetAll()) map[tool.Name] = tool;
        return map;
    }

    /// <summary>
    /// 按调用名取用户可见名称（<c>task</c> → 「子代理」）。
    /// 查不到（脚本 / MCP / 插件工具）就原样返回调用名 —— 它们的名字由各自的作者决定，
    /// 宿主没有权力也没有信息去替它起一个中文别名。
    /// </summary>
    public static string DisplayOf(string? name)
        => name is not null && ByName.TryGetValue(name, out var tool) ? tool.Display : name ?? "";

    /// <summary>按调用名取图标码位；查不到就用默认扳手。</summary>
    public static string IconOf(string? name)
        => name is not null && ByName.TryGetValue(name, out var tool) ? tool.ResolvedIcon : ToolDefinition.DefaultIcon;
}
