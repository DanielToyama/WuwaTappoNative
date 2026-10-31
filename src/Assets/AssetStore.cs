using System;
using System.Collections.Concurrent;
using System.Drawing;
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

    /// <summary>同步拿内存/磁盘图片；没有则返回 null 并在后台下载，完成回调 onReady。</summary>
    public Image Get(string url, Action onReady = null)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_memory.TryGetValue(url, out var img)) return img;
        string path = CachePathOf(url);
        if (File.Exists(path) && Load(path) is Image local)
        {
            _memory[url] = local;
            return local;
        }
        Prefetch(url, onReady);
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
            try
            {
                string path = CachePathOf(url);
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Paths.ImageCacheDir);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, path, true);
                _pathOf[url] = path;
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

    public void ClearMemory() => _memory.Clear();

    public void Dispose()
    {
        _http.Dispose();
        foreach (var img in _memory.Values) { try { img.Dispose(); } catch { } }
        _memory.Clear();
    }
}
