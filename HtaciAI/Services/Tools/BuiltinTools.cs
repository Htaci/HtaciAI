using System.Collections.Generic;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 内置工具清单：C# 进程内工具（<see cref="ToolSource.Builtin"/>），启动时注册进注册表（不入库）。
/// <c>invalid</c> 为内部辅助工具（<see cref="ToolDefinition.IsInternal"/>），不出现在 AI 工具列表与工具页。
/// </summary>
public static class BuiltinTools
{
    public const string InvalidId = "builtin-invalid";

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
    private const string IdViewImage = "builtin-view-image";

    public static IReadOnlyList<ToolDefinition> GetAll() => new[]
    {
        new ToolDefinition
        {
            Id = IdBash,
            Name = "bash",
            Description = "执行 shell 命令（支持 bash / PowerShell / cmd），可设置工作目录与超时。危险：可影响系统。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Danger,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"要执行的命令行\"},\"timeout\":{\"type\":\"integer\",\"description\":\"超时秒数（默认 60）\"},\"workdir\":{\"type\":\"string\",\"description\":\"工作目录\"},\"description\":{\"type\":\"string\",\"description\":\"该命令用途说明\"}},\"required\":[\"command\"]}",
        },
        new ToolDefinition
        {
            Id = IdRead,
            Name = "read",
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
            Description = "卸载已加载技能：从当前会话移除该技能的全文注入。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"技能名\"}},\"required\":[\"name\"]}",
        },
        new ToolDefinition
        {
            Id = IdViewImage,
            Name = "view_image",
            Description = "查看本地图片文件的内容。用户消息里给出的图片路径就是让你用这个工具去看的，看完再回答。安全：只读。需要当前模型支持视觉。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"图片文件的绝对路径\"}},\"required\":[\"path\"]}",
        },
        new ToolDefinition
        {
            Id = IdAskUser,
            Name = "ask_user_question",
            Description = "向用户提问并等待回答：可一次问多个问题，每题可单选或多选，用户也能用自定义文本作答。意图含糊、信息不足或需要用户在多个方案间拍板时使用。安全：只读，无副作用。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            SkipApproval = true,
            InputSchemaJson = """
                {"type":"object","properties":{"questions":{"type":"array","description":"要询问的问题列表（1-4 个）","items":{"type":"object","properties":{"question":{"type":"string","description":"问题本身，要具体、聚焦决策，不要问「我该怎么做」这类空泛问题"},"header":{"type":"string","description":"极短标签，不超过 12 字"},"multiSelect":{"type":"boolean","description":"是否多选，默认 false（单选）"},"options":{"type":"array","description":"候选项（1-6 个）。用户始终可以改用自定义输入作答，因此不必把「其他」写成选项。","items":{"type":"object","properties":{"label":{"type":"string","description":"选项文字"},"description":{"type":"string","description":"该选项意味着什么"}},"required":["label"]}}},"required":["question","options"]}}},"required":["questions"]}
                """,
        },
        new ToolDefinition
        {
            Id = InvalidId,
            Name = "invalid",
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
}
