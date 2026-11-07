using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;

internal static partial class Program
{
    // Snapshot of the upstream 0.1.7 drawing order; independent of the new base-frame cache.
    static void DrawOriginalRemote(Bitmap surface, PetFrames _frames, int _bodyFrame, float _scale,
        double _remoteNameScale, RoomMember _member, EmoteBubbleLayer _bubbles)
    {
        bool ready = _frames != null && _frames.Ready;
        using (var g = Graphics.FromImage(surface))
        {
            g.Clear(Color.Transparent);
            if (ready)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float s = _scale;
                float top = RemoteCatWindow.Headroom * s;
                float cat = RemoteCatWindow.CatSize * s;
                if (_frames.Head1 != null) g.DrawImage(_frames.Head1, 0, top, cat, cat);
                Image body = _bodyFrame switch
                {
                    2 => _frames.Body2 ?? _frames.Body1,
                    3 => _frames.Body3 ?? _frames.Body1,
                    _ => _frames.Body1
                };
                if (body != null) g.DrawImage(body, 0, top, cat, cat);
                if (_frames.Accessory != null)
                    g.DrawImage(_frames.Accessory, (float)(_frames.AccOffsetX * s), (float)(_frames.AccOffsetY * s) + top, cat, cat);

                float nameFontPx = 13.5f * s * (float)_remoteNameScale;
                float pillH = Math.Max(32f * s, nameFontPx * 1.35f);
                var pill = new RectangleF(10 * s, (RemoteCatWindow.Headroom + 179) * s - (pillH - 32 * s) / 2f, 180 * s, pillH);
                using (var path = PetComposer.Rounded(pill, 16 * s))
                using (var brush = new SolidBrush(PetComposer.Cream))
                    g.FillPath(brush, path);
                using (var font = new Font("Microsoft YaHei UI", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(PetComposer.InkStrong))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(string.IsNullOrEmpty(_member.Nickname) ? "漂泊者" : _member.Nickname, font, brush, pill, fmt);

                // 手/手臂要压住昵称条；角色头部和身体其余部分仍在 UI 后面。
                if (body != null)
                {
                    var state = g.Save();
                    try
                    {
                        using var clip = PetComposer.Rounded(pill, 16 * s);
                        g.SetClip(clip, CombineMode.Intersect);
                        g.DrawImage(body, 0, top, cat, cat);
                    }
                    finally { g.Restore(state); }
                }

                _bubbles.Draw(g, s, 100 * s, (RemoteCatWindow.Headroom + 100) * s);
            }
        }
    }
}
