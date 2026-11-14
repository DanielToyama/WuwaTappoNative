using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading;
using WuwatappoNative.Assets;

namespace WuwatappoNative.Pet;

public sealed class PetFrames
{
    public Image Head1, Head2, Body1, Body2, Body3, Accessory;
    public double AccOffsetX, AccOffsetY;
    readonly object _frameGate = new();

    /// <summary>必须整套角色帧到齐才算 Ready，避免真实头部/默认身体这类拼接画面。</summary>
    public bool Ready => Head1 != null && Head2 != null && Body1 != null && Body2 != null && Body3 != null;

    public void ApplyCharacterFrames(Image head1, Image head2, Image body1, Image body2, Image body3)
    {
        lock (_frameGate)
        {
            Head1 = head1;
            Head2 = head2;
            Body1 = body1;
            Body2 = body2;
            Body3 = body3;
        }
        Interlocked.Exchange(ref _fallbackPending, 0);
    }

    internal void BeginFallback() => Interlocked.Exchange(ref _fallbackPending, 1);
    internal void EndFallback() => Interlocked.Exchange(ref _fallbackPending, 0);

    int _pending;
    int _fallbackPending;
    /// <summary>还有图片在下载或等待超时默认帧。Ready 可能已经是默认图，Loading 仍为 true。</summary>
    public bool Loading => Volatile.Read(ref _pending) > 0 || Volatile.Read(ref _fallbackPending) > 0;

    internal void AddPending() => Interlocked.Increment(ref _pending);
    internal void CompletePending()
    {
        if (Interlocked.Decrement(ref _pending) < 0) Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>任一帧到货（含失败后的默认图兜底）都会触发，窗口据此重绘。</summary>
    public event Action Changed;

    internal void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null) return;
        try { handler(); } catch { }
    }
}

/// <summary>
/// 把角色 id / 配件 id 解析成帧资源。
/// 同一套装扮只保留一个 PetFrames 实例，多个窗口共享，谁先到货都会通知全部订阅者。
/// </summary>
public sealed class PetImageLoader
{
    readonly Catalog _catalog;
    readonly AssetStore _store;
    readonly Dictionary<string, PetFrames> _cache = new();
    readonly object _gate = new();

    public PetImageLoader(Catalog catalog, AssetStore store)
    {
        _catalog = catalog;
        _store = store;
    }

    public PetFrames Get(long skinId, long accessoryId)
    {
        string key = skinId + "/" + accessoryId;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var frames = new PetFrames();
        var skin = skinId > 0 ? _catalog.Find("character_skin", skinId) : null;
        bool hasSkin = skin != null && !string.IsNullOrEmpty(skin.Frame("head1")?.Url);
        bool catalogLoaded = _catalog.Count("character_skin") > 0;

        if (hasSkin)
        {
            string[] urls =
            {
                _catalog.CdnUrl(skin.Frame("head1").Url),
                _catalog.CdnUrl((skin.Frame("head2") ?? skin.Frame("head1")).Url),
                _catalog.CdnUrl(skin.Frame("body1").Url),
                _catalog.CdnUrl((skin.Frame("body2") ?? skin.Frame("body1")).Url),
                _catalog.CdnUrl((skin.Frame("body3") ?? skin.Frame("body1")).Url),
            };
            var loaded = new Image[5];
            var remaining = new[] { urls.Length };
            for (int i = 0; i < urls.Length; i++)
            {
                int slot = i;
                Fetch(urls[i], img =>
                {
                    loaded[slot] = img;
                    if (Interlocked.Decrement(ref remaining[0]) != 0) return;
                    if (loaded[0] != null && loaded[1] != null && loaded[2] != null && loaded[3] != null && loaded[4] != null)
                        frames.ApplyCharacterFrames(loaded[0], loaded[1], loaded[2], loaded[3], loaded[4]);
                }, frames);
            }
        }
        else
        {
            LoadDefaultFrames(frames);
        }

        var acc = accessoryId > 0 ? _catalog.Find("accessory", accessoryId) : null;
        if (acc != null)
        {
            frames.AccOffsetX = acc.OffsetX;
            frames.AccOffsetY = acc.OffsetY;
            frames.Accessory = Fetch(_catalog.CdnUrl(acc.Url), v => frames.Accessory = v, frames);
        }

        // 目录还没加载完时无法判断这个 id 是真的不存在还是暂时查不到：先给默认图但不入缓存，
        // 等 Catalog.Loaded 后重新解析，避免把默认皮肤固化成结果。
        bool cacheable = hasSkin || catalogLoaded || skinId <= 0;
        if (cacheable)
        {
            lock (_gate) _cache[key] = frames;
        }

        if (!frames.Ready) StartFallback(frames);
        return frames;
    }

    static void LoadDefaultFrames(PetFrames frames)
    {
        frames.ApplyCharacterFrames(
            LoadLocal("role_1501_1001_head_1.png"),
            LoadLocal("role_1501_1001_head_2.png"),
            LoadLocal("role_1501_1001_body_1.png"),
            LoadLocal("role_1501_1001_body_2.png"),
            LoadLocal("role_1501_1001_body_3.png"));
    }

    static Image LoadLocal(string file)
    {
        string path = System.IO.Path.Combine(Core.Paths.DefaultAssets, file);
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var bytes = System.IO.File.ReadAllBytes(path);
            using var ms = new System.IO.MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    /// <summary>下载迟迟不完成时用默认图补位，至少不留下空白桌宠。</summary>
    static void StartFallback(PetFrames frames)
    {
        frames.BeginFallback();
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3 && !frames.Ready; i++) await Task.Delay(4000);
            if (frames.Ready)
            {
                frames.EndFallback();
                return;
            }
            LoadDefaultFrames(frames);
            if (!frames.Ready)
            {
                frames.EndFallback();
                return;
            }
            Core.Log.Write("外观下载超时，回退默认帧");
            frames.RaiseChanged();
        });
    }

    /// <summary>同步取图；未缓存时后台下载，完成后回填并通知所有窗口重绘。</summary>
    Image Fetch(string url, Action<Image> assign, PetFrames frames)
    {
        if (string.IsNullOrEmpty(url)) { assign(null); return null; }
        // onStarted 在真正发起下载时 +1；完成回调无论成功失败都 -1，并只从缓存读取，
        // 绝不能再次调用 Get(url)，否则失败会重新 Prefetch，形成无限下载风暴。
        var img = _store.Get(url, () =>
        {
            try
            {
                assign(_store.GetCached(url));
            }
            finally
            {
                frames.CompletePending();
                frames.RaiseChanged();
            }
        }, () => frames.AddPending());
        if (img != null) assign(img);
        return img;
    }

    public void Clear()
    {
        lock (_gate) _cache.Clear();
    }
}

public static class PetComposer
{
    public const int WindowW = 272;
    public const int WindowH = 331;
    public const int CatX = 24, CatY = 120, CatSize = 200;
    public const int InfoY = 299;
    public const int CounterX = 29, CounterW = 136, CounterH = 32;
    public const int ToolbarX = 165, ToolbarW = 61;
    public const int KeyboardZoneX = 167, KeyboardZoneW = 24;
    public const int MenuZoneX = 194, MenuZoneW = 26;

    public static readonly Rectangle CounterRect = new(CounterX, InfoY, CounterW, CounterH);
    public static readonly Rectangle KeyboardRect = new(KeyboardZoneX, InfoY, KeyboardZoneW, CounterH);
    public static readonly Rectangle MenuRect = new(MenuZoneX, InfoY, MenuZoneW, CounterH);
    public static readonly Rectangle CatRect = new(CatX, CatY, CatSize, CatSize);

    public static readonly Color Cream = Color.FromArgb(253, 249, 237);
    public static readonly Color Ink = Color.FromArgb(142, 94, 67);
    public static readonly Color InkStrong = Color.FromArgb(110, 69, 45);
    public static readonly Color Border = Color.FromArgb(231, 183, 124);

    public sealed class Info
    {
        public long Clicks;
        public bool KeyboardOn;
        public string Nickname;
        public EmoteBubbleLayer Bubbles;
        public KeyBubbleLayer KeyBubbles;
        public EmoteWheelLayer Wheel;
        public long ChestLeftTs, ChestRightTs;
        public long NowMs;
        public bool ShowChestLeft, ShowChestRight;
        public bool Selected;
        /// <summary>是否绘制计数条/工具栏/手部压条；关闭时角色本体、宝箱、气泡和轮盘仍照常显示。</summary>
        public bool ShowBar = true;
        public Image ChestButton, ChestCountdown;
        public bool Loading;
        public double LoadingPhase;
        public int AccOffsetX, AccOffsetY;
        public int AccScalePercent = 100, AccRotationDegrees;
        public Color BarColor;
        public Color BarTextColor;
        /// <summary>键盘/菜单等底栏按钮图标的实际绘制色；由主题色按底栏背景派生，保证对比度。</summary>
        public Color BarAccentColor;
        /// <summary>键盘关闭时的次要按钮图标色；与 BarAccentColor 同色相但更弱，仍保证可读。</summary>
        public Color BarAccentMutedColor;
    }

    public static readonly Rectangle ChestLeftRect = new(183, 263, 62, 44);
    public static readonly Rectangle ChestRightRect = new(193, 215, 62, 44);
    const long ChestCycleMs = 30 * 60 * 1000;

    public static Bitmap Compose(PetFrames f, int bodyFrame, int headFrame, Size pixelSize, Info info, double opacity = 1.0)
    {
        var bmp = new Bitmap(pixelSize.Width, pixelSize.Height, PixelFormat.Format32bppPArgb);
        ComposeInto(bmp, f, bodyFrame, headFrame, info, opacity);
        return bmp;
    }

    /// <summary>把同一帧画到已有位图上（窗口重绘很频繁，避免每次分配新位图）。</summary>
    public static void ComposeInto(Bitmap bmp, PetFrames f, int bodyFrame, int headFrame, Info info, double opacity = 1.0)
        => ComposeInto(bmp, f, bodyFrame, headFrame, info, opacity, null);

    internal static void ComposeInto(Bitmap bmp, PetFrames f, int bodyFrame, int headFrame, Info info, FrameCache cache)
        => ComposeInto(bmp, f, bodyFrame, headFrame, info, 1.0, cache);

    static void ComposeInto(Bitmap bmp, PetFrames f, int bodyFrame, int headFrame, Info info, double opacity, FrameCache cache)
    {
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = bmp.Width / (float)WindowW;

        Image head = headFrame == 2 ? f.Head2 ?? f.Head1 : f.Head1;
        Image body = bodyFrame switch { 2 => f.Body2 ?? f.Body1, 3 => f.Body3 ?? f.Body1, _ => f.Body1 };
        var cat = new Rectangle((int)Math.Round(CatX * s), (int)Math.Round(CatY * s), (int)Math.Round(CatSize * s), (int)Math.Round(CatSize * s));
        if (cache == null)
            DrawCharacter(g, s, head, body, f.Accessory, f.AccOffsetX + info.AccOffsetX, f.AccOffsetY + info.AccOffsetY,
                info.AccScalePercent, info.AccRotationDegrees, cat);
        else
            cache.DrawCharacter(g, bmp.Size, s, head, body, f.Accessory,
                f.AccOffsetX + info.AccOffsetX, f.AccOffsetY + info.AccOffsetY,
                info.AccScalePercent, info.AccRotationDegrees, cat);
        if (info.Selected)
            DrawSelection(g, new RectangleF(cat.X, cat.Y, cat.Width, cat.Height), s);

        if (info.Bubbles != null)
            info.Bubbles.Draw(g, s, (CatX + CatSize / 2f) * s, (CatY + CatSize / 2f) * s);
        info.KeyBubbles?.Draw(g, s, (CatX + CatSize / 2f) * s, (CatY + CatSize / 2f) * s);

        DrawChests(g, s, info);
        info.Wheel?.Draw(g, s, CatX, CatY);

        // 气泡、宝箱、轮盘仍在底栏后面，不能把整个桌宠合成一张背景缓存。
        // 底栏隐藏时也必须走缓存路径，让缓存用新的 ShowBar key 清空旧底栏位图。
        if (cache == null)
        {
            if (info.ShowBar)
            {
                DrawStrip(g, s, info, bmp.Height);
                DrawHandsOverStrip(g, s, body, cat, bmp.Height);
            }
        }
        else cache.DrawStrip(g, bmp.Size, s, info, body, cat);
        if (info.Loading) DrawLoading(g, s, info.LoadingPhase, CatX + CatSize - 30, CatY + 30);
        if (opacity < 0.999)
        {
            using var attrs = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0.1, 1.0) };
            attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            using var copy = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
            using (var cg = Graphics.FromImage(copy)) cg.DrawImage(bmp, 0, 0);
            g.Clear(Color.Transparent);
            g.DrawImage(copy, new Rectangle(0, 0, copy.Width, copy.Height), 0, 0, copy.Width, copy.Height, GraphicsUnit.Pixel, attrs);
        }
    }

    static void DrawCharacter(Graphics g, float s, Image head, Image body, Image accessory, double offsetX, double offsetY,
        int scalePercent, int rotationDegrees, Rectangle cat)
    {
        if (head != null) g.DrawImage(head, cat);
        if (body != null) g.DrawImage(body, cat);
        if (accessory != null)
        {
            int ax = (int)Math.Round((CatX + offsetX) * s);
            int ay = (int)Math.Round((CatY + offsetY) * s);
            scalePercent = Math.Clamp(scalePercent <= 0 ? 100 : scalePercent,
                AccessoryTransform.MinScalePercent, AccessoryTransform.MaxScalePercent);
            rotationDegrees = Math.Clamp(rotationDegrees,
                AccessoryTransform.MinRotationDegrees, AccessoryTransform.MaxRotationDegrees);
            if (scalePercent != 100 || rotationDegrees != 0)
            {
                // 只对头饰做围绕自身中心的缩放/旋转，头和身体保持原样。
                var state = g.Save();
                try
                {
                    float cx = ax + cat.Width / 2f;
                    float cy = ay + cat.Height / 2f;
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(rotationDegrees);
                    g.ScaleTransform(scalePercent / 100f, scalePercent / 100f);
                    g.TranslateTransform(-cx, -cy);
                    g.DrawImage(accessory, ax, ay, cat.Width, cat.Height);
                }
                finally { g.Restore(state); }
            }
            else
            {
                g.DrawImage(accessory, ax, ay, cat.Width, cat.Height);
            }
        }
    }

    /// <summary>每个窗口只保留当前角色层和底栏层；图片到货、动作、偏移、尺寸变化均按实际值失效。</summary>
    internal sealed class FrameCache : IDisposable
    {
        Bitmap _character, _strip;
        (Size Size, Image Head, Image Body, Image Accessory, double X, double Y, int ScalePercent, int RotationDegrees) _characterKey;
        (Size Size, bool ShowBar, Image Body, long Clicks, bool KeyboardOn, Color BarColor, Color BarTextColor, Color BarAccentColor, Color BarAccentMutedColor) _stripKey;

        internal void DrawCharacter(Graphics target, Size size, float scale, Image head, Image body, Image accessory,
            double offsetX, double offsetY, int accessoryScalePercent, int accessoryRotationDegrees, Rectangle cat)
        {
            var key = (size, head, body, accessory, offsetX, offsetY, accessoryScalePercent, accessoryRotationDegrees);
            if (_character == null || _characterKey != key)
            {
                Prepare(ref _character, size);
                using var g = Graphics.FromImage(_character);
                Configure(g);
                PetComposer.DrawCharacter(g, scale, head, body, accessory, offsetX, offsetY,
                    accessoryScalePercent, accessoryRotationDegrees, cat);
                _characterKey = key;
            }
            target.DrawImageUnscaled(_character, 0, 0);
        }

        internal void DrawStrip(Graphics target, Size size, float scale, Info info, Image body, Rectangle cat)
        {
            var key = (size, info.ShowBar, body, info.Clicks, info.KeyboardOn, info.BarColor, info.BarTextColor, info.BarAccentColor, info.BarAccentMutedColor);
            if (_strip == null || _stripKey != key)
            {
                Prepare(ref _strip, size);
                using (var g = Graphics.FromImage(_strip))
                {
                    Configure(g);
                    if (info.ShowBar)
                    {
                        PetComposer.DrawStrip(g, scale, info, size.Height);
                        DrawHandsOverStrip(g, scale, body, cat, size.Height);
                    }
                }
                _stripKey = key;
            }
            if (info.ShowBar) target.DrawImageUnscaled(_strip, 0, 0);
        }

        static void Prepare(ref Bitmap bitmap, Size size)
        {
            if (bitmap == null || bitmap.Size != size)
            {
                bitmap?.Dispose();
                bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            }
        }

        static void Configure(Graphics g)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public void Dispose()
        {
            _character?.Dispose();
            _strip?.Dispose();
            _character = _strip = null;
            _characterKey = default;
            _stripKey = default;
        }
    }

    static Rectangle AlignStripBottom(Rectangle rect, int windowHeight)
    {
        int delta = windowHeight - rect.Bottom;
        if (delta != 0) rect.Height += delta;
        return rect;
    }

    static void DrawStrip(Graphics g, float s, Info info, int windowHeight)
    {
        if (!info.ShowBar) return;
        // 官方底栏是一整块圆角奶油条：计数与右侧按钮共用一个轮廓，不再画成两个分离的小胶囊。
        var counter = Scaled(CounterRect, s);
        var toolbar = new Rectangle((int)Math.Round(ToolbarX * s), (int)Math.Round(InfoY * s),
            (int)Math.Round(ToolbarW * s), (int)Math.Round(CounterH * s));
        var bar = AlignStripBottom(new Rectangle(counter.X, counter.Y, toolbar.Right - counter.X,
            Math.Max(counter.Height, toolbar.Height)), windowHeight);

        var barColor = info.BarColor.IsEmpty ? Cream : info.BarColor;
        using (var path = Rounded(bar, 16 * s))
        using (var brush = new SolidBrush(barColor))
            g.FillPath(brush, path);

        using (var font = new Font("Microsoft YaHei UI", 18f * s, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(info.BarTextColor.IsEmpty ? InkStrong : info.BarTextColor))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
        {
            var textRect = new RectangleF(counter.X + 18 * s, bar.Y, counter.Width - 18 * s, bar.Height);
            DrawClickCount(g, FormatClicks(info.Clicks), font, brush.Color, textRect, fmt);
        }

        // 键盘图标：描边粗细、可见高度和中心线与右侧菜单图标保持一致。
        var kb = Scaled(KeyboardRect, s);
        // 绘制端再兜底一次：即使调用方仍把原始主题色直接塞进 Info，也不会在浅色底栏上画出不可见图标。
        Color iconColor = PetWindow.EnsureBarIconReadable(
            info.BarAccentColor.IsEmpty ? Ink : info.BarAccentColor, barColor);
        // 键盘关闭时不再用 75% 透明叠加（会把已经贴近阈值的图标重新压到不可读），
        // 改用同色相、已校正对比度的次要图标色。
        Color mutedIconColor = PetWindow.EnsureBarIconReadable(
            info.BarAccentMutedColor.IsEmpty
                ? PetWindow.BlendColor(iconColor, barColor, 0.28f)
                : info.BarAccentMutedColor,
            barColor);
        float iconStroke = Math.Max(1.2f, 2.2f * s);
        using (var pen = new Pen(info.KeyboardOn ? iconColor : mutedIconColor, iconStroke))
        {
            float pad = 3.2f * s;
            float iconH = 10.0f * s;
            var r = new RectangleF(kb.X + pad, kb.Y + (kb.Height - iconH) / 2f, kb.Width - pad * 2, iconH);
            using var p = Rounded(r, 2.6f * s);
            g.DrawPath(pen, p);
            g.DrawLine(pen, r.X + r.Width * 0.34f, r.Y + r.Height / 2f, r.X + r.Width * 0.66f, r.Y + r.Height / 2f);
        }
        // 菜单图标 ≡：比原来短一点，线宽稍加厚，圆头端点让三条横线更有质感。
        var menu = Scaled(MenuRect, s);
        using (var pen = new Pen(iconColor, iconStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = menu.X + menu.Width / 2f, cy = menu.Y + menu.Height / 2f;
            float w = menu.Width * 0.34f;
            for (int i = -1; i <= 1; i++)
                g.DrawLine(pen, cx - w, cy + i * 5.0f * s, cx + w, cy + i * 5.0f * s);
        }
    }

    static string FormatClicks(long n) => n.ToString();

    /// <summary>
    /// 点击数字用矢量文字路径填充，而不是 DrawString 的 ClearType 子像素渲染。
    /// 底栏最终会缓存到透明位图并可能按 DPI/缩放重采样，路径填充在透明背景上更干净、边缘更平滑。
    /// </summary>
    static void DrawClickCount(Graphics g, string text, Font font, Color color, RectangleF rect, StringFormat format)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, rect, format);
            using var brush = new SolidBrush(color);
            using var oldHint = new GraphicsStateScope(g);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.FillPath(brush, path);
        }
        catch
        {
            // 极少数字体/环境不支持 AddString 时保持原行为，不能让 HUD 消失。
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, rect, format);
        }
    }

    /// <summary>临时切换 TextRenderingHint，避免影响同一次 Composer 里的其他文字绘制。</summary>
    readonly struct GraphicsStateScope : IDisposable
    {
        readonly Graphics _graphics;
        readonly TextRenderingHint _oldHint;
        public GraphicsStateScope(Graphics graphics)
        {
            _graphics = graphics;
            _oldHint = graphics.TextRenderingHint;
        }
        public void Dispose() { if (_graphics != null) _graphics.TextRenderingHint = _oldHint; }
    }

    /// <summary>只把角色手部帧重新画在计数条/工具栏范围内，避免整只角色压到 HUD 上。</summary>
    static void DrawHandsOverStrip(Graphics g, float s, Image body, Rectangle cat, int windowHeight)
    {
        if (body == null) return;
        var counter = Scaled(CounterRect, s);
        var toolbar = new Rectangle((int)Math.Round(ToolbarX * s), (int)Math.Round(InfoY * s),
            (int)Math.Round(ToolbarW * s), (int)Math.Round(CounterH * s));
        var bar = AlignStripBottom(new Rectangle(counter.X, counter.Y, toolbar.Right - counter.X,
            Math.Max(counter.Height, toolbar.Height)), windowHeight);
        var state = g.Save();
        try
        {
            using var clip = Rounded(bar, 16 * s);
            g.SetClip(clip, CombineMode.Intersect);
            g.DrawImage(body, cat);
        }
        finally { g.Restore(state); }
    }

    /// <summary>猫右侧两个宝箱锚点：剩余时间气泡 + 进度条，可点击领取。</summary>
    static void DrawChests(Graphics g, float s, Info info)
    {
        if (info.ShowChestLeft) DrawChest(g, s, ChestLeftRect, info.ChestLeftTs, info.NowMs, info.ChestButton, info.ChestCountdown);
        if (info.ShowChestRight) DrawChest(g, s, ChestRightRect, info.ChestRightTs, info.NowMs, info.ChestButton, info.ChestCountdown);
    }

    static void DrawChest(Graphics g, float s, Rectangle rect, long ts, long now, Image buttonArt, Image countdownArt)
    {
        bool has = ts > 0;
        long remain = has ? Math.Max(0, ts - now) : 0;
        bool ready = has && remain <= 0;
        double progress = has ? Math.Clamp(1.0 - remain / (double)ChestCycleMs, 0, 1) : 0;
        var box = Scaled(rect, s);

        if (ready)
        {
            // 原版宝箱按钮图（84×60 → 62×44）
            if (buttonArt != null) g.DrawImage(buttonArt, box);
            else
            {
                using var path = Rounded(box, 10 * s);
                using var brush = new SolidBrush(Cream);
                using var pen = new Pen(Color.FromArgb(245, 196, 120, 60), 2f * s);
                g.FillPath(brush, path); g.DrawPath(pen, path);
            }
            return;
        }

        // 原版倒计时气泡图（196×99 → 约 64×36），文本叠在中间
        var bubble = new RectangleF(box.X - 1 * s, box.Y + 4 * s, 64 * s, 36 * s);
        if (countdownArt != null) g.DrawImage(countdownArt, bubble);
        else
        {
            using var path = Rounded(bubble, 9 * s);
            using var brush = new SolidBrush(Cream);
            g.FillPath(brush, path);
        }
        string text = has ? FormatRemain(remain) : "--:--";
        using (var font = new Font("Microsoft YaHei UI", 14f * s, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(InkStrong))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, font, brush, new RectangleF(bubble.X, bubble.Y + 2 * s, bubble.Width, bubble.Height - 12 * s), fmt);

        var track = new RectangleF(bubble.X + 9 * s, bubble.Bottom - 8 * s, bubble.Width - 18 * s, 3f * s);
        using (var bg = new SolidBrush(Color.FromArgb(60, Border)))
            g.FillRectangle(bg, track);
        if (has)
        {
            using var fill = new SolidBrush(Color.FromArgb(230, 214, 152, 78));
            g.FillRectangle(fill, track.X, track.Y, (float)(track.Width * progress), track.Height);
        }
    }

    static string FormatRemain(long ms)
    {
        long total = (ms + 999) / 1000;
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>图片还在下载时画一个旋转圆环；网络慢时让用户知道不是卡死。</summary>
    public static void DrawLoading(Graphics g, float s, double phase, float centerX, float centerY)
    {
        float r = Math.Max(9f, 15f * s);
        var box = new RectangleF(centerX * s - r, centerY * s - r, r * 2, r * 2);
        using (var bg = new SolidBrush(Color.FromArgb(220, Cream)))
            g.FillEllipse(bg, box);
        using var pen = new Pen(Color.FromArgb(245, 214, 152, 78), Math.Max(2f, 3.2f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        float start = (float)((phase * 360.0) % 360.0);
        g.DrawArc(pen, box, start, 285f);
    }

    static Rectangle Scaled(Rectangle r, float s) =>
        new((int)Math.Round(r.X * s), (int)Math.Round(r.Y * s), (int)Math.Round(r.Width * s), (int)Math.Round(r.Height * s));

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(2, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>多选拖拽时在主角色/联机角色周围绘制选中框。</summary>
    public static void DrawSelection(Graphics g, RectangleF rect, float s)
    {
        using var path = Rounded(rect, 18 * s);
        using var fill = new SolidBrush(Color.FromArgb(24, 76, 151, 141));
        using var pen = new Pen(Color.FromArgb(220, 76, 151, 141), Math.Max(1.5f, 2f * s));
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// 只按角色图片的 alpha 判断鼠标是否真正落在角色本体上。
    /// 表情/键帽气泡画在角色上方，不能参与悬停虚化判定。
    /// </summary>
    public static bool HitTestCat(PetFrames f, int bodyFrame, int headFrame, int x, int y, int accOffsetX = 0, int accOffsetY = 0,
        int accScalePercent = 100, int accRotationDegrees = 0)
    {
        if (f == null || !f.Ready || x < 0 || y < 0 || x >= CatSize || y >= CatSize) return false;
        Image head = headFrame == 2 ? f.Head2 ?? f.Head1 : f.Head1;
        Image body = bodyFrame switch { 2 => f.Body2 ?? f.Body1, 3 => f.Body3 ?? f.Body1, _ => f.Body1 };
        if (HasOpaquePixel(head, x, y)) return true;
        if (HasOpaquePixel(body, x, y)) return true;
        if (f.Accessory == null) return false;

        // 与 DrawCharacter 一致：头饰围绕绘制中心缩放/旋转。命中检测先把点反向变换回原图坐标。
        double scale = Math.Clamp(accScalePercent <= 0 ? 100 : accScalePercent,
            AccessoryTransform.MinScalePercent, AccessoryTransform.MaxScalePercent) / 100.0;
        double angle = Math.Clamp(accRotationDegrees,
            AccessoryTransform.MinRotationDegrees, AccessoryTransform.MaxRotationDegrees) * Math.PI / 180.0;
        double cx = accOffsetX + CatSize / 2.0;
        double cy = accOffsetY + CatSize / 2.0;
        double dx = x - cx;
        double dy = y - cy;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double localX = cx + (dx * cos + dy * sin) / scale - accOffsetX;
        double localY = cy + (-dx * sin + dy * cos) / scale - accOffsetY;
        return HasOpaquePixel(f.Accessory, localX, localY);
    }

    static bool HasOpaquePixel(Image image, int x, int y) => HasOpaquePixel(image, (double)x, (double)y);

    static bool HasOpaquePixel(Image image, double x, double y)
    {
        if (image is not Bitmap bmp || x < 0 || y < 0) return false;
        int px = (int)Math.Round(x * (bmp.Width - 1) / (double)(CatSize - 1));
        int py = (int)Math.Round(y * (bmp.Height - 1) / (double)(CatSize - 1));
        if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) return false;
        try { return bmp.GetPixel(px, py).A > 8; } catch { return false; }
    }
}
