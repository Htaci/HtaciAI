using System;
using Avalonia.Input;

namespace HtaciAI.Controls;

/// <summary>
/// 与平台相关的按键手势判断。
///
/// <para>
/// <b>凡是写死 <see cref="KeyModifiers.Control"/> 的地方，在 macOS 上都是坏的。</b>
/// Mac 的惯例是 Command，在 Avalonia 里对应 <see cref="KeyModifiers.Meta"/>。
/// </para>
///
/// <para>
/// 这类 bug 的麻烦之处在于表现是「一半好使」：比如粘贴，写死 Ctrl 之后 Cmd+V 会落到
/// TextBox 的默认行为上，纯文本照样粘得进来，于是用户觉得「粘贴没问题」——
/// 但剪贴板里的文件和截图永远不会被转成附件。查的人会先怀疑剪贴板读取、怀疑格式，
/// 很难想到是修饰键根本没匹配上。
/// </para>
///
/// <para>
/// 所以判断集中在这里，不要在各处重新写一遍平台分支。
/// </para>
/// </summary>
public static class InputGestures
{
    /// <summary>本平台的主命令修饰键：macOS 是 Command（Meta），其余平台是 Ctrl。</summary>
    public static KeyModifiers PrimaryCommand =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>是否按下了本平台的主命令修饰键。</summary>
    public static bool HasPrimaryCommand(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(PrimaryCommand);

    /// <summary>
    /// 是否是粘贴手势（<c>Cmd+V</c> / <c>Ctrl+V</c>）。
    ///
    /// macOS 上额外接受 Ctrl：那是其它平台的惯例，跨平台用户的肌肉记忆不该失效。
    /// <b>反向不成立</b> —— Windows/Linux 上不接受 Meta，因为 <c>Win+V</c> 是系统的
    /// 剪贴板历史，抢过来会让那个功能打不开。
    /// </summary>
    public static bool IsPaste(KeyEventArgs e) =>
        e.Key == Key.V &&
        (e.KeyModifiers.HasFlag(PrimaryCommand) || e.KeyModifiers.HasFlag(KeyModifiers.Control));
}
