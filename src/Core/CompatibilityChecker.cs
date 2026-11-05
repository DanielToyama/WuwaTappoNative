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
    /// <summary>由客户端直接读取官方清单后生成的判断，不来自 compatibility.json。</summary>
    [JsonPropertyName("officialCheck")] public OfficialCheckStatus OfficialCheck { get; set; }

    [JsonIgnore] public string SourceUrl { get; set; } = "";
    [JsonIgnore] public bool IsOfflineRequested => KillSwitch != null && KillSwitch.ForceOffline;
    [JsonIgnore] public bool IsUnsafe => string.Equals(Status, "unsafe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "maintenance", StringComparison.OrdinalIgnoreCase)
        || IsOfflineRequested;
}

public sealed class CompatibilityNative
{
    /// <summary>最新发布版本；每次发版更新。</summary>
    [JsonPropertyName("latest")] public string Latest { get; set; } = "";
    /// <summary>仍兼容当前官方协议的最低版本；只在兼容基线变化时提升，不随普通修复版更新。</summary>
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
    /// <summary>没有单独维护该区服清单时，是否沿用国服的判断。</summary>
    [JsonPropertyName("inheritCn")] public bool InheritCn { get; set; }
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
    public const string PageUrl = "https://code.danieluu.top/WuwaTappoNative/";
    public const string JsonUrl = PageUrl + "compatibility.json";

    static readonly HttpClient Http = CreateClient();
    static readonly object Gate = new();
    static CompatibilityInfo _cached;
    static DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
