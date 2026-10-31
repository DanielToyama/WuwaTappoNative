using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

public sealed class ApiException : Exception
{
    public int Code { get; }
    public string RequestId { get; }
    public ApiException(int code, string message, string requestId = null) : base(message ?? "请求失败")
    {
        Code = code;
        RequestId = requestId;
    }
}

/// <summary>
/// 官方 REST 封装。信封 {code,message,request_id,data}；code 0/200 为成功。
/// 请求头固定带 X-BLM-CID（本机持久 UUID）与每请求随机的 X-BLM-FRID。
/// </summary>
public sealed class ApiClient : IDisposable
{
    public const string BaseUrl = "https://api-mc-wuwatappo.kurogames-global.com";
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    readonly HttpClient _http;
    readonly CookieContainer _cookies = new();
    string _token = "";
    public string Token => _token;
    public string LastCid { get; private set; } = "";
    public event Action<string> TokenRefreshed;

    public ApiClient(string cid)
    {
        LastCid = string.IsNullOrWhiteSpace(cid) ? Guid.NewGuid().ToString("N") : cid;
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
    }

    public void SetToken(string token) => _token = token ?? "";
    public string Cid => LastCid;

    public Task<JsonNode> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);
    public Task<JsonNode> PostAsync(string path, object body = null) => SendAsync(HttpMethod.Post, path, body);
    public Task<JsonNode> PutAsync(string path, object body = null) => SendAsync(HttpMethod.Put, path, body);
    public Task<JsonNode> DeleteAsync(string path, object body = null) => SendAsync(HttpMethod.Delete, path, body);

    async Task<JsonNode> SendAsync(HttpMethod method, string path, object body)
    {
        using var req = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : path.TrimStart('/'));
        req.Headers.TryAddWithoutValidation("X-BLM-CID", LastCid);
        req.Headers.TryAddWithoutValidation("X-BLM-FRID", Guid.NewGuid().ToString("N"));
        req.Headers.TryAddWithoutValidation("Origin", "https://tauri.localhost");
        req.Headers.TryAddWithoutValidation("Referer", "https://tauri.localhost/");
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)resp.StatusCode == 601)
            throw new ApiException(601, "当前服务正在维护中，请稍后再试。", null);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException((int)resp.StatusCode, $"HTTP {resp.StatusCode}", null);

        JsonNode root;
        try { root = JsonNode.Parse(text); }
        catch { throw new ApiException(-1, "响应格式异常", null); }
        if (root is not JsonObject obj)
            throw new ApiException(-1, "响应格式异常", null);

        int code = ReadInt(obj["code"]);
        string message = obj["message"]?.GetValue<string>();
        string requestId = obj["request_id"]?.GetValue<string>();
        if (code != 0 && code != 200)
            throw new ApiException(code, DescribeCode(code, message), requestId);

        ReadTokenCookie();
        return obj["data"];
    }

    /// <summary>把常见的英文业务错误换成用户能看懂的中文，保留原始信息便于排查。</summary>
    static string DescribeCode(int code, string message)
    {
        string m = message ?? "";
        string zh = code switch
        {
            10001 => "参数无效（请确认 UID 与验证码都是数字）",
            10002 or 10003 => "登录已失效，请重新登录",
            10023 => "手机号登录通道暂不可用，请改用游戏 UID 登录",
            10025 => "查询不到该 UID 对应的游戏角色",
            10031 => "验证码发送过于频繁，请稍后再试",
            10032 => "验证码发送失败，请稍后再试，或改用游戏 UID 登录",
            10035 => "验证码不正确或已过期",
            601 => "当前服务正在维护中，请稍后再试",
            70005 => "可用点击数不足",
            80003 => "仍在其他房间中，请先退出",
            90002 => "账号已在其他设备登录",
            _ => null
        };
        if (zh == null) return string.IsNullOrEmpty(m) ? $"请求失败（{code}）" : m;
        return string.IsNullOrEmpty(m) || m == zh ? zh : $"{zh}（{m}）";
    }

    void ReadTokenCookie()
    {
        try
        {
            foreach (Cookie c in _cookies.GetCookies(new Uri(BaseUrl)))
            {
                if (string.Equals(c.Name, "infoToken", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, "kuro_infoToken", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(c.Value) && c.Value != _token)
                    {
                        _token = c.Value;
                        TokenRefreshed?.Invoke(_token);
                    }
                }
            }
        }
        catch { }
    }

    public static int ReadInt(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<int>(out var i): return i;
                case JsonValue v when v.TryGetValue<long>(out var l): return (int)l;
                case JsonValue v when v.TryGetValue<string>(out var s): return int.TryParse(s, out var p) ? p : 0;
            }
        }
        catch { }
        return 0;
    }

    public static long ReadLong(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<long>(out var l): return l;
                case JsonValue v when v.TryGetValue<double>(out var d): return (long)d;
                case JsonValue v when v.TryGetValue<string>(out var s): return long.TryParse(s, out var p) ? p : 0;
            }
        }
        catch { }
        return 0;
    }

    public static string ReadString(JsonNode node)
    {
        if (node == null) return "";
        try { return node.GetValue<string>() ?? ""; } catch { return node.ToJsonString().Trim('"'); }
    }

    public static bool ReadBool(JsonNode node)
    {
        if (node == null) return false;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l != 0;
                if (v.TryGetValue<string>(out var s)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
