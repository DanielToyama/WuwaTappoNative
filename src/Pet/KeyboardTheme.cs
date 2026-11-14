using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 键盘 / 音游 / 鸣潮 / 手柄共用的派生调色板。主题配置（含高级配色 JSON）只在这里解析一次，
/// 绘制方法只读取解析后的 Color，避免每一帧或每个绘制方法重复解析 JSON。
/// 底栏跟随标准主题时使用面板主题（cream/cream2/cream3/cream4、ink、border、active 等）；
/// 关闭跟随时所有模式统一使用独立底栏背景/文字色重新推导。
/// </summary>
public sealed class KeyboardTheme
{
    static readonly Color InkDark = Color.FromArgb(43, 23, 16);
    static readonly Color FaceY = Color.FromArgb(252, 226, 156);
    static readonly Color FaceX = Color.FromArgb(214, 233, 248);
    static readonly Color FaceB = Color.FromArgb(250, 217, 212);
    static readonly Color FaceA = Color.FromArgb(215, 237, 208);

    // 基础主题色
    public Color Accent { get; init; }
    public Color Bar { get; init; }
    public Color Cream { get; init; }
    public Color Cream2 { get; init; }
    public Color Cream3 { get; init; }
    public Color Cream4 { get; init; }
    public Color Ink { get; init; }
    public Color InkStrong { get; init; }
    public Color InkDim { get; init; }
    public Color Border { get; init; }
    public Color Active { get; init; }
    public Color AccentStrong { get; init; }
    public Color AccentSoft { get; init; }

    // 工具栏
    public Color ToolbarBackground { get; init; }
    public Color ToolbarActive { get; init; }
    public Color ToolbarBorder { get; init; }
    public Color ToolbarText { get; init; }

    // 键盘底板
    public Color PlateTop { get; init; }
    public Color PlateBottom { get; init; }
    public Color PlateBorder { get; init; }
    public Color PlateShadow { get; init; }
    public Color PlateEdge { get; init; }
    public Color PlateFoot { get; init; }
    public Color Led { get; init; }

    // 键帽（普通键盘 / 鸣潮 / 音游共用）
    public Color KeyTop { get; init; }
    public Color KeyShadow { get; init; }
    public Color KeyPressed { get; init; }
    public Color KeyGlow { get; init; }
    public Color KeyText { get; init; }
    public Color KeyTextPressed { get; init; }
    public Color LaneNumber { get; init; }

    // 手柄
    public Color PadBody { get; init; }
    public Color PadOutline { get; init; }
    public Color PadPressed { get; init; }
    public Color PadText { get; init; }
    public Color PadTriggerTrack { get; init; }
    public Color PadTriggerFill { get; init; }
    public Color PadStickWell { get; init; }
    public Color PadStickCap { get; init; }
    public Color PadFaceOutline { get; init; }
    public Color PadFaceText { get; init; }

    /// <summary>
    /// 所有键盘窗口模式共用的入口：底栏跟随标准时使用面板主题；
    /// 关闭跟随时统一使用实际生效的独立底栏背景/文字色推导。
    /// </summary>
    public static KeyboardTheme Resolve(Settings cfg)
    {
        cfg ??= new Settings();
        var (bar, text, accent) = PetWindow.ResolveThemeBarColors(cfg);
        if (!cfg.ThemeBarFollowStandard) return BuildIndependentPalette(bar, text);

        var advanced = ReadAdvanced(cfg);
        var hsl = ToHsl(accent);

        Color Adv(string key, Color fallback) =>
            advanced.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? PetWindow.ParseThemeColor(value, fallback)
                : fallback;

        // 与面板 buildThemePalette 相同的默认推导：色相跟随主题色，明度/饱和度做弱化。
        var cream = Adv("cream", FromHsl(hsl.H, Math.Min(0.42, hsl.S), 0.963));
        var cream2 = Adv("cream2", FromHsl(hsl.H, Math.Min(0.48, hsl.S), 0.925));
        var cream3 = Adv("cream3", FromHsl(hsl.H, Math.Min(0.46, hsl.S), 0.865));
        var cream4 = Adv("cream4", FromHsl(hsl.H, Math.Min(0.32, hsl.S), 0.988));
        var ink = text;
        var inkStrong = Adv("inkStrong", PetWindow.BlendColor(ink, InkDark, 0.38f));
        var inkDim = Adv("inkDim", PetWindow.BlendColor(ink, cream4, 0.45f));
        var border = Adv("border", FromHsl(
            hsl.H,
            Math.Clamp(hsl.S, 0.25, 0.62),
            Math.Clamp(hsl.L * 0.78, 0.44, 0.68)));
        var active = Adv("active", FromHsl(hsl.H, Math.Clamp(hsl.S, 0.30, 0.72), 0.82));
        var accentStrong = Adv("accentStrong", FromHsl(
            hsl.H,
            Math.Clamp(hsl.S, 0.35, 0.82),
            Math.Clamp(hsl.L - 0.18, 0.30, 0.58)));
        var accentSoft = Adv("accentSoft", FromHsl(hsl.H, Math.Clamp(hsl.S, 0.25, 0.68), 0.88));

        // 跟随标准主题时所有模式都使用面板主题的 cream。
        var surface = cream;

        var toolbarBackground = PetWindow.BlendColor(cream2, surface, 0.35f);
        var plateTop = PetWindow.BlendColor(PetWindow.BlendColor(cream4, surface, 0.25f), accent, 0.06f);
        var plateBottom = PetWindow.BlendColor(cream3, surface, 0.55f);
        var keyTop = PetWindow.BlendColor(PetWindow.BlendColor(cream4, surface, 0.18f), accent, 0.10f);
        var padBody = PetWindow.BlendColor(PetWindow.BlendColor(cream4, surface, 0.18f), accent, 0.10f);
        var padStickWell = PetWindow.BlendColor(cream2, surface, 0.35f);
        var padTriggerTrack = PetWindow.BlendColor(cream4, surface, 0.22f);
        var padOutline = PetWindow.BlendColor(border, ink, 0.30f);
        var keyShadow = PetWindow.BlendColor(border, ink, 0.25f);

        return new KeyboardTheme
        {
            Accent = accent,
            Bar = bar,
            Cream = cream,
            Cream2 = cream2,
            Cream3 = cream3,
            Cream4 = cream4,
            Ink = ink,
            InkStrong = inkStrong,
            InkDim = inkDim,
            Border = border,
            Active = active,
            AccentStrong = accentStrong,
            AccentSoft = accentSoft,

            ToolbarBackground = toolbarBackground,
            ToolbarActive = active,
            ToolbarBorder = border,
            ToolbarText = EnsureReadable(inkStrong, 3.0, toolbarBackground, active),

            PlateTop = plateTop,
            PlateBottom = plateBottom,
            PlateBorder = border,
            PlateShadow = WithAlpha(keyShadow, 150),
            PlateEdge = accentStrong,
            PlateFoot = accent,
            Led = accentSoft,

            KeyTop = keyTop,
            KeyShadow = keyShadow,
            KeyPressed = active,
            KeyGlow = accentSoft,
            KeyText = EnsureReadable(ink, 3.2, keyTop),
            KeyTextPressed = EnsureReadable(inkStrong, 3.2, active),
            LaneNumber = WithAlpha(EnsureReadable(inkDim, 3.0, plateTop, plateBottom), 220),

            PadBody = padBody,
            PadOutline = EnsureReadable(padOutline, 1.8, padBody),
            PadPressed = active,
            PadText = EnsureReadable(inkStrong, 3.0, padBody, active),
            PadTriggerTrack = padTriggerTrack,
            PadTriggerFill = accentStrong,
            PadStickWell = padStickWell,
            PadStickCap = PetWindow.BlendColor(cream4, accent, 0.08f),
            PadFaceOutline = EnsureReadable(padOutline, 1.6, FaceY, FaceX, FaceB, FaceA),
            PadFaceText = EnsureReadable(inkStrong, 3.0, FaceY, FaceX, FaceB, FaceA),
        };
    }

    /// <summary>
    /// 独立底栏配色的统一调色板：面板主题之外的所有键盘窗口模式共用。
    /// 底板/键帽/轮廓/按下态/扳机/摇杆/工具栏都以底栏色为基底；
    /// 根据文字明暗选择深色或浅色底板，保证自定义底栏文字色仍然可读。
    /// </summary>
    static KeyboardTheme BuildIndependentPalette(Color bar, Color text)
    {
        bool lightText = Luminance(text) >= 0.55;

        Color Blend(Color a, Color b, float amount) => PetWindow.BlendColor(a, b, amount);

        // 文字深 -> 浅色底板 + 深色轮廓；文字浅 -> 深色底板 + 浅色轮廓。
        Color body = lightText ? Blend(bar, Color.Black, 0.18f) : Blend(bar, Color.White, 0.78f);
        Color bodyAlt = lightText ? Blend(bar, Color.Black, 0.10f) : Blend(bar, Color.White, 0.62f);
        Color active = lightText ? Blend(bar, Color.White, 0.22f) : Blend(bar, Color.White, 0.50f);
        Color outline = lightText ? Blend(bar, Color.White, 0.55f) : Blend(bar, Color.Black, 0.30f);
        Color shadow = Blend(bar, Color.Black, 0.25f);
        Color glow = lightText ? Blend(bar, Color.White, 0.40f) : Blend(bar, Color.White, 0.55f);
        Color triggerFill = lightText ? Blend(bar, Color.White, 0.35f) : Blend(bar, Color.Black, 0.05f);

        var toolbarText = EnsureReadable(text, 3.0, bodyAlt, active);
        var keyText = EnsureReadable(text, 3.2, body);
        var keyTextPressed = EnsureReadable(text, 3.2, active);
        var padText = EnsureReadable(text, 3.0, body, active);
        var padOutline = EnsureReadable(outline, 1.8, body);
        var faceOutline = EnsureReadable(outline, 1.6, FaceY, FaceX, FaceB, FaceA);
        var faceText = EnsureReadable(text, 3.0, FaceY, FaceX, FaceB, FaceA);
        var inkDim = Blend(text, body, 0.45f);
        var laneNumber = WithAlpha(EnsureReadable(inkDim, 3.0, body), 220);

        return new KeyboardTheme
        {
            // 独立桌宠 UI 配色就是手柄的基础色，不再混入面板主题色。
            Accent = bar,
            Bar = bar,
            Cream = body,
            Cream2 = bodyAlt,
            Cream3 = Blend(bodyAlt, bar, 0.25f),
            Cream4 = body,
            Ink = text,
            InkStrong = EnsureReadable(text, 3.2, body, active),
            InkDim = inkDim,
            Border = outline,
            Active = active,
            AccentStrong = triggerFill,
            AccentSoft = glow,

            ToolbarBackground = bodyAlt,
            ToolbarActive = active,
            ToolbarBorder = outline,
            ToolbarText = toolbarText,

            PlateTop = body,
            PlateBottom = bodyAlt,
            PlateBorder = outline,
            PlateShadow = WithAlpha(shadow, 150),
            PlateEdge = triggerFill,
            PlateFoot = bar,
            Led = glow,

            KeyTop = body,
            KeyShadow = shadow,
            KeyPressed = active,
            KeyGlow = glow,
            KeyText = keyText,
            KeyTextPressed = keyTextPressed,
            LaneNumber = laneNumber,

            PadBody = body,
            PadOutline = padOutline,
            PadPressed = active,
            PadText = padText,
            PadTriggerTrack = bodyAlt,
            PadTriggerFill = triggerFill,
            PadStickWell = bodyAlt,
            PadStickCap = body,
            PadFaceOutline = faceOutline,
            PadFaceText = faceText,
        };
    }

    /// <summary>读取高级配色 JSON；只解析一次，键名大小写不敏感。</summary>
    static Dictionary<string, string> ReadAdvanced(Settings cfg)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string json = cfg?.ThemeAdvanced;
        if (string.IsNullOrWhiteSpace(json)) return colors;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return colors;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                string value = (property.Value.GetString() ?? "").Trim();
                if (value.Length > 0) colors[property.Name] = value;
            }
        }
        catch { }
        return colors;
    }

    static Color WithAlpha(Color color, int alpha) =>
        Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    /// <summary>相对亮度（WCAG）。</summary>
    static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>两色对比度；测试与绘制前的可读性保护都使用同一套计算。</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>文字与所有背景都不够对比时，逐步向黑/白靠拢，保证键帽文字可读。</summary>
    static Color EnsureReadable(Color text, double minRatio, params Color[] backgrounds)
    {
        double Worst(Color color) => backgrounds.Min(b => ContrastRatio(color, b));
        if (Worst(text) >= minRatio) return text;
        Color target = Worst(Color.White) >= Worst(Color.Black) ? Color.White : Color.Black;
        for (int step = 1; step <= 10; step++)
        {
            var candidate = PetWindow.BlendColor(text, target, step / 10f);
            if (Worst(candidate) >= minRatio) return candidate;
        }
        return target;
    }

    static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 1) + 1) % 1;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s <= 0)
        {
            int gray = (int)Math.Round(l * 255);
            return Color.FromArgb(gray, gray, gray);
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Color.FromArgb(
            (int)Math.Round(Hue(p, q, h + 1.0 / 3) * 255),
            (int)Math.Round(Hue(p, q, h) * 255),
            (int)Math.Round(Hue(p, q, h - 1.0 / 3) * 255));
    }
}
