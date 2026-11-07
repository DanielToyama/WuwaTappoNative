using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

public sealed class AnnouncementItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("expiresAt")] public string ExpiresAt { get; set; } = "";
    [JsonPropertyName("minNativeVersion")] public string MinNativeVersion { get; set; } = "";
    [JsonPropertyName("maxNativeVersion")] public string MaxNativeVersion { get; set; } = "";
    [JsonPropertyName("platforms")] public string[] Platforms { get; set; }
    [JsonPropertyName("dismissible")] public bool Dismissible { get; set; } = true;
    [JsonPropertyName("action")] public AnnouncementAction Action { get; set; }
}

public sealed class AnnouncementAction
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class AnnouncementPayload
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("announcements")] public List<AnnouncementItem> Announcements { get; set; } = new();
}

/// <summary>
/// 只读公告接口。请求不带 token/Cookie/用户信息/查询参数；阅读状态只保存在本地 state.json。
/// 公告失败只写日志，不影响登录、版本提示或任何单机功能。
/// </summary>
public sealed class AnnouncementService
{
    public const string DefaultApiUrl = "https://p.danieluu.top:39000/tappo/api/announcements";
    const int MaxReadIds = 200;

    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly Settings _cfg;
    readonly string _currentVersion;
    readonly object _gate = new();
    List<AnnouncementItem> _visible = new();
    string _etag = "";
    int _busy;

    public AnnouncementService(Settings cfg)
    {
        _cfg = cfg;
        _currentVersion = typeof(AnnouncementService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public IReadOnlyList<AnnouncementItem> Visible
    {
        get
        {
            lock (_gate) return _visible.ToArray();
        }
    }

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // 不附带 Native 版本或设备信息；只声明客户端类型。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WuwatappoNative");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>拉取并过滤公告。304 或失败时保留当前队列。</summary>
    public async Task<bool> RefreshAsync(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            string etag;
            lock (_gate) etag = _etag;

            string url = string.IsNullOrWhiteSpace(_cfg.AnnouncementApiUrl)
                ? DefaultApiUrl
                : _cfg.AnnouncementApiUrl.Trim();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (!force && !string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotModified) return false;
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<AnnouncementPayload>(json, JsonOptions);
            if (payload == null || payload.Schema != 1)
                throw new InvalidOperationException("公告 schema 不支持");

            string newEtag = resp.Headers.ETag?.ToString() ?? "";
            var visible = Filter(payload.Announcements);
            lock (_gate)
            {
                _etag = newEtag;
                _visible = visible;
            }
            Log.Write($"公告已更新：{visible.Count} 条待显示");
            return true;
        }
        catch (Exception ex)
        {
            // 公告是可选信息：网络错误、401、5xx 都静默保留旧队列。
            Log.Write("读取公告失败：" + ex.Message);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>用户关闭/确认/点击操作后标记已读，返回剩余未读公告。</summary>
    public IReadOnlyList<AnnouncementItem> MarkRead(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Visible;
        lock (_gate)
        {
            var item = _visible.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (item == null) return _visible.ToArray();

            _cfg.ReadAnnouncementIds ??= new List<string>();
            if (!_cfg.ReadAnnouncementIds.Contains(id))
            {
                _cfg.ReadAnnouncementIds.Add(id);
                if (_cfg.ReadAnnouncementIds.Count > MaxReadIds)
                    _cfg.ReadAnnouncementIds.RemoveRange(0, _cfg.ReadAnnouncementIds.Count - MaxReadIds);
                StateStore.Save(_cfg);
            }
            _visible.RemoveAll(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            return _visible.ToArray();
        }
    }

    List<AnnouncementItem> Filter(List<AnnouncementItem> items)
    {
        var result = new List<AnnouncementItem>();
        if (items == null) return result;
        var now = DateTimeOffset.UtcNow;
        HashSet<string> readIds;
        lock (_gate)
            readIds = new HashSet<string>(_cfg.ReadAnnouncementIds ?? new List<string>(), StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item == null) continue;
            item.Id = (item.Id ?? "").Trim();
            if (!IsValidId(item.Id)) continue;
            item.Title = Clean(item.Title, 200);
            if (string.IsNullOrWhiteSpace(item.Title)) continue;
            item.Body = Clean(item.Body, 4000).Replace("\r\n", "\n").Replace('\r', '\n');
            if (readIds.Contains(item.Id)) continue;
            if (!PlatformApplies(item.Platforms)) continue;
            if (!VersionApplies(item.MinNativeVersion, item.MaxNativeVersion)) continue;
            if (!TimeApplies(item.PublishedAt, item.ExpiresAt, now)) continue;

            item.Severity = NormalizeSeverity(item.Severity);
            item.Action = NormalizeAction(item.Action);
            result.Add(item);
        }
        return result;
    }

    bool VersionApplies(string min, string max)
    {
        if (!string.IsNullOrWhiteSpace(min) && TryVersion(min, out var minVersion) &&
            TryVersion(_currentVersion, out var currentMin) && currentMin < minVersion)
            return false;
        if (!string.IsNullOrWhiteSpace(max) && TryVersion(max, out var maxVersion) &&
            TryVersion(_currentVersion, out var currentMax) && currentMax > maxVersion)
            return false;
        return true;
    }

    static bool TimeApplies(string publishedAt, string expiresAt, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(publishedAt) && TryDate(publishedAt, out var published) && published > now)
            return false;
        if (!string.IsNullOrWhiteSpace(expiresAt) && TryDate(expiresAt, out var expires) && expires <= now)
            return false;
        return true;
    }

    static bool TryDate(string value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }

    static bool PlatformApplies(string[] platforms)
    {
        if (platforms == null || platforms.Length == 0) return true;
        return platforms.Any(p => string.Equals(p, "windows", StringComparison.OrdinalIgnoreCase));
    }

    static bool TryVersion(string text, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text[..cut];
        var pieces = text.Split('.');
        if (pieces.Length < 2 || pieces.Length > 4) return false;
        var nums = new int[4];
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;
        }
        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != ':' && c != '-')
                return false;
        }
        return true;
    }

    static string NormalizeSeverity(string severity)
    {
        return (severity ?? "").Trim().ToLowerInvariant() switch
        {
            "warning" => "warning",
            "critical" => "critical",
            _ => "info",
        };
    }

    static AnnouncementAction NormalizeAction(AnnouncementAction action)
    {
        if (action == null) return null;
        string label = Clean(action.Label, 40);
        string url = (action.Url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return new AnnouncementAction { Label = label, Url = url };
    }

    static string Clean(string value, int max)
    {
        value = (value ?? "").Trim();
        return value.Length <= max ? value : value[..max];
    }
}
