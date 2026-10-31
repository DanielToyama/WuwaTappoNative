using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace WuwatappoNative.Pet;

/// <summary>
/// 表情气泡层：连续发送时每个表情独立上浮。
/// 原版参数：1s ease-out，起点中心偏移 (ox, oy-50)，上升 102px，缩放 1→1.08，70% 后淡出。
/// </summary>
public sealed class EmoteBubbleLayer
{
    sealed class Bubble
    {
        public long Id;
        public Image Image;
        public DateTime Start;
        public float OffsetX, OffsetY;
    }

    const int LifeMs = 1000;
    const float Rise = 102f;
    const int MaxBubbles = 30;

    // 原版偏移表（再叠加随机抖动）
    static readonly (float X, float Y)[] Offsets =
    {
        (-30, 10), (4, -6), (28, 12), (-14, 2), (36, 6), (-38, 16), (16, -10), (-6, 8)
    };

    readonly List<Bubble> _items = new();
    readonly Random _random = new();
    int _seq;
    long _nextId;

    public bool Active => _items.Count > 0;
    public int Count => _items.Count;

    public void Add(Image image)
    {
        if (image == null) return;
        var o = Offsets[_seq++ % Offsets.Length];
        _items.Add(new Bubble
        {
            Id = ++_nextId,
            Image = image,
            Start = DateTime.UtcNow,
            OffsetX = o.X + (float)(_random.NextDouble() * 10 - 5),
            OffsetY = o.Y + (float)(_random.NextDouble() * 8 - 4) - 50f,
        });
        if (_items.Count > MaxBubbles) _items.RemoveAt(0);
    }

    /// <summary>清理过期气泡；返回是否仍有活动气泡。</summary>
    public bool Tick()
    {
        var now = DateTime.UtcNow;
        _items.RemoveAll(b => (now - b.Start).TotalMilliseconds >= LifeMs);
        return _items.Count > 0;
    }

    public void Clear() => _items.Clear();

    /// <summary>在猫中心 (cx, cy) 处绘制，scale 为窗口缩放。</summary>
    public void Draw(Graphics g, float scale, float cx, float cy)
    {
        if (_items.Count == 0) return;
        var now = DateTime.UtcNow;
        using var attrs = new ImageAttributes();
        foreach (var b in _items)
        {
            double p = Math.Clamp((now - b.Start).TotalMilliseconds / LifeMs, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 2); // 近似 ease-out
            double alpha = p < 0.7 ? 1.0 : 1.0 - (p - 0.7) / 0.3;
            if (alpha <= 0.01) continue;
            double scl = 1 + 0.08 * ease;
            float size = (float)(80 * scl * scale);
            float x = cx + b.OffsetX * scale - size / 2f;
            float y = cy + b.OffsetY * scale - size * 0.3f - (float)(Rise * ease * scale);

            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
            var dest = new Rectangle(
                (int)Math.Round(x), (int)Math.Round(y),
                Math.Max(1, (int)Math.Round(size)), Math.Max(1, (int)Math.Round(size)));
            g.DrawImage(b.Image, dest, 0, 0, b.Image.Width, b.Image.Height, GraphicsUnit.Pixel, attrs);
        }
    }
}
