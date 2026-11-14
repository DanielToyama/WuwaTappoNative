using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;
using WuwatappoNative.Ui;

internal static class Program
{
    static int _checks;
    static string _root;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            _root = Path.Combine(Path.GetTempPath(), "wuwa-themechecks-" + Guid.NewGuid().ToString("N"));
            Paths.Root = _root;
            Paths.EnsureDirs();
            Console.WriteLine("Theme checks use isolated data root: " + _root);

            CheckBottomBarColors();
            CheckBarIconContrast();
            CheckBarVisibilityAndHotkeys();
            CheckKeyboardTheme();
            CheckAccessoryTransform();
            CheckCompressionAndCache();
            CheckThemePresetPayloadMapping();
            CheckPresetsAndRotation();
            CheckLegacyPresetFile();
            CheckPresetReorderAndLastDelete();
            CheckOfficialBaseline();
            CheckCompatibilityGate();
            CheckCatOrderScreenSelection();
            if (args.Length > 0 && File.Exists(args[0])) CheckExternalPresetFile(args[0]);

            Console.WriteLine($"PASS: {_checks} assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Paths.Root = null; } catch { }
            try { if (!string.IsNullOrEmpty(_root) && Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }

    static void Assert(bool condition, string message)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(message);
    }

    static void CheckBottomBarColors()
    {
        var cfg = new Settings
        {
            ThemeAccentColor = "#3366CC",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#112233",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#445566",
        };
        var custom = PetWindow.ResolveThemeBarColors(cfg);
        Assert(custom.bar == Color.FromArgb(0x11, 0x22, 0x33), "Custom bar color must be used exactly.");
        Assert(custom.text == Color.FromArgb(0x44, 0x55, 0x66), "Custom bar text color must be used exactly.");
        Assert(custom.accent == ColorTranslator.FromHtml("#3366CC"), "Bottom bar accent must still follow the theme accent.");

        cfg.ThemeBarFollowStandard = true;
        var standard = PetWindow.ResolveThemeBarColors(cfg);
        Assert(standard.bar != custom.bar, "Follow-standard mode must ignore the custom bar color.");
        Assert(standard.text != Color.FromArgb(0x44, 0x55, 0x66), "Follow-standard mode must ignore the custom text color.");

        cfg.ThemeFontColor = "#8E5E43";
        cfg.ThemeAdvanced = "{\"cream\":\"#101010\",\"ink\":\"#202020\"}";
        var advanced = PetWindow.ResolveThemeBarColors(cfg);
        Assert(advanced.text == Color.FromArgb(0x20, 0x20, 0x20),
            "Advanced ink override must reach the pet bar text color.");
        Assert(advanced.bar != standard.bar,
            "Advanced cream override must change the standard pet bar background.");

        // Real composer path: the custom color must reach the actual counter bar pixels.
        using var head = ArtTop(Color.FromArgb(210, 70, 130, 240));
        using var body = ArtTop(Color.FromArgb(200, 200, 70, 50));
        var frames = new PetFrames { Head1 = head, Head2 = head, Body1 = body, Body2 = body, Body3 = body };
        var info = new PetComposer.Info
        {
            Clicks = 123456,
            BarColor = custom.bar,
            BarTextColor = custom.text,
            BarAccentColor = custom.accent,
        };
        using var main = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(main, frames, 1, 1, info);
        var barPixel = main.GetPixel(PetComposer.CounterX + 5, PetComposer.InfoY + 16);
        Assert(barPixel.R == 0x11 && barPixel.G == 0x22 && barPixel.B == 0x33,
            $"Main pet bar pixel is {barPixel}, expected #112233.");

        // Real RemoteCatWindow path: SetTheme must repaint the nickname pill with the same color.
        using var store = new AssetStore();
        var catalog = new Catalog();
        var loader = new PetImageLoader(catalog, store);
        var member = new RoomMember { Nickname = "Theme test" };
        using var remote = new RemoteCatWindow(member, loader, catalog, store);
        SetField(remote, "_frames", frames);
        SetField(remote, "_scale", 1f);
        SetField(remote, "_remoteNameScale", 1.0);
        remote.SetTheme(custom.bar, custom.text);
        using var remoteBitmap = new Bitmap(RemoteCatWindow.W, RemoteCatWindow.H, PixelFormat.Format32bppPArgb);
        remote.ComposeInto(remoteBitmap);
        var pillPixel = remoteBitmap.GetPixel(20, 320);
        Assert(pillPixel.R == 0x11 && pillPixel.G == 0x22 && pillPixel.B == 0x33,
            $"Remote nickname pill pixel is {pillPixel}, expected #112233.");
        Console.WriteLine("PASS: bottom bar custom color reaches main PetComposer strip and RemoteCatWindow nickname pill.");
    }

    /// <summary>
    /// 底栏按钮图标不能直接使用浅色主题色：派生色必须在实际底栏背景上达到 3:1，
    /// 同时保留主题色相，并覆盖浅色/深色、标准/独立底栏、真实绘制、实时预览和主题预设路径。
    /// </summary>
    static void CheckBarIconContrast()
    {
        var lightCfg = new Settings
        {
            ThemeAccentColor = "#FFF3DC",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#FBF4E8",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#7A5B40",
        };
        var light = PetWindow.ResolveThemeBarPalette(lightCfg);
        Assert(light.accent == ColorTranslator.FromHtml("#FFF3DC"),
            "User theme accent must not be replaced by the derived icon color.");
        Assert(light.bar == ColorTranslator.FromHtml("#FBF4E8") && light.text == ColorTranslator.FromHtml("#7A5B40"),
            "Independent bottom bar background/text must keep the user values.");
        Assert(KeyboardTheme.ContrastRatio(light.icon, light.bar) >= 3.0,
            $"Light accent icon contrast is {KeyboardTheme.ContrastRatio(light.icon, light.bar):F2}, expected >= 3.0.");
        Assert(KeyboardTheme.ContrastRatio(light.iconMuted, light.bar) >= 3.0,
            $"Light accent muted icon contrast is {KeyboardTheme.ContrastRatio(light.iconMuted, light.bar):F2}, expected >= 3.0.");
        Assert(light.icon.GetBrightness() < light.accent.GetBrightness(),
            "Light theme accent on a light bar must be darkened for the icon.");
        Assert(HueDistance(light.icon, light.accent) <= 10,
            $"Warm icon must keep the theme hue, got {light.icon.GetHue():F1} vs {light.accent.GetHue():F1}.");

        // 标准主题底栏（默认奶油底 + 浅色主题色）同样不能把主题色直接当图标色。
        var standardLightCfg = new Settings { ThemeAccentColor = "#FFF3DC", ThemeFontColor = "#8E5E43" };
        var standardLight = PetWindow.ResolveThemeBarPalette(standardLightCfg);
        Assert(KeyboardTheme.ContrastRatio(standardLight.icon, standardLight.bar) >= 3.0,
            $"Standard light bar icon contrast is {KeyboardTheme.ContrastRatio(standardLight.icon, standardLight.bar):F2}, expected >= 3.0.");
        Assert(standardLight.icon.GetBrightness() < standardLight.accent.GetBrightness(),
            "Standard light bar must darken the light theme accent for icons.");

        // 冷色浅主题同样不能被固定成棕色。
        var coolCfg = new Settings
        {
            ThemeAccentColor = "#DCEBFF",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#F5F9FF",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#5B6B80",
        };
        var cool = PetWindow.ResolveThemeBarPalette(coolCfg);
        Assert(KeyboardTheme.ContrastRatio(cool.icon, cool.bar) >= 3.0,
            $"Cool light icon contrast is {KeyboardTheme.ContrastRatio(cool.icon, cool.bar):F2}, expected >= 3.0.");
        Assert(cool.icon.B > cool.icon.R,
            "Cool light theme must derive a blue-leaning dark icon, not a fixed brown.");
        Assert(HueDistance(cool.icon, cool.accent) <= 10,
            $"Cool icon must keep the theme hue, got {cool.icon.GetHue():F1} vs {cool.accent.GetHue():F1}.");

        // 深色独立底栏：深色主题色必须向浅色方向提亮，不能继续变暗。
        var darkCfg = new Settings
        {
            ThemeAccentColor = "#223344",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#101820",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#DCE6F2",
        };
        var dark = PetWindow.ResolveThemeBarPalette(darkCfg);
        Assert(KeyboardTheme.ContrastRatio(dark.icon, dark.bar) >= 3.0,
            $"Dark bar icon contrast is {KeyboardTheme.ContrastRatio(dark.icon, dark.bar):F2}, expected >= 3.0.");
        Assert(dark.icon.GetBrightness() > dark.accent.GetBrightness(),
            "Dark theme color on a dark bar must be lightened for the icon.");
        Assert(HueDistance(dark.icon, dark.accent) <= 10,
            $"Dark-bar icon must keep the theme hue, got {dark.icon.GetHue():F1} vs {dark.accent.GetHue():F1}.");

        // 标准主题底栏被高级 cream 调成深色时同样达标。
        var darkStandardCfg = new Settings
        {
            ThemeAccentColor = "#E7B77C",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = true,
            ThemeAdvanced = "{\"cream\":\"#101010\"}",
        };
        var darkStandard = PetWindow.ResolveThemeBarPalette(darkStandardCfg);
        Assert(KeyboardTheme.ContrastRatio(darkStandard.icon, darkStandard.bar) >= 3.0,
            "Dark advanced standard bar must keep the derived icon readable.");

        // 真实合成路径：键盘关闭/开启两种图标都要在底栏像素上达到 3:1。
        using (var offStrip = RenderPetStrip(light.bar, light.text, light.icon, light.iconMuted, keyboardOn: false))
        {
            var barPixel = offStrip.GetPixel(PetComposer.ToolbarX - 4, PetComposer.InfoY + 16);
            Assert(Near(barPixel, light.bar), $"Light bar pixel is {barPixel}, expected {light.bar}.");
            double kb = MaxIconContrast(offStrip, PetComposer.KeyboardRect, light.bar);
            double menu = MaxIconContrast(offStrip, PetComposer.MenuRect, light.bar);
            Assert(kb >= 3.0 && menu >= 3.0,
                $"Composed light bar icons must reach 3:1, got keyboard={kb:F2}, menu={menu:F2}.");
        }
        using (var onStrip = RenderPetStrip(light.bar, light.text, light.icon, light.iconMuted, keyboardOn: true))
        {
            double kb = MaxIconContrast(onStrip, PetComposer.KeyboardRect, light.bar);
            double menu = MaxIconContrast(onStrip, PetComposer.MenuRect, light.bar);
            Assert(kb >= 3.0 && menu >= 3.0,
                $"Composed light bar keyboard-on icons must reach 3:1, got keyboard={kb:F2}, menu={menu:F2}.");
        }
        // 兜底路径：调用方把未处理的浅色主题色直接塞进 Info，绘制端也必须产出可读图标。
        using (var rawStrip = RenderPetStrip(light.bar, light.text, light.accent, Color.Empty, keyboardOn: true))
        {
            double kb = MaxIconContrast(rawStrip, PetComposer.KeyboardRect, light.bar);
            double menu = MaxIconContrast(rawStrip, PetComposer.MenuRect, light.bar);
            Assert(kb >= 3.0 && menu >= 3.0,
                $"Draw-time icon guard must keep raw light accent readable, got keyboard={kb:F2}, menu={menu:F2}.");
        }

        // 实时预览：PreviewTheme 必须走同一套 ResolveThemeBarPalette，并把派生图标色带进预览缓存。
        var previewCfg = new Settings
        {
            ThemeAccentColor = "#FFF3DC",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#FBF4E8",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#7A5B40",
        };
        var preview = PetWindow.ResolveThemeBarPalette(previewCfg);
        Assert(preview.accent == ColorTranslator.FromHtml("#FFF3DC") && preview.bar == ColorTranslator.FromHtml("#FBF4E8"),
            "Preview must keep the user accent and independent bar color.");
        Assert(KeyboardTheme.ContrastRatio(preview.icon, preview.bar) >= 3.0 &&
               KeyboardTheme.ContrastRatio(preview.iconMuted, preview.bar) >= 3.0,
            "Preview icons must stay readable on the preview bar.");
        using (var previewStrip = RenderPetStrip(preview.bar, preview.text, preview.icon, preview.iconMuted, keyboardOn: false))
        {
            Assert(MaxIconContrast(previewStrip, PetComposer.KeyboardRect, preview.bar) >= 3.0 &&
                   MaxIconContrast(previewStrip, PetComposer.MenuRect, preview.bar) >= 3.0,
                "Preview strip icons must reach 3:1 on the preview bar.");
        }
        string petSource = ReadRepoSource("src", "Pet", "PetWindow.cs");
        Assert(Regex.IsMatch(petSource,
                @"public\s+void\s+PreviewTheme\(Settings\s+preview\)[\s\S]*?ResolveThemeBarPalette\(preview\);[\s\S]*?_themePreview\s*=\s*theme;"),
            "PreviewTheme must resolve the preview palette and store it for BuildInfo.");
        Assert(petSource.Contains("BarAccentColor = barIconColor") &&
               petSource.Contains("BarAccentMutedColor = barIconMutedColor"),
            "BuildInfo must pass the derived icon colors into PetComposer.");

        // 主题预设：应用浅色预设后仍要得到同一套可读图标色。
        string presetFile = Path.Combine(_root, "theme_presets_icon.json");
        var presetCfg = new Settings
        {
            ThemeAccentColor = "#FFF3DC",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#FBF4E8",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#7A5B40",
        };
        using (var manager = new ThemePresetManager(presetCfg, presetFile))
        {
            var preset = manager.SaveCurrent("Light icon");
            presetCfg.ThemeAccentColor = "#101820";
            presetCfg.ThemeBarColor = "#101820";
            manager.Apply(preset.Id);
            var applied = PetWindow.ResolveThemeBarPalette(presetCfg);
            Assert(applied.accent == ColorTranslator.FromHtml("#FFF3DC") &&
                   applied.bar == ColorTranslator.FromHtml("#FBF4E8"),
                "Applying a theme preset must restore the saved light accent/bar.");
            Assert(KeyboardTheme.ContrastRatio(applied.icon, applied.bar) >= 3.0 &&
                   KeyboardTheme.ContrastRatio(applied.iconMuted, applied.bar) >= 3.0,
                "Theme preset application must keep bar icons readable.");
        }

        Console.WriteLine("PASS: bar icon contrast (light/dark/standard/independent/preset) >= 3:1 with hue preserved.");
        Console.WriteLine("PASS: real PetComposer strip and live PreviewTheme paths draw readable keyboard/menu icons.");
    }

    static void CheckBarVisibilityAndHotkeys()
    {
        // 默认全部关闭；三个底栏快捷键默认必须为空，不能占用任何默认键位。
        var defaults = new Settings();
        Assert(!defaults.HideMainBar && !defaults.HideRemoteBars && !defaults.SeparateBarHotkeys,
            "Bar visibility and separate-hotkey switches must default to false.");
        string[] barActions = { "barToggle", "barToggleMain", "barToggleRemote" };
        foreach (string id in barActions)
        {
            Assert(Hotkeys.Actions.Any(a => a.Id == id), "Missing hotkey action: " + id);
            Assert(Hotkeys.DefaultFor(id) == "", $"Default hotkey for {id} must be empty.");
        }
        var defaultTable = Hotkeys.FromSettings(null);
        foreach (string id in barActions)
            Assert(defaultTable[id] == "", $"FromSettings must leave {id} unbound by default.");

        // 单/双模式只筛选当前启用的动作；另一模式的绑定原样保留。
        var saved = new Dictionary<string, string>
        {
            ["barToggle"] = "Ctrl+Alt+B",
            ["barToggleMain"] = "Ctrl+Alt+M",
            ["barToggleRemote"] = "Ctrl+Alt+R",
        };
        var all = Hotkeys.FromSettings(saved);
        Assert(all["barToggle"] == "Ctrl+Alt+B" && all["barToggleMain"] == "Ctrl+Alt+M" &&
               all["barToggleRemote"] == "Ctrl+Alt+R",
            "FromSettings must preserve all three bar bindings.");
        var single = Hotkeys.ActiveFromSettings(saved, false);
        Assert(single.ContainsKey("barToggle") && !single.ContainsKey("barToggleMain") &&
               !single.ContainsKey("barToggleRemote"),
            "Single mode must register only barToggle.");
        var separate = Hotkeys.ActiveFromSettings(saved, true);
        Assert(!separate.ContainsKey("barToggle") && separate.ContainsKey("barToggleMain") &&
               separate.ContainsKey("barToggleRemote"),
            "Separate mode must register only barToggleMain/barToggleRemote.");

        var merged = Hotkeys.Merge(saved, new Dictionary<string, string> { ["barToggle"] = "Ctrl+Alt+9" });
        var mergedTable = Hotkeys.FromSettings(merged);
        Assert(mergedTable["barToggle"] == "Ctrl+Alt+9" &&
               mergedTable["barToggleMain"] == "Ctrl+Alt+M" &&
               mergedTable["barToggleRemote"] == "Ctrl+Alt+R",
            "Saving one mode's visible rows must not clear the hidden mode bindings.");

        // 只检查当前激活动作：隐藏动作与 barToggle 同键不算冲突。
        var conflictTable = Hotkeys.FromSettings(new Dictionary<string, string>
        {
            ["barToggle"] = "Ctrl+Alt+B",
            ["barToggleMain"] = "Ctrl+Alt+B",
            ["barToggleRemote"] = "Ctrl+Alt+B",
        });
        Assert(Hotkeys.FindConflict(conflictTable, false) == null,
            "Hidden separate-mode bindings must not cause a conflict in single mode.");
        Assert(Hotkeys.FindConflict(conflictTable, true) != null,
            "Duplicate active separate-mode bindings must be rejected.");

        // 设置持久化。
        var cfg = new Settings
        {
            HideMainBar = true,
            HideRemoteBars = true,
            SeparateBarHotkeys = true,
            Hotkeys = new Dictionary<string, string>(saved),
        };
        ConfigStore.Save(cfg);
        var reloaded = ConfigStore.Load();
        Assert(reloaded.HideMainBar && reloaded.HideRemoteBars && reloaded.SeparateBarHotkeys,
            "Bar visibility/separate-hotkey settings must persist through config.json.");
        Assert(reloaded.Hotkeys["barToggleMain"] == "Ctrl+Alt+M" &&
               reloaded.Hotkeys["barToggleRemote"] == "Ctrl+Alt+R",
            "Bar hotkey bindings must persist through config.json.");

        // 主角底栏像素：显示时计数条不透明，隐藏时该区域透明而角色本体仍在。
        using var head = ArtTop(Color.FromArgb(255, 70, 130, 240));
        using var body = ArtTop(Color.FromArgb(255, 200, 70, 50));
        var frames = new PetFrames { Head1 = head, Head2 = head, Body1 = body, Body2 = body, Body3 = body };
        var showInfo = new PetComposer.Info { Clicks = 42, ShowBar = true };
        var hideInfo = new PetComposer.Info { Clicks = 42, ShowBar = false };
        int barX = PetComposer.CounterX + 5, barY = PetComposer.InfoY + 16;
        int catX = PetComposer.CatX + 100, catY = PetComposer.CatY + 10;

        using var mainVisible = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(mainVisible, frames, 1, 1, showInfo);
        Assert(mainVisible.GetPixel(barX, barY).A > 200, "Main bar must be painted when ShowBar=true.");
        Assert(mainVisible.GetPixel(catX, catY).A > 200, "Main character must stay visible with the bar shown.");

        using var mainHidden = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(mainHidden, frames, 1, 1, hideInfo);
        Assert(mainHidden.GetPixel(barX, barY).A < 20, "Main bar area must be transparent when ShowBar=false.");
        Assert(mainHidden.GetPixel(catX, catY).A > 200, "Main character must stay fully visible when the bar is hidden.");

        // 同一个 FrameCache 先画显示、再画隐藏、再画显示：不能复用旧底栏缓存。
        using (var cache = new PetComposer.FrameCache())
        {
            using var first = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
            using var hidden = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
            using var restored = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
            PetComposer.ComposeInto(first, frames, 1, 1, showInfo, cache);
            PetComposer.ComposeInto(hidden, frames, 1, 1, hideInfo, cache);
            PetComposer.ComposeInto(restored, frames, 1, 1, showInfo, cache);
            Assert(first.GetPixel(barX, barY).A > 200, "Cached first frame must show the main bar.");
            Assert(hidden.GetPixel(barX, barY).A < 20,
                "FrameCache must not reuse the visible bar bitmap when ShowBar turns false.");
            Assert(restored.GetPixel(barX, barY).A > 200,
                "FrameCache must redraw the main bar after ShowBar turns true again.");
        }

        // 远程昵称条像素：隐藏时 pill 透明、角色本体保留；缓存 key 必须包含该状态。
        using var store = new AssetStore();
        var catalog = new Catalog();
        var loader = new PetImageLoader(catalog, store);
        var member = new RoomMember { Nickname = "Bar test" };
        using var remote = new RemoteCatWindow(member, loader, catalog, store);
        SetField(remote, "_frames", frames);
        SetField(remote, "_scale", 1f);
        SetField(remote, "_remoteNameScale", 1.0);
        using var remoteVisible = new Bitmap(RemoteCatWindow.W, RemoteCatWindow.H, PixelFormat.Format32bppPArgb);
        remote.ComposeInto(remoteVisible);
        Assert(remoteVisible.GetPixel(20, 320).A > 200, "Remote nickname pill must be painted by default.");
        Assert(remoteVisible.GetPixel(100, 200).A > 200, "Remote character must be visible with the pill shown.");

        remote.SetBarVisible(false);
        Assert(!remote.BarVisible, "SetBarVisible(false) must update the state.");
        using var remoteHidden = new Bitmap(RemoteCatWindow.W, RemoteCatWindow.H, PixelFormat.Format32bppPArgb);
        remote.ComposeInto(remoteHidden);
        Assert(remoteHidden.GetPixel(20, 320).A < 20, "Remote nickname pill must be transparent when hidden.");
        Assert(remoteHidden.GetPixel(100, 200).A > 200, "Remote character must remain visible when the pill is hidden.");

        remote.SetBarVisible(true);
        using var remoteRestored = new Bitmap(RemoteCatWindow.W, RemoteCatWindow.H, PixelFormat.Format32bppPArgb);
        remote.ComposeInto(remoteRestored);
        Assert(remoteRestored.GetPixel(20, 320).A > 200,
            "Remote pill must return after SetBarVisible(true); stale _baseFrameKey cache suspected.");

        Console.WriteLine("PASS: bar visibility defaults, pixels, frame-cache invalidation, persistence and single/separate hotkey filtering.");
    }

    static void CheckKeyboardTheme()
    {
        var warmCfg = new Settings { ThemeAccentColor = "#E7B77C", ThemeFontColor = "#8E5E43" };
        var coolCfg = new Settings { ThemeAccentColor = "#3366CC", ThemeFontColor = "#8E5E43" };
        var warm = KeyboardTheme.Resolve(warmCfg);
        var cool = KeyboardTheme.Resolve(coolCfg);
        AssertPadPressedContrast(warm, "Warm");
        AssertPadPressedContrast(cool, "Cool");

        // 浅色主题/低饱和主题：active 不再能直接当按下态，派生色必须仍与本体、扳机槽、摇杆槽有 3:1。
        var paleCfg = new Settings { ThemeAccentColor = "#F4EBDD", ThemeFontColor = "#8E5E43" };
        var pale = KeyboardTheme.Resolve(paleCfg);
        AssertPadPressedContrast(pale, "Pale standard");
        Assert(HueDistance(pale.PadPressed, pale.Active) <= 10,
            $"Pale standard pressed state must keep the theme hue, got {pale.PadPressed.GetHue():F1} vs {pale.Active.GetHue():F1}.");
        Assert(pale.PadPressed != pale.Active,
            "Pale standard PadPressed must be derived instead of reusing the too-light active color.");
        Assert(pale.KeyPressed == pale.Active && pale.ToolbarActive == pale.Active,
            "Keyboard and toolbar pressed states must not be altered by the gamepad pressed-state fix.");

        var lowSatCfg = new Settings { ThemeAccentColor = "#B8B8B8", ThemeFontColor = "#8E5E43" };
        var lowSat = KeyboardTheme.Resolve(lowSatCfg);
        AssertPadPressedContrast(lowSat, "Low-saturation standard");

        var paleIndependentCfg = new Settings
        {
            ThemeAccentColor = "#F2E9DD",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#F7F3EC",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#3F3A34",
        };
        var paleIndependent = KeyboardTheme.Resolve(paleIndependentCfg);
        AssertPadPressedContrast(paleIndependent, "Pale independent");

        var darkIndependentCfg = new Settings
        {
            ThemeAccentColor = "#223344",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#101820",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#F0F4F8",
        };
        var darkIndependent = KeyboardTheme.Resolve(darkIndependentCfg);
        AssertPadPressedContrast(darkIndependent, "Dark independent");
        Assert(darkIndependent.PadPressed.GetBrightness() > darkIndependent.PadBody.GetBrightness(),
            "Dark independent palette must lighten the pressed state instead of blending into the body.");

        Assert(warm.ToolbarBackground != cool.ToolbarBackground,
            "Toolbar background must follow the theme accent.");
        Assert(warm.ToolbarBorder != cool.ToolbarBorder, "Toolbar border must follow the theme accent.");
        Assert(warm.PlateTop != cool.PlateTop && warm.PlateBottom != cool.PlateBottom,
            "Keyboard plate gradient must follow the theme accent.");
        Assert(warm.KeyTop != cool.KeyTop && warm.KeyPressed != cool.KeyPressed,
            "Keycaps and pressed state must follow the theme accent.");
        Assert(warm.KeyShadow != cool.KeyShadow, "Keycap shadow must follow the theme accent.");
        Assert(warm.PadBody != cool.PadBody && warm.PadOutline != cool.PadOutline,
            "Controller body and outline must follow the theme accent.");
        Assert(warm.PadPressed != cool.PadPressed && warm.PadTriggerTrack != cool.PadTriggerTrack,
            "Controller pressed state and trigger track must follow the theme accent.");
        Assert(warm.LaneNumber != cool.LaneNumber, "Rhythm lane numbers must follow the theme accent.");

        // 文字必须和各自背景保持可读对比度（正常 / 按下 / 工具栏 / 手柄）。
        Assert(KeyboardTheme.ContrastRatio(warm.KeyText, warm.KeyTop) >= 3.0,
            $"Warm key text contrast too low: {KeyboardTheme.ContrastRatio(warm.KeyText, warm.KeyTop):F2}");
        Assert(KeyboardTheme.ContrastRatio(warm.KeyTextPressed, warm.KeyPressed) >= 3.0,
            $"Warm pressed key text contrast too low: {KeyboardTheme.ContrastRatio(warm.KeyTextPressed, warm.KeyPressed):F2}");
        Assert(KeyboardTheme.ContrastRatio(warm.ToolbarText, warm.ToolbarBackground) >= 3.0 &&
               KeyboardTheme.ContrastRatio(warm.ToolbarText, warm.ToolbarActive) >= 3.0,
            "Warm toolbar text must stay readable on normal and active buttons.");
        Assert(KeyboardTheme.ContrastRatio(warm.PadText, warm.PadBody) >= 3.0 &&
               KeyboardTheme.ContrastRatio(warm.PadText, warm.PadPressed) >= 3.0,
            "Warm controller text must stay readable on body and pressed states.");
        Assert(KeyboardTheme.ContrastRatio(cool.KeyText, cool.KeyTop) >= 3.0 &&
               KeyboardTheme.ContrastRatio(cool.PadText, cool.PadBody) >= 3.0,
            "Cool theme text must stay readable.");

        // 高级配色也可能给出深色底板 + 浅色文字，不能因为取色器值而丢可读性。
        var darkCfg = new Settings
        {
            ThemeAccentColor = "#3366CC",
            ThemeFontColor = "#8E5E43",
            ThemeAdvanced = "{\"cream\":\"#101010\",\"cream2\":\"#181818\",\"cream3\":\"#202020\"," +
                            "\"cream4\":\"#282828\",\"ink\":\"#F0F0F0\",\"active\":\"#445566\"," +
                            "\"border\":\"#8899AA\"}",
        };
        var dark = KeyboardTheme.Resolve(darkCfg);
        AssertPadPressedContrast(dark, "Dark advanced");
        Assert(KeyboardTheme.ContrastRatio(dark.KeyText, dark.KeyTop) >= 3.0 &&
               KeyboardTheme.ContrastRatio(dark.KeyTextPressed, dark.KeyPressed) >= 3.0 &&
               KeyboardTheme.ContrastRatio(dark.ToolbarText, dark.ToolbarBackground) >= 3.0 &&
               KeyboardTheme.ContrastRatio(dark.PadText, dark.PadBody) >= 3.0,
            "Dark advanced palette must keep keyboard/controller text readable.");

        // 高级配色必须真正进入调色板，而不是仍用暖橙默认值。
        var advancedCfg = new Settings
        {
            ThemeAccentColor = "#3366CC",
            ThemeFontColor = "#8E5E43",
            ThemeAdvanced = "{\"cream\":\"#101820\",\"cream2\":\"#202830\",\"cream3\":\"#303840\"," +
                            "\"cream4\":\"#F0F4F8\",\"ink\":\"#1A1A1A\",\"inkStrong\":\"#000000\"," +
                            "\"active\":\"#C0D0E0\",\"border\":\"#405060\"}",
        };
        var advanced = KeyboardTheme.Resolve(advancedCfg);
        AssertPadPressedContrast(advanced, "Advanced palette");
        Assert(advanced.Cream == ColorTranslator.FromHtml("#101820"), "Advanced cream override was ignored.");
        Assert(advanced.Cream2 == ColorTranslator.FromHtml("#202830"), "Advanced cream2 override was ignored.");
        Assert(advanced.Cream3 == ColorTranslator.FromHtml("#303840"), "Advanced cream3 override was ignored.");
        Assert(advanced.Cream4 == ColorTranslator.FromHtml("#F0F4F8"), "Advanced cream4 override was ignored.");
        Assert(advanced.Ink == ColorTranslator.FromHtml("#1A1A1A"), "Advanced ink override was ignored.");
        Assert(advanced.InkStrong == Color.FromArgb(0, 0, 0), "Advanced inkStrong override was ignored.");
        Assert(advanced.Active == ColorTranslator.FromHtml("#C0D0E0"), "Advanced active override was ignored.");
        Assert(advanced.Border == ColorTranslator.FromHtml("#405060"), "Advanced border override was ignored.");

        // 用户填了独立字体色时以用户值为准，不再被 advanced ink 覆盖（与 PetWindow 一致）。
        var customFontCfg = new Settings
        {
            ThemeAccentColor = "#3366CC",
            ThemeFontColor = "#AA3300",
            ThemeAdvanced = "{\"ink\":\"#1A1A1A\"}",
        };
        Assert(KeyboardTheme.Resolve(customFontCfg).Ink == ColorTranslator.FromHtml("#AA3300"),
            "Explicit theme font color must win over advanced ink, matching PetWindow.");

        // 独立底栏配色：所有键盘窗口模式统一使用独立底栏色；跟随标准时回退面板主题。
        var customBarCfg = new Settings
        {
            ThemeAccentColor = "#E7B77C",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#3366CC",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#DDEEFF",
        };
        var independent = KeyboardTheme.Resolve(customBarCfg);
        Assert(independent.Bar == ColorTranslator.FromHtml("#3366CC") &&
               independent.Accent == ColorTranslator.FromHtml("#3366CC"),
            "All keyboard window modes must use the independent bottom-bar accent exactly.");
        Assert(independent.Ink == ColorTranslator.FromHtml("#DDEEFF"),
            "All keyboard window modes must use the independent bottom-bar text color.");
        Assert(independent.KeyTop != warm.KeyTop && independent.PadBody != warm.PadBody &&
               independent.ToolbarBackground != warm.ToolbarBackground && independent.LaneNumber != warm.LaneNumber,
            "Independent bottom-bar colors must replace the panel palette for keyboard/rhythm/wuwa/controller.");
        Assert(independent.KeyTop.B > independent.KeyTop.R + 10 &&
               independent.PadBody.B > independent.PadBody.R + 10 &&
               independent.PadOutline.B > independent.PadOutline.R + 10 &&
               independent.PadPressed.B > independent.PadPressed.R + 10 &&
               independent.PadTriggerFill.B > independent.PadTriggerFill.R + 10,
            "Independent keyboard/gamepad body/outline/pressed/trigger must be blue-derived, not warm orange.");

        var followTextCfg = new Settings
        {
            ThemeAccentColor = "#E7B77C",
            ThemeFontColor = "#8E5E43",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#3366CC",
            ThemeBarTextFollowTheme = true,
            ThemeBarTextColor = "#DDEEFF",
        };
        var followTextPalette = KeyboardTheme.Resolve(followTextCfg);
        Assert(followTextPalette.Ink == warm.Ink,
            "All modes must follow the theme font when ThemeBarTextFollowTheme is enabled.");
        Assert(KeyboardTheme.ContrastRatio(followTextPalette.PadText, followTextPalette.PadBody) >= 3.0 &&
               KeyboardTheme.ContrastRatio(
                   followTextPalette.PadTextFor(followTextPalette.PadPressedFor(followTextPalette.PadBody)),
                   followTextPalette.PadPressedFor(followTextPalette.PadBody)) >= 3.0,
            "Theme-following text must remain readable on independent bar colors.");
        Assert(KeyboardTheme.ContrastRatio(independent.PadText, independent.PadBody) >= 3.0 &&
               KeyboardTheme.ContrastRatio(
                   independent.PadTextFor(independent.PadPressedFor(independent.PadBody)),
                   independent.PadPressedFor(independent.PadBody)) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.PadFaceText, Color.FromArgb(252, 226, 156)) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.PadFaceText, Color.FromArgb(214, 233, 248)) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.PadFaceText, Color.FromArgb(250, 217, 212)) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.PadFaceText, Color.FromArgb(215, 237, 208)) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.ToolbarText, independent.ToolbarBackground) >= 3.0 &&
               KeyboardTheme.ContrastRatio(independent.ToolbarText, independent.ToolbarActive) >= 3.0,
            "Independent bottom-bar colors must keep keyboard/gamepad text readable.");

        // 跟随标准时必须回退到面板主题，包括普通键盘和手柄。
        var standardPalette = KeyboardTheme.Resolve(warmCfg);
        Assert(standardPalette.Accent == warm.Accent && standardPalette.KeyTop == warm.KeyTop &&
               standardPalette.PadBody == warm.PadBody && standardPalette.PadOutline == warm.PadOutline,
            "All modes must keep the panel palette when the bottom bar follows the standard theme.");

        // 真实绘制路径：不 Show 窗口，直接 DrawTo 到隔离位图，按像素验证三种模式。
        using var window = new KeyboardWindow();
        window.ApplyTheme(warmCfg);
        SetField(window, "_mode", KeyboardWindow.Mode.Letters);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Letters);
        using var warmLetters = RenderKeyboard(window, 145);
        var warmKeyPixel = warmLetters.GetPixel(14, 40);
        Assert(Near(warmKeyPixel, warm.KeyTop), $"Warm letters key pixel is {warmKeyPixel}, expected {warm.KeyTop}.");

        window.ApplyTheme(coolCfg);
        using var coolLetters = RenderKeyboard(window, 145);
        var coolKeyPixel = coolLetters.GetPixel(14, 40);
        Assert(!Near(warmKeyPixel, coolKeyPixel),
            $"Letters key must change between themes, got {warmKeyPixel} and {coolKeyPixel}.");
        Assert(Near(coolKeyPixel, cool.KeyTop), $"Cool letters key pixel is {coolKeyPixel}, expected {cool.KeyTop}.");

        var down = GetField<HashSet<string>>(window, "_down");
        down.Add("Q");
        using var pressedLetters = RenderKeyboard(window, 145);
        var pressedKeyPixel = pressedLetters.GetPixel(14, 40);
        Assert(!Near(pressedKeyPixel, coolKeyPixel),
            $"Pressed key must be visibly different from idle, got {pressedKeyPixel} and {coolKeyPixel}.");
        Assert(Near(pressedKeyPixel, cool.KeyPressed),
            $"Pressed key pixel is {pressedKeyPixel}, expected {cool.KeyPressed}.");
        down.Clear();

        // 取色预览：PreviewTheme 立即生效，ClearThemePreview 恢复正式配置。
        window.ApplyTheme(warmCfg);
        window.PreviewTheme(coolCfg);
        using var previewLetters = RenderKeyboard(window, 145);
        Assert(Near(previewLetters.GetPixel(14, 40), cool.KeyTop), "Keyboard preview did not use preview colors.");
        window.ClearThemePreview();
        using var restoredLetters = RenderKeyboard(window, 145);
        Assert(Near(restoredLetters.GetPixel(14, 40), warm.KeyTop), "Clearing preview did not restore formal colors.");

        // 音游：同样按像素验证键帽和轨道文字随主题变化。
        window.ApplyTheme(warmCfg);
        SetField(window, "_mode", KeyboardWindow.Mode.Rhythm);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Rhythm);
        using var warmRhythm = RenderKeyboard(window, 110);
        var warmRhythmPixel = warmRhythm.GetPixel(20, 48);
        Assert(Near(warmRhythmPixel, warm.KeyTop), $"Warm rhythm key pixel is {warmRhythmPixel}, expected {warm.KeyTop}.");

        window.ApplyTheme(coolCfg);
        using var coolRhythm = RenderKeyboard(window, 110);
        var coolRhythmPixel = coolRhythm.GetPixel(20, 48);
        Assert(!Near(warmRhythmPixel, coolRhythmPixel),
            $"Rhythm key must change between themes, got {warmRhythmPixel} and {coolRhythmPixel}.");
        Assert(cool.LaneNumber != warm.LaneNumber, "Rhythm lane number color must be theme-derived.");

        // 手柄：不连接也能渲染（这里注入连接状态），验证本体和 ABXY 按下态。
        SetField(window, "_padConnected", true);
        SetField(window, "_padButtons", (ushort)0);
        SetField(window, "_mode", KeyboardWindow.Mode.Controller);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Controller);
        window.ApplyTheme(warmCfg);
        using var warmPad = RenderKeyboard(window, 180);
        var warmBodyPixel = warmPad.GetPixel(104, 107);
        Assert(Near(warmBodyPixel, warm.PadBody), $"Warm controller body pixel is {warmBodyPixel}, expected {warm.PadBody}.");
        var warmFacePixel = warmPad.GetPixel(137, 81);

        window.ApplyTheme(coolCfg);
        using var coolPad = RenderKeyboard(window, 180);
        var coolBodyPixel = coolPad.GetPixel(104, 107);
        Assert(!Near(warmBodyPixel, coolBodyPixel),
            $"Controller body must change between themes, got {warmBodyPixel} and {coolBodyPixel}.");
        Assert(Near(coolBodyPixel, cool.PadBody), $"Cool controller body pixel is {coolBodyPixel}, expected {cool.PadBody}.");

        // 浅色主题真实 DrawTo：D-pad、LB、Guide、Back、LS、LT 的按下区域必须实际出现 PadPressed。
        window.ApplyTheme(paleCfg);
        SetField(window, "_padButtons", (ushort)0);
        SetField(window, "_lt", (byte)0);
        Color paleFaceIdle;
        using (var paleIdlePad = RenderKeyboard(window, 180))
            paleFaceIdle = paleIdlePad.GetPixel(137, 81);
        SetField(window, "_padButtons", (ushort)(0x0001 | 0x0100 | 0x0400 | 0x0020 | 0x0040 | 0x1000));
        SetField(window, "_lt", (byte)255);
        using (var palePressedPad = RenderKeyboard(window, 180))
        {
            var regions = new (string Label, Rectangle Rect, Color Pressed)[]
            {
                ("D-pad", new Rectangle(40, 56, 34, 34), pale.PadPressedFor(pale.PadBody)),
                ("LB", new Rectangle(44, 40, 26, 10), pale.PadPressedFor(pale.PadBody)),
                ("Guide", new Rectangle(92, 58, 18, 18), pale.PadPressedFor(pale.PadBody)),
                ("Back", new Rectangle(84, 82, 10, 6), pale.PadPressedFor(pale.Cream2)),
                ("LS", new Rectangle(62, 104, 32, 32), pale.PadPressedFor(pale.PadStickCap)),
                ("LT", new Rectangle(42, 30, 30, 10), pale.PadPressedFor(pale.PadTriggerTrack)),
            };
            foreach (var (label, rect, pressed) in regions)
                Assert(RegionContainsColor(palePressedPad, rect, pressed),
                    $"Pale controller pressed {label} must be drawn with its readable pressed color ({pressed}).");
            var paleFacePressed = palePressedPad.GetPixel(137, 81);
            Assert(!Near(paleFaceIdle, paleFacePressed),
                $"Pale controller A button pressed state must be visible, got {paleFaceIdle} and {paleFacePressed}.");
        }
        SetField(window, "_padButtons", (ushort)0);
        SetField(window, "_lt", (byte)0);

        // 面板暖橙 + 独立底栏蓝：手柄、普通键盘、音游、鸣潮都必须使用蓝色。
        window.ApplyTheme(customBarCfg);
        using var independentPad = RenderKeyboard(window, 180);
        var independentBodyPixel = independentPad.GetPixel(104, 107);
        Assert(!Near(warmBodyPixel, independentBodyPixel),
            $"Independent palette must change the controller body, got {warmBodyPixel} and {independentBodyPixel}.");
        Assert(independentBodyPixel.B > independentBodyPixel.R + 10,
            $"Independent controller body pixel must be blue-derived, got {independentBodyPixel}.");
        Assert(Near(independentBodyPixel, independent.PadBody),
            $"Independent controller body pixel is {independentBodyPixel}, expected {independent.PadBody}.");

        var independentFacePixel = independentPad.GetPixel(137, 81);
        SetField(window, "_padButtons", (ushort)0x1000);
        using var independentPressedPad = RenderKeyboard(window, 180);
        var independentPressedFacePixel = independentPressedPad.GetPixel(137, 81);
        Assert(!Near(independentFacePixel, independentPressedFacePixel),
            $"Independent controller pressed state must be visible, got {independentFacePixel} and {independentPressedFacePixel}.");
        SetField(window, "_padButtons", (ushort)0);

        SetField(window, "_mode", KeyboardWindow.Mode.Letters);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Letters);
        using var independentLetters = RenderKeyboard(window, 145);
        var independentKeyPixel = independentLetters.GetPixel(14, 40);
        Assert(Near(independentKeyPixel, independent.KeyTop),
            $"Letters must use the independent bar palette, got {independentKeyPixel}, expected {independent.KeyTop}.");
        Assert(independentKeyPixel.B > independentKeyPixel.R + 10,
            $"Independent letters key pixel must be blue-derived, got {independentKeyPixel}.");

        // 键盘预览：从面板主题拖到独立底栏色，再取消恢复正式面板主题。
        window.ApplyTheme(warmCfg);
        window.PreviewTheme(customBarCfg);
        using var independentPreviewLetters = RenderKeyboard(window, 145);
        Assert(Near(independentPreviewLetters.GetPixel(14, 40), independent.KeyTop),
            "Letters preview did not use the independent bottom-bar palette.");
        window.ClearThemePreview();
        using var independentRestoredLetters = RenderKeyboard(window, 145);
        Assert(Near(independentRestoredLetters.GetPixel(14, 40), warm.KeyTop),
            "Clearing letters preview did not restore the formal panel palette.");

        // 音游独立配色：键帽像素和轨道数字都要走独立底栏色。
        window.ApplyTheme(customBarCfg);
        SetField(window, "_mode", KeyboardWindow.Mode.Rhythm);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Rhythm);
        using var independentRhythm = RenderKeyboard(window, 110);
        var independentRhythmPixel = independentRhythm.GetPixel(20, 48);
        Assert(Near(independentRhythmPixel, independent.KeyTop),
            $"Rhythm keys must use the independent bar palette, got {independentRhythmPixel}, expected {independent.KeyTop}.");
        Assert(independentRhythmPixel.B > independentRhythmPixel.R + 10,
            $"Independent rhythm key pixel must be blue-derived, got {independentRhythmPixel}.");

        // 鸣潮专属独立配色。
        SetField(window, "_mode", KeyboardWindow.Mode.Wuwa);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Wuwa);
        using var independentWuwa = RenderKeyboard(window, 140);
        var independentWuwaPixel = independentWuwa.GetPixel(16, 40);
        Assert(Near(independentWuwaPixel, independent.KeyTop),
            $"Wuwa keys must use the independent bar palette, got {independentWuwaPixel}, expected {independent.KeyTop}.");
        Assert(independentWuwaPixel.B > independentWuwaPixel.R + 10,
            $"Independent Wuwa key pixel must be blue-derived, got {independentWuwaPixel}.");

        // 手柄预览同样使用独立配色，取消后恢复正式面板主题。
        SetField(window, "_mode", KeyboardWindow.Mode.Controller);
        Invoke(window, "BuildLayout", KeyboardWindow.Mode.Controller);
        window.ApplyTheme(warmCfg);
        window.PreviewTheme(customBarCfg);
        using var previewPad = RenderKeyboard(window, 180);
        Assert(Near(previewPad.GetPixel(104, 107), independent.PadBody),
            "Controller preview did not use the independent bottom-bar palette.");
        window.ClearThemePreview();
        using var restoredPad = RenderKeyboard(window, 180);
        Assert(Near(restoredPad.GetPixel(104, 107), warm.PadBody),
            "Clearing controller preview did not restore the formal panel palette.");

        SetField(window, "_padButtons", (ushort)0x1000);
        using var pressedPad = RenderKeyboard(window, 180);
        var pressedFacePixel = pressedPad.GetPixel(137, 81);
        Assert(!Near(warmFacePixel, pressedFacePixel),
            $"Controller A button pressed state must be visible, got {warmFacePixel} and {pressedFacePixel}.");
        SetField(window, "_padButtons", (ushort)0);

        Console.WriteLine("PASS: all modes (letters/wuwa/rhythm/controller) use independent bottom-bar colors; follow-standard falls back to panel palette.");
        Console.WriteLine("PASS: preview/restore and real DrawTo pixels verified for letters/rhythm/wuwa/controller.");
    }

    static Bitmap RenderKeyboard(KeyboardWindow window, int height)
    {
        var bitmap = new Bitmap(200, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        window.DrawTo(g, new Rectangle(0, 0, 200, height));
        return bitmap;
    }

    static bool Near(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 8 && Math.Abs(a.G - b.G) <= 8 && Math.Abs(a.B - b.B) <= 8;

    static T GetField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("Field not found: " + name);
        return (T)field.GetValue(target);
    }

    static object Invoke(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Method not found: " + name);
        return method.Invoke(target, args);
    }

    static Bitmap ArtTop(Color color)
    {
        var image = new Bitmap(200, 200, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image);
        using var brush = new SolidBrush(color);
        // Only the upper half is opaque so the bottom HUD remains visible for pixel assertions.
        g.FillRectangle(brush, 0, 0, 200, 110);
        return image;
    }

    static Bitmap RenderPetStrip(Color bar, Color text, Color icon, Color iconMuted, bool keyboardOn)
    {
        using var head = ArtTop(Color.FromArgb(210, 70, 130, 240));
        using var body = ArtTop(Color.FromArgb(200, 200, 70, 50));
        var frames = new PetFrames { Head1 = head, Head2 = head, Body1 = body, Body2 = body, Body3 = body };
        var info = new PetComposer.Info
        {
            Clicks = 7,
            KeyboardOn = keyboardOn,
            BarColor = bar,
            BarTextColor = text,
            BarAccentColor = icon,
            BarAccentMutedColor = iconMuted,
        };
        var bitmap = new Bitmap(PetComposer.WindowW, PetComposer.WindowH, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(bitmap, frames, 1, 1, info);
        return bitmap;
    }

    /// <summary>图标区域里与底栏背景差异最大的像素对比度；抗锯齿边缘不会高于实心描边像素。</summary>
    static double MaxIconContrast(Bitmap bitmap, Rectangle rect, Color background)
    {
        double max = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A < 240 || Near(pixel, background)) continue;
                max = Math.Max(max, KeyboardTheme.ContrastRatio(pixel, background));
            }
        }
        return max;
    }

    static double HueDistance(Color a, Color b)
    {
        double delta = Math.Abs(a.GetHue() - b.GetHue());
        return Math.Min(delta, 360 - delta);
    }

    static void AssertPadPressedContrast(KeyboardTheme theme, string label)
    {
        Assert(KeyboardTheme.ContrastRatio(theme.PadPressed, theme.PadBody) >= 3.0,
            $"{label}: PadPressed vs body contrast is {KeyboardTheme.ContrastRatio(theme.PadPressed, theme.PadBody):F2}, expected >= 3.0.");
        foreach (var (name, background) in new[]
        {
            ("body", theme.PadBody),
            ("trigger track", theme.PadTriggerTrack),
            ("stick well", theme.PadStickWell),
            ("stick cap", theme.PadStickCap),
            ("unpressed dash", theme.Cream2),
        })
        {
            var pressed = theme.PadPressedFor(background);
            double ratio = KeyboardTheme.ContrastRatio(pressed, background);
            Assert(ratio >= 3.0,
                $"{label}: PadPressedFor({name}) contrast is {ratio:F2}, expected >= 3.0.");
        }
        Assert(KeyboardTheme.ContrastRatio(theme.PadText, theme.PadBody) >= 3.0 &&
               KeyboardTheme.ContrastRatio(theme.PadText, theme.PadTriggerTrack) >= 3.0,
            $"{label}: PadText must remain readable on body and trigger track.");
        var pressedBody = theme.PadPressedFor(theme.PadBody);
        var pressedTrack = theme.PadPressedFor(theme.PadTriggerTrack);
        Assert(KeyboardTheme.ContrastRatio(theme.PadTextFor(pressedBody), pressedBody) >= 3.0 &&
               KeyboardTheme.ContrastRatio(theme.PadTextFor(pressedTrack), pressedTrack) >= 3.0,
            $"{label}: PadTextFor must remain readable on pressed body/trigger.");
        Assert(KeyboardTheme.ContrastRatio(theme.PadTriggerFill, theme.PadTriggerTrack) >= 3.0 &&
               KeyboardTheme.ContrastRatio(theme.PadTriggerFill, pressedTrack) >= 3.0,
            $"{label}: trigger progress fill must contrast with both track and pressed fill.");
        var pressedFaceA = PetWindow.BlendColor(Color.FromArgb(255, 215, 237, 208), theme.PadPressed, 0.45f);
        Assert(KeyboardTheme.ContrastRatio(theme.PadFaceTextFor(pressedFaceA), pressedFaceA) >= 3.0,
            $"{label}: ABXY label must stay readable on the pressed face color.");
    }

    static bool RegionContainsColor(Bitmap bitmap, Rectangle rect, Color target)
    {
        int left = Math.Max(0, rect.Left), top = Math.Max(0, rect.Top);
        int right = Math.Min(bitmap.Width, rect.Right), bottom = Math.Min(bitmap.Height, rect.Bottom);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (Near(bitmap.GetPixel(x, y), target)) return true;
            }
        }
        return false;
    }

    static void CheckAccessoryTransform()
    {
        // 旧配置 [x,y] 必须补齐默认缩放 100 / 旋转 0。
        var legacy = AccessoryTransform.FromArray(new[] { 12, -7 });
        Assert(legacy.X == 12 && legacy.Y == -7 && legacy.ScalePercent == 100 && legacy.RotationDegrees == 0,
            $"Legacy [x,y] accessory offset decoded as {legacy}.");

        // 新配置的越界值统一收敛，0/负数缩放回退到默认值。
        var clamped = AccessoryTransform.FromArray(new[] { 999, -999, 500, -900 });
        Assert(clamped.X == 160 && clamped.Y == -160 && clamped.ScalePercent == 175 && clamped.RotationDegrees == -180,
            $"Accessory transform was not clamped: {clamped}.");
        var legacyOversize = AccessoryTransform.FromArray(new[] { 0, 0, 300, 0 });
        Assert(legacyOversize.ScalePercent == 175,
            $"Legacy 300% scale must clamp to the new symmetric maximum, got {legacyOversize.ScalePercent}%.");
        var invalidScale = AccessoryTransform.FromArray(new[] { 0, 0, 0, 0 });
        Assert(invalidScale.ScalePercent == 100 && invalidScale.RotationDegrees == 0,
            "Zero/negative scale must fall back to 100%.");
        var belowMin = AccessoryTransform.FromArray(new[] { 0, 0, 3, 0 });
        Assert(belowMin.ScalePercent == 25, "Scale below 25% must clamp to 25%.");

        using var clear = new Bitmap(200, 200, PixelFormat.Format32bppPArgb);
        using var dot = new Bitmap(200, 200, PixelFormat.Format32bppPArgb);
        using (var dg = Graphics.FromImage(dot))
        using (var red = new SolidBrush(Color.FromArgb(255, 220, 30, 30)))
            dg.FillRectangle(red, 90, 0, 20, 20);
        var frames = new PetFrames
        {
            Head1 = clear, Head2 = clear,
            Body1 = clear, Body2 = clear, Body3 = clear,
            Accessory = dot,
        };
        var info = new PetComposer.Info { AccScalePercent = 100, AccRotationDegrees = 0 };
        using var upright = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(upright, frames, 1, 1, info);
        var topPixel = upright.GetPixel(PetComposer.CatX + 100, PetComposer.CatY + 10);
        Assert(topPixel.A > 200 && topPixel.R > 150,
            $"Accessory top-center dot missing before rotation: {topPixel}.");

        // 旋转 90° 后同一点应转到右侧；坐标必须随 transform 变化。
        info.AccRotationDegrees = 90;
        using var rotated = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        PetComposer.ComposeInto(rotated, frames, 1, 1, info);
        var oldPoint = rotated.GetPixel(PetComposer.CatX + 100, PetComposer.CatY + 10);
        var newPoint = rotated.GetPixel(PetComposer.CatX + 190, PetComposer.CatY + 100);
        Assert(oldPoint.A < 20, $"Original dot position should be empty after 90° rotation, got {oldPoint}.");
        Assert(newPoint.A > 200 && newPoint.R > 150,
            $"Dot should land at the rotated position, got {newPoint}.");

        // FrameCache 必须把缩放/旋转计入 key，否则沿用旧缓存不会重绘。
        using var cachedA = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        using var cachedB = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        using (var cache = new PetComposer.FrameCache())
        {
            var a = new PetComposer.Info { AccScalePercent = 100, AccRotationDegrees = 0 };
            var b = new PetComposer.Info { AccScalePercent = 50, AccRotationDegrees = 0 };
            PetComposer.ComposeInto(cachedA, frames, 1, 1, a, cache);
            PetComposer.ComposeInto(cachedB, frames, 1, 1, b, cache);
        }
        var cachedEdge = cachedB.GetPixel(PetComposer.CatX + 100, PetComposer.CatY + 10);
        Assert(cachedEdge.A < 20,
            $"FrameCache did not invalidate when scale changed; pixel={cachedEdge}.");

        using var cachedRot = new Bitmap(272, 331, PixelFormat.Format32bppPArgb);
        using (var cache = new PetComposer.FrameCache())
        {
            var r = new PetComposer.Info { AccScalePercent = 100, AccRotationDegrees = 90 };
            PetComposer.ComposeInto(cachedRot, frames, 1, 1, r, cache);
        }
        var cachedRotOld = cachedRot.GetPixel(PetComposer.CatX + 100, PetComposer.CatY + 10);
        var cachedRotNew = cachedRot.GetPixel(PetComposer.CatX + 190, PetComposer.CatY + 100);
        Assert(cachedRotOld.A < 20 && cachedRotNew.A > 200 && cachedRotNew.R > 150,
            $"FrameCache did not invalidate when rotation changed; old={cachedRotOld}, new={cachedRotNew}.");

        // 命中检测要跟随缩放：50% 时 5px 处已超出，60px 处仍应命中。
        using var full = new Bitmap(200, 200, PixelFormat.Format32bppPArgb);
        using (var fg = Graphics.FromImage(full))
        using (var red = new SolidBrush(Color.FromArgb(255, 220, 30, 30)))
            fg.FillRectangle(red, 0, 0, 200, 200);
        frames.Accessory = full;
        Assert(PetComposer.HitTestCat(frames, 1, 1, 100, 100, 0, 0, 100, 0),
            "Unscaled accessory center should hit.");
        Assert(!PetComposer.HitTestCat(frames, 1, 1, 5, 100, 0, 0, 50, 0),
            "50% scaled accessory must not hit its old outer edge.");
        Assert(PetComposer.HitTestCat(frames, 1, 1, 60, 100, 0, 0, 50, 0),
            "50% scaled accessory must hit inside the scaled rect.");
        Console.WriteLine("PASS: accessory transform legacy compatibility, clamping, rendering, cache key and hit testing.");
    }

    static void CheckCompressionAndCache()
    {
        Assert(Paths.ThemeDir == Path.Combine(Paths.CacheDir, "theme"),
            "Theme images must live under %LOCALAPPDATA%/cache/theme.");
        byte[] png = NoisyPng(1600);
        Assert(png.Length > ThemeBackgroundStore.CompressThresholdBytes,
            $"Test PNG should be >4MB, actual {png.Length} bytes.");
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        string name = ThemeBackgroundStore.SaveDataUrl(dataUrl);
        string path = Path.Combine(ThemeBackgroundStore.Dir, name);
        Assert(File.Exists(path), "Compressed background file must exist in the theme cache directory.");
        Assert(name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase), "Over-4MB PNG must be transcoded to JPEG.");
        Assert(new FileInfo(path).Length <= ThemeBackgroundStore.CompressThresholdBytes,
            $"Compressed file is {new FileInfo(path).Length} bytes, expected <=4MB.");
        Assert(ThemeBackgroundStore.HasImage(name), "HasImage must resolve the compressed cache file.");
        Assert(ThemeBackgroundStore.DisplayUrl(name).StartsWith("https://wuwatappo.theme/", StringComparison.Ordinal),
            "Virtual-host URL must resolve for compressed cache files.");
        using (var check = Image.FromFile(path))
            Assert(check.Width <= 3840 && check.Height <= 3840, "Compressed image dimensions must be capped at 3840.");
        Console.WriteLine($"PASS: {png.Length / (1024 * 1024)}MB PNG compressed to {new FileInfo(path).Length / 1024}KB at {path}.");

        // Small images keep their format and still land in the same cache directory.
        byte[] small = TinyPng();
        string smallName = ThemeBackgroundStore.SaveDataUrl("data:image/png;base64," + Convert.ToBase64String(small));
        Assert(smallName.EndsWith(".png", StringComparison.OrdinalIgnoreCase), "Small PNG should stay PNG.");
        Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, smallName)), "Small image must be saved in cache.");

        string orphan = ThemeBackgroundStore.SaveDataUrl("data:image/png;base64," + Convert.ToBase64String(TinyPng(2)));
        ThemeBackgroundStore.CleanupOrphans(new[] { smallName });
        Assert(!File.Exists(Path.Combine(ThemeBackgroundStore.Dir, orphan)), "Unreferenced theme file must be cleaned.");
        Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, smallName)), "Referenced theme file must be kept.");

        // Exercise the real theme.image.begin/chunk/commit path with a >4MB PNG payload.
        string uploadId = ThemeBackgroundStore.BeginUpload("image/png");
        string payload = Convert.ToBase64String(png);
        for (int offset = 0; offset < payload.Length; offset += 256 * 1024)
            ThemeBackgroundStore.AppendUpload(uploadId, payload.Substring(offset, Math.Min(256 * 1024, payload.Length - offset)));
        string chunkName = ThemeBackgroundStore.CommitUpload(uploadId);
        string chunkPath = Path.Combine(ThemeBackgroundStore.Dir, chunkName);
        Assert(File.Exists(chunkPath), "Chunk upload must commit to the theme cache.");
        Assert(chunkName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
               new FileInfo(chunkPath).Length <= ThemeBackgroundStore.CompressThresholdBytes,
            "Chunk-uploaded >4MB image must be compressed before landing in the cache.");
        Console.WriteLine("PASS: theme.image chunk upload of >4MB image commits a compressed cache file.");
        Console.WriteLine("PASS: cache cleanup removes unreferenced files and keeps referenced files.");
    }

    static void CheckThemePresetPayloadMapping()
    {
        var preset = new ThemePreset
        {
            Id = "legacy-id-1",
            Name = "说的是",
            AccentColor = "#454C73",
            FontColor = "#3A3D51",
            Advanced = "{\"ink\":\"#3a3d51\"}",
            BarFollowStandard = false,
            BarColor = "",
            BarTextFollowTheme = true,
            BarTextColor = "",
            BackgroundImage = "preset-test.jpg",
            BackgroundOpacity = 1,
            BackgroundBlur = 2,
            CreatedAtMs = 1794590997968,
        };
        var rotation = new ThemeRotationState
        {
            Enabled = true,
            IntervalMinutes = 45,
            CurrentPresetId = preset.Id,
            LastAppliedUtcMs = 1794591003992,
        };

        // 走真实 BootPayload 使用的映射函数和默认 JsonSerializer 路径。
        string json = JsonSerializer.Serialize(new
        {
            themePresets = PanelWindow.MapThemePresets(new[] { preset }),
            themeRotation = PanelWindow.MapThemeRotation(rotation),
        });
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("themePresets")[0];
        string[] expected = {
            "id", "name", "accentColor", "fontColor", "advanced", "barFollowStandard",
            "barColor", "barTextFollowTheme", "barTextColor", "backgroundImage",
            "backgroundImageSet", "backgroundOpacity", "backgroundBlur", "createdAtMs",
        };
        string[] actual = row.EnumerateObject().Select(p => p.Name).ToArray();
        Assert(actual.SequenceEqual(expected),
            "Boot themePresets keys must be camelCase, actual: " + string.Join(",", actual));
        Assert(row.GetProperty("id").GetString() == preset.Id, "Boot id must map from ThemePreset.Id.");
        Assert(row.GetProperty("name").GetString() == preset.Name, "Boot name must map from ThemePreset.Name.");
        Assert(row.GetProperty("createdAtMs").GetInt64() == preset.CreatedAtMs, "Boot createdAtMs must map from CreatedAtMs.");
        Assert(!row.TryGetProperty("Id", out _) && !row.TryGetProperty("Name", out _) &&
               !row.TryGetProperty("CreatedAtMs", out _),
            "Boot themePresets must not emit PascalCase keys.");
        var rot = doc.RootElement.GetProperty("themeRotation");
        Assert(rot.GetProperty("currentPresetId").GetString() == preset.Id, "Boot rotation currentPresetId must stay camelCase.");
        Assert(rot.GetProperty("lastAppliedMs").GetInt64() == rotation.LastAppliedUtcMs, "Boot rotation lastAppliedMs must stay camelCase.");

        // 保存/重命名命令返回的 preset 也复用同一个映射。
        string saveJson = JsonSerializer.Serialize(new { preset = PanelWindow.MapThemePreset(preset) });
        using var saveDoc = JsonDocument.Parse(saveJson);
        var saved = saveDoc.RootElement.GetProperty("preset");
        Assert(saved.GetProperty("id").GetString() == preset.Id && saved.GetProperty("name").GetString() == preset.Name,
            "theme.presets.save/rename response must keep the same camelCase contract.");
        Console.WriteLine("PASS: theme preset boot/save DTO keys are camelCase (id/name/createdAtMs).");
    }

    static void CheckLegacyPresetFile()
    {
        string file = Path.Combine(_root, "theme_presets-legacy.json");
        File.WriteAllText(file, """
        {
          "Schema": 1,
          "Presets": [
            {
              "Id": "legacy-a",
              "Name": "说的是",
              "AccentColor": "#454C73",
              "FontColor": "#3A3D51",
              "Advanced": "",
              "BarFollowStandard": false,
              "BarColor": "",
              "BarTextFollowTheme": true,
              "BarTextColor": "",
              "BackgroundImage": "",
              "BackgroundOpacity": 1,
              "BackgroundBlur": 2,
              "CreatedAtMs": 1794590997968
            },
            {
              "Id": "legacy-b",
              "Name": "是的是的是的",
              "AccentColor": "#454C73",
              "FontColor": "#3A3D51",
              "Advanced": "",
              "BarFollowStandard": false,
              "BarColor": "",
              "BarTextFollowTheme": true,
              "BarTextColor": "",
              "BackgroundImage": "",
              "BackgroundOpacity": 1,
              "BackgroundBlur": 2,
              "CreatedAtMs": 1794591003992
            }
          ],
          "Rotation": { "Enabled": false, "IntervalMinutes": 30, "CurrentPresetId": "", "LastAppliedUtcMs": 0 }
        }
        """);
        var cfg = new Settings();
        using (var manager = new ThemePresetManager(cfg, file))
        {
            Assert(manager.Presets.Count == 2, "Existing PascalCase theme_presets.json must load all presets.");
            Assert(manager.Presets[0].Name == "说的是" && manager.Presets[1].Name == "是的是的是的",
                "Existing preset names must survive loading.");
            Assert(!manager.Delete(""), "Delete with an empty id must fail instead of removing a preset.");
            Assert(manager.Presets.Count == 2, "Empty-id delete must not change the preset list.");
            Assert(manager.Delete("legacy-a"), "Deleting an existing legacy preset must succeed.");
            Assert(manager.Presets.Count == 1 && manager.Presets[0].Id == "legacy-b",
                "Deleted preset must disappear from the in-memory list immediately.");
        }
        string saved = File.ReadAllText(file);
        Assert(!saved.Contains("legacy-a", StringComparison.Ordinal), "Deleted preset id must be removed from theme_presets.json.");
        Assert(saved.Contains("legacy-b", StringComparison.Ordinal), "Unrelated preset must remain in theme_presets.json.");
        using (var restarted = new ThemePresetManager(cfg, file))
        {
            Assert(restarted.Presets.Count == 1 && restarted.Presets[0].Id == "legacy-b",
                "Deleted preset must not reappear after restart.");
            Assert(restarted.Presets[0].Name == "是的是的是的", "Remaining preset name must survive restart.");
        }
        Console.WriteLine("PASS: legacy PascalCase theme_presets.json loads, deletes and stays deleted after restart.");
    }

    static void CheckExternalPresetFile(string source)
    {
        string copy = Path.Combine(_root, "theme_presets-external.json");
        File.Copy(source, copy, true);
        var cfg = new Settings();
        string firstId;
        string firstName;
        int before;
        using (var manager = new ThemePresetManager(cfg, copy))
        {
            before = manager.Presets.Count;
            Assert(before > 0, "External theme preset file must contain at least one preset.");
            firstId = manager.Presets[0].Id;
            firstName = manager.Presets[0].Name;
            Assert(!string.IsNullOrWhiteSpace(firstName), "External preset name must load correctly.");
            Assert(manager.Delete(firstId), "Deleting a preset from the external file copy must succeed.");
        }
        string saved = File.ReadAllText(copy);
        Assert(!saved.Contains(firstId, StringComparison.Ordinal),
            "Deleted external preset id must be removed from the copied file.");
        using (var restarted = new ThemePresetManager(cfg, copy))
        {
            Assert(restarted.Presets.Count == before - 1 && !restarted.Presets.Any(p => p.Id == firstId),
                "Deleted external preset must stay deleted after restart.");
        }
        Console.WriteLine($"PASS: external theme_presets.json copy loaded {before} presets (first: {firstName}) and deleted one persistently.");
    }

    static void CheckPresetsAndRotation()
    {
        string backgroundA = ThemeBackgroundStore.SaveDataUrl("data:image/png;base64," + Convert.ToBase64String(TinyPng(3)));
        string backgroundB = ThemeBackgroundStore.SaveDataUrl("data:image/png;base64," + Convert.ToBase64String(TinyPng(4)));
        var cfg = new Settings
        {
            ThemeAccentColor = "#123456",
            ThemeFontColor = "#654321",
            ThemeAdvanced = "{\"ink\":\"#112233\"}",
            ThemeBarFollowStandard = false,
            ThemeBarColor = "#ABCDEF",
            ThemeBarTextFollowTheme = false,
            ThemeBarTextColor = "#123123",
            ThemeBackgroundImage = backgroundA,
            ThemeBackgroundOpacity = 0.4,
            ThemeBackgroundBlur = 8,
        };
        ConfigStore.Save(cfg);
        var reloadedCfg = ConfigStore.Load();
        Assert(!reloadedCfg.ThemeBarFollowStandard && reloadedCfg.ThemeBarColor == "#ABCDEF" &&
               !reloadedCfg.ThemeBarTextFollowTheme && reloadedCfg.ThemeBarTextColor == "#123123",
            "Bottom-bar independent colors must persist through config.json.");
        string presetFile = Path.Combine(_root, "theme_presets.json");

        ThemePreset presetA;
        string deletedPresetId = "";
        DateTime rotationBase;
        using (var manager = new ThemePresetManager(cfg, presetFile))
        {
            presetA = manager.SaveCurrent("A");
            Assert(presetA.BackgroundImage != backgroundA, "Preset must store an independent copy of the background image.");
            Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, presetA.BackgroundImage)), "Preset image copy must exist.");
            Assert(presetA.AccentColor == "#123456" && presetA.FontColor == "#654321", "Preset must capture accent/font colors.");
            Assert(!presetA.BarFollowStandard && presetA.BarColor == "#ABCDEF" && !presetA.BarTextFollowTheme && presetA.BarTextColor == "#123123",
                "Preset must capture independent bottom-bar settings.");
            Assert(presetA.BackgroundOpacity == 0.4 && presetA.BackgroundBlur == 8, "Preset must capture background opacity/blur.");

            cfg.ThemeAccentColor = "#AA5500";
            cfg.ThemeFontColor = "#223344";
            cfg.ThemeBackgroundImage = backgroundB;
            cfg.ThemeBarFollowStandard = true;
            var presetB = manager.SaveCurrent("B");
            deletedPresetId = presetB.Id;
            Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, presetA.BackgroundImage)),
                "Saving a new preset must not overwrite the previous preset's image.");
            Assert(presetB.BackgroundImage != backgroundB, "Second preset must also own its image copy.");

            manager.Apply(presetA.Id);
            Assert(cfg.ThemeAccentColor == "#123456" && cfg.ThemeFontColor == "#654321", "Apply must restore panel colors.");
            Assert(cfg.ThemeAdvanced == "{\"ink\":\"#112233\"}", "Apply must restore the advanced palette JSON.");
            Assert(cfg.ThemeBarColor == "#ABCDEF" && cfg.ThemeBarTextColor == "#123123" && !cfg.ThemeBarFollowStandard,
                "Apply must restore bottom-bar settings.");
            Assert(cfg.ThemeBackgroundImage == presetA.BackgroundImage, "Apply must restore the preset background image.");
            Assert(cfg.ThemeBackgroundOpacity == 0.4 && cfg.ThemeBackgroundBlur == 8, "Apply must restore background opacity/blur.");
            Assert(!File.Exists(Path.Combine(ThemeBackgroundStore.Dir, backgroundB)),
                "Switching preset must delete the previous current image once it is unreferenced.");
            Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, presetB.BackgroundImage)),
                "The next preset's independent image copy must remain after switching.");

            manager.Rename(presetA.Id, "A renamed");
            Assert(manager.Presets.First(p => p.Id == presetA.Id).Name == "A renamed", "Rename must update the preset name.");
            Assert(manager.Delete(presetB.Id), "Delete must remove the preset.");
            Assert(!File.Exists(Path.Combine(ThemeBackgroundStore.Dir, presetB.BackgroundImage)),
                "Deleting a preset must clean its exclusive image.");
            Assert(File.Exists(Path.Combine(ThemeBackgroundStore.Dir, presetA.BackgroundImage)),
                "Deleting another preset must keep the current/owned image.");

            var presetC = manager.SaveCurrent("C");
            var presetD = manager.SaveCurrent("D");
            manager.SetRotation(true, 1);
            Assert(manager.RotationTimerRunning, "Enabling rotation with presets must start the manager timer.");
            rotationBase = DateTime.UtcNow;
            var t0 = rotationBase;
            Assert(manager.CheckRotation(t0) == null, "Rotation must wait for the configured interval.");
            var first = manager.CheckRotation(t0.AddSeconds(61));
            Assert(first != null && first.Id == presetC.Id, "Rotation must advance from the current preset in list order.");
            var second = manager.CheckRotation(t0.AddSeconds(122));
            Assert(second != null && second.Id == presetD.Id, "Rotation must continue to the next preset.");
            var third = manager.CheckRotation(t0.AddSeconds(183));
            Assert(third != null && third.Id == presetA.Id, "Rotation must wrap to the first preset.");
            Assert(cfg.ThemeBarColor == "#ABCDEF", "Rotated preset must apply bottom-bar settings through the real apply path.");
        }

        string presetJson = File.ReadAllText(presetFile);
        Assert(presetJson.Length < 100_000, "Preset JSON must stay small.");
        Assert(!presetJson.Contains("data:image", StringComparison.OrdinalIgnoreCase), "Preset JSON must not embed image data URLs.");
        string configJson = File.ReadAllText(Paths.ConfigFile);
        Assert(!configJson.Contains("data:image", StringComparison.OrdinalIgnoreCase), "Config must never contain background data URLs.");
        Assert(configJson.Length < 100_000, "Config must stay small after presets/backgrounds.");

        using (var restarted = new ThemePresetManager(cfg, presetFile))
        {
            Assert(restarted.Rotation.Enabled && restarted.Rotation.IntervalMinutes == 1,
                "Rotation settings must survive a manager restart.");
            Assert(restarted.Rotation.CurrentPresetId == presetA.Id, "Current preset id must survive a manager restart.");
            Assert(!restarted.Presets.Any(p => p.Id == deletedPresetId), "Deleted preset must stay deleted after a manager restart.");
            restarted.Start();
            Assert(restarted.RotationTimerRunning, "Restarted manager must re-arm the rotation timer.");
            Assert(restarted.CheckRotation(rotationBase.AddSeconds(200)) == null,
                "Restart must preserve the last rotation time and not fire early.");
            var resumed = restarted.CheckRotation(rotationBase.AddSeconds(245));
            Assert(resumed != null, "Rotation must resume after restart when the interval has elapsed.");
            restarted.SetRotation(false, 1);
            Assert(!restarted.RotationTimerRunning, "Disabling rotation must stop the timer.");
        }
        Console.WriteLine("PASS: presets save/apply/rename/delete, independent images, rotation order and restart persistence.");
    }

    static byte[] NoisyPng(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[size * 4];
            var random = new Random(12345);
            for (int y = 0; y < size; y++)
            {
                random.NextBytes(row);
                for (int x = 3; x < row.Length; x += 4) row[x] = 255;
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    static byte[] TinyPng(int seed = 1)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.FromArgb(255, 40 + seed, 80 + seed, 120 + seed));
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    static void CheckPresetReorderAndLastDelete()
    {
        string file = Path.Combine(_root, "theme_presets_reorder.json");
        var cfg = new Settings { ThemeAccentColor = "#336699", ThemeFontColor = "#223344" };
        using (var manager = new ThemePresetManager(cfg, file))
        {
            var a = manager.SaveCurrent("A");
            var b = manager.SaveCurrent("B");
            var c = manager.SaveCurrent("C");
            Assert(manager.Presets.Count == 3, "Three presets must be saved before reorder test.");
            Assert(manager.Move(c.Id, -1), "Moving a preset up must succeed.");
            Assert(manager.Presets[1].Id == c.Id, "Up must place the preset one slot earlier.");
            Assert(!manager.Move(a.Id, -1), "Moving the first preset up must be rejected.");
            var overwritten = manager.SaveCurrent("B renamed", b.Id);
            Assert(overwritten.Id == b.Id && manager.Presets.Count == 3,
                "Saving with an existing id must overwrite instead of creating a new preset.");

            manager.Apply(b.Id);
            Assert(manager.Rotation.CurrentPresetId == b.Id, "Applying a preset must mark it current.");
            Assert(manager.ClearCurrentPreset(), "Manual theme changes must be able to clear the current marker.");
            Assert(manager.Rotation.CurrentPresetId == "", "Clearing the current marker must persist.");

            manager.SetRotation(true, 15);
            Assert(manager.RotationTimerRunning, "Rotation must run with presets present.");
            Assert(manager.Delete(a.Id) && manager.Delete(b.Id) && manager.Delete(c.Id), "All presets must delete.");
            Assert(!manager.Rotation.Enabled, "Deleting the last preset must disable rotation.");
            Assert(!manager.RotationTimerRunning, "Deleting the last preset must stop the rotation timer.");
        }
        using (var restarted = new ThemePresetManager(cfg, file))
        {
            Assert(restarted.Presets.Count == 0, "Empty preset list must survive restart.");
            Assert(!restarted.Rotation.Enabled, "Disabled rotation after last delete must survive restart.");
        }
    }

    static void CheckOfficialBaseline()
    {
        var official = new OfficialVersionInfo
        {
            Available = true,
            Version = "1.0.5",
            ForceManualDownload = false,
        };
        Assert(OfficialVersionChecker.Evaluate(official).State == "unverified",
            "New official version without a remote baseline must remain unverified.");
        var verified = OfficialVersionChecker.Evaluate(official, "1.0.5");
        Assert(verified.State == "verified",
            "Remote protocol.testedOfficial must be able to mark the current official version verified.");
        Assert(verified.TestedVersion == "1.0.5",
            "Remote baseline must be reflected in the effective tested version.");
        var stale = OfficialVersionChecker.Evaluate(official, "1.0.3");
        Assert(stale.State == "unverified",
            "A remote baseline below the compiled floor must not downgrade compatibility.");
        var forced = OfficialVersionChecker.Evaluate(new OfficialVersionInfo
        {
            Available = true,
            Version = "1.0.6",
            ForceManualDownload = true,
        }, "1.0.5");
        Assert(forced.State == "forced",
            "A newer forced official version must still block after a remote baseline update.");
        Console.WriteLine("PASS: remote TESTED BASELINE can verify a new official version without lowering the compiled floor.");
    }

    static void CheckCompatibilityGate()
    {
        Assert(CompatibilityGate.BlockedReason(null) == "", "Null compatibility state must not block networking.");
        Assert(CompatibilityGate.BlockedReason(new CompatibilityInfo { Status = "safe" }) == "",
            "Safe compatibility state must allow networking.");
        Assert(CompatibilityGate.BlockedReason(new CompatibilityInfo { Status = "unsafe", Message = "bad" }).Contains("不安全"),
            "Unsafe status must produce a blocking reason.");
        Assert(CompatibilityGate.BlockedReason(new CompatibilityInfo { Status = "maintenance" }).Contains("维护"),
            "Maintenance status must produce a blocking reason.");
        var kill = new CompatibilityInfo
        {
            Status = "safe",
            KillSwitch = new CompatibilityKillSwitch { ForceOffline = true, Reason = "维护者暂停" },
        };
        Assert(CompatibilityGate.BlockedReason(kill).Contains("安全模式"), "ForceOffline must block networking.");
        var forced = new CompatibilityInfo
        {
            Status = "safe",
            OfficialCheck = new OfficialCheckStatus { State = "forced", Message = "官方已强制更新" },
        };
        Assert(CompatibilityGate.BlockedReason(forced).Contains("强制更新"), "Forced official update must block networking.");

        var forcedVersioned = new CompatibilityInfo
        {
            Status = "safe",
            OfficialCheck = new OfficialCheckStatus
            {
                State = "forced",
                OfficialVersion = "1.0.5",
                TestedVersion = "1.0.4",
                Message = "官方已强制更新",
            },
        };
        var accepted = new Settings { ForcedOnlineAcceptedVersion = "1.0.5" };
        Assert(CompatibilityGate.BlockedReason(forcedVersioned, accepted) == "",
            "Explicit acceptance of the current forced official version must allow networking.");
        Assert(CompatibilityGate.BlockedReason(forcedVersioned, new Settings { ForcedOnlineAcceptedVersion = "1.0.4" }).Contains("强制更新"),
            "Acceptance of an older official version must not allow the new forced version.");
        var unsafeAccepted = new CompatibilityInfo
        {
            Status = "unsafe",
            OfficialCheck = forcedVersioned.OfficialCheck,
        };
        Assert(CompatibilityGate.BlockedReason(unsafeAccepted, accepted).Contains("不安全"),
            "Unsafe status must remain a hard block even after forced-update acceptance.");
        var killAccepted = new CompatibilityInfo
        {
            Status = "safe",
            KillSwitch = new CompatibilityKillSwitch { ForceOffline = true, Reason = "维护者暂停" },
            OfficialCheck = forcedVersioned.OfficialCheck,
        };
        Assert(CompatibilityGate.BlockedReason(killAccepted, accepted).Contains("安全模式"),
            "KillSwitch must remain a hard block even after forced-update acceptance.");

        ConfigStore.Save(new Settings { ForcedOnlineAcceptedVersion = " 1.0.5 " });
        Assert(ConfigStore.Load().ForcedOnlineAcceptedVersion == "1.0.5",
            "Forced-online acceptance version must trim and persist across config reload.");
    }

    /// <summary>
    /// 多屏环境不能依赖测试机的真实显示器，这里静态检查所有自动排序入口的屏幕来源：
    /// 托盘/排列方向/面板重置排序都必须落到 ConfiguredScreen()，显式 targetScreen 仍优先。
    /// </summary>
    static void CheckCatOrderScreenSelection()
    {
        string pet = ReadRepoSource("src", "Pet", "PetWindow.cs");
        Assert(Regex.IsMatch(pet, @"public\s+void\s+ResetCatOrder\(\)\s*=>\s*ResetCatOrder\(ConfiguredScreen\(\)\);"),
            "Parameterless ResetCatOrder must sort on ConfiguredScreen().");
        Assert(!pet.Contains("ResetCatOrder(null)", StringComparison.Ordinal),
            "ResetCatOrder must not pass null and fall back to the main pet's current screen.");
        Assert(Regex.IsMatch(pet, @"var\s+wa\s*=\s*\(targetScreen\s*\?\?\s*ConfiguredScreen\(\)\)\.WorkingArea;"),
            "ResetCatOrder(Screen) must fall back to ConfiguredScreen(), not the current screen.");
        Assert(Regex.IsMatch(pet, @"public\s+void\s+SetCatLayout\([^)]*\)\s*\{[^{}]*ResetCatOrder\(\);", RegexOptions.Singleline),
            "SetCatLayout must reorder through the parameterless ResetCatOrder().");

        string panel = ReadRepoSource("src", "Ui", "PanelWindow.cs");
        Assert(Regex.IsMatch(panel, @"case\s+""room\.resetOrder"":\s*_app\.Pet\.BeginInvoke\(new Action\(_app\.Pet\.ResetCatOrder\)\);"),
            "Panel reset-order command must use the parameterless ResetCatOrder() overload.");
    }

    static string ReadRepoSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Static source check could not find " + Path.Combine(relativeParts));
    }

    static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("Field not found: " + name);
        field.SetValue(target, value);
    }
}
