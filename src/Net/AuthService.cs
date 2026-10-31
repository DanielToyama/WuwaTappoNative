using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

/// <summary>登录编排：UID 验证码登录（原版主路径）、token 存档、48 小时内自动续期、user/me 同步。</summary>
public sealed class AuthService
{
    readonly Settings _cfg;
    public ApiClient Api { get; }
    public event Action Changed;

    public string Token => _cfg.Token;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_cfg.Token);
    public long UserId => _cfg.UserId;
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
        Api = new ApiClient(Guid.NewGuid().ToString("N"));
        Api.SetToken(cfg.Token);
        Api.TokenRefreshed += t =>
        {
            _cfg.Token = t;
            ConfigStore.Save(_cfg);
        };
    }

    public async Task<bool> TryRestoreAsync()
    {
        if (string.IsNullOrEmpty(_cfg.Token)) return false;
        try
        {
            if (_cfg.TokenExpireMs > 0 && _cfg.TokenExpireMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < 48L * 3600 * 1000)
                await RenewAsync();
            await WhoAmIAsync();
            return true;
        }
        catch (ApiException ex) when (ex.Code == 10002 || ex.Code == 10003 || ex.Code == 401 || ex.Code == 403)
        {
            Log.Write("token 失效：" + ex.Code);
            ClearLocal();
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("恢复登录失败：" + ex.Message);
            return IsLoggedIn;
        }
    }

    /// <summary>
    /// 发送验证码。服务端按数字校验 playerId，传字符串会返回 10001 Invalid parameters
    /// （原版前端也是 Number(uid)）。
    /// </summary>
    public async Task SendUidCodeAsync(string playerId)
    {
        long uid = ParsePlayerId(playerId);
        await Api.PostAsync("api/auth/uid-send-code", new { playerId = uid });
    }

    /// <summary>UID / 手机号登录。playerId 与 code 都必须是数字，字符串会被判定为无效参数。</summary>
    public async Task LoginWithUidAsync(string playerId, string code)
    {
        long uid = ParsePlayerId(playerId);
        string trimmed = (code ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ApiException(-1, "请输入验证码");
        if (!trimmed.All(char.IsDigit)) throw new ApiException(-1, "验证码只能是数字");
        if (trimmed.Length != 6) throw new ApiException(-1, "验证码为 6 位数字");
        long codeNum = long.Parse(trimmed);
        var data = await Api.PostAsync("api/auth/uid-login", new { playerId = uid, code = codeNum });
        ApplyAuthToken(data);
        await WhoAmIAsync();
    }

    public async Task RenewAsync()
    {
        var data = await Api.GetAsync("api/frontend/user/renew-token");
        if (data is JsonValue v && v.TryGetValue<bool>(out var ok) && ok)
        {
            if (_cfg.TokenExpireMs > 0) _cfg.TokenExpireMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 7L * 24 * 3600 * 1000;
            ConfigStore.Save(_cfg);
        }
    }

    /// <summary>轻量刷新：只同步点击数、宝箱时间戳等运行时字段，不回写本地外观。</summary>
    public async Task RefreshClicksAsync()
    {
        var data = await Api.GetAsync("api/frontend/user/me");
        if (data is not JsonObject me) return;
        UserInfo = me;
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);
        Changed?.Invoke();
    }

    public async Task WhoAmIAsync()
    {
        var data = await Api.GetAsync("api/frontend/user/me");
        if (data is not JsonObject me) return;
        UserInfo = me;
        string serverCuid = ApiClient.ReadString(me["cuid"]);
        bool accountChanged = !string.IsNullOrEmpty(_cfg.Cuid) && !string.IsNullOrEmpty(serverCuid) && _cfg.Cuid != serverCuid;
        if (accountChanged)
        {
            _cfg.LocalAppearance = false;
            _cfg.LocalEmoteSlots = false;
        }
        _cfg.UserId = ApiClient.ReadLong(me["id"]);
        _cfg.Cuid = serverCuid;
        _cfg.Nickname = ApiClient.ReadString(me["nickname"]);
        _cfg.RoleId = ApiClient.ReadLong(me["role_id"]);
        _cfg.RoomNo = ApiClient.ReadString(me["room_no"]);
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);

        long skin = ApiClient.ReadLong(me["character"]?["id"]);
        long acc = ApiClient.ReadLong(me["accessory"]?["id"]);
        if (!_cfg.LocalAppearance)
        {
            if (skin > 0) _cfg.CharacterAssetId = skin;
            _cfg.AccessoryAssetId = acc;
        }

        if (!_cfg.LocalEmoteSlots)
        {
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
            if (emotes.Any(x => x > 0)) _cfg.EmoteAssetIds = emotes;
        }
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        try { await Api.PostAsync("api/frontend/user/logout", new { click_num = 0 }); } catch (Exception ex) { Log.Write("登出请求失败：" + ex.Message); }
        ClearLocal();
    }

    public void ClearLocal()
    {
        _cfg.Token = "";
        _cfg.TokenExpireMs = 0;
        _cfg.RoomNo = "";
        _cfg.UserId = 0;
        _cfg.Nickname = "";
        Api.SetToken("");
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
    }

    public void SetTokenManually(string token)
    {
        _cfg.Token = (token ?? "").Trim();
        Api.SetToken(_cfg.Token);
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
    }

    void ApplyAuthToken(JsonNode data)
    {
        if (data is not JsonObject obj) throw new ApiException(-1, "登录响应缺少 data");
        string token = ApiClient.ReadString(obj["token"]);
        if (string.IsNullOrEmpty(token)) throw new ApiException(-1, "登录响应缺少 token");
        _cfg.Token = token;
        _cfg.TokenExpireMs = ParseExpire(obj["expire_time"]);
        Api.SetToken(token);
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
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

    /// <summary>原版校验：纯数字且至少 9 位（游戏特征码/UID）。</summary>
    /// <summary>
    /// 接受游戏 UID（9~10 位）或手机号（11 位，可带 +86 / 86 前缀）。
    /// 两者都通过同一个 playerId 数字参数提交，服务端按位数区分：
    /// 11 位会走第三方短信通道，实测会返回 10023 而不是“查询不到角色”。
    /// </summary>
    static long ParsePlayerId(string playerId)
    {
        string raw = (playerId ?? "").Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(raw)) throw new ApiException(-1, "请输入 UID 或手机号");
        if (raw.StartsWith("+")) raw = raw[1..];
        if (!raw.All(char.IsDigit)) throw new ApiException(-1, "UID / 手机号只能是数字");
        // 手机号允许带 86 国家码，统一裁掉后再按 11 位校验。
        if (raw.Length == 13 && raw.StartsWith("86")) raw = raw[2..];
        if (raw.Length < 9) throw new ApiException(-1, "UID 至少 9 位数字，手机号请填 11 位");
        if (raw.Length > 11) throw new ApiException(-1, "最多 11 位（手机号）");
        if (raw.Length == 11 && raw[0] != '1') throw new ApiException(-1, "手机号格式不正确");
        return long.Parse(raw);
    }
}
