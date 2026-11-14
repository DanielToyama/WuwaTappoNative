using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WuwatappoNative.Net;

namespace WuwatappoNative.Core;

/// <summary>官方客户端版本清单（latest_host.json → latest.json / latest-global.json）。</summary>
public sealed class OfficialVersionInfo
{
    public string Region { get; set; } = "cn";
    public bool Available { get; set; }
    public bool Stale { get; set; }
    public string Version { get; set; } = "";
    public bool ForceManualDownload { get; set; }
    public string MinOtaClientVersion { get; set; } = "";
    public string PubDate { get; set; } = "";
    public string CheckedAt { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>给客户端 UI 使用的官方版本判断结果。</summary>
public sealed class OfficialCheckStatus
{
    /// <summary>verified / unverified / forced / unavailable</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "unavailable";
    [JsonPropertyName("officialVersion")] public string OfficialVersion { get; set; } = "";
    [JsonPropertyName("testedVersion")] public string TestedVersion { get; set; } = "";
    [JsonPropertyName("forceManualDownload")] public bool ForceManualDownload { get; set; }
    [JsonPropertyName("minOtaClientVersion")] public string MinOtaClientVersion { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("checkedAt")] public string CheckedAt { get; set; } = "";
    [JsonPropertyName("stale")] public bool Stale { get; set; }
}

/// <summary>
/// 直接读取官方版本清单，不依赖 GitHub Actions 或 compatibility.json。
/// 官方清单里的 force_manual_download / min_ota_client_version 可以判断强制更新；
/// 和“本地编译基线 / 维护者发布的最新验证基线”比较，才能发现官方更新了但 Native 还没跟上。
/// </summary>
public static class OfficialVersionChecker
{
    /// <summary>发布 Native 时已经实际验证过的官方客户端版本；作为最低兜底基线。</summary>
    public const string TestedOfficialVersion = "1.0.4";

    const string CnHost = "https://mc-wuwatappo.kurogames-global.com";
    const string GlobalHost = "https://wutheringwaves-wuwatappo.kurogames-global.com";

    static readonly HttpClient Http = CreateClient();
    static readonly object Gate = new();
    static readonly Dictionary<string, (OfficialVersionInfo Info, DateTimeOffset Expires)> Cache = new();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WuwatappoNative/" + (typeof(OfficialVersionChecker).Assembly.GetName().Version?.ToString(3) ?? "0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>读取指定区服的官方最新版本；失败时返回上次成功结果或 unavailable。</summary>
    public static async Task<OfficialVersionInfo> FetchAsync(string region, bool force = false)
    {
        region = string.Equals(region, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "cn";
        OfficialVersionInfo cached = null;
        lock (Gate)
        {
            if (Cache.TryGetValue(region, out var hit))
            {
                cached = hit.Info;
                if (!force && DateTimeOffset.UtcNow < hit.Expires) return hit.Info;
            }
        }

        try
        {
            string host = region == "global" ? GlobalHost : CnHost;
            string hostFile = region == "global" ? "latest-global_host.json" : "latest_host.json";
            var hostInfo = await GetJsonAsync($"{host}/app/json-host/{hostFile}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}").ConfigureAwait(false);
            string url = ApiClient.ReadString(hostInfo?["url"]);
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("官方版本清单缺少 url");

            var manifest = await GetJsonAsync(url).ConfigureAwait(false);
            var info = new OfficialVersionInfo
            {
                Region = region,
                Available = true,
                Version = ApiClient.ReadString(manifest?["version"]),
                ForceManualDownload = ApiClient.ReadBool(manifest?["force_manual_download"]),
                MinOtaClientVersion = ApiClient.ReadString(manifest?["min_ota_client_version"]),
                PubDate = ApiClient.ReadString(manifest?["pub_date"]),
                CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
                Error = "",
            };
            if (string.IsNullOrWhiteSpace(info.Version))
                throw new InvalidOperationException("官方版本清单缺少 version");

            lock (Gate) Cache[region] = (info, DateTimeOffset.UtcNow.AddMinutes(30));
            return info;
        }
        catch (Exception ex)
        {
            Log.Write($"读取官方版本失败（{region}）：{ex.Message}");
            if (cached != null)
            {
                cached.Stale = true;
                cached.Error = ex.Message;
                return cached;
            }
            return new OfficialVersionInfo
            {
                Region = region,
                Available = false,
                Stale = false,
                CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// 把官方版本清单转换成客户端可展示的状态。
    /// remoteTestedVersion 来自 compatibility.json 的 protocol.testedOfficial：
    /// 维护者验证新官方版本后，只需更新远端基线即可让旧客户端恢复正常，不必等 Native 再发版；
    /// 远端基线低于本地编译基线时忽略，避免状态倒退。
    /// </summary>
    public static OfficialCheckStatus Evaluate(OfficialVersionInfo info, string remoteTestedVersion = null)
    {
        string testedVersion = TestedOfficialVersion;
        string remote = (remoteTestedVersion ?? "").Trim();
        if (remote.Length > 0 && CompareVersions(remote, testedVersion) > 0)
            testedVersion = remote;

        var status = new OfficialCheckStatus
        {
            TestedVersion = testedVersion,
            OfficialVersion = info?.Version ?? "",
            ForceManualDownload = info?.ForceManualDownload ?? false,
            MinOtaClientVersion = info?.MinOtaClientVersion ?? "",
            CheckedAt = info?.CheckedAt ?? "",
            Stale = info?.Stale ?? false,
        };

        if (info == null || !info.Available)
        {
            status.State = "unavailable";
            status.Message = info?.Stale == true
                ? "暂时无法检查官方版本，当前显示上次检查结果，不影响当前使用。"
                : "暂时无法检查官方版本，不影响当前使用。";
            return status;
        }

        int compare = CompareVersions(info.Version, testedVersion);
        bool forced = info.ForceManualDownload ||
            (!string.IsNullOrWhiteSpace(info.MinOtaClientVersion) &&
             CompareVersions(info.MinOtaClientVersion, testedVersion) > 0);

        if (compare > 0)
        {
            status.State = forced ? "forced" : "unverified";
            status.Message = forced
                ? $"官方客户端已强制更新至 {info.Version}，旧版 Native 的联机功能可能已经失效。建议暂停联机并等待 Native 更新；单机功能仍可继续使用。"
                : $"官方客户端已更新至 {info.Version}，当前 Native 尚未验证该版本。大多数情况下，如果当前功能正常，不必立即更新；如果联机异常，建议暂停联机并等待 Native 更新。";
        }
        else
        {
            status.State = "verified";
            status.Message = $"当前 Native 已验证官方 {info.Version}，可继续使用。";
        }
        return status;
    }

    /// <summary>比较 1.0.4、v1.0.5 这类版本号；无法解析的部分按 0 处理。</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = SplitVersion(left);
        var b = SplitVersion(right);
        int length = Math.Max(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    static int[] SplitVersion(string raw)
    {
        string value = (raw ?? "").Trim().TrimStart('v', 'V');
        int dash = value.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) value = value[..dash];
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }

    static async Task<JsonNode> GetJsonAsync(string url)
    {
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text);
    }
}
