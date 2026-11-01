using System;
using System.Collections.Generic;
using System.Linq;

namespace WuwatappoNative.Core;

/// <summary>
/// 音游模式四个从左到右的触发键。只接受单字符字母/数字，重复或空项回退默认 D F J K。
/// </summary>
public static class RhythmKeys
{
    public static readonly string[] Defaults = { "D", "F", "J", "K" };

    public static string[] Normalize(IEnumerable<string> keys)
    {
        var input = keys?.ToArray() ?? Array.Empty<string>();
        var result = new string[4];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < result.Length; i++)
        {
            string candidate = NormalizeOne(i < input.Length ? input[i] : null);
            if (candidate.Length == 0 || used.Contains(candidate))
            {
                candidate = Defaults.FirstOrDefault(k => !used.Contains(k));
                if (candidate == null)
                {
                    for (char c = 'A'; c <= 'Z'; c++)
                    {
                        string probe = c.ToString();
                        if (used.Contains(probe)) continue;
                        candidate = probe;
                        break;
                    }
                }
                candidate ??= "";
            }
            result[i] = candidate;
            if (candidate.Length > 0) used.Add(candidate);
        }
        return result;
    }

    public static string NormalizeOne(string value)
    {
        string s = (value ?? "").Trim().ToUpperInvariant();
        if (s.Length != 1) return "";
        char c = s[0];
        return char.IsLetterOrDigit(c) ? s : "";
    }
}