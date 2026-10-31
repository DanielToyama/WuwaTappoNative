using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WuwatappoNative.Core;

public sealed class Settings
{
    public string Token { get; set; } = "";
    public long TokenExpireMs { get; set; }
    public string Cuid { get; set; } = "";
    public long UserId { get; set; }
    public string Nickname { get; set; } = "";
    public long RoleId { get; set; }
    public long CharacterAssetId { get; set; }
    public long AccessoryAssetId { get; set; }
    public long[] EmoteAssetIds { get; set; } = new long[5];
    /// <summary>本地换装标记：为 true 时 user/me 不再覆盖角色/配件。</summary>
    public bool LocalAppearance { get; set; }
    /// <summary>表情栏已被本地编辑过；为 true 时 user/me 不再覆盖槽位。</summary>
    public bool LocalEmoteSlots { get; set; }
    public long[] PresetIds { get; set; } = Array.Empty<long>();

    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HoverFade { get; set; }
    public string KeyboardMode { get; set; } = "hidden"; // hidden | letters | wuwa
    public bool ShowMappingControls { get; set; } = true;
    public bool HideOtherPets { get; set; }
    public bool AlwaysShowChest { get; set; }
    public bool AutoChest { get; set; }
    /// <summary>开机自动启动（写 HKCU Run 项，启动时会与注册表对齐）。</summary>
    public bool LaunchAtLogin { get; set; }

    public int PetX { get; set; } = -1;
    public int PetY { get; set; } = -1;
    public string RoomNo { get; set; } = "";
    public string LastRoom { get; set; } = "";
    public string[] RoomHistory { get; set; } = Array.Empty<string>();
    public Dictionary<string, int[]> MemberPositions { get; set; } = new();
    public string Locale { get; set; } = "zh";
    /// <summary>全局快捷键：动作 id → 组合键描述；空串表示禁用。缺省项使用原版默认键位。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>首次启动的免责声明是否已经确认过。</summary>
    public bool DisclaimerShown { get; set; }
}

public static class ConfigStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                var cfg = JsonSerializer.Deserialize<Settings>(json, Options);
                if (cfg != null)
                {
                    if (cfg.EmoteAssetIds == null || cfg.EmoteAssetIds.Length != 5)
                    {
                        var slots = new long[5];
                        if (cfg.EmoteAssetIds != null)
                            Array.Copy(cfg.EmoteAssetIds, slots, Math.Min(5, cfg.EmoteAssetIds.Length));
                        cfg.EmoteAssetIds = slots;
                    }
                    return cfg;
                }
            }
        }
        catch (Exception ex) { Log.Write("配置读取失败 " + ex.Message); }
        return new Settings();
    }

    public static void Save(Settings cfg)
    {
        try
        {
            var tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
            File.Move(tmp, Paths.ConfigFile, true);
        }
        catch (Exception ex) { Log.Write("配置写入失败 " + ex.Message); }
    }
}
