using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WuwatappoNative.Pet;

/// <summary>
/// 按键气泡：每次按下生成一个键帽，按表情气泡的方式上浮、淡出。
/// 键帽图片按虚拟键缓存，绘制时只做合成，不在高频输入路径上重新排版文字。
/// </summary>
public sealed class KeyBubbleLayer : IDisposable
{
    sealed class Bubble
    {
        public Image Image;
        public readonly BubbleSprite Sprite = new();
        public int LogicalW, LogicalH;
        public long Start;
        public float OffsetX, OffsetY;
    }

    enum Kind { Normal, Modifier, Space, Enter, XboxA, XboxB, XboxX, XboxY }

    const int LifeMs = 1000;
    const float Rise = 96f;
    const int DefaultMaxBubbles = 10;
    const int LogicalH = 44;
    const int RenderScale = 2;

    static readonly (float X, float Y)[] Offsets =
    {
        (-70, 6), (-35, -10), (0, 10), (35, -6), (70, 12), (-55, 18), (55, 16), (-20, -18), (20, -16), (0, 22)
    };

    readonly List<Bubble> _items = new();
    readonly Dictionary<string, Image> _cache = new();
    readonly Random _random = new();
    readonly int _maxBubbles;
    int _seq;

    public KeyBubbleLayer(int maxBubbles = DefaultMaxBubbles)
    {
        _maxBubbles = Math.Clamp(maxBubbles, 1, 30);
    }

    public bool Active => _items.Count > 0;
    public int Count => _items.Count;

    /// <summary>键盘按键边沿调用；返回是否真的生成了气泡。</summary>
    public bool Add(int vk)
    {
        string label = LabelFor(vk);
        if (string.IsNullOrEmpty(label)) return false;
        Image image = GetImage("vk:" + vk, label, KindFor(vk));
        if (image == null) return false;

        var offset = Offsets[_seq++ % Offsets.Length];
        _items.Add(new Bubble
        {
            Image = image,
            LogicalW = image.Width / RenderScale,
            LogicalH = image.Height / RenderScale,
            Start = Environment.TickCount64,
            OffsetX = offset.X + (float)(_random.NextDouble() * 8 - 4),
            OffsetY = offset.Y + (float)(_random.NextDouble() * 6 - 3) - 36f,
        });
        Trim();
        return true;
    }

    /// <summary>手柄按键边沿调用；accent 用于肩键/扳机等次要键。</summary>
    public bool AddLabel(string label, bool accent = false)
    {
        label = (label ?? "").Trim();
        if (label.Length == 0) return false;
        Kind kind = accent ? Kind.Modifier : PadKindFor(label);
        Image image = GetImage("pad:" + label + ":" + (int)kind, label, kind);
        if (image == null) return false;

        var offset = Offsets[_seq++ % Offsets.Length];
        _items.Add(new Bubble
        {
            Image = image,
            LogicalW = image.Width / RenderScale,
            LogicalH = image.Height / RenderScale,
            Start = Environment.TickCount64,
            OffsetX = offset.X + (float)(_random.NextDouble() * 8 - 4),
            OffsetY = offset.Y + (float)(_random.NextDouble() * 6 - 3) - 36f,
        });
        Trim();
        return true;
    }

    void Trim()
    {
        if (_items.Count <= _maxBubbles) return;
        _items[0].Sprite.Dispose();
        _items.RemoveAt(0);
    }

    public bool Tick()
    {
        long now = Environment.TickCount64;
        for (int i = _items.Count - 1; i >= 0; --i)
        {
            if (now - _items[i].Start < LifeMs) continue;
            _items[i].Sprite.Dispose();
            _items.RemoveAt(i);
        }
        return _items.Count > 0;
    }

    public void Clear()
    {
        foreach (var bubble in _items) bubble.Sprite.Dispose();
        _items.Clear();
    }

    /// <summary>在猫中心 (cx, cy) 处绘制，scale 为窗口缩放。</summary>
    public void Draw(Graphics g, float scale, float cx, float cy)
    {
        if (_items.Count == 0) return;
        long now = Environment.TickCount64;
        using var attrs = new ImageAttributes();
        foreach (var b in _items)
        {
            double p = Math.Clamp((now - b.Start) / (double)LifeMs, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 2);
            double alpha = p < 0.72 ? 1.0 : 1.0 - (p - 0.72) / 0.28;
            if (alpha <= 0.01) continue;

            double pop = 1 + 0.06 * ease;
            float w = (float)(b.LogicalW * scale * pop);
            float h = (float)(b.LogicalH * scale * pop);
            float x = cx + b.OffsetX * scale - w / 2f;
            float y = cy + b.OffsetY * scale - h * 0.72f - (float)(Rise * ease * scale);
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
            b.Sprite.Draw(g, b.Image,
                new Rectangle((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h))),
                alpha, attrs);
        }
    }

    Image GetImage(string key, string label, Kind kind)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var image = RenderKeycap(label, kind);
        if (image != null) _cache[key] = image;
        return image;
    }

    static Image RenderKeycap(string label, Kind kind)
    {
        using var font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel);
        Size textSize;
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
            textSize = Size.Ceiling(measure.MeasureString(label, font));

        int logicalW = Math.Max(48, textSize.Width + 24);
        int logicalH = LogicalH;
        var bmp = new Bitmap(logicalW * RenderScale, logicalH * RenderScale, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(RenderScale, RenderScale);
        g.Clear(Color.Transparent);

        var (fill, border, ink) = Colors(kind);
        var box = new RectangleF(1.5f, 1.5f, logicalW - 3f, logicalH - 5f);
        using var shadowPath = Rounded(new RectangleF(box.X + 2f, box.Y + 3f, box.Width, box.Height), 13f);
        using (var shadow = new SolidBrush(Color.FromArgb(38, 110, 69, 45)))
            g.FillPath(shadow, shadowPath);

        using var path = Rounded(box, 13f);
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        using (var pen = new Pen(border, 2f))
            g.DrawPath(pen, path);
        using (var highlight = new Pen(Color.FromArgb(180, 255, 255, 255), 1.2f))
            g.DrawLine(highlight, box.X + 10f, box.Y + 3.2f, box.Right - 10f, box.Y + 3.2f);

        using (var brush = new SolidBrush(ink))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(label, font, brush, new RectangleF(0, 0, logicalW, logicalH - 2f), format);
        return bmp;
    }

    static (Color Fill, Color Border, Color Ink) Colors(Kind kind) => kind switch
    {
        Kind.Modifier => (Color.FromArgb(255, 240, 207), Color.FromArgb(201, 146, 63), Color.FromArgb(110, 69, 45)),
        Kind.Space => (Color.FromArgb(225, 241, 237), Color.FromArgb(76, 151, 141), Color.FromArgb(54, 105, 99)),
        Kind.Enter => (Color.FromArgb(248, 223, 228), Color.FromArgb(217, 111, 134), Color.FromArgb(122, 63, 77)),
        Kind.XboxA => (Color.FromArgb(234, 246, 228), Color.FromArgb(90, 158, 75), Color.FromArgb(47, 107, 42)),
        Kind.XboxB => (Color.FromArgb(252, 233, 230), Color.FromArgb(217, 88, 75), Color.FromArgb(142, 48, 40)),
        Kind.XboxX => (Color.FromArgb(232, 240, 252), Color.FromArgb(59, 127, 212), Color.FromArgb(43, 86, 142)),
        Kind.XboxY => (Color.FromArgb(255, 243, 209), Color.FromArgb(214, 163, 47), Color.FromArgb(126, 90, 24)),
        _ => (Color.FromArgb(255, 253, 247), Color.FromArgb(231, 183, 124), Color.FromArgb(110, 69, 45)),
    };

    static Kind PadKindFor(string label) => label switch
    {
        "A" => Kind.XboxA,
        "B" => Kind.XboxB,
        "X" => Kind.XboxX,
        "Y" => Kind.XboxY,
        _ => Kind.Normal,
    };

    static Kind KindFor(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 => Kind.Modifier,
        0x20 => Kind.Space,
        0x0D => Kind.Enter,
        _ => Kind.Normal,
    };

    static string LabelFor(int vk) => vk switch
    {
        0x01 => "左键",
        0x02 => "右键",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Insert",
        0x2E => "Delete",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1),
        _ => null,
    };

    static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(2, radius * 2);
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        Clear();
        foreach (var image in _cache.Values) image.Dispose();
        _cache.Clear();
    }
}
