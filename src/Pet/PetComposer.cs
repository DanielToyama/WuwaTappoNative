using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using WuwatappoNative.Assets;

namespace WuwatappoNative.Pet;

public sealed class PetFrames
{
    public Image Head1, Head2, Body1, Body2, Body3, Accessory;
    public double AccOffsetX, AccOffsetY;
    public bool Ready => Head1 != null && Body1 != null;

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
            frames.Head1 = Fetch(_catalog.CdnUrl(skin.Frame("head1").Url), v => frames.Head1 = v, frames);
            frames.Head2 = Fetch(_catalog.CdnUrl((skin.Frame("head2") ?? skin.Frame("head1")).Url), v => frames.Head2 = v, frames);
            frames.Body1 = Fetch(_catalog.CdnUrl(skin.Frame("body1").Url), v => frames.Body1 = v, frames);
            frames.Body2 = Fetch(_catalog.CdnUrl((skin.Frame("body2") ?? skin.Frame("body1")).Url), v => frames.Body2 = v, frames);
            frames.Body3 = Fetch(_catalog.CdnUrl((skin.Frame("body3") ?? skin.Frame("body1")).Url), v => frames.Body3 = v, frames);
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
        frames.Head1 ??= LoadLocal("role_1501_1001_head_1.png");
        frames.Head2 ??= LoadLocal("role_1501_1001_head_2.png");
        frames.Body1 ??= LoadLocal("role_1501_1001_body_1.png");
        frames.Body2 ??= LoadLocal("role_1501_1001_body_2.png");
        frames.Body3 ??= LoadLocal("role_1501_1001_body_3.png");
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
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3 && !frames.Ready; i++) await Task.Delay(4000);
            if (frames.Ready) return;
            LoadDefaultFrames(frames);
            if (!frames.Ready) return;
            Core.Log.Write("外观下载超时，回退默认帧");
            frames.RaiseChanged();
        });
    }

    /// <summary>同步取图；未缓存时后台下载，完成后回填并通知所有窗口重绘。</summary>
    Image Fetch(string url, Action<Image> assign, PetFrames frames)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var img = _store.Get(url, () =>
        {
            var loaded = _store.Get(url);
            if (loaded == null) return;
            assign(loaded);
            frames.RaiseChanged();
        });
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
        public EmoteWheelLayer Wheel;
        public long ChestLeftTs, ChestRightTs;
        public long NowMs;
        public bool ShowChestLeft, ShowChestRight;
        public Image ChestButton, ChestCountdown;
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
        if (head != null) g.DrawImage(head, cat);
        if (body != null) g.DrawImage(body, cat);
        if (f.Accessory != null)
        {
            int ax = (int)Math.Round((CatX + f.AccOffsetX) * s);
            int ay = (int)Math.Round((CatY + f.AccOffsetY) * s);
            g.DrawImage(f.Accessory, ax, ay, cat.Width, cat.Height);
        }

        if (info.Bubbles != null)
            info.Bubbles.Draw(g, s, (CatX + CatSize / 2f) * s, (CatY + CatSize / 2f) * s);

        DrawChests(g, s, info);
        info.Wheel?.Draw(g, s, CatX, CatY);

        DrawStrip(g, s, info);
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

    static void DrawStrip(Graphics g, float s, Info info)
    {
        // 计数条 + 工具栏：原版为 136px 奶油底计数条与 61px 工具栏，底边对齐大猫。
        var counter = Scaled(CounterRect, s);
        var toolbar = new Rectangle((int)Math.Round(ToolbarX * s), (int)Math.Round(InfoY * s),
            (int)Math.Round(ToolbarW * s), (int)Math.Round(CounterH * s));

        using (var path = Rounded(counter, 16 * s))
        using (var brush = new SolidBrush(Cream))
            g.FillPath(brush, path);
        using (var path = Rounded(toolbar, 16 * s))
        using (var brush = new SolidBrush(Cream))
            g.FillPath(brush, path);

        using (var font = new Font("Microsoft YaHei UI", 18f * s, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(InkStrong))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
        {
            var textRect = new RectangleF(counter.X + 18 * s, counter.Y, counter.Width - 18 * s, counter.Height);
            g.DrawString(FormatClicks(info.Clicks), font, brush, textRect, fmt);
        }

        // 键盘图标
        var kb = Scaled(KeyboardRect, s);
        using (var pen = new Pen(info.KeyboardOn ? InkStrong : Color.FromArgb(190, 160, 130), Math.Max(1.2f, 1.5f * s)))
        {
            float pad = 4 * s;
            var r = new RectangleF(kb.X + pad, kb.Y + kb.Height * 0.32f, kb.Width - pad * 2, kb.Height * 0.42f);
            using var p = Rounded(r, 3 * s);
            g.DrawPath(pen, p);
            g.DrawLine(pen, kb.X + kb.Width * 0.34f, kb.Y + kb.Height * 0.5f, kb.X + kb.Width * 0.66f, kb.Y + kb.Height * 0.5f);
        }
        // 菜单图标 ≡
        var menu = Scaled(MenuRect, s);
        using (var pen = new Pen(Ink, Math.Max(1.4f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = menu.X + menu.Width / 2f, cy = menu.Y + menu.Height / 2f;
            float w = menu.Width * 0.42f;
            for (int i = -1; i <= 1; i++)
                g.DrawLine(pen, cx - w, cy + i * 5.5f * s, cx + w, cy + i * 5.5f * s);
        }
    }

    static string FormatClicks(long n) => n.ToString();

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
}
