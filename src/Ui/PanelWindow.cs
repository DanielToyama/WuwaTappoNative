using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WuwatappoNative.Core;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;

namespace WuwatappoNative.Ui;

/// <summary>
/// 主面板：WebView2 承载本地 HTML/CSS，与原生层用 postMessage 通信。
/// 页面本身不联网取接口，全部数据走 Bridge，避免把 token 暴露给网页。
/// </summary>
public sealed class PanelWindow : Form
{
    readonly App _app;
    WebView2 _web;
    bool _ready;
    bool _fitting;
    bool _detached;

    public PanelWindow(App app)
    {
        _app = app;
        Text = "Wuwatappo";
        // 不设 Icon 时任务栏/Alt+Tab 会显示 WinForms 默认图标。
        Icon = AppIcon.Panel;
        FormBorderStyle = FormBorderStyle.None;
        // 面板尺寸由下面自己按“桌宠所在屏幕”的 DPI/工作区计算，避免 WinForms 再缩放一次。
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1120, 780);
        BackColor = Color.FromArgb(251, 237, 213);
        ShowInTaskbar = true;
        DoubleBuffered = true;
        BuildWebView();

        app.Room.StateChanged += OnRoomStateChanged;
        // 人数多时名册/外观事件是成批来的：合并到下一次消息循环再推一次，
        // 否则 40 人的房间每次按键都会序列化 40 份完整房间状态发给 WebView。
        app.Room.MemberUpdated += OnMemberListChanged;
        app.Room.MemberRemoved += OnMemberListChanged;
        app.Room.RosterChanged += QueuePushRoom;
        app.Auth.Changed += OnAuthChanged;
    }

    void OnRoomStateChanged() => SafePushRoom();
    void OnMemberListChanged(long _) => QueuePushRoom();
    void OnAuthChanged() => SafePushAuth();

    /// <summary>关闭面板时解除 App 的事件订阅并释放 WebView2，否则 Hide() 会一直占着整个浏览器实例。</summary>
    void Detach()
    {
        if (_detached) return;
        _detached = true;
        _ready = false;
        try { _app.Room.StateChanged -= OnRoomStateChanged; } catch { }
        try { _app.Room.MemberUpdated -= OnMemberListChanged; } catch { }
        try { _app.Room.MemberRemoved -= OnMemberListChanged; } catch { }
        try { _app.Room.RosterChanged -= QueuePushRoom; } catch { }
        try { _app.Auth.Changed -= OnAuthChanged; } catch { }
        try { _web?.Dispose(); } catch { }
        _web = null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Detach();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Detach();
        base.Dispose(disposing);
    }

    /// <summary>
    /// 按桌面工作区适配面板。以 1120×780 DIP 为设计尺寸，乘当前显示器 DPI 后
    /// 再夹到工作区；小屏幕/高缩放下不会比屏幕还大。宽高上限与间距使用 DIP，
    /// 保证 1366×768、1920×1080@150% 等环境仍能完整看到标题栏和内容。
    /// </summary>
    void FitToWorkingArea(Screen screen = null, int? dpi = null, bool center = true)
    {
        if (!IsHandleCreated || _fitting) return;
        _fitting = true;
        try
        {
            screen ??= _app?.Pet != null ? Screen.FromControl(_app.Pet) : Screen.FromControl(this);
            int targetDpi = dpi ?? _app?.Pet?.DeviceDpi ?? DeviceDpi;
            float scale = Math.Clamp(targetDpi / 96f, 0.75f, 3f);
            var wa = screen.WorkingArea;
            int margin = Math.Max(8, (int)Math.Round(24 * scale));
            int availW = Math.Max(1, wa.Width - margin * 2);
            int availH = Math.Max(1, wa.Height - margin * 2);
            int targetW = Math.Min((int)Math.Round(1120 * scale), availW);
            int targetH = Math.Min((int)Math.Round(780 * scale), availH);
            // 保留一个能正常操作的下限，但不得超出当前屏幕。
            targetW = Math.Max(targetW, Math.Min(availW, (int)Math.Round(640 * scale)));
            targetH = Math.Max(targetH, Math.Min(availH, (int)Math.Round(480 * scale)));
            targetW = Math.Min(targetW, availW);
            targetH = Math.Min(targetH, availH);

            var target = new Size(targetW, targetH);
            if (Size != target) Size = target;

            int x, y;
            if (center)
            {
                x = wa.Left + Math.Max(0, (wa.Width - targetW) / 2);
                y = wa.Top + Math.Max(0, (wa.Height - targetH) / 2);
            }
            else
            {
                int cx = Left + Width / 2, cy = Top + Height / 2;
                x = Math.Clamp(cx - targetW / 2, wa.Left, Math.Max(wa.Left, wa.Right - targetW));
                y = Math.Clamp(cy - targetH / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - targetH));
            }
            Location = new Point(x, y);
            ApplyRegion();
        }
        finally { _fitting = false; }
    }

    /// <summary>
    /// 打开面板并真正拿到前台焦点。桌宠窗口是 WS_EX_NOACTIVATE，从托盘/桌宠点开时
    /// 当前进程并不持有前台窗口，单纯 Show()+Activate() 会被 Windows 的前台锁定规则忽略。
    /// 这里把自己的输入队列临时附到当前前台线程，再 SetForegroundWindow/SetFocus。
    /// </summary>
    public void ShowAndFocus()
    {
        if (IsDisposed) return;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ForceForeground();
        try { _web?.Focus(); } catch { }
        // WebView2 初始化/布局可能晚一拍，下一轮消息循环再补一次焦点。
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            ForceForeground();
            try { _web?.Focus(); } catch { }
            try { _web?.CoreWebView2?.ExecuteScriptAsync("window.focus()"); } catch { }
        }));
    }

    void ForceForeground()
    {
        if (!IsHandleCreated) return;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint ownThread = GetWindowThreadProcessId(Handle, out _);
        bool attached = fgThread != 0 && fgThread != ownThread && AttachThreadInput(fgThread, ownThread, true);
        try
        {
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
            SetFocus(Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, ownThread, false);
        }
    }

    bool _pushRoomQueued;

    void QueuePushRoom()
    {
        if (_pushRoomQueued) return;
        _pushRoomQueued = true;
        Safe(() =>
        {
            _pushRoomQueued = false;
            Push("room", _app.RoomState());
        });
    }

    void SafePushRoom() => Safe(() => Push("room", _app.RoomState()));
    void SafePushAuth() => Safe(() => { _app.PushAuth(); Push("boot", BootPayload()); });

    /// <summary>让面板重新拉取 boot（设置/可见性等被快捷键改变时用）。</summary>
    public void PushBoot() => SafePushAuth();

    void Safe(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
    }

    public void Push(string evt, object data)
    {
        // 先切线程再碰 CoreWebView2：后台线程连读属性都会抛
        // “CoreWebView2 can only be accessed from the UI thread”。
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            Safe(() => Push(evt, data));
            return;
        }
        if (!_ready || _web?.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt, data })); } catch { }
    }

    async void BuildWebView()
    {
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(251, 237, 213) };
        Controls.Add(_web);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(Paths.CacheDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("wuwatappo.local", Paths.WwwRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (s, e) => _ = HandleMessageAsync(e.WebMessageAsJson);
            // 页面里的 http(s) 链接一律交给系统浏览器，避免面板内跳走丢不掉。
            core.NavigationStarting += (s, e) =>
            {
                if (e.Uri.StartsWith("https://wuwatappo.local", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationCompleted += (s, e) =>
            {
                _ready = e.IsSuccess;
                if (_ready) Push("boot", BootPayload());
            };
            // 带版本号查询串，避免升级后 WebView2 仍使用旧 index.html/app.js 缓存。
            string panelVersion = typeof(PanelWindow).Assembly.GetName().Version?.ToString(3) ?? "0";
            string url = "https://wuwatappo.local/index.html?v=" + Uri.EscapeDataString(panelVersion);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--demo") >= 0) url += "&demo=1";
            string page = ArgValue("--panel-page");
            string hash = "";
            if (!string.IsNullOrEmpty(page)) hash = "#page=" + page;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--edit-emotes") >= 0)
                hash += (hash.Length == 0 ? "#" : "&") + "editEmotes=1";
            core.Navigate(url + hash);
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 初始化失败 " + ex);
            MessageBox.Show("WebView2 初始化失败：\n" + ex.Message, "Wuwatappo");
        }
    }

    static void OpenExternal(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("打开外部链接失败 " + uri + " " + ex.Message); }
    }

    object BootPayload() => new
    {
        auth = new
        {
            loggedIn = _app.Auth.IsLoggedIn,
            nickname = _app.Auth.Nickname,
            cuid = _app.Auth.Cuid,
            uid = _app.Auth.UserId,
            clicks = _app.Room.AvailableClicks,
            clickTotal = _app.Room.DisplayClicks,
            clickConsume = _app.Room.ServerConsume,
            chestLeft = ApiClient.ReadLong(_app.Auth.UserInfo?["left_treasure_chest"]),
            chestRight = ApiClient.ReadLong(_app.Auth.UserInfo?["right_treasure_chest"]),
        },
        room = _app.RoomState(),
        disclaimerShown = _app.Cfg.DisclaimerShown,
        settings = new
        {
            scale = _app.Cfg.Scale,
            opacity = _app.Cfg.Opacity,
            topmost = _app.Cfg.Topmost,
            clickThrough = _app.Cfg.ClickThrough,
            hoverFade = _app.Cfg.HoverFade,
            keyboardMode = _app.Cfg.KeyboardMode,
            showMappingControls = _app.Cfg.ShowMappingControls,
            hideOtherPets = _app.Cfg.HideOtherPets,
            allowOverlap = _app.Cfg.AllowOverlap,
            captureMode = _app.Cfg.CaptureMode,
            captureWidth = _app.Cfg.CaptureWidth,
            captureHeight = _app.Cfg.CaptureHeight,
            alwaysShowChest = _app.Cfg.AlwaysShowChest,
            autoChest = _app.Cfg.AutoChest,
            notifyChestClaimed = _app.Cfg.NotifyChestClaimed,
            notifyHotkeys = _app.Cfg.NotifyHotkeys,
            launchAtLogin = AutoStart.IsEnabled,
            runAsAdmin = _app.Cfg.RunAsAdmin,
            isElevated = Elevation.IsElevated,
            roomNo = _app.Cfg.RoomNo,
            lastRoom = _app.Cfg.LastRoom,
            hotkeys = Hotkeys.FromSettings(_app.Cfg.Hotkeys),
        },
        appearance = new
        {
            characterAssetId = _app.Cfg.CharacterAssetId,
            accessoryAssetId = _app.Cfg.AccessoryAssetId,
            emoteAssetIds = _app.Cfg.EmoteAssetIds,
        },
        catalog = new
        {
            assetsHost = _app.Catalog.AssetsHost,
            skins = _app.Catalog.Count("character_skin"),
            accessories = _app.Catalog.Count("accessory"),
            emotes = _app.Catalog.Count("emote"),
        },
        version = typeof(PanelWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.1",
    };

    // ---------------- Bridge ----------------

    async Task HandleMessageAsync(string json)
    {
        int id = 0;
        try
        {
            var msg = JsonNode.Parse(json) as JsonObject;
            if (msg == null) return;
            id = (int)ApiClient.ReadLong(msg["id"]);
            string cmd = ApiClient.ReadString(msg["cmd"]);
            var data = msg["data"] as JsonObject ?? new JsonObject();
            object result = await DispatchAsync(cmd, data);
            Reply(id, true, result, null);
        }
        catch (ApiException ex)
        {
            Log.Write("命令失败 " + ex.Code + " " + ex.Message);
            Reply(id, false, null, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("命令异常 " + ex);
            Reply(id, false, null, ex.Message);
        }
    }

    void Reply(int id, bool ok, object data, string error)
    {
        if (!_ready || _web?.CoreWebView2 == null) return;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, data, error }));
        }
        catch { }
    }

    async Task<object> DispatchAsync(string cmd, JsonObject data)
    {
        var auth = _app.Auth;
        var cfg = _app.Cfg;
        switch (cmd)
        {
            case "win.drag":
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 2, 0);
                return true;
            case "win.min":
                WindowState = FormWindowState.Minimized;
                return true;
            case "win.close":
                // 不在 WebView2 的消息处理栈里直接 Dispose，先排到下一轮消息循环。
                BeginInvoke(new Action(Close));
                return true;

            case "ui.disclaimerShown":
                if (!_app.Cfg.DisclaimerShown)
                {
                    _app.Cfg.DisclaimerShown = true;
                    ConfigStore.Save(_app.Cfg);
                }
                return true;

            case "boot":
                return BootPayload();

            case "login.sendCode":
                await auth.SendUidCodeAsync(ApiClient.ReadString(data["uid"]));
                return new { sent = true };

            case "login.submit":
                await auth.LoginWithUidAsync(ApiClient.ReadString(data["uid"]), ApiClient.ReadString(data["code"]));
                _app.Pet.BeginInvoke(new Action(() => _app.Pet.ApplySettings()));
                await _app.Room.EnsureLobbyAsync();
                await _app.ResumeLastRoomAsync();
                _app.CheckAutoChestNow();
                return new { loggedIn = true, nickname = auth.Nickname };

            case "login.token":
                auth.SetTokenManually(ApiClient.ReadString(data["token"]));
                if (await auth.TryRestoreAsync())
                {
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.ApplySettings()));
                    await _app.Room.EnsureLobbyAsync();
                    await _app.ResumeLastRoomAsync();
                    _app.CheckAutoChestNow();
                    return new { loggedIn = true, nickname = auth.Nickname };
                }
                throw new ApiException(-1, "token 无效或已过期");

            case "token.inherit":
            {
                var extracted = await Task.Run(() =>
                {
                    var clients = OfficialTokenReader.FindClientProcesses();
                    try
                    {
                        if (clients.Count > 0)
                            OfficialTokenReader.CloseClientProcesses(clients, Log.Write);
                    }
                    finally
                    {
                        foreach (var process in clients) process.Dispose();
                    }
                    return OfficialTokenReader.Extract(Log.Write);
                });

                var token = extracted.PreferredToken;
                if (token == null || string.IsNullOrWhiteSpace(token.Value))
                {
                    if (extracted.SawV20)
                        throw new ApiException(-1, "官方客户端使用了 v20 App-Bound Encryption，无法读取 token，请改用 UID + 验证码登录。");
                    if (extracted.TokenDecryptFailed)
                        throw new ApiException(-1, "找到 token 但解密失败；请确认官方客户端与本程序使用同一 Windows 用户，并在客户端重新登录后再试。");
                    throw new ApiException(-1, "官方客户端中没有找到登录 token，请先在官方客户端完成登录。");
                }

                auth.SetTokenManually(token.Value);
                if (!await auth.TryRestoreAsync())
                    throw new ApiException(-1, "读取到 token，但服务器验证失败；请重新登录官方客户端后再试。");

                _app.Pet.BeginInvoke(new Action(() => _app.Pet.ApplySettings()));
                await _app.Room.EnsureLobbyAsync();
                await _app.ResumeLastRoomAsync();
                _app.CheckAutoChestNow();
                return new { loggedIn = true, nickname = auth.Nickname };
            }

            case "logout":
                await auth.LogoutAsync();
                await _app.Room.LeaveAsync();
                return new { loggedIn = false };

            case "user.refresh":
                await auth.WhoAmIAsync();
                return BootPayload();

            case "clicks.boost":
            {
                if (!auth.IsLoggedIn) throw new ApiException(-1, "请先登录后再补充点击数");
                long delta = ApiClient.ReadLong(data["delta"]);
                if (delta != 10000) throw new ApiException(-1, "仅支持增加 10000 点击");
                bool accepted = _app.Room.BoostLocalClicks(delta);
                return new
                {
                    accepted,
                    available = _app.Room.AvailableClicks,
                    total = _app.Room.DisplayClicks,
                    consume = _app.Room.ServerConsume,
                };
            }

            case "bag.list":
                return await LoadBagAsync();

            case "catalog.list":
                return CatalogList(ApiClient.ReadString(data["type"]));

            case "atlas.list":
                return await LoadAtlasAsync(ApiClient.ReadString(data["type"]));

            case "appearance.set":
            {
                string type = ApiClient.ReadString(data["type"]);
                long value = ApiClient.ReadLong(data["id"]);
                if (type == "character_skin") cfg.CharacterAssetId = value;
                else if (type == "accessory") cfg.AccessoryAssetId = value;
                else if (type == "emote")
                {
                    var slots = cfg.EmoteAssetIds ?? new long[5];
                    int slot = Array.FindIndex(slots, v => v <= 0);
                    if (slot < 0) throw new ApiException(-1, "表情栏已满，请在“编辑表情栏”里调整槽位");
                    slots[slot] = value;
                    cfg.EmoteAssetIds = slots;
                    cfg.LocalEmoteSlots = true;
                }
                if (type != "emote") cfg.LocalAppearance = true;
                ConfigStore.Save(cfg);
                Log.Write($"外观切换 type={type} id={value}");
                if (type == "emote")
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.OnEmoteSlotsChanged()));
                else
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.ReloadAppearance()));
                await _app.Room.AnnounceAppearanceAsync();
                return BootPayload();
            }

            case "appearance.applyLocal":
            {
                string type = ApiClient.ReadString(data["type"]);
                long id = ApiClient.ReadLong(data["id"]);
                if (id <= 0) throw new ApiException(-1, "素材无效");
                if (type == "character_skin")
                {
                    cfg.CharacterAssetId = id;
                    cfg.LocalAppearance = true;
                    ConfigStore.Save(cfg);
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.ReloadAppearance()));
                }
                else if (type == "accessory")
                {
                    cfg.AccessoryAssetId = id;
                    cfg.LocalAppearance = true;
                    ConfigStore.Save(cfg);
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.ReloadAppearance()));
                }
                else if (type == "emote")
                {
                    var slots = cfg.EmoteAssetIds ?? new long[5];
                    int slot = (int)ApiClient.ReadLong(data["slot"]);
                    if (slot < -1 || slot >= slots.Length) slot = -1;
                    if (slot >= 0)
                    {
                        slots[slot] = id;
                    }
                    else
                    {
                        slot = Array.FindIndex(slots, v => v <= 0);
                        if (slot < 0) throw new ApiException(-1, "表情栏已满，请先在编辑器中调整槽位");
                        slots[slot] = id;
                    }
                    cfg.EmoteAssetIds = slots;
                    cfg.LocalEmoteSlots = true;
                    ConfigStore.Save(cfg);
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.OnEmoteSlotsChanged()));
                }
                else throw new ApiException(-1, "未知类型");
                Log.Write($"应用本地外观 type={type} id={id}（不上传）");
                return BootPayload();
            }

            case "appearance.setSlot":
            {
                int slot = (int)ApiClient.ReadLong(data["slot"]);
                long id = ApiClient.ReadLong(data["id"]);
                var slots = cfg.EmoteAssetIds ?? new long[5];
                if (slot < 0 || slot >= slots.Length) throw new ApiException(-1, "槽位无效");
                slots[slot] = id;
                cfg.EmoteAssetIds = slots;
                cfg.LocalEmoteSlots = true;
                ConfigStore.Save(cfg);
                Log.Write($"表情槽位 {slot} = {id}（仅本地）");
                _app.Pet.BeginInvoke(new Action(() => _app.Pet.OnEmoteSlotsChanged()));
                return BootPayload();
            }

            case "appearance.sync":
            {
                var serverEmotes = new List<long>();
                if (_app.Auth.UserInfo?["emote"] is JsonArray serverEmoteArr)
                    foreach (var e in serverEmoteArr)
                    {
                        long eid = ApiClient.ReadLong(e?["id"]);
                        if (eid > 0) serverEmotes.Add(eid);
                    }
                await auth.Api.PutAsync("api/frontend/user/me/appearance", new
                {
                    character_asset_id = cfg.CharacterAssetId,
                    accessory_asset_id = cfg.AccessoryAssetId,
                    // 表情栏只存本地；云端保留服务器现有表情，避免跨角色被拒
                    emote_asset_ids = serverEmotes.ToArray(),
                });
                cfg.LocalAppearance = false;
                ConfigStore.Save(cfg);
                await auth.WhoAmIAsync();
                await _app.Room.AnnounceAppearanceAsync();
                return new { synced = true };
            }

            case "room.create":
                await _app.Room.CreateAsync();
                return _app.RoomState();
            case "room.join":
                await _app.Room.JoinAsync(ApiClient.ReadString(data["roomNo"]));
                return _app.RoomState();
            case "room.leave":
                await _app.Room.LeaveAsync();
                return _app.RoomState();
            case "room.state":
                return _app.RoomState();
            case "room.hideOthers":
            {
                // 总开关只影响“是否显示其他成员”，不要覆盖每个成员自己的显示状态，
                // 否则关掉总开关会把用户单独隐藏的人全部放出来。
                bool hide = ApiClient.ReadBool(data["hide"]);
                cfg.HideOtherPets = hide;
                ConfigStore.Save(cfg);
                _app.Pet.BeginInvoke(new Action(() => { _app.Pet.ApplySettings(); _app.Pet.RefreshMembers(); }));
                return _app.RoomState();
            }

            case "room.hideMember":
            {
                long memberId = ApiClient.ReadLong(data["id"]);
                bool hide = ApiClient.ReadBool(data["hide"]);
                if (_app.Room.Members.TryGetValue(memberId, out var member))
                {
                    member.Visible = !hide;
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.RefreshMembers()));
                }
                return _app.RoomState();
            }

            case "room.resetOrder":
                _app.Pet.BeginInvoke(new Action(_app.Pet.ResetCatOrder));
                return new { ok = true };

            case "emote.send":
            {
                long emoteId = ApiClient.ReadLong(data["id"]);
                await _app.Room.SendEmoteAsync(emoteId);
                return new { sent = true };
            }

            case "hotkeys.save":
            {
                // 前端传完整的 {动作id: 组合键} 表；服务端做规范化和冲突检查，
                // 保存后立刻重注册，不需要重启程序。
                var next = new System.Collections.Generic.Dictionary<string, string>();
                if (data["hotkeys"] is JsonObject obj)
                {
                    foreach (var kv in obj)
                        next[kv.Key] = ApiClient.ReadString(kv.Value);
                }
                var normalized = Hotkeys.FromSettings(next);
                var used = new Dictionary<string, string>();
                foreach (var (id, label, _) in Hotkeys.Actions)
                {
                    string value = normalized.GetValueOrDefault(id, "");
                    if (string.IsNullOrEmpty(value)) continue;
                    if (used.TryGetValue(value, out var other))
                        return new { ok = false, error = $"{label} 与 {other} 冲突：{value}" };
                    used[value] = label;
                }
                _app.Cfg.Hotkeys = normalized;
                ConfigStore.Save(_app.Cfg);
                Log.Write("快捷键已保存 " + string.Join(" ", normalized.Select(kv => kv.Key + "=" + kv.Value)));
                _app.Pet.BeginInvoke(new Action(_app.Pet.RebindHotkeys));
                return new { ok = true, hotkeys = normalized };
            }

            case "settings.set":
            {
                bool hideOthersChanged = false;
                foreach (var kv in data)
                {
                    switch (kv.Key)
                    {
                        case "scale": cfg.Scale = ReadDouble(kv.Value); break;
                        case "opacity": cfg.Opacity = ReadDouble(kv.Value); break;
                        case "topmost": cfg.Topmost = ApiClient.ReadBool(kv.Value); break;
                        case "clickThrough": cfg.ClickThrough = ApiClient.ReadBool(kv.Value); break;
                        case "hoverFade": cfg.HoverFade = ApiClient.ReadBool(kv.Value); break;
                        case "keyboardMode": cfg.KeyboardMode = ApiClient.ReadString(kv.Value); break;
                        case "showMappingControls": cfg.ShowMappingControls = ApiClient.ReadBool(kv.Value); break;
                        case "hideOtherPets":
                            cfg.HideOtherPets = ApiClient.ReadBool(kv.Value);
                            hideOthersChanged = true;
                            break;
                        case "allowOverlap": cfg.AllowOverlap = ApiClient.ReadBool(kv.Value); break;
                        case "captureMode": cfg.CaptureMode = ApiClient.ReadBool(kv.Value); break;
                        case "captureWidth": cfg.CaptureWidth = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 160, 1600); break;
                        case "captureHeight": cfg.CaptureHeight = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 160, 1600); break;
                        case "alwaysShowChest": cfg.AlwaysShowChest = ApiClient.ReadBool(kv.Value); break;
                        case "autoChest":
                            cfg.AutoChest = ApiClient.ReadBool(kv.Value);
                            if (cfg.AutoChest) _app.CheckAutoChestNow();
                            break;
                        case "notifyChestClaimed": cfg.NotifyChestClaimed = ApiClient.ReadBool(kv.Value); break;
                        case "notifyHotkeys": cfg.NotifyHotkeys = ApiClient.ReadBool(kv.Value); break;
                        case "launchAtLogin":
                            cfg.LaunchAtLogin = AutoStart.Apply(ApiClient.ReadBool(kv.Value));
                            break;
                        case "runAsAdmin":
                        {
                            bool enable = ApiClient.ReadBool(kv.Value);
                            if (enable && !Elevation.IsElevated)
                            {
                                // 先释放单实例锁，否则提权后的新进程会把自己当成第二个实例直接退出。
                                SingleInstance.ReleaseForRelaunch();
                                // UAC 取消时保持普通权限，并把开关改回关闭，避免设置与实际权限不一致。
                                if (!Elevation.TryRestartElevated(reopenPanel: true))
                                {
                                    cfg.RunAsAdmin = false;
                                    if (SingleInstance.Acquire())
                                        SingleInstance.SetActivationHandler(() => _app.RequestPanelFromOtherInstance());
                                    break;
                                }
                                cfg.RunAsAdmin = true;
                                ConfigStore.Save(cfg);
                                _app.ScheduleRestartExit();
                                return BootPayload();
                            }
                            cfg.RunAsAdmin = enable;
                            break;
                        }
                    }
                }
                cfg.Opacity = Math.Clamp(cfg.Opacity, 0.2, 1.0);
                cfg.Scale = Math.Clamp(cfg.Scale, 0.25, 2.0);
                cfg.CaptureWidth = Math.Clamp(cfg.CaptureWidth, 160, 1600);
                cfg.CaptureHeight = Math.Clamp(cfg.CaptureHeight, 160, 1600);
                ConfigStore.Save(cfg);
                _app.Pet.BeginInvoke(new Action(() =>
                {
                    _app.Pet.ApplySettings();
                    _app.Pet.ApplyCaptureMode();
                    if (!cfg.AllowOverlap) _app.Pet.ResolveOverlaps();
                    // “隐藏其他成员”开关必须立刻重建/移除联机桌宠，不能等下一次房间事件。
                    if (hideOthersChanged) _app.Pet.RefreshMembers();
                }));
                _app.Pet.BeginInvoke(new Action(_app.ApplyKeyboardMode));
                return BootPayload();
            }

            case "chest.open":
            {
                string place = ApiClient.ReadString(data["place"]);
                return await _app.OpenChestAsync(place, false);
            }

            case "synthesize":
            {
                var ids = new List<long>();
                if (data["ids"] is JsonArray arr) foreach (var n in arr) ids.Add(ApiClient.ReadLong(n));
                bool same = ApiClient.ReadBool(data["sameLevel"]);
                string path = same ? "api/frontend/assets/synthesis/same-level" : "api/frontend/assets/synthesis";
                var resp = await auth.Api.PostAsync(path, new { asset_ids = ids.ToArray() });
                return resp;
            }

            case "favorite":
            {
                long assetId = ApiClient.ReadLong(data["id"]);
                bool on = ApiClient.ReadBool(data["on"]);
                if (on) await auth.Api.PostAsync("api/frontend/assets/favorites", new { asset_id = assetId });
                else await auth.Api.PostAsync("api/frontend/assets/favorites/cancel", new { asset_id = assetId });
                return new { favorite = on };
            }

            case "redeem":
                await auth.Api.PostAsync("api/frontend/redeems/use", new { code = ApiClient.ReadString(data["code"]) });
                return new { ok = true };

            case "presets.list":
                return await auth.Api.GetAsync("api/frontend/presets");

            case "presets.create":
                return await auth.Api.PostAsync("api/frontend/presets", new
                {
                    character_asset_id = cfg.CharacterAssetId,
                    accessory_asset_id = cfg.AccessoryAssetId,
                    emote_asset_ids = cfg.EmoteAssetIds ?? Array.Empty<long>(),
                });

            case "presets.delete":
                return await auth.Api.DeleteAsync("api/frontend/presets", new { preset_id = ApiClient.ReadLong(data["id"]) });

            case "open.log":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + Paths.LogFile + "\""));
                return true;
        }
        throw new ApiException(-1, "未知命令 " + cmd);
    }

    static double ReadDouble(JsonNode n)
    {
        if (n == null) return 0;
        try { return n.GetValue<double>(); } catch { return 0; }
    }

    async Task<object> LoadBagAsync()
    {
        var auth = _app.Auth;
        if (!auth.IsLoggedIn) throw new ApiException(-1, "请先登录");
        var data = await auth.Api.PostAsync("api/frontend/bags/list");
        var list = new List<object>();
        if (data is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                long id = ApiClient.ReadLong(o["asset_id"]);
                string type = ApiClient.ReadString(o["asset_type"]);
                string assetName = ApiClient.ReadString(o["asset_name"]);
                string rarity = ApiClient.ReadString(o["rarity"]).ToLowerInvariant();
                if (rarity == "") rarity = _app.Catalog.Rarities.TryGetValue(id, out var r) ? r : "common";
                string display = _app.Catalog.DisplayName(type, assetName);
                if (string.IsNullOrEmpty(display) && _app.Catalog.Names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n))
                    display = n;
                if (string.IsNullOrEmpty(display)) display = StripExt(assetName);
                string series = _app.Catalog.SeriesName(type, assetName);
                string thumb = _app.Catalog.ThumbUrl(type, assetName);
                list.Add(new
                {
                    id,
                    type,
                    assetName,
                    name = display,
                    series,
                    rarity,
                    roleId = ApiClient.ReadString(o["role_id"]),
                    quantity = ApiClient.ReadLong(o["quantity"]),
                    isCollect = ApiClient.ReadBool(o["is_collect"]),
                    thumb,
                });
            }
        }
        return new { items = list, count = list.Count };
    }

    object CatalogList(string type)
    {
        var catalog = _app.Catalog;
        if (string.IsNullOrEmpty(type)) type = "character_skin";
        string atlasType = type switch
        {
            "character_skin" => "character_skin_atlas",
            "accessory" => "accessory_atlas",
            "emote" => "emote_atlas",
            _ => type
        };
        var atlasByMain = new Dictionary<long, string>();
        foreach (var a in catalog.All(atlasType))
            if (a.HeightLightId > 0 && !atlasByMain.ContainsKey(a.HeightLightId))
                atlasByMain[a.HeightLightId] = catalog.CdnUrl(a.Url);

        var items = catalog.All(type).Select(e => new
        {
            id = e.Id,
            type = e.Type,
            name = catalog.Names.TryGetValue(e.Id, out var n) && !string.IsNullOrEmpty(n) ? n : e.Id.ToString(),
            series = catalog.Series.TryGetValue(e.Id, out var s) ? s : "",
            rarity = catalog.Rarities.TryGetValue(e.Id, out var r) ? r : "common",
            thumb = atlasByMain.TryGetValue(e.Id, out var t) ? t : catalog.CdnUrl(e.Url),
            gameRoleId = e.GameRoleId,
        }).ToList();
        return new { items, count = items.Count, assetsHost = catalog.AssetsHost };
    }

    async Task<object> LoadAtlasAsync(string requested)
    {
        var data = await _app.Auth.Api.GetAsync("api/common/atlas");
        var items = new List<object>();
        if (data is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                string atlasType = ApiClient.ReadString(o["asset_type"]);
                if (!atlasType.EndsWith("_atlas", StringComparison.Ordinal)) continue;
                string baseType = atlasType.Substring(0, atlasType.Length - "_atlas".Length);
                if (!string.IsNullOrEmpty(requested) && baseType != requested) continue;
                string assetName = ApiClient.ReadString(o["asset_name"]);
                string name = _app.Catalog.DisplayName(baseType, assetName);
                if (string.IsNullOrEmpty(name)) name = StripExt(assetName);
                items.Add(new
                {
                    id = ApiClient.ReadLong(o["asset_id"]),
                    mainId = _app.Catalog.ResolveId(baseType, assetName),
                    type = baseType,
                    assetName,
                    name,
                    rarity = ApiClient.ReadString(o["rarity"]).ToLowerInvariant(),
                    series = _app.Catalog.SeriesName(baseType, assetName),
                    thumb = _app.Catalog.AtlasThumb(atlasType, ApiClient.ReadLong(o["asset_id"])),
                });
            }
        }
        return new { items, count = items.Count };
    }

    static string StripExt(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int i = name.LastIndexOf('.');
        return i > 0 ? name.Substring(0, i) : name;
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitToWorkingArea(center: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToWorkingArea(Screen.FromControl(this), e.DeviceDpiNew, center: false);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x007E) // WM_DISPLAYCHANGE：分辨率/缩放布局改变后重新夹紧
        {
            try { BeginInvoke(new Action(() => FitToWorkingArea(Screen.FromControl(this), DeviceDpi, center: false))); } catch { }
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
    }

    void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = PetComposer.Rounded(new RectangleF(0, 0, Width, Height), 18);
        Region = new Region(path);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
}
