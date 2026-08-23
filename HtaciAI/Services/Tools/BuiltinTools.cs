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
            Description = "正则搜索文件内容，输出 文件:行号。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"正则表达式\"},\"path\":{\"type\":\"string\",\"description\":\"搜索根目录（默认当前目录）\"},\"include\":{\"type\":\"string\",\"description\":\"文件名过滤（如 *.cs）\"}},\"required\":[\"pattern\"]}",
        },
        new ToolDefinition
        {
            Id = IdWebfetch,
            Name = "webfetch",
            Description = "从 URL 获取网页内容。安全：只读。",
            Source = ToolSource.Builtin,
            DangerLevel = ToolDangerLevel.Safe,
            InputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\",\"description\":\"要抓取的 URL\"},\"format\":{\"type\":\"string\",\"description\":\"输出格式（text / markdown，默认 text）\"},\"timeout\":{\"type\":\"integer\",\"description\":\"超时秒数（默认 30）\"}},\"required\":[\"url\"]}",
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
}
