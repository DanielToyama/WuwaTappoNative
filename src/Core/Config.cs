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
    /// <summary>库洛 H5 SDK 的设备标识；首次生成后复用，避免每次登录都被当成新设备。</summary>
    public string SdkDeviceNum { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public long PlayerId { get; set; }
    public string Nickname { get; set; } = "";
    public long RoleId { get; set; }
    public long CharacterAssetId { get; set; }
    public long AccessoryAssetId { get; set; }
    public long[] EmoteAssetIds { get; set; } = new long[5];
    /// <summary>本地换装标记：为 true 时 user/me 不再覆盖角色/配件。</summary>
    public bool LocalAppearance { get; set; }
    /// <summary>表情栏已被本地编辑过；为 true 时 user/me 不再覆盖槽位。</summary>
    public bool LocalEmoteSlots { get; set; }
    /// <summary>服务器最近一次确认过的角色/配件/表情；本地装扮可以任意，但上传云端时用这组安全值。</summary>
    public long CloudCharacterAssetId { get; set; }
    public long CloudAccessoryAssetId { get; set; }
    public long[] CloudEmoteAssetIds { get; set; } = new long[5];
    public long[] PresetIds { get; set; } = Array.Empty<long>();

    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    /// <summary>联机角色自定义大小；RemoteFollowMainScale=true 时忽略此值，直接跟随主角 Scale。</summary>
    public double RemoteScale { get; set; } = 1.0;
    /// <summary>联机角色大小是否跟随主角，默认开启。</summary>
    public bool RemoteFollowMainScale { get; set; } = true;
    /// <summary>联机角色自定义不透明度；RemoteFollowMainOpacity=true 时忽略此值，直接跟随主角 Opacity。</summary>
    public double RemoteOpacity { get; set; } = 1.0;
    /// <summary>联机角色昵称字号倍率，限制在 50%–200%。</summary>
    public double RemoteNameScale { get; set; } = 1.0;
    /// <summary>按键时是否在桌宠上方冒出键帽气泡。</summary>
    public bool KeyBubbles { get; set; } = true;
    /// <summary>表情、按键气泡等活动动画的帧率：30 / 60 / 120。</summary>
    public int AnimationFps { get; set; } = 60;
    public static int NormalizeAnimationFps(int fps) => fps is 30 or 120 ? fps : 60;
    /// <summary>联机角色不透明度是否跟随主角，默认开启。</summary>
    public bool RemoteFollowMainOpacity { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HoverFade { get; set; }
    /// <summary>悬停隐藏时的不透明度倍数（0.1-1.0，默认 0.4）。</summary>
    public double HoverFadeOpacity { get; set; } = 0.4;
    public string KeyboardMode { get; set; } = "hidden"; // hidden | letters | wuwa | rhythm | controller
    public bool ShowMappingControls { get; set; } = true;
    public bool HideOtherPets { get; set; }
    /// <summary>开启后把鸣潮专属指示器替换成四键音游指示器。</summary>
    public bool RhythmMode { get; set; }
    /// <summary>音游模式按键数量：4K / 5K / 6K。</summary>
    public int RhythmKeyCount { get; set; } = 4;
    /// <summary>音游模式当前选中键数的触发键（兼容旧配置和运行时代码）。</summary>
    public string[] RhythmKeys { get; set; } = { "D", "F", "J", "K" };
    /// <summary>各键数模式分别保存的键位。</summary>
    public string[] RhythmKeys4 { get; set; }
    public string[] RhythmKeys5 { get; set; }
    public string[] RhythmKeys6 { get; set; }

    /// <summary>读取指定键数模式的键位；未保存时使用默认键位。</summary>
    public string[] GetRhythmKeys(int count)
    {
        count = Math.Clamp(count, 4, 6);
        string[] value = count switch
        {
            4 => RhythmKeys4,
            5 => RhythmKeys5,
            _ => RhythmKeys6,
        };
        return value ?? WuwatappoNative.Core.RhythmKeys.DefaultsFor(count);
    }

    /// <summary>保存指定键数模式的键位，并同步当前活跃键位。</summary>
    public void SetRhythmKeys(int count, string[] keys)
    {
        count = Math.Clamp(count, 4, 6);
        keys = WuwatappoNative.Core.RhythmKeys.Normalize(keys, count);
        switch (count)
        {
            case 4: RhythmKeys4 = keys; break;
            case 5: RhythmKeys5 = keys; break;
            default: RhythmKeys6 = keys; break;
        }
    }
    /// <summary>允许角色桌宠互相重叠；关闭后重叠的联机桌宠会自动挪到旁边。</summary>
    public bool AllowOverlap { get; set; } = true;
    /// <summary>联机桌宠排列方向：horizontal = 底部横向，vertical = 右侧纵向。</summary>
    public string CatLayout { get; set; } = "horizontal";
    /// <summary>获得新道具后的合成提醒检查偏好：up / same / off。</summary>
    public string SynthCheckMode { get; set; } = "up";
    /// <summary>OBS 绿幕采集窗口：只显示主角，背景纯绿。</summary>
    public bool CaptureMode { get; set; }
    public int CaptureWidth { get; set; } = 400;
    public int CaptureHeight { get; set; } = 700;
    public bool AlwaysShowChest { get; set; }
    public bool AutoChest { get; set; }
    /// <summary>每次产生敲击时自动发送一个表情。</summary>
    public bool AutoEmoteEnabled { get; set; }
    /// <summary>自动表情每秒最多发送数量，范围 1-20。</summary>
    public int AutoEmoteMaxPerSecond { get; set; } = 20;
    /// <summary>自动表情来源：quickbar / specific / library。</summary>
    public string AutoEmoteMode { get; set; } = "quickbar";
    /// <summary>自动表情指定表情 id（mode=specific 时使用）。</summary>
    public long AutoEmoteId { get; set; }
    /// <summary>自动产生敲击并上传。</summary>
    public bool AutoClickEnabled { get; set; }
    /// <summary>自动敲击每秒次数，范围 1-10。</summary>
    public int AutoClickPerSecond { get; set; } = 10;
    /// <summary>自动敲击是否驱动自己的桌宠挥手；关闭后只响应真实敲击，键位显示器始终不响应虚拟敲击。</summary>
    public bool AutoClickAnimate { get; set; } = true;
    /// <summary>自动领取宝箱成功后是否发 Windows 托盘通知。</summary>
    public bool NotifyChestClaimed { get; set; } = true;
    /// <summary>快捷键触发动作后是否发 Windows 托盘通知。</summary>
    public bool NotifyHotkeys { get; set; } = true;
    /// <summary>开机自动启动（写 HKCU Run 项，启动时会与注册表对齐）。</summary>
    public bool LaunchAtLogin { get; set; }
    /// <summary>以管理员权限启动：管理员前台窗口下 Raw Input 才不会被 UIPI 拦截。</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>游戏模式规则：检测到这些进程启动时自动开启鼠标穿透；进程退出后恢复。</summary>
    public string[] GameModeRules { get; set; } = Array.Empty<string>();

    [JsonIgnore] public int PetX { get; set; } = -1;
    [JsonIgnore] public int PetY { get; set; } = -1;
    [JsonIgnore] public string RoomNo { get; set; } = "";
    [JsonIgnore] public string LastRoom { get; set; } = "";
    [JsonIgnore] public string[] RoomHistory { get; set; } = Array.Empty<string>();
    /// <summary>成员位置：key = 成员 id；由 StateStore 持久化，不写入 config.json。</summary>
    [JsonIgnore] public Dictionary<string, int[]> MemberPositions { get; set; } = new();
    /// <summary>联机成员单独显示/隐藏；key = 成员 id，false 表示隐藏，缺省为显示。</summary>
    [JsonIgnore] public Dictionary<string, bool> MemberVisibility { get; set; } = new();
    /// <summary>本地已读公告 id；由 StateStore 持久化，不写入 config.json，也不回传服务器。</summary>
    [JsonIgnore] public List<string> ReadAnnouncementIds { get; set; } = new();
    /// <summary>服务器区服：cn（国服） | global（国际服），默认 cn。</summary>
    public string ServerRegion { get; set; } = "cn";
    /// <summary>公告接口完整地址；只读 GET，不携带用户身份。</summary>
    public string AnnouncementApiUrl { get; set; } = AnnouncementService.DefaultApiUrl;
    public string Locale { get; set; } = "zh";
    /// <summary>全局快捷键：动作 id → 组合键描述；空串表示禁用。缺省项使用原版默认键位。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>首次启动的免责声明是否已经确认过。</summary>
    public bool DisclaimerShown { get; set; }
    /// <summary>首次启动的 UAC 管理员权限询问是否已经回答过。</summary>
    public bool UacPromptShown { get; set; }
    /// <summary>当前头饰相对默认位置的用户微调偏移（key = 配件 asset id，value = [x,y] 逻辑像素）。</summary>
    public Dictionary<string, int[]> AccessoryOffsets { get; set; } = new();
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
                    if (cfg.CloudEmoteAssetIds == null || cfg.CloudEmoteAssetIds.Length != 5)
                    {
                        var cloudSlots = new long[5];
                        if (cfg.CloudEmoteAssetIds != null)
                            Array.Copy(cfg.CloudEmoteAssetIds, cloudSlots, Math.Min(5, cfg.CloudEmoteAssetIds.Length));
                        cfg.CloudEmoteAssetIds = cloudSlots;
                    }
                    cfg.RhythmKeyCount = Math.Clamp(cfg.RhythmKeyCount, 4, 6);
                    bool hasProfiles = cfg.RhythmKeys4 != null || cfg.RhythmKeys5 != null || cfg.RhythmKeys6 != null;
                    cfg.RhythmKeys4 = RhythmKeys.Normalize(cfg.RhythmKeys4 ?? RhythmKeys.DefaultsFor(4), 4);
                    cfg.RhythmKeys5 = RhythmKeys.Normalize(cfg.RhythmKeys5 ?? RhythmKeys.DefaultsFor(5), 5);
                    cfg.RhythmKeys6 = RhythmKeys.Normalize(cfg.RhythmKeys6 ?? RhythmKeys.DefaultsFor(6), 6);
                    if (!hasProfiles)
                        cfg.SetRhythmKeys(cfg.RhythmKeyCount, cfg.RhythmKeys);
                    cfg.RhythmKeys = cfg.GetRhythmKeys(cfg.RhythmKeyCount);
                    if (cfg.RhythmMode && cfg.KeyboardMode == "wuwa") cfg.KeyboardMode = "rhythm";
                    else if (!cfg.RhythmMode && cfg.KeyboardMode == "rhythm") cfg.KeyboardMode = "wuwa";
                    cfg.AutoEmoteMaxPerSecond = Math.Clamp(cfg.AutoEmoteMaxPerSecond, 1, 20);
                    cfg.AutoClickPerSecond = Math.Clamp(cfg.AutoClickPerSecond, 1, 10);
                    cfg.HoverFadeOpacity = Math.Clamp(cfg.HoverFadeOpacity, 0.1, 1.0);
                    cfg.RemoteScale = Math.Clamp(cfg.RemoteScale, 0.25, 2.0);
                    cfg.RemoteOpacity = Math.Clamp(cfg.RemoteOpacity, 0.2, 1.0);
                    cfg.RemoteNameScale = Math.Clamp(cfg.RemoteNameScale, 0.5, 2.0);
                    cfg.AnimationFps = Settings.NormalizeAnimationFps(cfg.AnimationFps);
                    cfg.GameModeRules = GameModeRules.Normalize(cfg.GameModeRules);
                    if (cfg.AutoEmoteMode != "specific" && cfg.AutoEmoteMode != "library") cfg.AutoEmoteMode = "quickbar";
                    cfg.ServerRegion = NormalizeRegion(cfg.ServerRegion);
                    if (string.IsNullOrWhiteSpace(cfg.AnnouncementApiUrl))
                        cfg.AnnouncementApiUrl = AnnouncementService.DefaultApiUrl;
                    cfg.CatLayout = string.Equals(cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";
                    cfg.SynthCheckMode = cfg.SynthCheckMode switch
                    {
                        "same" => "same",
                        "off" => "off",
                        _ => "up",
                    };
                    if (string.IsNullOrWhiteSpace(cfg.SdkDeviceNum) || cfg.SdkDeviceNum.Length < 16)
                        cfg.SdkDeviceNum = Guid.NewGuid().ToString("N");
                    return cfg;
                }
            }
        }
        catch (Exception ex) { Log.Write("配置读取失败 " + ex.Message); }
        var def = new Settings();
        def.ServerRegion = AutoDetectRegion();
        return def;
    }

    static string NormalizeRegion(string region)
    {
        if (string.Equals(region, "global", StringComparison.OrdinalIgnoreCase)) return "global";
        if (string.Equals(region, "cn", StringComparison.OrdinalIgnoreCase)) return "cn";
        return AutoDetectRegion();
    }

    static string AutoDetectRegion()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            bool hasGlobal = Directory.Exists(Path.Combine(local, "com.kuro.wuwatappo.global"));
            bool hasCn = Directory.Exists(Path.Combine(local, "com.kuro.wuwatappo.cn"));
            if (hasGlobal && !hasCn) return "global";
        }
        catch { }
        return "cn";
    }

    public static void Save(Settings cfg)
    {
        // 先写独立状态，再写 config；避免状态写入失败时 config 已经丢掉旧字段。
        StateStore.Save(cfg);
        try
        {
            var tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
            File.Move(tmp, Paths.ConfigFile, true);
        }
        catch (Exception ex) { Log.Write("配置写入失败 " + ex.Message); }
    }
}
