using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace WuwatappoNative.Core;

/// <summary>
/// 主题背景图存储。图片本体放在 %LOCALAPPDATA%\WuwatappoNative\cache\theme，
/// config.json 只保存文件名索引。WebView2 的 postMessage 对单条消息体积有限制，
/// 因此使用分片上传 + 虚拟主机按需读取。
///
/// 超过 4MB 的图片会先在前端 Canvas 压缩；如果仍然走到后端（旧 data URL、直接调用），
/// 这里再用 System.Drawing 兜底压缩成 JPEG，确保缓存目录里没有超大原图。
/// </summary>
public static class ThemeBackgroundStore
{
    /// <summary>允许选择/上传的原始图片上限。</summary>
    public const long MaxSourceBytes = 20 * 1024 * 1024;
    /// <summary>超过该大小自动压缩后落盘。</summary>
    public const long CompressThresholdBytes = 4 * 1024 * 1024;
    const int MaxBase64Chars = 28 * 1024 * 1024;
    const int MaxChunkChars = 1024 * 1024;
    const string VirtualHost = "wuwatappo.theme";
    static readonly object Gate = new();
    static readonly Dictionary<string, UploadSession> Uploads = new(StringComparer.Ordinal);
    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
    };

    sealed class UploadSession
    {
        public string Mime = "";
        public readonly StringBuilder Data = new();
        public DateTime CreatedAt = DateTime.UtcNow;
    }

    public static string Dir => Paths.ThemeDir;

    /// <summary>返回前端可访问的虚拟主机 URL；没有图片或文件缺失时返回空串。</summary>
    public static string DisplayUrl(string stored)
    {
        stored = (stored ?? "").Trim();
        if (stored.Length == 0) return "";
        string name = Path.GetFileName(stored);
        if (name.Length == 0) return "";
        string path = Path.Combine(Dir, name);
        try
        {
            if (!File.Exists(path)) return "";
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            return $"https://{VirtualHost}/{Uri.EscapeDataString(name)}?v={stamp}";
        }
        catch { return ""; }
    }

    public static bool HasImage(string stored)
    {
        stored = (stored ?? "").Trim();
        if (stored.Length == 0) return false;
        if (stored.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string name = Path.GetFileName(stored);
            return name.Length > 0 && File.Exists(Path.Combine(Dir, name));
        }
        catch { return false; }
    }

    /// <summary>把旧版本 config 里的 data URL 迁移成独立图片文件；返回 true 表示配置已改写。</summary>
    public static bool MigrateLegacy(Settings cfg)
    {
        if (cfg == null) return false;
        string stored = (cfg.ThemeBackgroundImage ?? "").Trim();
        if (!stored.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string name = SaveDataUrl(stored);
            if (name.Length == 0) return false;
            cfg.ThemeBackgroundImage = name;
            Log.Write("主题背景图已迁移到文件 " + name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("主题背景图迁移失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>把旧版 %LOCALAPPDATA%\WuwatappoNative\theme 下的图片移动到 cache\theme。</summary>
    public static void MigrateLegacyDirectory()
    {
        try
        {
            string legacy = Paths.LegacyThemeDir;
            if (!Directory.Exists(legacy)) return;
            Directory.CreateDirectory(Dir);
            foreach (var file in Directory.GetFiles(legacy))
            {
                if (!AllowedExtensions.Contains(Path.GetExtension(file))) continue;
                string target = Path.Combine(Dir, Path.GetFileName(file));
                try
                {
                    if (!File.Exists(target))
                    {
                        File.Move(file, target);
                    }
                    else
                    {
                        // 新目录已有同名文件时保留新文件，旧文件仅在确认可删时移除。
                        var oldInfo = new FileInfo(file);
                        var newInfo = new FileInfo(target);
                        if (newInfo.Length > 0 && oldInfo.Length == newInfo.Length) File.Delete(file);
                    }
                }
                catch (Exception ex) { Log.Write("迁移旧主题图片失败 " + file + " " + ex.Message); }
            }
        }
        catch (Exception ex) { Log.Write("迁移旧主题目录失败 " + ex.Message); }
    }

    public static string SaveDataUrl(string dataUrl)
    {
        dataUrl = (dataUrl ?? "").Trim();
        int comma = dataUrl.IndexOf(',');
        if (comma <= 0 || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("背景图数据格式不正确");
        string header = dataUrl.Substring(5, comma - 5);
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("背景图必须使用 base64 data URL");
        string mime = header.Split(';')[0].Trim().ToLowerInvariant();
        string base64 = dataUrl.Substring(comma + 1);
        if (base64.Length > MaxBase64Chars) throw new InvalidDataException("背景图不能超过 20MB");
        byte[] bytes = Convert.FromBase64String(base64);
        return SaveBytes(bytes, mime);
    }

    public static string BeginUpload(string mime)
    {
        mime = NormalizeMime(mime);
        lock (Gate)
        {
            CleanupUploads();
            string id = Guid.NewGuid().ToString("N");
            Uploads[id] = new UploadSession { Mime = mime };
            return id;
        }
    }

    public static long AppendUpload(string uploadId, string chunk)
    {
        chunk = chunk ?? "";
        if (chunk.Length > MaxChunkChars) throw new InvalidDataException("分片过大");
        lock (Gate)
        {
            if (!Uploads.TryGetValue(uploadId ?? "", out var session))
                throw new InvalidDataException("上传会话不存在或已过期");
            if (session.Data.Length + chunk.Length > MaxBase64Chars)
                throw new InvalidDataException("背景图不能超过 20MB");
            session.Data.Append(chunk);
            return session.Data.Length;
        }
    }

    public static string CommitUpload(string uploadId)
    {
        UploadSession session;
        lock (Gate)
        {
            if (!Uploads.TryGetValue(uploadId ?? "", out session))
                throw new InvalidDataException("上传会话不存在或已过期");
            Uploads.Remove(uploadId);
        }
        byte[] bytes = Convert.FromBase64String(session.Data.ToString());
        return SaveBytes(bytes, session.Mime);
    }

    public static void CancelUpload(string uploadId)
    {
        lock (Gate) Uploads.Remove(uploadId ?? "");
    }

    /// <summary>把当前背景图物化成缓存文件。data URL 会被落盘，普通文件名原样校验。</summary>
    public static string Materialize(string stored)
    {
        stored = (stored ?? "").Trim();
        if (stored.Length == 0) return "";
        if (stored.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return SaveDataUrl(stored);
        string name = Path.GetFileName(stored);
        return name.Length > 0 && File.Exists(Path.Combine(Dir, name)) ? name : "";
    }

    /// <summary>预设保存时复制一份独立图片，后续替换当前背景图不会覆盖预设。</summary>
    public static string CopyForPreset(string stored)
    {
        string name = Materialize(stored);
        if (name.Length == 0) return "";
        string source = Path.Combine(Dir, name);
        string ext = Path.GetExtension(name);
        if (!AllowedExtensions.Contains(ext)) ext = ".jpg";
        string copy = UniqueName("preset", ext);
        string target = Path.Combine(Dir, copy);
        Directory.CreateDirectory(Dir);
        string tmp = target + ".tmp";
        File.Copy(source, tmp, true);
        File.Move(tmp, target, true);
        return copy;
    }

    /// <summary>删除缓存中的某个主题图片；只接受文件名，防止路径穿越。</summary>
    public static bool Delete(string stored)
    {
        stored = (stored ?? "").Trim();
        if (stored.Length == 0) return false;
        try
        {
            string name = Path.GetFileName(stored);
            if (name.Length == 0) return false;
            string path = Path.Combine(Dir, name);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("删除主题图片失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>删除缓存目录中不再被 config / 预设引用的图片，并清掉超时的上传临时文件。</summary>
    public static void CleanupOrphans(IEnumerable<string> referenced)
    {
        var keep = new HashSet<string>(
            (referenced ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.GetFileName(x.Trim())),
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.GetFiles(Dir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file);
                    }
                    catch { }
                    continue;
                }
                if (!AllowedExtensions.Contains(Path.GetExtension(file))) continue;
                if (keep.Contains(name)) continue;
                // 只清理本程序生成的文件，避免误删用户手动放进目录的东西。
                if (!name.StartsWith("theme-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("preset-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("background.", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch (Exception ex) { Log.Write("清理主题图片失败 " + name + " " + ex.Message); }
            }
        }
        catch (Exception ex) { Log.Write("清理主题缓存失败 " + ex.Message); }
    }

    static string SaveBytes(byte[] bytes, string mime)
    {
        if (bytes == null || bytes.Length == 0) throw new InvalidDataException("背景图为空");
        if (bytes.Length > MaxSourceBytes) throw new InvalidDataException("背景图不能超过 20MB");
        string ext = ExtensionFor(mime, bytes);
        // 后端兜底压缩：旧 data URL / 第三方调用即使绕过前端也会先压缩再落盘。
        if (bytes.Length > CompressThresholdBytes)
        {
            try
            {
                bytes = CompressToJpeg(bytes);
                ext = ".jpg";
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("背景图超过 4MB 且无法自动压缩：" + ex.Message);
            }
        }
        Directory.CreateDirectory(Dir);
        string name = UniqueName("theme", ext);
        string path = Path.Combine(Dir, name);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        return name;
    }

    static string UniqueName(string prefix, string ext)
        => $"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{ext}";

    /// <summary>使用 System.Drawing 把超大图片压成适合面板显示的 JPEG。</summary>
    static byte[] CompressToJpeg(byte[] source)
    {
        using var input = new MemoryStream(source, writable: false);
        using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
        int maxSide = Math.Max(image.Width, image.Height);
        if (maxSide <= 0) throw new InvalidDataException("图片尺寸无效");
        foreach (int side in new[] { 3840, 2880, 2048, 1600, 1280 })
        {
            float scale = Math.Min(1f, side / (float)maxSide);
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
            using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            canvas.SetResolution(96, 96);
            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(image, 0, 0, width, height);
            }
            foreach (float quality in new[] { 0.86f, 0.76f, 0.66f, 0.55f })
            {
                byte[] encoded = EncodeJpeg(canvas, quality);
                if (encoded.Length <= CompressThresholdBytes) return encoded;
            }
        }
        throw new InvalidDataException("压缩后仍超过 4MB");
    }

    static byte[] EncodeJpeg(Bitmap image, float quality)
    {
        using var output = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x =>
            string.Equals(x.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
        if (encoder == null)
        {
            image.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Round(quality * 100));
        image.Save(output, encoder, parameters);
        return output.ToArray();
    }

    static string NormalizeMime(string mime)
    {
        mime = (mime ?? "").Trim().ToLowerInvariant();
        return mime switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/webp" => "image/webp",
            "image/gif" => "image/gif",
            _ => throw new InvalidDataException("只支持 PNG/JPG/WebP/GIF 背景图"),
        };
    }

    static string ExtensionFor(string mime, byte[] bytes)
    {
        // 以文件头为准修正扩展名，避免 MIME 与实际内容不一致导致虚拟主机返回错误类型。
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return ".gif";
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return ".webp";
        throw new InvalidDataException("无法识别背景图格式");
    }

    static void CleanupUploads()
    {
        if (Uploads.Count == 0) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-20);
        var expired = new List<string>();
        foreach (var pair in Uploads)
            if (pair.Value.CreatedAt < cutoff) expired.Add(pair.Key);
        foreach (var id in expired) Uploads.Remove(id);
    }
}
