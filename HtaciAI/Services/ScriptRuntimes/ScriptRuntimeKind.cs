namespace HtaciAI.Services.ScriptRuntimes;

/// <summary>
/// 脚本工具支持的运行时。
/// <see cref="Git"/> 不是脚本运行时，但它需要的「找可执行文件 + 读版本 + 可手动指定」
/// 和运行时完全一样，所以复用同一套探测与配置（它不会被创建工具对话框列为选项）。
/// </summary>
public enum ScriptRuntimeKind
{
    Python,
    Node,

    /// <summary>版本控制 Git（供工作空间的 diff / 提交等能力使用）。</summary>
    Git
}
