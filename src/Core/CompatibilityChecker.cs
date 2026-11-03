using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

/// <summary>项目维护者发布的兼容性状态。客户端只读取静态 JSON，不上传任何数据。</summary>
public sealed class CompatibilityInfo
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("native")] public CompatibilityNative Native { get; set; } = new();
    [JsonPropertyName("official")] public CompatibilityOfficial Official { get; set; } = new();
    [JsonPropertyName("protocol")] public CompatibilityProtocol Protocol { get; set; } = new();
    [JsonPropertyName("killSwitch")] public CompatibilityKillSwitch KillSwitch { get; set; } = new();

    [JsonIgnore] public string SourceUrl { get; set; } = "";
    [JsonIgnore] public bool IsOfflineRequested => KillSwitch != null && KillSwitch.ForceOffline;
    [JsonIgnore] public bool IsUnsafe => string.Equals(Status, "unsafe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "maintenance", StringComparison.OrdinalIgnoreCase)
        || IsOfflineRequested;
}

public sealed class CompatibilityNative
{
    [JsonPropertyName("latest")] public string Latest { get; set; } = "";
    [JsonPropertyName("minSafe")] public string MinSafe { get; set; } = "";
}

public sealed class CompatibilityOfficial
{
    [JsonPropertyName("cn")] public CompatibilityRegion Cn { get; set; } = new();
    [JsonPropertyName("global")] public CompatibilityRegion Global { get; set; } = new();
}

public sealed class CompatibilityRegion
{
    [JsonPropertyName("latest")] public string Latest { get; set; } = "";
    [JsonPropertyName("minOta")] public string MinOta { get; set; } = "";
}

public sealed class CompatibilityProtocol
{
    [JsonPropertyName("testedOfficial")] public string TestedOfficial { get; set; } = "";
    [JsonPropertyName("generation")] public int Generation { get; set; }
    [JsonPropertyName("breaking")] public bool Breaking { get; set; }
}

public sealed class CompatibilityKillSwitch
{
    [JsonPropertyName("forceOffline")] public bool ForceOffline { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>读取 GitHub Pages 上的 compatibility.json；失败时返回上次成功结果或不阻塞。</summary>
public static class CompatibilityChecker
{
    public const string PageUrl = "https://danieltoyama.github.io/WuwaTappoNative/";
    public const string JsonUrl = PageUrl + "compatibility.json";

    static readonly HttpClient Http = CreateClient();
    static readonly object Gate = new();
    static CompatibilityInfo _cached;
    static DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WuwatappoNative/" + (typeof(CompatibilityChecker).Assembly.GetName().Version?.ToString(3) ?? "0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static async Task<CompatibilityInfo> CheckAsync(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != null && DateTimeOffset.UtcNow < _cachedUntil) return _cached;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, JsonUrl + "?v=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<CompatibilityInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (info == null || info.Schema != 1)
                throw new InvalidOperationException("兼容性状态 JSON 版本不支持");
            info.SourceUrl = JsonUrl;
            lock (Gate)
            {
                _cached = info;
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
            }
            return info;
        }
        catch (Exception ex)
        {
            Log.Write("读取版本状态失败：" + ex.Message);
            lock (Gate) _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            lock (Gate) return _cached;
        }
    }
}
