using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

public sealed class KuroSdkUser
{
    public string Cuid { get; set; } = "";
    public string SdkOpenId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string Phone { get; set; } = "";
}

/// <summary>
/// 库洛通行证 H5 SDK 的手机号验证码通道。仅使用公开 H5 SDK 的登录接口，
/// 不加载 SDK 的登录界面；界面由本项目提供。
/// </summary>
public static class KuroSdkClient
{
    const string SdkJsUrl = "https://pro-cdn-sdk.kurogame.com/pro/web-sdk/1.x/kr-sdk.js";
    const string ProjectId = "G152";
    const string ProductId = "A1476";
    // clientId / clientSecret 是官方 App 传给 SDK 的宿主配置，不在 kr-sdk.js 里；
    // 这里保留官方 1.0.4 的值作为兜底，SDK 版本和 API 地址会从公开 JS 动态刷新。
    const string ClientId = "vvkewnskrxxwfo0yi61cy24l";
    const string ClientSecret = "g9ej0i1jf3y68wchb0ncm266";

    static volatile string BaseUrl = "https://sdkapi.kurogame.com/sdkcom";
    static volatile string SdkVersion = "1.2.3w";
    static volatile string KrVersion = "1.9.0";
    static long _configValidUntilTicks;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>从公开 SDK JS 刷新 SDK 版本、API 地址和 Kr-Ver；失败时继续使用内置值。</summary>
    static async Task EnsureConfigAsync()
    {
        if (DateTimeOffset.UtcNow.UtcTicks < System.Threading.Interlocked.Read(ref _configValidUntilTicks)) return;
        string text = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SdkJsUrl);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write("读取 Kuro SDK 配置失败，使用内置配置：" + ex.Message);
        }

        if (!string.IsNullOrEmpty(text))
        {
            var version = Regex.Match(text, "const\\s+version\\s*=\\s*\"([^\"]+)\"");
            var api = Regex.Match(text, "VITE_APP_API_URL\\s*:\\s*\"([^\"]+)\"");
            var kr = Regex.Match(text, "\"Kr-Ver\"\\s*:\\s*\"([^\"]+)\"");
            if (version.Success) SdkVersion = version.Groups[1].Value;
            if (api.Success) BaseUrl = api.Groups[1].Value.TrimEnd('/');
            if (kr.Success) KrVersion = kr.Groups[1].Value;
            Log.Write($"Kuro SDK 配置：version={SdkVersion} kr={KrVersion} base={BaseUrl}");
        }

        long next = DateTimeOffset.UtcNow.AddHours(6).UtcTicks;
        if (string.IsNullOrEmpty(text)) next = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
        System.Threading.Interlocked.Exchange(ref _configValidUntilTicks, next);
    }

    public static async Task SendPhoneCodeAsync(string phone, string deviceNum)
    {
        await EnsureConfigAsync().ConfigureAwait(false);
        await PostFormAsync("/v2/login/getPhoneCode.lg", new Dictionary<string, string>
        {
            ["phone"] = phone,
        }, deviceNum).ConfigureAwait(false);
    }

    public static async Task<KuroSdkUser> PhoneLoginAsync(string phone, string code, string deviceNum)
    {
        await EnsureConfigAsync().ConfigureAwait(false);
        var phoneData = await PostFormAsync("/v2/login/phoneCode.lg", new Dictionary<string, string>
        {
            ["phone"] = phone,
            ["code"] = code,
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            // 与官方 H5 SDK 的浏览器环境字段保持一致。
            ["deviceModel"] = "Chrome",
            ["os"] = "Windows",
        }, deviceNum).ConfigureAwait(false);

        string authCode = ApiClient.ReadString(phoneData["code"]);
        if (string.IsNullOrEmpty(authCode))
            throw new ApiException(-1, "手机号验证成功，但未返回登录凭据");

        string cuid = ApiClient.ReadString(phoneData["cuid"]);
        // 1.0.4 的 sdkuserid 才是游戏侧认可的通行证用户 id；
        // id 只是 SDK 内部用户 id，拿它去 api/auth/login 会返回 10001。
        string sdkOpenId = ApiClient.ReadString(phoneData["sdkuserid"]);
        if (string.IsNullOrEmpty(sdkOpenId)) sdkOpenId = ApiClient.ReadString(phoneData["id"]);
        string userName = ApiClient.ReadString(phoneData["username"]);
        string phoneValue = ApiClient.ReadString(phoneData["phone"]);

        var tokenData = await PostFormAsync("/v2/auth/getToken.lg", new Dictionary<string, string>
        {
            ["code"] = authCode,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "authorization_code",
        }, deviceNum).ConfigureAwait(false);

        string accessToken = ApiClient.ReadString(tokenData["access_token"]);
        if (string.IsNullOrEmpty(accessToken))
            throw new ApiException(-1, "手机号登录成功，但未返回 access_token");

        if (string.IsNullOrEmpty(cuid))
            cuid = ApiClient.ReadString(tokenData["cuid"]);
        if (string.IsNullOrEmpty(sdkOpenId))
            sdkOpenId = cuid;

        return new KuroSdkUser
        {
            Cuid = cuid,
            SdkOpenId = sdkOpenId,
            UserName = userName,
            AccessToken = accessToken,
            Phone = phoneValue,
        };
    }

    static async Task<JsonObject> PostFormAsync(string path, Dictionary<string, string> extra, string deviceNum)
    {
        var form = new Dictionary<string, string>
        {
            ["redirect_uri"] = "1",
            ["__e__"] = "1",
            ["pack_mark"] = "1",
            ["projectId"] = ProjectId,
            ["productId"] = ProductId,
            ["platform"] = "h5",
            ["channelId"] = "201",
            ["deviceNum"] = string.IsNullOrWhiteSpace(deviceNum) ? Guid.NewGuid().ToString("N") : deviceNum,
            ["version"] = SdkVersion,
            ["sdkVersion"] = SdkVersion,
        };
        foreach (var pair in extra)
        {
            if (!string.IsNullOrEmpty(pair.Value)) form[pair.Key] = pair.Value;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation("Kr-Ver", KrVersion);
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-Hans,zh-CN,zh-Hans-CN,en-US");
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException((int)resp.StatusCode, $"手机号登录服务 HTTP {resp.StatusCode}");

        JsonNode root;
        try { root = JsonNode.Parse(text); }
        catch { throw new ApiException(-1, "手机号登录服务响应格式异常"); }
        int code = ApiClient.ReadInt(root?["code"]);
        string message = ApiClient.ReadString(root?["msg"]);
        if (code != 0)
        {
            if (code == 41000)
                throw new ApiException(41000, "手机号登录需要人机验证，请稍后重试或使用其他登录方式");
            throw new ApiException(code, string.IsNullOrEmpty(message) ? "手机号登录失败" : message);
        }
        return root?["data"] as JsonObject ?? new JsonObject();
    }
}
