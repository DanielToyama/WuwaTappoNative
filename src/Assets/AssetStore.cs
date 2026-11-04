using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Assets;

/// <summary>图片下载与磁盘缓存。缓存名 = URL 去掉查询串后的 SHA1，避免 ?t= 造成每次启动重下。</summary>
public sealed class AssetStore : IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly ConcurrentDictionary<string, Image> _memory = new();
    readonly ConcurrentDictionary<string, Image> _bubbleMemory = new();
    readonly ConcurrentQueue<string> _bubbleOrder = new();
    const int MaxBubbleImages = 160;
    readonly ConcurrentDictionary<string, byte> _inflight = new();
    readonly ConcurrentDictionary<string, string> _pathOf = new();
    // 同一个 URL 在下载期间可能被多个窗口请求；回调必须全部保留，否则后建窗口永远收不到通知。
    readonly ConcurrentDictionary<string, List<Action>> _waiters = new();

    public string CachePathOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string clean = url.Split('?')[0];
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(clean))).ToLowerInvariant();
        return Path.Combine(Paths.ImageCacheDir, hash + ".png");
    }

    /// <summary>只查内存/磁盘缓存，绝不触发下载。下载完成回调里必须用它，避免失败后再次 Prefetch 造成死循环。</summary>
    public Image GetCached(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_memory.TryGetValue(url, out var img)) return img;
        string path = CachePathOf(url);
        if (File.Exists(path) && Load(path) is Image local)
        {
            _memory[url] = local;
            return local;
        }
        return null;
    }

    /// <summary>同步拿内存/磁盘图片；没有则返回 null 并在后台下载，完成回调 onReady。
    /// onStarted 只在实际发起后台下载时同步调用，用于统计“正在加载”的帧数量。</summary>
    public Image Get(string url, Action onReady = null, Action onStarted = null)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (GetCached(url) is Image cached) return cached;
        onStarted?.Invoke();
        Prefetch(url, onReady);
        return null;
    }

    /// <summary>
    /// 表情气泡专用：不解码并缓存 560px 原图，而是按显示尺寸生成缩略图。
    /// 高频发表情时避免 504 张表情原图长期占用数百 MB 内存。
    /// </summary>
    public Image GetBubbleImage(string url, int maxSize = 192, Action onReady = null, bool allowDownload = true)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string key = url + "|" + maxSize;
        if (_bubbleMemory.TryGetValue(key, out var cached)) return cached;

        string path = CachePathOf(url);
        if (File.Exists(path))
        {
            var scaled = LoadScaled(path, maxSize);
            if (scaled != null)
            {
                if (_bubbleMemory.TryAdd(key, scaled))
                {
                    _bubbleOrder.Enqueue(key);
                    while (_bubbleOrder.Count > MaxBubbleImages && _bubbleOrder.TryDequeue(out var oldKey))
                        _bubbleMemory.TryRemove(oldKey, out _);
                    return scaled;
                }
                scaled.Dispose();
                return _bubbleMemory.TryGetValue(key, out var existing) ? existing : null;
            }
        }

        if (allowDownload) Prefetch(url, onReady);
        return null;
    }

    public void Prefetch(string url, Action onReady = null)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (onReady != null)
        {
            var list = _waiters.GetOrAdd(url, _ => new List<Action>());
            lock (list) list.Add(onReady);
        }
        if (!_inflight.TryAdd(url, 0)) return;
        _ = Task.Run(async () =>
        {
            Exception last = null;
            try
            {
                string path = CachePathOf(url);
                Directory.CreateDirectory(Paths.ImageCacheDir);
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                        var tmp = path + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                        File.Move(tmp, path, true);
                        _pathOf[url] = path;
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (attempt < 3)
                        {
                            Log.Write($"图片下载重试 {attempt}/3 {url} {ex.Message}");
                            await Task.Delay(400 * attempt).ConfigureAwait(false);
                        }
                    }
                }
                if (last != null) Log.Write("图片下载失败（已重试 3 次） " + url + " " + last.Message);
            }
            catch (Exception ex) { Log.Write("图片下载失败 " + url + " " + ex.Message); }
            finally { _inflight.TryRemove(url, out _); }

            // 下载期间可能有多个窗口在等这张图，必须逐个通知。
            if (_waiters.TryRemove(url, out var pending))
            {
                Action[] callbacks;
                lock (pending) callbacks = pending.ToArray();
                foreach (var cb in callbacks)
                {
                    try { cb(); } catch (Exception ex) { Log.Write("图片回调失败 " + ex.Message); }
                }
            }
        });
    }

    public Image LoadLocal(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        string key = "file:" + path;
        if (_memory.TryGetValue(key, out var img)) return img;
        img = Load(path);
        if (img != null) _memory[key] = img;
        return img;
    }

    static Image Load(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            // 复制到独立位图，避免锁定文件
            using var src = Image.FromStream(ms);
            return new Bitmap(src);
        }
        catch (Exception ex)
        {
            Log.Write("图片解码失败 " + path + " " + ex.Message);
            return null;
        }
    }

    static Image LoadScaled(string path, int maxSize)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            int width = src.Width, height = src.Height;
            double scale = Math.Min(1.0, (double)maxSize / Math.Max(width, height));
            int tw = Math.Max(1, (int)Math.Round(width * scale));
            int th = Math.Max(1, (int)Math.Round(height * scale));
            var bmp = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, tw, th));
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Write("表情缩略图解码失败 " + path + " " + ex.Message);
            return null;
        }
    }

    public void ClearMemory()
    {
        _memory.Clear();
        _bubbleMemory.Clear();
        while (_bubbleOrder.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var img in _memory.Values) { try { img.Dispose(); } catch { } }
        foreach (var img in _bubbleMemory.Values) { try { img.Dispose(); } catch { } }
        _memory.Clear();
        _bubbleMemory.Clear();
        while (_bubbleOrder.TryDequeue(out _)) { }
    }
}
