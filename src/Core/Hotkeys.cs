using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WuwatappoNative.Core;

/// <summary>
/// 全局快捷键：默认键位与原版 Helper 一致，可在设置里录制修改。
/// 只支持 Ctrl / Alt / Shift 与普通按键的组合（Win 键留给系统）。
/// </summary>
public static class Hotkeys
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004;

    /// <summary>动作 id → 默认按键。顺序即设置页显示顺序。</summary>
    public static readonly (string Id, string Label, string Default)[] Actions =
    {
        ("game", "游戏模式（鼠标穿透）", "F7"),
        ("monitor", "切换屏幕", "F8"),
        ("visibility", "显示／隐藏桌宠", "F9"),
        ("reload", "刷新界面", "F5"),
        ("emote1", "发送表情 · 槽位 1", "Ctrl+Alt+1"),
        ("emote2", "发送表情 · 槽位 2", "Ctrl+Alt+2"),
        ("emote3", "发送表情 · 槽位 3", "Ctrl+Alt+3"),
        ("emote4", "发送表情 · 槽位 4", "Ctrl+Alt+4"),
        ("emote5", "发送表情 · 槽位 5", "Ctrl+Alt+5"),
        ("emote6", "发送表情 · 槽位 6", "Ctrl+Alt+6"),
    };

    public static Dictionary<string, string> Defaults()
        => Actions.ToDictionary(a => a.Id, a => a.Default);

    public static string DefaultFor(string id)
        => Actions.FirstOrDefault(a => a.Id == id).Default ?? "";

    /// <summary>规范化按键描述；格式非法时返回空串。</summary>
    public static string Normalize(string raw)
    {
        var parsed = Parse(raw);
        return parsed.HasValue ? Format(parsed.Value.Mods, parsed.Value.Vk) : "";
    }

    public static bool TryParse(string raw, out uint mods, out uint vk)
    {
        var p = Parse(raw);
        mods = p?.Mods ?? 0;
        vk = p?.Vk ?? 0;
        return p.HasValue;
    }

    static (uint Mods, uint Vk)? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        uint mods = 0;
        string baseKey = "";
        foreach (var part in raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": case "meta": return null;
                default: baseKey = part; break;
            }
        }
        if (string.IsNullOrEmpty(baseKey)) return null;
        uint vk = VirtualKey(baseKey);
        if (vk == 0) return null;
        return (mods, vk);
    }

    static string Format(uint mods, uint vk)
    {
        var sb = new StringBuilder();
        if ((mods & ModControl) != 0) sb.Append("Ctrl+");
        if ((mods & ModAlt) != 0) sb.Append("Alt+");
        if ((mods & ModShift) != 0) sb.Append("Shift+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    static uint VirtualKey(string key)
    {
        string k = key.Trim();
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        if (k.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(k[1..], out int f) && f >= 1 && f <= 12)
            return (uint)(0x70 + f - 1);
        return k.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "up" or "arrowup" => 0x26,
            "down" or "arrowdown" => 0x28,
            "left" or "arrowleft" => 0x25,
            "right" or "arrowright" => 0x27,
            "`" or "backquote" => 0xC0,
            "-" or "minus" => 0xBD,
            "=" or "equal" => 0xBB,
            "[" or "bracketleft" => 0xDB,
            "]" or "bracketright" => 0xDD,
            ";" or "semicolon" => 0xBA,
            "'" or "quote" => 0xDE,
            "," or "comma" => 0xBC,
            "." or "period" => 0xBE,
            "/" or "slash" => 0xBF,
            "\\" or "backslash" => 0xDC,
            _ => 0,
        };
    }

    static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            0xDC => "\\",
            _ => "?"
        };
    }

    /// <summary>读取配置中的快捷键表，缺失或非法项回落到默认值。</summary>
    public static Dictionary<string, string> FromSettings(Dictionary<string, string> saved)
    {
        var result = new Dictionary<string, string>();
        foreach (var (id, _, def) in Actions)
        {
            string value = def;
            if (saved != null && saved.TryGetValue(id, out var raw))
            {
                string normalized = Normalize(raw);
                // 空串表示用户主动禁用；非法值（非空但规范化失败）回落到默认。
                value = string.IsNullOrWhiteSpace(raw) ? "" : (normalized.Length > 0 ? normalized : def);
            }
            result[id] = value;
        }
        return result;
    }
}
