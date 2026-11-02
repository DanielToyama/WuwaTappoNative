using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

public sealed class ReleaseInfo
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public string PublishedAt { get; set; } = "";
}

/// <summary>GitHub 最新 Release 检查。只读取公开 Release，不上传任何本机数据。</summary>
public static class UpdateChecker
{
    const string LatestApi = "https://api.github.com/repos/DanielToyama/WuwaTappoNative/releases/latest";
    const string ReleasesPage = "https://github.com/DanielToyama/WuwaTappoNative/releases/latest";
    static ReleaseInfo _cached;
    static DateTimeOffset _cachedUntil;

    public static async Task<ReleaseInfo> CheckAsync(Version current, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow < _cachedUntil) return _cached;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WuwatappoNative/" + current);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestApi).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cached = null;
            }
            if (ReadBool(root["draft"]) || ReadBool(root["prerelease"])) return null;

            string tag = ReadString(root["tag_name"]);
            if (!IsNewer(tag, current))
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                return _cached = null;
            }
            var info = new ReleaseInfo
            {
                Tag = tag,
                Name = ReadString(root["name"]),
                Body = ReadString(root["body"]),
                Url = ReadString(root["html_url"]) is { Length: > 0 } url ? url : ReleasesPage,
                PublishedAt = ReadString(root["published_at"]),
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

    static bool IsNewer(string tag, Version current)
    {
        string raw = (tag ?? "").Trim().TrimStart('v', 'V');
        int dash = raw.IndexOf('-');
        if (dash >= 0) raw = raw.Substring(0, dash);
        return Version.TryParse(raw, out var latest) && latest > current;
    }

    static string ReadString(JsonNode node)
    {
        try { return node?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    static bool ReadBool(JsonNode node)
    {
        try { return node?.GetValue<bool>() ?? false; } catch { return false; }
    }
}