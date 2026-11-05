using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

/// <summary>登录编排：UID 验证码、国服手机号通行证、token 存档、48 小时内自动续期、user/me 同步。</summary>
public sealed class AuthService
{
    readonly Settings _cfg;
    readonly object _sessionGate = new();
    public ApiClient Api { get; private set; }
    public event Action Changed;

    public string Token => _cfg.Token;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_cfg.Token);
    public long UserId => _cfg.UserId;
    public long PlayerId => _cfg.PlayerId > 0 ? _cfg.PlayerId : _cfg.UserId;
    public string Cuid => _cfg.Cuid;
    public string Nickname => _cfg.Nickname;
    public long RoleId => _cfg.RoleId;
    public string ServerRoomNo => _cfg.RoomNo;
    public long TotalClickCount { get; private set; }
    /// <summary>服务器记录的累计消耗（开宝箱等），用于算可用余额。</summary>
    public long TotalClickConsume { get; private set; }
    public JsonObject UserInfo { get; private set; }

    public AuthService(Settings cfg)
    {
        _cfg = cfg;
        Api = CreateApi(Guid.NewGuid().ToString("N"), ApiClient.ResolveBaseUrl(cfg.ServerRegion), cfg.Token);
    }

    ApiClient CreateApi(string cid, string baseUrl, string token)
    {
        var api = new ApiClient(cid, baseUrl);
        api.SetToken(token);
        api.TokenRefreshed += refreshed => TryUseSession(api, () =>
        {
            _cfg.Token = refreshed;
            ConfigStore.Save(_cfg);
        });
        return api;
    }

    internal ApiClient CaptureSession()
    {
        lock (_sessionGate) return IsLoggedIn ? Api : null;
    }

    internal bool TryUseSession(ApiClient session, Action action)
    {
        lock (_sessionGate)
        {
            if (session == null || !ReferenceEquals(Api, session) || !IsLoggedIn) return false;
            action();
            return true;
        }
    }

    // Each login gets its own client and cookie jar. Late requests cannot use the next login's cookies.
    void ReplaceSession(string token, long expires, string baseUrl = null)
    {
        var previous = Api;
        Api = CreateApi(baseUrl == null ? previous.Cid : Guid.NewGuid().ToString("N"), baseUrl ?? previous.BaseUrl, token);
        _cfg.Token = token;
        _cfg.TokenExpireMs = expires;
        _cfg.UserId = 0;
        UserInfo = null;
        TotalClickCount = TotalClickConsume = 0;
        previous.Dispose();
    }

    public void UpdateBaseUrl(string newBaseUrl)
    {
        lock (_sessionGate) ReplaceSession(_cfg.Token, _cfg.TokenExpireMs, newBaseUrl);
    }

    public async Task<bool> TryRestoreAsync()
    {
        var session = CaptureSession();
        if (session == null) return false;
        try
        {
            if (_cfg.TokenExpireMs > 0 && _cfg.TokenExpireMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < 48L * 3600 * 1000)
                await RenewAsync(session);
            await ReadUserAsync(session, ApplyUserInfo);
            return CaptureSession() == session;
        }
        catch (ApiException ex) when (ex.Code == 10002 || ex.Code == 10003 || ex.Code == 401 || ex.Code == 403)
        {
            Log.Write("token 失效：" + ex.Code);
            TryUseSession(session, ClearLocal);
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("恢复登录失败：" + ex.Message);
            return CaptureSession() == session;
        }
    }

    /// <summary>
    /// 发送 UID 验证码。服务端按数字校验 playerId，传字符串会返回 10001 Invalid parameters
    /// （原版前端也是 Number(uid)）。
    /// </summary>
    public async Task SendUidCodeAsync(string playerId)
    {
        long uid = ParsePlayerId(playerId);
        await Api.PostAsync("api/auth/uid-send-code", new { playerId = uid });
    }

    /// <summary>
    /// UID 登录。playerId 与 code 都必须是数字，字符串会被判定为无效参数。
    /// </summary>
    public async Task LoginWithUidAsync(string playerId, string code)
    {
        long uid = ParsePlayerId(playerId);
        string trimmed = (code ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ApiException(-1, "请输入验证码");
        if (!trimmed.All(char.IsDigit)) throw new ApiException(-1, "验证码只能是数字");
        if (trimmed.Length != 6) throw new ApiException(-1, "UID 验证码为 6 位数字");

        long codeNum = long.Parse(trimmed);
        _cfg.PlayerId = uid;
        var data = await Api.PostAsync("api/auth/uid-login", new { playerId = uid, code = codeNum });
        ApplyAuthToken(data);
        await WhoAmIAsync();
    }

    /// <summary>发送库洛通行证手机号验证码（国服 H5 SDK 通道）。</summary>
    public async Task SendPhoneCodeAsync(string phone)
    {
        if (!string.Equals(_cfg.ServerRegion, "cn", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "手机号登录仅支持国服");
        string rawPhone = NormalizePhone(phone);
        await KuroSdkClient.SendPhoneCodeAsync(rawPhone, _cfg.SdkDeviceNum);
    }

    /// <summary>手机号验证码登录：先用 H5 SDK 换通行证 access_token，再换 WuwaTappo token。</summary>
    public async Task LoginWithPhoneAsync(string phone, string code)
    {
        if (!string.Equals(_cfg.ServerRegion, "cn", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "手机号登录仅支持国服");
        string rawPhone = NormalizePhone(phone);
        string trimmed = (code ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ApiException(-1, "请输入短信验证码");
        if (!trimmed.All(char.IsDigit)) throw new ApiException(-1, "短信验证码只能是数字");
        if (trimmed.Length < 4 || trimmed.Length > 6) throw new ApiException(-1, "请输入 4-6 位短信验证码");

        var user = await KuroSdkClient.PhoneLoginAsync(rawPhone, trimmed, _cfg.SdkDeviceNum);
        await LoginWithSdkSessionAsync(user.Cuid, user.SdkOpenId, user.UserName, user.AccessToken, domestic: true);
    }

    /// <summary>
    /// 用通行证会话换取 WuwaTappo token。国服走 is_domestic=1，查询角色后按官方 SDK 参数登录。
    /// </summary>
    public async Task LoginWithSdkSessionAsync(string cuid, string sdkOpenId, string userName, string accessToken, bool domestic)
    {
        if (string.IsNullOrWhiteSpace(cuid)) throw new ApiException(-1, "缺少通行证 cuid");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ApiException(-1, "缺少通行证 access_token");
        if (string.IsNullOrWhiteSpace(sdkOpenId)) sdkOpenId = cuid;

        var queryResp = await Api.PostAsync("api/auth/player/query", new
        {
            cuid,
            is_domestic = domestic ? 1 : 0,
        });
        if (queryResp is not JsonObject queryObj)
            throw new ApiException(-1, "查询游戏角色失败");

        long channelId = ApiClient.ReadLong(queryObj["channelId"]);
        if (channelId <= 0) channelId = domestic ? 201 : 18;
        if (queryObj["items"] is not JsonArray items || items.Count == 0)
            throw new ApiException(-1, "未找到游戏角色，请先进入游戏创建角色");

        // 官方客户端取玩家列表第一项；这里保持一致。
        var selectedRole = items.OfType<JsonObject>().FirstOrDefault();
        if (selectedRole == null) throw new ApiException(-1, "未找到可用游戏角色");
        long playerId = ApiClient.ReadLong(selectedRole["playerId"]);
        string serverId = ApiClient.ReadString(selectedRole["serverId"]);
        if (playerId <= 0) throw new ApiException(-1, "游戏角色信息不完整");
        if (string.IsNullOrEmpty(serverId)) serverId = "0";

        _cfg.PlayerId = playerId;
        string cname = string.IsNullOrWhiteSpace(userName) ? sdkOpenId : userName;
        var loginData = await Api.PostAsync("api/auth/login", new
        {
            cuid,
            puid = sdkOpenId,
            channelId,
            userid = sdkOpenId,
            cname,
            accessToken,
            source = "sdk",
            playerId,
            serverId,
            is_domestic = domestic ? 1 : 0,
        });
        ApplyAuthToken(loginData);
        await WhoAmIAsync();
    }

    static string NormalizePhone(string phone)
    {
        string raw = (phone ?? "").Trim().Replace(" ", "").Replace("-", "");
        if (raw.Length != 11 || !raw.All(char.IsDigit) || raw[0] != '1' || raw[1] < '3' || raw[1] > '9')
            throw new ApiException(-1, "请输入 11 位手机号");
        return raw;
    }

    /// <summary>
    /// 国际服凭海外通行证会话（Kuro Passport OAuth）登录。
    /// 服务端不支持国际服 UID 邮件验证码，采用官方通行证的 cuid + accessToken 登录。
    /// </summary>
    public async Task LoginWithForeignSessionAsync(
        string cuid,
        string puid,
        string userId,
        string userName,
        string accessToken,
        long? targetPlayerId = null,
        string targetServerId = null)
    {
        if (string.IsNullOrWhiteSpace(cuid)) throw new ApiException(-1, "缺少 cuid");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ApiException(-1, "缺少 accessToken");

        // 1. 查询该 cuid 下的角色列表。官方国际服客户端允许“通行证已登录但账号下没有游戏角色”，
        // 这种情况下仍然可以用 playerId=0 / serverId=0 换取账号级 token，不能直接判定登录失败。
        var queryResp = await Api.PostAsync("api/auth/player/query", new { cuid = cuid, is_domestic = 0 });
        if (queryResp is not JsonObject queryObj)
            throw new ApiException(-1, "查询国际服角色失败");

        long channelId = ApiClient.ReadLong(queryObj["channelId"]);
        if (channelId <= 0) channelId = 18;

        var items = queryObj["items"] as JsonArray;
        JsonObject selectedRole = null;
        if (targetPlayerId.HasValue && targetPlayerId.Value > 0)
        {
            selectedRole = items?.OfType<JsonObject>().FirstOrDefault(r => ApiClient.ReadLong(r["playerId"]) == targetPlayerId.Value);
        }
        if (selectedRole == null && !string.IsNullOrWhiteSpace(targetServerId))
        {
            selectedRole = items?.OfType<JsonObject>().FirstOrDefault(r => string.Equals(ApiClient.ReadString(r["serverId"]), targetServerId, StringComparison.OrdinalIgnoreCase));
        }
        if (selectedRole == null && items != null && items.Count > 0)
            selectedRole = items[0] as JsonObject;

        long playerId = 0;
        string serverId = "0";
        if (selectedRole != null)
        {
            playerId = ApiClient.ReadLong(selectedRole["playerId"]);
            serverId = ApiClient.ReadString(selectedRole["serverId"]);
            if (playerId <= 0) throw new ApiException(-1, "游戏角色信息不完整");
        }
        _cfg.PlayerId = playerId;

        // 2. 调用 SDK 登录；无角色账号使用 0/0，官方客户端也是同样的降级登录方式。
        var loginData = await Api.PostAsync("api/auth/login", new
        {
            cuid = cuid,
            puid = string.IsNullOrEmpty(puid) ? cuid : puid,
            channelId = channelId,
            userid = string.IsNullOrEmpty(userId) ? cuid : userId,
            cname = string.IsNullOrEmpty(userName) ? (userId ?? cuid) : userName,
            accessToken = accessToken,
            source = "sdk",
            playerId = playerId,
            serverId = string.IsNullOrEmpty(serverId) ? "0" : serverId,
            is_domestic = 0
        });

        ApplyAuthToken(loginData);

        // 无角色账号的 user/me 可能没有 player_id 或超时；token 已经换到，不能让登录状态被这次
        // 同步失败整体回滚。后续刷新用户信息时会自然补齐。
        try
        {
            await WhoAmIAsync();
        }
        catch (Exception ex) when (playerId == 0)
        {
            Log.Write("无角色国际服账号登录成功，但 user/me 同步失败：" + ex.Message);
        }
    }

    public Task RenewAsync() => RenewAsync(CaptureSession());

    async Task RenewAsync(ApiClient session)
    {
        Task<JsonNode> request = null;
        if (!TryUseSession(session, () => request = session.GetAsync("api/frontend/user/renew-token"))) return;
        var data = await request;
        if (data is JsonValue v && v.TryGetValue<bool>(out var ok) && ok)
        {
            TryUseSession(session, () =>
            {
                if (_cfg.TokenExpireMs > 0) _cfg.TokenExpireMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 7L * 24 * 3600 * 1000;
                ConfigStore.Save(_cfg);
            });
        }
    }

    /// <summary>开箱响应成功后先本地把该侧宝箱时间戳清零，让 UI 立即消失；随后 user/me 会用真实冷却时间修正。</summary>
    public void MarkChestOpened(string place)
    {
        if (UserInfo is not JsonObject me) return;
        string key = string.Equals(place, "left", StringComparison.OrdinalIgnoreCase)
            ? "left_treasure_chest"
            : "right_treasure_chest";
        me[key] = 0;
    }

    /// <summary>轻量刷新：只同步点击数、宝箱时间戳等运行时字段，不回写本地外观。</summary>
    public Task RefreshClicksAsync() => RefreshClicksAsync(CaptureSession());

    internal Task<bool> RefreshClicksAsync(ApiClient session) => ReadUserAsync(session, me =>
    {
        UserInfo = me;
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);
        Changed?.Invoke();
    });

    async Task<bool> ReadUserAsync(ApiClient session, Action<JsonObject> apply)
    {
        Task<JsonNode> request = null;
        if (!TryUseSession(session, () => request = session.GetAsync("api/frontend/user/me"))) return false;
        try
        {
            var data = await request;
            return data is JsonObject me && TryUseSession(session, () => apply(me));
        }
        catch (OperationCanceledException) when (CaptureSession() != session) { return false; }
    }

    public Task WhoAmIAsync() => WhoAmIAsync(CaptureSession());

    internal Task<bool> WhoAmIAsync(ApiClient session) => ReadUserAsync(session, ApplyUserInfo);

    void ApplyUserInfo(JsonObject me)
    {
        UserInfo = me;
        string serverCuid = ApiClient.ReadString(me["cuid"]);
        bool accountChanged = !string.IsNullOrEmpty(_cfg.Cuid) && !string.IsNullOrEmpty(serverCuid) && _cfg.Cuid != serverCuid;
        if (accountChanged)
        {
            _cfg.LocalAppearance = false;
            _cfg.LocalEmoteSlots = false;
        }
        _cfg.UserId = ApiClient.ReadLong(me["id"]);
        long pid = ApiClient.ReadLong(me["player_id"]);
        if (pid <= 0) pid = ApiClient.ReadLong(me["playerId"]);
        if (pid > 0) _cfg.PlayerId = pid;
        _cfg.Cuid = serverCuid;
        _cfg.Nickname = ApiClient.ReadString(me["nickname"]);
        _cfg.RoleId = ApiClient.ReadLong(me["role_id"]);
        _cfg.RoomNo = ApiClient.ReadString(me["room_no"]);
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);

        long skin = ApiClient.ReadLong(me["character"]?["id"]);
        long acc = ApiClient.ReadLong(me["accessory"]?["id"]);
        // 云端安全值始终记录服务器最近一次确认的内容；本地是否覆盖由 LocalAppearance 决定。
        _cfg.CloudCharacterAssetId = skin;
        _cfg.CloudAccessoryAssetId = acc;
        if (!_cfg.LocalAppearance)
        {
            if (skin > 0) _cfg.CharacterAssetId = skin;
            _cfg.AccessoryAssetId = acc;
        }

        var emotes = new long[5];
        if (me["emote"] is JsonArray arr)
        {
            int i = 0;
            foreach (var e in arr)
            {
                if (i >= 5) break;
                emotes[i++] = ApiClient.ReadLong(e?["id"]);
            }
        }
        _cfg.CloudEmoteAssetIds = emotes;
        if (!_cfg.LocalEmoteSlots && emotes.Any(x => x > 0)) _cfg.EmoteAssetIds = emotes;
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        var session = CaptureSession();
        try
        {
            Task<JsonNode> request = null;
            if (!TryUseSession(session, () => request = session.PostAsync("api/frontend/user/logout", new { click_num = 0 }))) return;
            await request;
        }
        catch (Exception ex) { Log.Write("登出请求失败：" + ex.Message); }
        TryUseSession(session, ClearLocal);
    }

    public void ClearLocal()
    {
        lock (_sessionGate)
        {
            ReplaceSession("", 0);
            _cfg.RoomNo = "";
            _cfg.PlayerId = 0;
            _cfg.Nickname = "";
            _cfg.CloudCharacterAssetId = 0;
            _cfg.CloudAccessoryAssetId = 0;
            _cfg.CloudEmoteAssetIds = new long[5];
            ConfigStore.Save(_cfg);
            Changed?.Invoke();
        }
    }

    public void SetTokenManually(string token)
    {
        StoreSession((token ?? "").Trim(), 0);
    }

    void ApplyAuthToken(JsonNode data)
    {
        if (data is not JsonObject obj) throw new ApiException(-1, "登录响应缺少 data");
        string token = ApiClient.ReadString(obj["token"]);
        if (string.IsNullOrEmpty(token)) throw new ApiException(-1, "登录响应缺少 token");
        StoreSession(token, ParseExpire(obj["expire_time"]));
    }

    void StoreSession(string token, long expires)
    {
        lock (_sessionGate)
        {
            ReplaceSession(token, expires);
            ConfigStore.Save(_cfg);
            Changed?.Invoke();
        }
    }

    static long ParseExpire(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l < 100000000000L ? l * 1000 : l;
                if (v.TryGetValue<string>(out var s))
                {
                    if (long.TryParse(s, out var n)) return n < 100000000000L ? n * 1000 : n;
                    if (DateTimeOffset.TryParse(s, out var dto)) return dto.ToUnixTimeMilliseconds();
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>UID 登录校验：游戏 UID 为 9~10 位纯数字；手机号请走手机号登录通道。</summary>
    static long ParsePlayerId(string playerId)
    {
        string raw = (playerId ?? "").Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(raw)) throw new ApiException(-1, "请输入游戏 UID");
        if (!raw.All(char.IsDigit)) throw new ApiException(-1, "游戏 UID 只能是数字");
        if (raw.Length == 11)
            throw new ApiException(-1, "UID 登录不支持手机号，请切换到手机号登录");
        if (raw.Length < 9 || raw.Length > 10)
            throw new ApiException(-1, "游戏 UID 为 9-10 位数字");
        return long.Parse(raw);
    }
}
