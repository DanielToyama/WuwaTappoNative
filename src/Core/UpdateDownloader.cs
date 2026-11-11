using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

public sealed class UpdateDownloadProgress
{
    public string Phase { get; init; } = "";
    public string Source { get; init; } = "";
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public string Message { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed class UpdateDownloadResult
{
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Bytes { get; init; }
}

public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string message) : base(message) { }
}

/// <summary>
/// Release 安装包下载。自动模式按 GH-Proxy -> GitHub 的顺序尝试，
/// 只在请求失败时回退，不按速度中断，也不做下载前测速。
/// 所有源都必须通过 GitHub Release asset digest 的 SHA-256 校验后才能交给安装器。
/// </summary>
public static class UpdateDownloader
{
    const string GhProxyPrefix = "https://gh-proxy.com/";
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    sealed class Attempt
    {
        public string Label;
        public string Url;
    }

    public static async Task<UpdateDownloadResult> DownloadAsync(
        ReleaseAsset asset,
        string sourceMode,
        string httpProxy,
        CancellationToken cancellationToken,
        Action<UpdateDownloadProgress> report)
    {
        if (asset == null || string.IsNullOrWhiteSpace(asset.Url))
            throw new UpdateDownloadException("当前发布没有可用的安装包");
        string expected = NormalizeDigest(asset.Digest);
        if (string.IsNullOrEmpty(expected))
            throw new UpdateDownloadException("发布资产缺少 SHA-256，已停止自动下载，请手动前往 Release 下载");

        string fileName = SafeFileName(asset.Name, asset.Url);
        Directory.CreateDirectory(Paths.UpdatesDir);
        string finalPath = Path.Combine(Paths.UpdatesDir, fileName);
        if (File.Exists(finalPath) && VerifyFile(finalPath, expected, out _))
        {
            long size = new FileInfo(finalPath).Length;
            report?.Invoke(new UpdateDownloadProgress
            {
                Phase = "done",
                Source = "已缓存",
                Downloaded = size,
                Total = size,
                Message = "安装包已下载并通过 SHA-256 校验",
                Path = finalPath,
            });
            return new UpdateDownloadResult { Path = finalPath, Source = "已缓存", Sha256 = expected, Bytes = size };
        }

        var attempts = BuildSources(asset.Url, sourceMode);

        string partPath = finalPath + ".part";
        Exception lastError = null;
        for (int i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(partPath);
            try
            {
                return await DownloadOnceAsync(asset, attempt, expected, partPath, finalPath, httpProxy, cancellationToken, report)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(partPath);
                if (i < attempts.Count - 1)
                {
                    report?.Invoke(new UpdateDownloadProgress
                    {
                        Phase = "fallback",
                        Source = attempt.Label,
                        Message = $"{attempt.Label} 下载失败，正在尝试下一优先级下载源…",
                    });
                }
            }
        }
        throw new UpdateDownloadException(lastError?.Message ?? "更新下载失败");
    }

    static List<Attempt> BuildSources(string githubUrl, string sourceMode)
    {
        string mode = (sourceMode ?? "").Trim().ToLowerInvariant();
        var list = new List<Attempt>();
        if (mode == "github")
        {
            list.Add(new Attempt { Label = "GitHub", Url = githubUrl });
            return list;
        }
        if (mode == "ghproxy")
        {
            list.Add(new Attempt { Label = "GH-Proxy", Url = GhProxyPrefix + githubUrl });
            return list;
        }
        // 自动模式固定顺序：先 GH-Proxy，再 GitHub 官方。
        list.Add(new Attempt { Label = "GH-Proxy", Url = GhProxyPrefix + githubUrl });
        list.Add(new Attempt { Label = "GitHub", Url = githubUrl });
        return list;
    }

    static async Task<UpdateDownloadResult> DownloadOnceAsync(
        ReleaseAsset asset,
        Attempt attempt,
        string expectedHash,
        string partPath,
        string finalPath,
        string httpProxy,
        CancellationToken cancellationToken,
        Action<UpdateDownloadProgress> report)
    {
        using var http = CreateHttpClient(httpProxy, TimeSpan.FromMinutes(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, attempt.Url);
        request.Headers.UserAgent.ParseAdd("WuwatappoNative-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateDownloadException($"{attempt.Label} HTTP {(int)response.StatusCode}");

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        report?.Invoke(new UpdateDownloadProgress
        {
            Phase = "downloading",
            Source = attempt.Label,
            Downloaded = 0,
            Total = total,
            Message = $"正在从 {attempt.Label} 下载…",
        });

        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long downloaded = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport >= ProgressInterval)
            {
                lastReport = elapsed;
                double speed = downloaded / Math.Max(0.001, elapsed.TotalSeconds);
                report?.Invoke(new UpdateDownloadProgress
                {
                    Phase = "downloading",
                    Source = attempt.Label,
                    Downloaded = downloaded,
                    Total = total,
                    SpeedBytesPerSecond = speed,
                    Message = $"正在从 {attempt.Label} 下载…",
                });
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Close();
        report?.Invoke(new UpdateDownloadProgress
        {
            Phase = "verifying",
            Source = attempt.Label,
            Downloaded = downloaded,
            Total = total,
            Message = "正在校验 SHA-256…",
        });

        if (!VerifyFile(partPath, expectedHash, out string actual))
        {
            TryDelete(partPath);
            throw new UpdateDownloadException($"SHA-256 校验失败（实际 {actual}）");
        }

        File.Move(partPath, finalPath, true);
        report?.Invoke(new UpdateDownloadProgress
        {
            Phase = "done",
            Source = attempt.Label,
            Downloaded = downloaded,
            Total = total,
            Message = "安装包下载完成，已通过 SHA-256 校验",
            Path = finalPath,
        });
        return new UpdateDownloadResult
        {
            Path = finalPath,
            Source = attempt.Label,
            Sha256 = expectedHash,
            Bytes = downloaded,
        };
    }

    static HttpClient CreateHttpClient(string httpProxy, TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(httpProxy) &&
            Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri) &&
            (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }
        else if (!string.IsNullOrWhiteSpace(httpProxy))
        {
            Log.Write("更新代理地址无效，使用系统代理：" + httpProxy);
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static string NormalizeDigest(string digest)
    {
        string value = (digest ?? "").Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("sha256:".Length);
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : "";
    }

    public static bool VerifyFile(string path, string digest, out string actual)
    {
        actual = "";
        try
        {
            string expected = NormalizeDigest(digest);
            if (string.IsNullOrEmpty(expected) || !File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static string SafeFileName(string name, string url)
    {
        string file = Path.GetFileName((name ?? "").Trim());
        if (string.IsNullOrWhiteSpace(file))
        {
            try { file = Path.GetFileName(new Uri(url).LocalPath); } catch { }
        }
        file = Path.GetFileName((file ?? "").Trim());
        if (string.IsNullOrWhiteSpace(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateDownloadException("发布资产文件名无效，已停止自动下载");
        return file;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
