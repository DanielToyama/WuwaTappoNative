using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("digest")] public string Digest { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ReleaseEntry
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("installer")] public ReleaseAsset Installer { get; set; }
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("installer")] public ReleaseAsset Installer { get; set; }
    /// <summary>从新到旧的更新日志；当前版本与最新版之间跨多个版本时用于拼接展示。</summary>
    [JsonPropertyName("entries")] public ReleaseEntry[] Entries { get; set; } = Array.Empty<ReleaseEntry>();
}

/// <summary>GitHub Release 检查。只读取公开 Release，不上传任何本机数据。</summary>
public static class UpdateChecker
{
    const string ReleasesApi = "https://api.github.com/repos/DanielToyama/WuwaTappoNative/releases?per_page=30";
    const string ReleasesPage = "https://github.com/DanielToyama/WuwaTappoNative/releases/latest";
    const int MaxEntries = 20;
    static ReleaseInfo _cached;
    static DateTimeOffset _cachedUntil;

    public static async Task<ReleaseInfo> CheckAsync(Version current, bool force = false, string httpProxy = null)
    {
        if (!force && DateTimeOffset.UtcNow < _cachedUntil) return _cached;
        try
        {
            using var http = CreateHttpClient(httpProxy, TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WuwatappoNative/" + current);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(ReleasesApi).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonArray releases)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cached = null;
            }

            var newer = new List<ReleaseEntry>();
            foreach (var node in releases)
            {
                if (node is not JsonObject root) continue;
                if (ReadBool(root["draft"]) || ReadBool(root["prerelease"])) continue;
                string tag = ReadString(root["tag_name"]);
                if (!TryParseVersion(tag, out var version) || version <= current) continue;
                var assets = ReadAssets(root["assets"]);
                newer.Add(new ReleaseEntry
                {
                    Tag = tag,
                    Name = ReadString(root["name"]),
                    Body = ReadString(root["body"]),
                    Url = ReadString(root["html_url"]),
                    PublishedAt = ReadString(root["published_at"]),
                    Installer = SelectInstaller(assets),
                });
            }
            if (newer.Count == 0)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                return _cached = null;
            }

            // GitHub 返回顺序不保证，按版本号明确从新到旧排序。
            newer.Sort((a, b) =>
            {
                TryParseVersion(a.Tag, out var va);
                TryParseVersion(b.Tag, out var vb);
                return vb.CompareTo(va);
            });
            if (newer.Count > MaxEntries) newer.RemoveRange(MaxEntries, newer.Count - MaxEntries);
            var latest = newer[0];
            var info = new ReleaseInfo
            {
                Tag = latest.Tag,
                Name = latest.Name,
                Body = latest.Body,
                Url = string.IsNullOrEmpty(latest.Url) ? ReleasesPage : latest.Url,
                PublishedAt = latest.PublishedAt,
                Installer = latest.Installer,
                Entries = newer.ToArray(),
            };
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
            return _cached = info;
        }
        catch (Exception ex)
        {
            Log.Write("检查更新失败 " + ex.Message);
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            return _cached = null;
        }
    }

    static HttpClient CreateHttpClient(string httpProxy, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(httpProxy) &&
            Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri) &&
            (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    static List<ReleaseAsset> ReadAssets(JsonNode node)
    {
        var assets = new List<ReleaseAsset>();
        if (node is not JsonArray arr) return assets;
        foreach (var item in arr)
        {
            if (item is not JsonObject asset) continue;
            string name = ReadString(asset["name"]);
            string url = ReadString(asset["browser_download_url"]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            assets.Add(new ReleaseAsset
            {
                Name = name,
                Url = url,
                Digest = ReadString(asset["digest"]),
                Size = ReadLong(asset["size"]),
            });
        }
        return assets;
    }

    static ReleaseAsset SelectInstaller(List<ReleaseAsset> assets)
    {
        return assets.FirstOrDefault(a => a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                          a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    static bool TryParseVersion(string tag, out Version version)
    {
        string raw = (tag ?? "").Trim().TrimStart('v', 'V');
        int dash = raw.IndexOf('-');
        if (dash >= 0) raw = raw.Substring(0, dash);
        return Version.TryParse(raw, out version);
    }

    static string ReadString(JsonNode node)
    {
        try { return node?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    static long ReadLong(JsonNode node)
    {
        try { return node?.GetValue<long>() ?? 0; } catch { return 0; }
    }

    static bool ReadBool(JsonNode node)
    {
        try { return node?.GetValue<bool>() ?? false; } catch { return false; }
    }
}
