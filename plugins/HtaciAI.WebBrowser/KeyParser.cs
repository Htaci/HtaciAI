using System;
using System.Collections.Generic;
using System.Linq;

namespace HtaciAI.WebBrowser;

/// <summary>
/// 把 <c>"Control+Shift+R"</c> 这类写法翻译成 CDP 的键盘参数。
///
/// <b>为什么要单独一个解析器</b>：CDP 的 <c>Input.dispatchKeyEvent</c> 要的是
/// <c>key</c> / <c>code</c> / <c>windowsVirtualKeyCode</c> 三个字段同时正确 ——
/// 少一个，某些页面就收不到。而这套映射既长又容易写错，集中一处比散在工具里好维护。
/// </summary>
internal static class KeyParser
{
    /// <summary>修饰键位掩码。顺序与 CDP 一致，改动会直接让所有组合键失效。</summary>
    private const int AltBit = 1;
    private const int ControlBit = 2;
    private const int MetaBit = 4;
    private const int ShiftBit = 8;

    /// <summary>常用键的三元组。<c>code</c> 用 DOM 的 KeyboardEvent.code 命名。</summary>
    private static readonly Dictionary<string, (string Key, string Code, int KeyCode)> SpecialKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = ("Enter", "Enter", 13),
            ["Return"] = ("Enter", "Enter", 13),
            ["Tab"] = ("Tab", "Tab", 9),
            ["Escape"] = ("Escape", "Escape", 27),
            ["Esc"] = ("Escape", "Escape", 27),
            ["Backspace"] = ("Backspace", "Backspace", 8),
            ["Delete"] = ("Delete", "Delete", 46),
            ["Insert"] = ("Insert", "Insert", 45),
            ["Home"] = ("Home", "Home", 36),
            ["End"] = ("End", "End", 35),
            ["PageUp"] = ("PageUp", "PageUp", 33),
            ["PageDown"] = ("PageDown", "PageDown", 34),
            ["ArrowUp"] = ("ArrowUp", "ArrowUp", 38),
            ["Up"] = ("ArrowUp", "ArrowUp", 38),
            ["ArrowDown"] = ("ArrowDown", "ArrowDown", 40),
            ["Down"] = ("ArrowDown", "ArrowDown", 40),
            ["ArrowLeft"] = ("ArrowLeft", "ArrowLeft", 37),
            ["Left"] = ("ArrowLeft", "ArrowLeft", 37),
            ["ArrowRight"] = ("ArrowRight", "ArrowRight", 39),
            ["Right"] = ("ArrowRight", "ArrowRight", 39),
            ["Space"] = (" ", "Space", 32),
            [" "] = (" ", "Space", 32),
        };

    /// <summary>解析一个按键描述，返回 CDP 需要的四元组。</summary>
    public static (string Key, string Code, int KeyCode, int Modifiers) Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("按键不能为空。");

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count == 0)
            throw new ArgumentException($"无法解析按键：{spec}");

        var modifiers = 0;
        var keyPart = parts[^1];   // 最后一段才是主键

        foreach (var part in parts.Take(parts.Count - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= AltBit; break;
                case "control":
                case "ctrl": modifiers |= ControlBit; break;
                case "meta":
                case "cmd":
                case "command":
                case "win": modifiers |= MetaBit; break;
                case "shift": modifiers |= ShiftBit; break;
                default:
                    throw new ArgumentException($"无法识别的修饰键：{part}（可用 Control / Shift / Alt / Meta）");
            }
        }

        // 带修饰键时，key 要用修饰后的形态（如 Control+A 的 key 是 "A" 而不是 "a"）
        if (SpecialKeys.TryGetValue(keyPart, out var special))
            return (special.Key, special.Code, special.KeyCode, modifiers);

        // 单字符：字母数字走 keyCode 映射，其余（标点、中文）只要 key 正确即可
        if (keyPart.Length == 1)
        {
            var ch = keyPart[0];
            var upper = char.ToUpperInvariant(ch);

            var code = ch switch
            {
                >= 'a' and <= 'z' => "Key" + upper,
                >= 'A' and <= 'Z' => "Key" + upper,
                >= '0' and <= '9' => "Digit" + ch,
                _ => "",
            };

            var keyCode = upper switch
            {
                >= 'A' and <= 'Z' => upper,          // A-Z 的 VK 就是 ASCII
                >= '0' and <= '9' => upper,
                _ => 0,
            };

            // 没有 Shift 时，字母键的 key 应当是小写；CDP 对这点比较宽松，
            // 但小写更接近真实键盘行为
            var key = (modifiers & ShiftBit) != 0 ? upper.ToString() : ch.ToString();

            return (key, code, keyCode, modifiers);
        }

        // F1–F12
        if (keyPart.Length >= 2 && keyPart.Length <= 3 &&
            (keyPart[0] is 'f' or 'F') && int.TryParse(keyPart[1..], out var fn) && fn is >= 1 and <= 12)
            return ($"F{fn}", $"F{fn}", 111 + fn, modifiers);

        throw new ArgumentException(
            $"无法识别的按键：{keyPart}（可用：Enter / Tab / Escape / Backspace / Delete / " +
            "Home / End / PageUp / PageDown / ArrowUp / ArrowDown / ArrowLeft / ArrowRight / Space / F1–F12 / 单个字符）");
    }
}
