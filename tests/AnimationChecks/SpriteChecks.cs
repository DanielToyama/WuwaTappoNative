using System.Collections;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static void CheckSprites()
    {
        using var source = Art(Color.FromArgb(180, 70, 130, 240), true);
        using var expected = new Bitmap(240, 240, PixelFormat.Format32bppPArgb);
        using var actual = new Bitmap(240, 240, PixelFormat.Format32bppPArgb);
        using var sprite = new BubbleSprite();
        using var attributes = new ImageAttributes();
        foreach (var mode in new[] { PixelOffsetMode.Default, PixelOffsetMode.HighQuality })
        foreach (int size in new[] { 80, 83, 86, 160 })
        foreach (double alpha in new[] { 1.0, 0.8, 0.4, 0.05 })
        {
            var dest = new Rectangle(30, 40, size, size);
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
            using (var g = Graphics.FromImage(expected))
            {
                g.Clear(Color.FromArgb(140, 50, 90, 40));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = mode;
                g.DrawImage(source, dest, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            using (var g = Graphics.FromImage(actual))
            {
                g.Clear(Color.FromArgb(140, 50, 90, 40));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = mode;
                sprite.Draw(g, source, dest, alpha, attributes);
            }
            SamePixels(expected, actual, $"bubble sprite {mode}, {size}px, alpha={alpha}", 2);
        }
        var cached = Field(sprite, "_scaled").GetValue(sprite);
        using (var g = Graphics.FromImage(actual)) sprite.Draw(g, source, new Rectangle(30, 40, 160, 160), 0.5, attributes);
        // The pixel-offset mode changed above; repeat with identical graphics settings.
        using (var g = Graphics.FromImage(actual))
        {
            var current = Field(sprite, "_scaled").GetValue(sprite);
            sprite.Draw(g, source, new Rectangle(30, 40, 160, 160), 0.5, attributes);
            Assert(ReferenceEquals(current, Field(sprite, "_scaled").GetValue(sprite)), "Bubble sprite must reuse identical pixel size.");
        }
        sprite.Dispose();
        Assert(Field(sprite, "_scaled").GetValue(sprite) == null, "Bubble sprite must release its bitmap.");
        var bubbles = new EmoteBubbleLayer(1);
        bubbles.Add(source);
        using (var g = Graphics.FromImage(actual)) bubbles.Draw(g, 1, 120, 220);
        var items = (IList)Field(bubbles, "_items").GetValue(bubbles)!;
        var bubble = items[0]!;
        var ownedSprite = bubble.GetType().GetField("Sprite")!.GetValue(bubble)!;
        Assert(Field(ownedSprite, "_scaled").GetValue(ownedSprite) != null, "Draw must prepare a sprite.");
        bubbles.Add(source);
        Assert(Field(ownedSprite, "_scaled").GetValue(ownedSprite) == null, "Evicting a bubble must dispose its sprite.");
        using (var g = Graphics.FromImage(actual)) bubbles.Draw(g, 1, 120, 220);
        var nextSprite = items[0]!.GetType().GetField("Sprite")!.GetValue(items[0])!;
        Expire(bubbles); bubbles.Tick();
        Assert(Field(nextSprite, "_scaled").GetValue(nextSprite) == null, "Expiry must dispose a sprite.");
        bubbles.Clear();
        Console.WriteLine("PASS: cached bubble scaling/fading matches original drawing; reuse and expiry/eviction release checked.");
    }

    static void SetProgress(EmoteBubbleLayer layer, double progress)
    {
        var items = (IList)Field(layer, "_items").GetValue(layer)!;
        foreach (var item in items) item.GetType().GetField("Start")!.SetValue(item, AnimationTimer.NowMs() - (long)(progress * 1000));
    }

    // Snapshot of the original per-frame scaling algorithm, used only for performance comparison.
    static void DrawOriginalEmotes(Graphics g, EmoteBubbleLayer layer, float scale, float cx, float cy)
    {
        long now = AnimationTimer.NowMs();
        var items = (IList)Field(layer, "_items").GetValue(layer)!;
        using var attributes = new ImageAttributes();
        foreach (var item in items)
        {
            var type = item.GetType();
            var image = (Image)type.GetField("Image")!.GetValue(item)!;
            long start = (long)type.GetField("Start")!.GetValue(item)!;
            float ox = (float)type.GetField("OffsetX")!.GetValue(item)!;
            float oy = (float)type.GetField("OffsetY")!.GetValue(item)!;
            double p = Math.Clamp((now - start) / 1000.0, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 2);
            double alpha = p < 0.7 ? 1.0 : 1.0 - (p - 0.7) / 0.3;
            if (alpha <= 0.01) continue;
            float size = (float)(80 * (1 + 0.08 * ease) * scale);
            float x = cx + ox * scale - size / 2f;
            float y = cy + oy * scale - size * 0.3f - (float)(102 * ease * scale);
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
            var dest = new Rectangle((int)Math.Round(x), (int)Math.Round(y),
                Math.Max(1, (int)Math.Round(size)), Math.Max(1, (int)Math.Round(size)));
            g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
    }
}
