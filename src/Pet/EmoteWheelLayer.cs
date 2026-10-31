using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WuwatappoNative.Pet;

/// <summary>
/// 表情轮盘：按原版 emotion-dot-slot 的坐标放在猫身上，32px 圆点、30px 表情图，
/// 展开 520ms、每槽延迟 45ms、带回弹。
/// </summary>
public sealed class EmoteWheelLayer
{
    // 相对猫左上角的槽位中心（来自原版 CSS 的 left/top + 16px 半径）
    static readonly PointF[] Centers =
    {
        new(6, 159), new(-5, 124), new(-2, 88), new(16, 56), new(44, 32)
    };

    const int SlotSize = 32;
    const int ImageSize = 30;
    const float OpenMs = 520f;
    const float StaggerMs = 45f;

    long[] _ids = Array.Empty<long>();
    Func<long, Image> _loader;
    DateTime _openedAt;
    bool _open;

    public bool Open => _open;
    public bool Animating => _open && (DateTime.UtcNow - _openedAt).TotalMilliseconds < OpenMs + StaggerMs * 4;

    public void Show(long[] ids, Func<long, Image> loader)
    {
        _ids = ids ?? Array.Empty<long>();
        _loader = loader;
        _openedAt = DateTime.UtcNow;
        _open = true;
    }

    public void Close() => _open = false;

    public static PointF CenterOf(int index, int catX, int catY)
    {
        var c = Centers[Math.Clamp(index, 0, Centers.Length - 1)];
        return new PointF(catX + c.X, catY + c.Y);
    }

    public bool Hit(PointF p, int catX, int catY, out long id)
    {
        id = 0;
        if (!_open) return false;
        for (int i = 0; i < 5 && i < _ids.Length; i++)
        {
            var c = CenterOf(i, catX, catY);
            float dx = p.X - c.X, dy = p.Y - c.Y;
            if (dx * dx + dy * dy <= (SlotSize / 2f + 2) * (SlotSize / 2f + 2))
            {
                id = _ids[i];
                return id > 0;
            }
        }
        return false;
    }

    public void Draw(Graphics g, float scale, int catX, int catY)
    {
        if (!_open) return;
        double elapsed = (DateTime.UtcNow - _openedAt).TotalMilliseconds;
        var origin = new PointF(catX + 40, catY + 182);
        for (int i = 0; i < 5 && i < _ids.Length; i++)
        {
            double p = Math.Clamp((elapsed - i * StaggerMs) / OpenMs, 0, 1);
            if (p <= 0) continue;
            // 近似 cubic-bezier(.34,1.45,.48,1) 的回弹
            double e = EaseOutBack(p);
            var target = CenterOf(i, catX, catY);
            float cx = (float)(origin.X + (target.X - origin.X) * e);
            float cy = (float)(origin.Y + (target.Y - origin.Y) * e);
            float scl = (float)(0.15 + 0.85 * e);
            float opacity = (float)Math.Min(1, p * 2.5);

            float size = SlotSize * scale * scl;
            var rect = new RectangleF(cx * scale - size / 2, cy * scale - size / 2, size, size);
            using (var path = PetComposer.Rounded(rect, size / 2))
            using (var brush = new SolidBrush(Color.FromArgb((int)(225 * opacity), 253, 249, 237)))
            using (var pen = new Pen(Color.FromArgb((int)(120 * opacity), 142, 94, 67), 1f * scale))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            long id = _ids[i];
            if (id <= 0 || _loader == null) continue;
            var img = _loader(id);
            if (img == null) continue;
            float isz = ImageSize * scale * scl;
            var imgRect = new RectangleF(cx * scale - isz / 2, cy * scale - isz / 2, isz, isz);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
            g.DrawImage(img, new Rectangle((int)imgRect.X, (int)imgRect.Y, Math.Max(1, (int)imgRect.Width), Math.Max(1, (int)imgRect.Height)),
                0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        }
    }

    static double EaseOutBack(double x)
    {
        const double c1 = 1.1, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
}
