namespace HtaciAI.Models;

/// <summary>
/// 应用数据的存放模式。决定 <see cref="Services.Storage.AppPaths.Root"/> 解析到哪。
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// 默认：程序目录下存在 <c>data</c> 子目录就用它（等价于便携），否则用
    /// <c>%APPDATA%\HtaciAI</c>。这样便携版拷到别的机器上（那里没有本机的 storage.json）会自动跟着程序走。
    /// </summary>
    Default,

    /// <summary>便携：始终用程序目录下的 <c>data</c>，无视 %APPDATA%。</summary>
    Portable,

    /// <summary>始终用 <c>%APPDATA%\HtaciAI</c>，即使程序目录下有 <c>data</c>。</summary>
    AppData,
}
