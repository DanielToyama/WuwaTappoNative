using System;
using System.Collections.Generic;
using System.IO;

namespace WuwatappoNative.Core;

/// <summary>
/// 游戏模式规则：用户填写进程名（可带 .exe 或完整路径），启动后匹配到该进程时自动开启鼠标穿透。
/// 统一保存为不带扩展名的文件名，比较时忽略大小写。
/// </summary>
public static class GameModeRules
{
    public const int MaxRules = 32;

    public static string[] Normalize(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return Array.Empty<string>();

        foreach (string raw in values)
        {
            string name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
                name = name[1..^1].Trim();
            try { name = Path.GetFileName(name); } catch { }
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4].Trim();
            if (name.Length == 0 || !seen.Add(name)) continue;
            result.Add(name);
            if (result.Count >= MaxRules) break;
        }
        return result.ToArray();
    }

    public static bool Matches(string processName, IReadOnlyList<string> rules)
    {
        if (string.IsNullOrWhiteSpace(processName) || rules == null) return false;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        for (int i = 0; i < rules.Count; i++)
        {
            if (string.Equals(name, rules[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
