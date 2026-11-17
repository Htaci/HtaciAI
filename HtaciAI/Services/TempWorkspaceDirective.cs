using System;
using System.IO;
using HtaciAI.Services.Storage;

namespace HtaciAI.Services;

/// <summary>
/// 普通对话的「临时工作目录」说明，追加到 system 提示词。
///
/// <b>要解决的问题</b>：普通对话没有工作目录，工具的基准目录是进程当前目录（即 exe 旁边）。
/// 模型不知道这一点，随手写 <c>tmp_days.py</c>、<c>dir/</c> 这样的相对路径，文件就落在
/// 程序目录里，跟 dll、exe 混在一起。给它一个明确的落盘位置就够了。
///
/// <b>路径不写死</b>：数据根由存储模式（便携 / AppData）决定，用户可以在数据管理页切换，
/// 所以这里每轮现取 <see cref="AppPaths.Root"/>，而不是常量。提示词里给的是<b>完整路径</b> ——
/// 只给相对写法，模型仍会把它相对到程序目录上去，等于没改。
///
/// <b>目录由这里创建</b>：只在提示词里说「用这个目录」而目录不存在，模型第一次写文件就会
/// 撞上「路径不存在」。创建失败不抛异常：真写不进去时，让模型把错误原样报给用户，
/// 比让整轮发送失败要好。
/// </summary>
public static class TempWorkspaceDirective
{
    /// <summary>临时工作目录的完整路径：<c>&lt;数据根&gt;/TempWorkspace</c>。</summary>
    public static string DirectoryPath => Path.Combine(AppPaths.Root, "TempWorkspace");

    /// <summary>追加到 system 的文本（含本机解析出的完整路径）。</summary>
    public static string Build()
    {
        var directory = DirectoryPath;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 建不出来也照常下发：模型尝试写入时会拿到系统的真实报错，那比提示词里一句空话有用
        }

        return string.Format(Text, directory);
    }

    private const string Text = """
        本次会话没有工作目录：工具的相对路径以程序目录为基准，而程序目录只放程序自己的文件 —
        不要往那里写脚本、临时文件或任何产物。需要落盘的东西（脚本、生成的图片、下载的文件、
        中间结果）一律放到下面这个目录：

        {0}

        这是完整路径，直接用绝对路径即可（该目录已存在）。用户明确指定了别的路径时，以用户的为准。
        """;
}
