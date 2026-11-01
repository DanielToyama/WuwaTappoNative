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
    public string KeyboardMode { get; set; } = "hidden"; // hidden | letters | wuwa | rhythm | controller
    public bool ShowMappingControls { get; set; } = true;
    public bool HideOtherPets { get; set; }
    /// <summary>开启后把鸣潮专属指示器替换成四键音游指示器。</summary>
    public bool RhythmMode { get; set; }
    /// <summary>音游模式从左到右的四个触发键（单字符字母/数字）。</summary>
    public string[] RhythmKeys { get; set; } = { "D", "F", "J", "K" };
    /// <summary>允许角色桌宠互相重叠；关闭后重叠的联机桌宠会自动挪到旁边。</summary>
    public bool AllowOverlap { get; set; } = true;
    /// <summary>OBS 绿幕采集窗口：只显示主角，背景纯绿。</summary>
    public bool CaptureMode { get; set; }
    public int CaptureWidth { get; set; } = 400;
    public int CaptureHeight { get; set; } = 700;
    public bool AlwaysShowChest { get; set; }
    public bool AutoChest { get; set; }
    /// <summary>自动领取宝箱成功后是否发 Windows 托盘通知。</summary>
    public bool NotifyChestClaimed { get; set; } = true;
    /// <summary>快捷键触发动作后是否发 Windows 托盘通知。</summary>
    public bool NotifyHotkeys { get; set; } = true;
    /// <summary>开机自动启动（写 HKCU Run 项，启动时会与注册表对齐）。</summary>
    public bool LaunchAtLogin { get; set; }
    /// <summary>以管理员权限启动：管理员前台窗口下 Raw Input 才不会被 UIPI 拦截。</summary>
    public bool RunAsAdmin { get; set; }

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
    /// <summary>首次启动的 UAC 管理员权限询问是否已经回答过。</summary>
    public bool UacPromptShown { get; set; }
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
                    cfg.RhythmKeys = RhythmKeys.Normalize(cfg.RhythmKeys);
                    if (cfg.RhythmMode && cfg.KeyboardMode == "wuwa") cfg.KeyboardMode = "rhythm";
                    else if (!cfg.RhythmMode && cfg.KeyboardMode == "rhythm") cfg.KeyboardMode = "wuwa";
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
