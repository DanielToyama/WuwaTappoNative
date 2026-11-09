using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace WuwatappoNative.Net;

/// <summary>
/// 只用于隐藏测试模式：限制请求体写出的速率，不影响响应下载。
/// </summary>
sealed class ThrottledContent : HttpContent
{
    readonly HttpContent _inner;
    readonly int _bytesPerSecond;

    public ThrottledContent(HttpContent inner, int bytesPerSecond)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _bytesPerSecond = Math.Max(1, bytesPerSecond);
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
    {
        using var source = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[1024];
        long started = Environment.TickCount64;
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            total += read;
            long targetMs = total * 1000L / _bytesPerSecond;
            long elapsedMs = Environment.TickCount64 - started;
            if (targetMs > elapsedMs)
                await Task.Delay((int)Math.Min(int.MaxValue, targetMs - elapsedMs)).ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is long contentLength)
        {
            length = contentLength;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
