using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

public sealed class RoomMember
{
    public long Id;
    public string Nickname = "";
    public string RoleKey = "";   // b：目录 id 或 fileKey
    public string PartKey = "";   // c
    public string Action = "";    // d
    public bool Online = true;
    public bool IsOwner;
    public bool Visible = true;
    public bool HandLeft; // 远端动作包按次交替左右手
}

/// <summary>
/// 房间状态机：REST 创建/加入/退出 + Socket.IO 快照。事件表见 README。
/// rd 拉外观快照、rm 拉成员快照、cr 上报增量点击、re 发表情、rc 广播装扮。
/// </summary>
public sealed class RoomManager : IDisposable
{
    readonly AuthService _auth;
    readonly Settings _cfg;
    SocketClient _socket;
    System.Threading.Timer _reportTimer;
    System.Threading.Timer _reconnectTimer;
    int _reconnectBusy;
    bool _reconnectSuppressed;
    readonly object _clicksLock = new();
    readonly SemaphoreSlim _clickUploadGate = new(1, 1);
    long _clickEpoch;
    long _baseClicks;
    long _pendingClicks;
    long _inflightClicks;
    long _serverConsume;
    long _lastBoostMs;
    int _busy;
    int _generation;
    bool _roomReady;
    bool _awaitingSnapshot;
    long _lastRejectedClickLogMs;
    int _connectionFailures;
    volatile bool _serviceUnavailable;

    /// <summary>累计总额：服务器已记录 + 本地未上报 + 正在上报的点击。上报 cr 与开宝箱的 click_num 都用它。</summary>
    public long DisplayClicks
    {
        get { lock (_clicksLock) { return _baseClicks + _pendingClicks + _inflightClicks; } }
    }

    /// <summary>可用余额：总额减去已消耗。原版桌宠计数条显示这个值，开箱也按它判断够不够。</summary>
    public long AvailableClicks
    {
        get { lock (_clicksLock) { return Math.Max(0, _baseClicks + _pendingClicks + _inflightClicks - _serverConsume); } }
    }

    public long ServerConsume
    {
        get { lock (_clicksLock) { return _serverConsume; } }
    }
    public event Action ClicksChanged;
    /// <summary>每次本地产生一次敲击（真实或自动）时触发，用于自动表情等本地联动。</summary>
    public event Action LocalClick;

    public string State { get; private set; } = "idle";
    public string RoomNo { get; private set; } = "";
    /// <summary>连续连接/房间快照失败达到阈值后置位，用于提示联机服务暂不可用。</summary>
    public bool ServiceUnavailable => _serviceUnavailable;
    public long SelfId => _auth.UserId;
    /// <summary>只有 socket 握手成功且房间成员快照也成功，才算真正进入房间。</summary>
    public bool IsConnected => _socket?.Connected == true && (string.IsNullOrEmpty(RoomNo) || _roomReady);
    /// <summary>服务器成员快照中自己的房主标记。</summary>
    public bool SelfIsOwner { get; private set; }
    public readonly ConcurrentDictionary<long, RoomMember> Members = new();

    public event Action StateChanged;
    /// <summary>联机服务不可用/恢复时触发。</summary>
    public event Action<bool> ServiceUnavailableChanged;
    /// <summary>成员名册/外观/在线状态变化。人数多时高频，订阅方必须做合并（不要每条都全量重推）。</summary>
    public event Action<long> MemberUpdated;
    /// <summary>仅动作包（按键/抬手）触发的轻量事件：只驱动对应桌宠的动画，不改变名册信息。</summary>
    public event Action<long, string> MemberAction;
    public event Action<long> MemberRemoved;
    /// <summary>整批快照（rm/rd）应用完成时触发一次；人多时比逐成员事件便宜得多。</summary>
    public event Action RosterChanged;
    public event Action<long, string> MemberEmote;
    public event Action<int> ForcedDisconnect;
    public event Action<string> SocketClosed;
    /// <summary>服务器拒绝进入房间（例如房间不存在/已解散），UI 可提示用户。</summary>
    public event Action<string> JoinRejected;

    public RoomManager(AuthService auth, Settings cfg)
    {
        _auth = auth;
        _cfg = cfg;
        // 与原版一致：点击上报周期 30 秒。首次也等 30 秒，避免启动时撞上服务端频率窗口。
        _reportTimer = new System.Threading.Timer(_ => _ = FlushClicksAsync(), null, 30000, 30000);
        // Socket 断开或首次连接失败后，不能只等一次重试；后台每 5 秒检查并恢复房间连接。
        _reconnectTimer = new System.Threading.Timer(_ => _ = AutoReconnectTickAsync(), null, 3000, 5000);
    }

    public async Task CreateAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            await LeaveSocketOnlyAsync();
            var data = await _auth.Api.PostAsync("api/frontend/rooms", new { });
            string no = ApiClient.ReadString(data?["room_no"]);
            if (string.IsNullOrEmpty(no)) throw new ApiException(-1, "创建房间失败：未返回房间号");
            await EnterAsync(no);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public async Task JoinAsync(string roomNo)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _reconnectSuppressed = false;
        try
        {
            roomNo = (roomNo ?? "").Trim();
            if (string.IsNullOrEmpty(roomNo)) throw new ApiException(-1, "房间号不能为空");

            // 已在别的房间时先发 rl 退出（原版没有 REST 退出接口，退房只走 socket），
            // 否则服务器会返回 80003 或把两个房间状态搅在一起。
            if (State == "joined" && !string.IsNullOrEmpty(RoomNo) && RoomNo != roomNo)
            {
                await LeaveSocketOnlyAsync();
                await Task.Delay(200);
            }

            bool alreadyHere = ReadUserRoom() == roomNo;
            if (!alreadyHere)
            {
                await LeaveSocketOnlyAsync();
                try
                {
                    await _auth.Api.PostAsync("api/frontend/rooms/join", new { room_no = roomNo });
                }
                catch (ApiException ex) when (ex.Code == 80003)
                {
                    // 服务器认为仍在旧房间：先进旧房发 rl，再重试加入
                    string old = ReadUserRoom();
                    if (!string.IsNullOrEmpty(old))
                    {
                        await EnterAsync(old, announce: false);
                        await EmitLeaveAsync();
                        await CloseSocketAsync();
                    }
                    await _auth.Api.PostAsync("api/frontend/rooms/join", new { room_no = roomNo });
                }
            }
            await EnterAsync(roomNo);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <param name="reconnectLobby">
    /// 退房后是否立刻回到大厅。切换区服时必须传 false：否则会用旧区服的 token/BaseUrl
    /// 重连旧大厅，随后登录新区服时 EnsureLobbyAsync 看到旧 socket 仍连接，就不会建立新连接。
    /// </param>
    public async Task LeaveAsync(bool reconnectLobby = true)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        // 退房后 cfg.RoomNo 会被清空，自动重连只会恢复大厅，不会误回旧房间。
        _reconnectSuppressed = false;
        try
        {
            await LeaveSocketOnlyAsync();
            State = "idle";
            RoomNo = "";
            _cfg.RoomNo = "";
            ConfigStore.Save(_cfg);
            Members.Clear();
            StateChanged?.Invoke();
            if (reconnectLobby) await EnsureLobbyAsync();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>回到大厅 socket（rm 为空）。大厅里没有房间成员，但仍用于点击上报。</summary>
    public async Task EnsureLobbyAsync()
    {
        if (State == "joined" || string.IsNullOrEmpty(_auth.Token)) return;
        if (_socket?.Connected == true) return;
        await ConnectSocketAsync("");
    }

    async Task AutoReconnectTickAsync()
    {
        if (Interlocked.Exchange(ref _reconnectBusy, 1) == 1) return;
        try
        {
            if (_reconnectSuppressed || string.IsNullOrEmpty(_auth.Token)) return;
            string desiredRoom = _cfg.RoomNo;
            if (!string.IsNullOrEmpty(desiredRoom))
            {
                if (State == "joined" && IsConnected) return;
                if (_awaitingSnapshot || Volatile.Read(ref _busy) == 1) return;
                try
                {
                    Log.Write("自动重连房间 " + desiredRoom);
                    await JoinAsync(desiredRoom);
                }
                catch (Exception ex) { Log.Write("自动重连房间失败 " + desiredRoom + " " + ex.Message); }
            }
            else
            {
                if (_socket?.Connected == true) return;
                if (Volatile.Read(ref _busy) == 1) return;
                try { await EnsureLobbyAsync(); }
                catch (Exception ex) { Log.Write("自动重连大厅失败 " + ex.Message); }
            }
        }
        finally { Interlocked.Exchange(ref _reconnectBusy, 0); }
    }

    async Task LeaveSocketOnlyAsync()
    {
        if (!string.IsNullOrEmpty(RoomNo) && _socket != null)
        {
            // EnterAsync 返回时 WebSocket 可能刚连上、Socket.IO 命名空间还没就绪
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!_socket.Connected && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (_socket.Connected) await EmitLeaveAsync();
            await Task.Delay(150);
        }
        await CloseSocketAsync();
        Members.Clear();
    }

    async Task EmitLeaveAsync()
    {
        try
        {
            if (_socket?.Connected == true && !string.IsNullOrEmpty(RoomNo))
                await _socket.EmitAsync("rl", new JsonObject { ["r"] = RoomNo }, 8000);
        }
        catch (Exception ex) { Log.Write("离开房间事件失败 " + ex.Message); }
    }

    string ReadUserRoom() => string.IsNullOrEmpty(_cfg.RoomNo) ? "" : _cfg.RoomNo;

    /// <summary>把房间号记进历史（用于登录时补录服务器上的当前房间）。</summary>
    public void RememberRoom(string roomNo)
    {
        roomNo = (roomNo ?? "").Trim();
        if (string.IsNullOrEmpty(roomNo)) return;
        var history = (_cfg.RoomHistory ?? Array.Empty<string>()).Where(x => x != roomNo).ToList();
        history.Insert(0, roomNo);
        _cfg.RoomHistory = history.Take(100).ToArray();
        ConfigStore.Save(_cfg);
    }

    string MemberVisibilityKey(long id) => id.ToString();

    RoomMember NewMember(long id) => new RoomMember { Id = id, Visible = IsMemberVisible(id) };

    /// <summary>成员是否显示；按成员 id 全局记忆，没有单独设置过时默认显示。</summary>
    public bool IsMemberVisible(long id)
    {
        if (_cfg.MemberVisibility == null || _cfg.MemberVisibility.Count == 0) return true;
        if (_cfg.MemberVisibility.TryGetValue(MemberVisibilityKey(id), out bool visible)) return visible;
        // 兼容早期测试版写入的“房间号:成员 id”键；即使房间已销毁也能沿用。
        string suffix = ":" + id;
        foreach (var pair in _cfg.MemberVisibility)
            if (pair.Key.EndsWith(suffix, StringComparison.Ordinal)) return pair.Value;
        return true;
    }

    /// <summary>保存单个成员的显示/隐藏设置；按成员 id 记忆，不随房间销毁丢失。</summary>
    public void SetMemberVisible(long id, bool visible)
    {
        if (id <= 0) return;
        _cfg.MemberVisibility ??= new Dictionary<string, bool>();
        string key = MemberVisibilityKey(id);
        string suffix = ":" + id;
        foreach (string oldKey in _cfg.MemberVisibility.Keys
                     .Where(k => k == key || k.EndsWith(suffix, StringComparison.Ordinal)).ToList())
            _cfg.MemberVisibility.Remove(oldKey);
        if (!visible) _cfg.MemberVisibility[key] = false;
        if (Members.TryGetValue(id, out var member)) member.Visible = visible;
        ConfigStore.Save(_cfg);
    }

    async Task EnterAsync(string roomNo, bool announce = true)
    {
        RoomNo = roomNo;
        _cfg.RoomNo = roomNo;
        _cfg.LastRoom = roomNo;
        var history = (_cfg.RoomHistory ?? Array.Empty<string>()).Where(x => x != roomNo).ToList();
        history.Insert(0, roomNo);
        _cfg.RoomHistory = history.Take(100).ToArray();
        ConfigStore.Save(_cfg);

        await CloseSocketAsync();
        State = "joined";
        _roomReady = false;
        _awaitingSnapshot = false;
        SelfIsOwner = false;
        // 先让 UI 进入“连接中”，即使后面 WebSocket 连接失败也还有自动重试。
        StateChanged?.Invoke();
        await ConnectSocketAsync(roomNo);
        StateChanged?.Invoke();
        if (announce) Log.Write("进入房间 " + roomNo);
    }

    void MarkConnectionFailure(string reason)
    {
        if (string.IsNullOrEmpty(_auth.Token) || _reconnectSuppressed) return;
        int failures = Interlocked.Increment(ref _connectionFailures);
        Log.Write($"联机服务连接失败（{failures}）：{reason}");
        if (failures >= 3) SetServiceUnavailable(true);
    }

    void MarkConnectionSuccess()
    {
        Interlocked.Exchange(ref _connectionFailures, 0);
        SetServiceUnavailable(false);
    }

    void SetServiceUnavailable(bool value)
    {
        if (_serviceUnavailable == value) return;
        _serviceUnavailable = value;
        Log.Write(value ? "联机服务暂不可用" : "联机服务已恢复");
        ServiceUnavailableChanged?.Invoke(value);
        StateChanged?.Invoke();
    }

    async Task ConnectSocketAsync(string roomNo)
    {
        await CloseSocketAsync();
        int gen = ++_generation;
        _socket = new SocketClient();
        _socket.Event += (name, payload) => { if (gen == _generation) OnEvent(name, payload); };
        _socket.ConnectedEvent += () => { if (gen == _generation) _ = OnConnectedAsync(); };
        _socket.Closed += reason =>
        {
            if (gen != _generation) return;
            bool wasJoined = State == "joined";
            _roomReady = false;
            _awaitingSnapshot = false;
            if (wasJoined)
            {
                // 保留 joined 状态让 UI 显示“连接中…”，由后台定时器持续重连。
                Members.Clear();
                StateChanged?.Invoke();
            }
            MarkConnectionFailure(reason);
            SocketClosed?.Invoke(reason);
        };
        try
        {
            await _socket.ConnectAsync(_auth.Token, roomNo ?? "", _auth.Api.BaseUrl);
            Log.Write("socket 已连接 " + (string.IsNullOrEmpty(roomNo) ? "(大厅)" : roomNo));
        }
        catch (Exception ex)
        {
            MarkConnectionFailure(ex.Message);
            throw;
        }
    }

    async Task ConfirmLobbyConnectionAsync(int generation)
    {
        // 大厅没有房间快照可确认，等连接稳定 5 秒再清除故障计数，避免连上即断时误判恢复。
        await Task.Delay(5000).ConfigureAwait(false);
        if (generation != _generation || _socket?.Connected != true) return;
        MarkConnectionSuccess();
        StateChanged?.Invoke();
    }

    async Task OnConnectedAsync()
    {
        var socket = _socket;
        if (socket == null) return;
        try
        {
            if (string.IsNullOrEmpty(RoomNo))
            {
                _awaitingSnapshot = false;
                _ = ConfirmLobbyConnectionAsync(_generation);
                StateChanged?.Invoke();
                return;
            }
            _awaitingSnapshot = true;
            var payload = new JsonObject { ["r"] = RoomNo };
            var snap = await socket.EmitAsync("rm", payload, 8000);
            if (snap == null)
            {
                // 超时/连接中断：保持“连接中”，交给后台重连，不要误报成已连接。
                Log.Write("房间快照超时 " + RoomNo);
                MarkConnectionFailure("房间快照超时");
                return;
            }
            if (!ApplyMemberSnapshot(snap))
            {
                int code = snap is JsonObject o ? ApiClient.ReadInt(o["c"]) : -1;
                if (code != 0 && code != 200)
                    RejectRoom($"房间不存在或已解散（{code}）");
                else
                    RejectRoom("房间数据异常，已停止自动重连");
                return;
            }
            _roomReady = true;
            MarkConnectionSuccess();
            StateChanged?.Invoke();
            var appearanceSnap = await socket.EmitAsync("rd", new JsonObject { ["r"] = RoomNo }, 8000);
            ApplyAppearanceSnapshot(appearanceSnap);
            await AnnounceAppearanceAsync();
        }
        catch (Exception ex)
        {
            Log.Write("房间快照失败 " + ex.Message);
            MarkConnectionFailure(ex.Message);
        }
        finally { _awaitingSnapshot = false; }
    }

    void RejectRoom(string message)
    {
        _roomReady = false;
        _awaitingSnapshot = false;
        SelfIsOwner = false;
        State = "idle";
        RoomNo = "";
        _cfg.RoomNo = "";
        ConfigStore.Save(_cfg);
        Members.Clear();
        Log.Write(message);
        JoinRejected?.Invoke(message);
        StateChanged?.Invoke();
        _ = CloseSocketAsync();
    }

    bool ApplyMemberSnapshot(JsonNode snap)
    {
        if (snap is not JsonObject o) return false;
        int code = ApiClient.ReadInt(o["c"]);
        if (code != 0 && code != 200) return false;
        var list = o["d"]?["m"] as JsonArray;
        if (list == null) return false;
        var seen = new HashSet<long>();
        bool touched = false;
        bool selfSeen = false;
        foreach (var item in list)
        {
            if (item is not JsonObject m) continue;
            long id = ApiClient.ReadLong(m["u"]);
            if (id <= 0) continue;
            if (id == SelfId)
            {
                // 服务器快照包含自己时，直接使用它的房主标记。
                SelfIsOwner = ApiClient.ReadBool(m["o"]);
                selfSeen = true;
                continue;
            }
            seen.Add(id);
            var member = Members.GetOrAdd(id, _ => NewMember(id));
            member.Nickname = ApiClient.ReadString(m["n"]);
            member.IsOwner = ApiClient.ReadBool(m["o"]);
            member.Online = !m.ContainsKey("l") || ApiClient.ReadBool(m["l"]);
            // 不能在这里重置 Visible：快照/重连会周期触发，否则用户单独隐藏的成员会被重新放出来。
            touched = true;
        }
        foreach (var id in Members.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (Members.TryRemove(id, out _)) MemberRemoved?.Invoke(id);
        }
        // 部分服务端快照不返回自己；这时若名单里没有其他房主，则自己就是房主。
        if (!selfSeen) SelfIsOwner = !Members.Values.Any(m => m.IsOwner);
        if (touched) RosterChanged?.Invoke();
        return true;
    }

    void OnEvent(string name, JsonNode payload)
    {
        try
        {
            switch (name)
            {
                case "rj":
                {
                    long id = ParseId(payload?["a"]);
                    if (id <= 0 || id == SelfId) return;
                    var m = Members.GetOrAdd(id, _ => NewMember(id));
                    if (!string.IsNullOrEmpty(ApiClient.ReadString(payload?["n"])))
                        m.Nickname = ApiClient.ReadString(payload?["n"]);
                    ApplyAppearance(m, payload as JsonObject);
                    m.Online = true;
                    // 新成员沿用默认显示状态；老成员的本地显示开关不被 rj 覆盖。
                    MemberUpdated?.Invoke(id);
                    break;
                }
                case "rl":
                {
                    long id = ParseId(payload?["a"]);
                    if (id <= 0) return;
                    if (id == SelfId) { State = "idle"; Members.Clear(); StateChanged?.Invoke(); return; }
                    if (Members.TryRemove(id, out _)) MemberRemoved?.Invoke(id);
                    break;
                }
                case "ro":
                {
                    long id = ParseId(payload?["a"]);
                    if (id > 0 && Members.TryGetValue(id, out var m)) { m.Online = false; MemberUpdated?.Invoke(id); }
                    break;
                }
                case "rs":
                    _ = _socket?.EmitAsync("rm", new JsonObject { ["r"] = RoomNo }, 8000).ContinueWith(t => ApplyMemberSnapshot(t.Result));
                    break;
                case "rd":
                    ApplyAppearanceSnapshot(payload);
                    break;
                case "rc":
                    ApplyRemoteChange(payload);
                    break;
                case "ru":
                {
                    long id = ParseId(payload?["u"]);
                    string emote = ApiClient.ReadString(payload?["e"]);
                    if (id > 0 && id != SelfId) MemberEmote?.Invoke(id, emote);
                    break;
                }
                case "fd":
                {
                    int code = ApiClient.ReadInt(payload?["c"]);
                    _reconnectSuppressed = true;
                    State = "idle";
                    Members.Clear();
                    ForcedDisconnect?.Invoke(code);
                    StateChanged?.Invoke();
                    break;
                }
            }
        }
        catch (Exception ex) { Log.Write("房间事件处理失败 " + name + " " + ex.Message); }
    }

    void ApplyAppearanceSnapshot(JsonNode payload)
    {
        // rd ack 原版兼容三种形态：{c:0,l:[...]}、{c:0,d:[...]}、直接数组。
        JsonArray list = payload switch
        {
            JsonArray arr => arr,
            JsonObject o => o["l"] as JsonArray ?? o["d"] as JsonArray,
            _ => null
        };
        if (list == null) return;
        bool touched = false;
        foreach (var item in list)
        {
            if (item is not JsonObject e) continue;
            long id = ParseId(e["a"]);
            if (id <= 0 || id == SelfId) continue;
            // 与 rm 保持同一成员集合：rd 只补外观，不为快照外的人凭空建成员（否则会冒出幽灵桌宠）。
            if (!Members.TryGetValue(id, out var m)) continue;
            if (!string.IsNullOrEmpty(ApiClient.ReadString(e["n"])))
                m.Nickname = ApiClient.ReadString(e["n"]);
            ApplyAppearance(m, e);
            // 在线/离线以 rm 快照为准；rd 只提供外观，不能把离线成员标成在线。
            touched = true;
        }
        // 一次性通知：几十人的快照逐个触发会让每个订阅者各跑一遍全量刷新。
        if (touched) RosterChanged?.Invoke();
        // 成员增删以 rm/rj/rl 为准；rd 可能只包含有外观的成员，不能据此移除其他人。
    }

    void ApplyRemoteChange(JsonNode payload)
    {
        if (payload is not JsonObject o) return;
        // 形态二：{o:1,a:id} 重连
        if (ApiClient.ReadInt(o["o"]) == 1)
        {
            long rid = ParseId(o["a"]);
            if (rid > 0 && Members.TryGetValue(rid, out var rm)) { rm.Online = true; MemberUpdated?.Invoke(rid); }
            _ = _socket?.EmitAsync("rd", new JsonObject { ["r"] = RoomNo }, 3000);
            return;
        }
        // 形态二：{a:"id1|id2|..."} 是服务端批量动作包。官方 1.0.3 会按 | 拆分，
        // 对每个成员各触发一次挥手；旧实现只解析单个数字 ID，批量包会被整包丢掉。
        if (o["a"] is JsonValue actionValue && actionValue.TryGetValue<string>(out var actionIds))
        {
            foreach (long actionId in ParseActionIds(actionIds))
                ApplyRemoteAction(actionId);
            return;
        }
        if (o["a"] is JsonArray actionArray)
        {
            foreach (var item in actionArray)
                if (item != null)
                {
                    long actionId = ParseId(item);
                    if (actionId > 0) ApplyRemoteAction(actionId);
                }
            return;
        }

        // 形态三：装扮广播 {a,n,b,c,d}。只有 a 的包表示“该成员按了一次键”，用来驱动远端手部动画。
        long id = ParseId(o["a"]);
        if (id > 0 && id != SelfId)
        {
            var m = Members.GetOrAdd(id, _ => NewMember(id));
            string nick = ApiClient.ReadString(o["n"]);
            bool nickChanged = !string.IsNullOrEmpty(nick) && nick != m.Nickname;
            if (nickChanged) m.Nickname = nick;
            bool hasAppearance = o.ContainsKey("b") || o.ContainsKey("c");
            ApplyAppearance(m, o);
            bool actionOnly = !hasAppearance && !o.ContainsKey("d");
            bool rosterChanged = hasAppearance || o.ContainsKey("d") || nickChanged || !m.Online;
            if (actionOnly)
            {
                m.HandLeft = !m.HandLeft;
                m.Action = m.HandLeft ? "left" : "right";
            }
            m.Online = true;
            // 按键动作每秒可能出现几十次：走轻量事件，避免触发面板全量房间推送和名册刷新。
            if (rosterChanged) MemberUpdated?.Invoke(id);
            else MemberAction?.Invoke(id, m.Action);
        }
    }

    static IEnumerable<long> ParseActionIds(string raw)
    {
        var seen = new HashSet<long>();
        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, out long id) || id <= 0 || !seen.Add(id)) continue;
            yield return id;
        }
    }

    void ApplyRemoteAction(long id)
    {
        if (id <= 0 || id == SelfId) return;
        if (!Members.TryGetValue(id, out var m)) return;
        bool wasOffline = !m.Online;
        m.Online = true;
        m.HandLeft = !m.HandLeft;
        m.Action = m.HandLeft ? "left" : "right";
        if (wasOffline) MemberUpdated?.Invoke(id);
        else MemberAction?.Invoke(id, m.Action);
    }

    /// <summary>只在字段实际存在时更新外观，避免动作包把 b/c 清空。</summary>
    static void ApplyAppearance(RoomMember m, JsonObject o)
    {
        if (o == null) return;
        if (o.ContainsKey("b")) m.RoleKey = ReadAssetKey(o["b"]);
        if (o.ContainsKey("c")) m.PartKey = ReadAssetKey(o["c"]);
        if (o.ContainsKey("d")) m.Action = ApiClient.ReadString(o["d"]);
    }

    /// <summary>本地每次按键/点击都发一次无载荷 rc，和官方客户端保持同样频率，不做丢包节流。</summary>
    public void NotifyLocalAction()
    {
        if (_socket?.Connected != true) return;
        _ = _socket.EmitNoAckAsync("rc");
    }

    public async Task SendEmoteAsync(long emoteId)
    {
        if (_socket?.Connected == true)
            await _socket.EmitNoAckAsync("re", new JsonObject { ["e"] = emoteId });
    }

    public async Task AnnounceAppearanceAsync()
    {
        if (_socket?.Connected != true) return;
        // 原版换装广播会带 {b,c}（数字 id），其他成员用它立即换皮；
        // 不带载荷的 rc 只用于“按键动作”。
        await _socket.EmitNoAckAsync("rc", new JsonObject
        {
            ["b"] = _cfg.CharacterAssetId,
            ["c"] = _cfg.AccessoryAssetId
        });
    }

    public void AddClicks(long n)
    {
        if (n <= 0) return;
        lock (_clicksLock) { _pendingClicks += n; }
        ClicksChanged?.Invoke();
        LocalClick?.Invoke();
    }

    /// <summary>
    /// 设置页“+10000”：对齐原版 Helper 暴露的本地点击数补充按钮。
    /// 先记入本地待上报，再立即尝试一次 cr；500ms 内只接受一次，避免连点造成保存风暴。
    /// </summary>
    public bool BoostLocalClicks(long delta)
    {
        if (delta != 10000) return false;
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastBoostMs);
        if (previous != 0 && now - previous < 500) return false;
        if (Interlocked.CompareExchange(ref _lastBoostMs, now, previous) != previous) return false;

        lock (_clicksLock) { _pendingClicks += delta; }
        ClicksChanged?.Invoke();
        // 与原版一致：按钮不等待服务器回包，避免网络慢时把设置页卡住。
        _ = FlushClicksAsync();
        return true;
    }

    /// <summary>
    /// user/me 刷新后同步服务器累计值与已消耗值。
    /// 原版区分两个数：cumulative（累计总额，上报/开箱用）与 available（可用余额 = 总额 − 已消耗）。
    /// </summary>
    public void SyncServerClicks(long total, long consume)
    {
        lock (_clicksLock)
        {
            total = Math.Max(0, total);
            consume = Math.Max(0, consume);
            // 服务器累计值只允许前进；账号切换由 App 调用 ResetClickState 单独重置。
            // 这样不会被晚到的旧 user/me 响应把显示数字拉回去。
            if (total >= _baseClicks)
            {
                // 这次同步如果已经包含正在上报的 cr，ack 回来时不能再加一次。
                if (_inflightClicks > 0 && total >= _baseClicks + _inflightClicks)
                    _inflightClicks = 0;
                _baseClicks = total;
            }
            _serverConsume = consume;
        }
        ClicksChanged?.Invoke();
    }

    internal Task EnterClickUploadAsync() => _clickUploadGate.WaitAsync();
    internal void ExitClickUpload() => _clickUploadGate.Release();

    internal readonly record struct ChestClicks(long Total, long Pending, long Consumed, long Epoch)
    {
        public long Available => Math.Max(0, Total - Consumed);
    }

    internal ChestClicks CaptureChestClicks()
    {
        lock (_clicksLock)
            return new ChestClicks(_baseClicks + _pendingClicks, _pendingClicks, _serverConsume, _clickEpoch);
    }

    internal void CompleteChestClicks(ChestClicks sent, long consume)
    {
        lock (_clicksLock)
        {
            if (sent.Epoch != _clickEpoch) return;
            // user/me may already reflect this receipt. Keep the newer server totals.
            _baseClicks = Math.Max(_baseClicks, sent.Total);
            _serverConsume = Math.Max(_serverConsume, sent.Consumed + consume);
            _pendingClicks -= Math.Min(_pendingClicks, sent.Pending);
        }
        ClicksChanged?.Invoke();
    }

    public void ResetPendingClicks()
    {
        lock (_clicksLock) { _pendingClicks = 0; }
        ClicksChanged?.Invoke();
    }

    /// <summary>登录用户变化时清空本地点击账本，避免新账号继承旧账号的累计值。</summary>
    public void ResetClickState()
    {
        lock (_clicksLock)
        {
            _clickEpoch++;
            _baseClicks = 0;
            _pendingClicks = 0;
            _inflightClicks = 0;
            _serverConsume = 0;
        }
        ClicksChanged?.Invoke();
    }

    /// <summary>立即尝试上报当前待上传点击；自动敲击按秒合并调用。</summary>
    public Task FlushClicksNowAsync() => FlushClicksAsync();

    /// <summary>--stress 压测：造 N 个假成员并标记为已进入房间（只影响本机显示，不联网）。</summary>
    public void DebugSeedMembers(int count, long skin, long part)
    {
        for (int k = 1; k <= count; k++)
        {
            long id = 900000 + k;
            Members[id] = new RoomMember
            {
                Id = id,
                Nickname = "压测" + k,
                RoleKey = skin.ToString(),
                PartKey = part.ToString(),
                Online = true,
            };
        }
        State = "joined";
        RosterChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>--stress 压测：模拟全房间同时按键一次的广播。</summary>
    public void DebugPulseMembers()
    {
        foreach (var pair in Members)
        {
            var m = pair.Value;
            m.HandLeft = !m.HandLeft;
            m.Action = m.HandLeft ? "left" : "right";
            MemberAction?.Invoke(m.Id, m.Action);
        }
    }

    async Task FlushClicksAsync()
    {
        if (!await _clickUploadGate.WaitAsync(0)) return;
        long sent = 0, gross = 0;
        try
        {
            var socket = _socket;
            lock (_clicksLock)
            {
                if (socket?.Connected != true || _pendingClicks <= 0) return;
                sent = _pendingClicks;
                _pendingClicks = 0;
                _inflightClicks = sent;
                gross = _baseClicks + sent;
            }

            bool success = false;
            try
            {
                var ack = await socket.EmitAsync("cr", new JsonObject { ["num"] = gross });
                int code = ack is JsonObject o ? ApiClient.ReadInt(o["c"]) : -1;
                success = code == 0 || code == 200;
                if (!success)
                {
                    // 80013 是官方客户端会静默忽略的错误（通常是上报频率/风控），
                    // 保留待上报增量并在下个 30 秒周期重试；日志限频，避免刷屏。
                    long now = Environment.TickCount64;
                    if (code != 80013 || now - Interlocked.Read(ref _lastRejectedClickLogMs) >= 300_000)
                    {
                        Interlocked.Exchange(ref _lastRejectedClickLogMs, now);
                        Log.Write($"点击上报被拒绝 c={code} num={gross} pending={sent} base={_baseClicks}");
                    }
                }
            }
            catch (Exception ex) { Log.Write("点击上报异常 " + ex.Message); }

            lock (_clicksLock)
            {
                if (_inflightClicks > 0)
                {
                    if (success) _baseClicks += _inflightClicks;
                    else _pendingClicks += _inflightClicks;
                    _inflightClicks = 0;
                }
            }
            ClicksChanged?.Invoke();
        }
        finally
        {
            ExitClickUpload();
        }
    }

    async Task CloseSocketAsync()
    {
        _generation++;
        var s = _socket;
        _socket = null;
        if (s != null)
        {
            try { s.Event -= OnEvent; } catch { }
            await s.CloseAsync();
            s.Dispose();
        }
    }

    static long ParseId(JsonNode node)
    {
        long id = ApiClient.ReadLong(node);
        if (id > 0) return id;
        string s = ApiClient.ReadString(node);
        return long.TryParse(s, out var v) ? v : 0;
    }

    static string ReadAssetKey(JsonNode node)
    {
        if (node == null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l.ToString();
            if (v.TryGetValue<string>(out var s)) return s;
        }
        return node.ToJsonString().Trim('"');
    }

    public void Dispose()
    {
        _reportTimer?.Dispose();
        _reconnectTimer?.Dispose();
        _ = CloseSocketAsync();
    }
}
