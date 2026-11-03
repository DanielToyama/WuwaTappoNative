using System;
using System.Collections.Generic;
using System.Linq;

namespace WuwatappoNative.Core;

/// <summary>
/// 音游模式从左到右的触发键，支持 4K/5K/6K、字母、数字、空格和常用标点。
/// 空格内部使用 SPACE 标记，键帽使用旋转 90° 的 "[" 绘制。
/// </summary>
public static class RhythmKeys
{
    public const string Space = "SPACE";
    public static readonly string[] Defaults = { "D", "F", "J", "K", "L", ";" };
    const string SupportedPunctuation = ";',./[]=-`\\";

    public static string[] DefaultsFor(int count) => Math.Clamp(count, 4, 6) switch
    {
        4 => new[] { "D", "F", "J", "K" },
        5 => new[] { "D", "F", "J", "K", "L" },
        _ => new[] { "S", "D", "F", "J", "K", "L" },
    };

    public static string[] Normalize(IEnumerable<string> keys, int count = 4)
    {
        count = Math.Clamp(count, 4, 6);
        var input = keys?.ToArray() ?? Array.Empty<string>();
        var defaults = DefaultsFor(count);
        var result = new string[count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < result.Length; i++)
        {
            string candidate = NormalizeOne(i < input.Length ? input[i] : null);
            if (candidate.Length == 0 || used.Contains(candidate))
                candidate = defaults.FirstOrDefault(k => !used.Contains(k));
            if (string.IsNullOrEmpty(candidate))
            {
                foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
                {
                    string probe = c.ToString();
                    if (used.Contains(probe)) continue;
                    candidate = probe;
                    break;
                }
            }
            result[i] = candidate ?? "";
            if (candidate?.Length > 0) used.Add(candidate);
        }
        return result;
    }

    public static string NormalizeOne(string value)
    {
        string s = (value ?? "").Trim();
        if (s.Length == 0) return "";
        if (s == " " || s == "␣" || s.Equals("空格", StringComparison.Ordinal) ||
            s.Equals(Space, StringComparison.OrdinalIgnoreCase))
            return Space;
        if (s.Length != 1) return "";
        char c = s[0];
        if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return SupportedPunctuation.IndexOf(c) >= 0 ? c.ToString() : "";
    }

    /// <summary>把 Raw Input 的虚拟键码转换成音游键位标记。</summary>
    public static string FromVirtualKey(int vk)
    {
        if (vk == 0x20) return Space;
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        return vk switch
        {
            0xBA => ";", // OEM_1
            0xBB => "=", // OEM_PLUS
            0xBC => ",", // OEM_COMMA
            0xBD => "-", // OEM_MINUS
            0xBE => ".", // OEM_PERIOD
            0xBF => "/", // OEM_2
            0xC0 => "`", // OEM_3
            0xDB => "[", // OEM_4
            0xDC => "\\", // OEM_5
            0xDD => "]", // OEM_6
            0xDE => "'", // OEM_7
            0xE2 => "\\", // OEM_102
            _ => "",
        };
    }
}
