using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WuwatappoNative.Core;

/// <summary>
/// 统一应用图标。设置面板/任务栏和托盘共用 app.ico，但会先裁掉素材四周的透明留白，
/// 再按系统实际图标尺寸重绘；否则 16/24px 的托盘图标看起来会比旁边的系统图标小一圈。
/// </summary>
public static class AppIcon
{
    static Icon _panel;
    static Icon _tray;

    /// <summary>任务栏/Alt+Tab 使用的大图标尺寸（100% 缩放下为 32px）。</summary>
    public static Icon Panel => _panel ??= Create(Math.Max(32, SystemInformation.IconSize.Width));

    /// <summary>托盘使用的小图标尺寸（100% 缩放下为 16px，高 DPI 下由系统给出 20/24px）。</summary>
    public static Icon Tray => _tray ??= Create(Math.Max(16, SystemInformation.SmallIconSize.Width));

    static Icon Create(int size)
    {
        size = Math.Clamp(size, 16, 256);
        try
        {
            using var source = LoadSource(size);
            if (source == null) return (Icon)SystemIcons.Application.Clone();

            using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(normalized))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
            }

            Rectangle crop = AlphaBounds(normalized);
            // app.ico 已按各尺寸单独绘制并铺满画布，这里不再额外留白，
            // 否则 16px 托盘图标会比旁边的系统图标小一圈。
            using var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            using (var attributes = new ImageAttributes())
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(normalized,
                    new Rectangle(0, 0, size, size),
                    crop.X, crop.Y, crop.Width, crop.Height,
                    GraphicsUnit.Pixel, attributes);
            }

            IntPtr handle = target.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally { DestroyIcon(handle); }
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    static Bitmap LoadSource(int size)
    {
        string path = Path.Combine(Paths.AppDir, "assets", "app.ico");
        if (File.Exists(path))
        {
            // 取与目标像素一致的帧，16/24/32/48 不会因为从 256 缩放而丢掉细线。
            using var icon = new Icon(path, new Size(size, size));
            return icon.ToBitmap();
        }

        using var executable = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        return executable?.ToBitmap();
    }

    /// <summary>找出素材实际有内容的区域，避免把原图四周的大块透明边一起缩进图标。</summary>
    static Rectangle AlphaBounds(Bitmap bitmap)
    {
        var full = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = null;
        try
        {
            data = bitmap.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bytes[row + x * 4 + 3] <= 8) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX >= minX
                ? Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1)
                : full;
        }
        finally
        {
            if (data != null) bitmap.UnlockBits(data);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}
