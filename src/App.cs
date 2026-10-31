using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;
using WuwatappoNative.Ui;

namespace WuwatappoNative;

public sealed class App : ApplicationContext
{
    public Settings Cfg { get; }
    public static volatile bool UiInputFocused;
    public AuthService Auth { get; }
    public Catalog Catalog { get; } = new();
    public AssetStore Store { get; } = new();
    public RoomManager Room { get; }
    public PetWindow Pet { get; }
    public KeyboardWindow Keyboard { get; }
    public PanelWindow Panel { get; private set; }
    System.Threading.Timer _autoChestTimer;
    System.Threading.Timer _clickRefreshTimer;
    DateTime _nextLeftAttempt = DateTime.MinValue, _nextRightAttempt = DateTime.MinValue;
    int _chestBusy;

    public App()
    {
        Cfg = ConfigStore.Load();
        Auth = new AuthService(Cfg);
        Room = new RoomManager(Auth, Cfg);
        Pet = new PetWindow(Cfg, Auth, Catalog, Store, Room);
        Keyboard = new KeyboardWindow();

        Pet.OpenPanelRequested += OpenPanel;
        Pet.ChestOpenRequested += place => _ = OpenChestSafeAsync(place);
        Pet.KeyboardModeChanged += ApplyKeyboardMode;
        Pet.PetVisibilityChanged += visible =>
        {
            if (!visible) Keyboard.Hide();
            else if (Cfg.KeyboardMode != "hidden")
            {
                Keyboard.SetMode(KeyboardWindow.Mode.Hidden);
                ApplyKeyboardMode();
            }
        };
        Auth.Changed += () =>
        {
            if (Auth.IsLoggedIn) Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
            else Room.ResetPendingClicks();
        };
        Room.ClicksChanged += () => Panel?.Push("clicks", new
        {
            available = Room.AvailableClicks,
            total = Room.DisplayClicks,
            consume = Room.ServerConsume
        });
        Pet.GeometryChanged += LayoutKeyboard;
        Pet.RefreshRequested += () => _ = RefreshEverythingAsync();
        // 快捷键/托盘菜单改了窗口设置后，面板里的开关要跟着变，否则界面显示和实际状态不一致。
        Pet.SettingsChanged += () => { try { Panel?.PushBoot(); } catch { } };
        // 手柄按键和键盘按键走同一条链路：计数、广播、挥手。
        Keyboard.PadButtonDown += () => Pet.BeginInvoke(new Action(Pet.GamepadTap));
        Keyboard.PadButtonUp += () => Pet.BeginInvoke(new Action(Pet.GamepadRelease));
        Pet.FormClosed += (s, e) => ExitThread();

        Pet.Show();
        ApplyKeyboardMode();
        Keyboard.ModeRequested += mode =>
        {
            Cfg.KeyboardMode = mode switch
            {
                KeyboardWindow.Mode.Letters => "letters",
                KeyboardWindow.Mode.Wuwa => "wuwa",
                KeyboardWindow.Mode.Controller => "controller",
                _ => "hidden"
            };
            ConfigStore.Save(Cfg);
            ApplyKeyboardMode();
            Pet.RefreshHud();
        };
        Keyboard.CloseRequested += () =>
        {
            Cfg.KeyboardMode = "hidden";
            ConfigStore.Save(Cfg);
            ApplyKeyboardMode();
            Pet.RefreshHud();
        };
        Log.Write("桌宠窗口已创建");

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--panel") >= 0)
            Pet.BeginInvoke(new Action(OpenPanel));

        _ = BootAsync();
        _autoChestTimer = new System.Threading.Timer(_ => _ = AutoChestTickAsync(), null, 30000, 30000);
        _clickRefreshTimer = new System.Threading.Timer(_ => _ = RefreshClicksAsync(), null, 60000, 60000);
        StartStressHarness();
    }

    bool _stressMode;

    /// <summary>F5：重新拉取目录/外观/房间等数据，等价于原版的“刷新界面”。</summary>
    public async Task RefreshEverythingAsync()
    {
        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
            Pet.BeginInvoke(new Action(() => { Pet.ReloadAppearance(); Pet.RefreshMembers(); }));
            if (Auth.IsLoggedIn)
            {
                await Auth.WhoAmIAsync();
                Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
                Pet.BeginInvoke(new Action(Pet.RefreshHud));
                if (Panel != null && Panel.IsHandleCreated) Panel.BeginInvoke(new Action(PushAuth));
            }
            PushRoom();
        }
        catch (Exception ex) { Log.Write("刷新失败 " + ex.Message); }
    }

    /// <summary>人数多场景的压测入口：--stress N [--stress-hz H]
    /// 只在本机伪造 N 个成员并按 H 次/秒模拟全员按键，不联网、不影响服务器房间。
    /// </summary>
    void StartStressHarness()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--stress");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int count) || count <= 0) return;
        int hz = 10;
        int j = Array.IndexOf(args, "--stress-hz");
        if (j >= 0 && j + 1 < args.Length && int.TryParse(args[j + 1], out int v) && v > 0) hz = v;
        _stressMode = true;
        Room.DebugSeedMembers(count, Cfg.CharacterAssetId, Cfg.AccessoryAssetId);
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(8, 1000 / hz) };
        timer.Tick += (s, e) => Room.DebugPulseMembers();
        timer.Start();
        Log.Write($"压测模式：成员={count} 频率={hz}Hz");
    }

    /// <summary>
    /// 原版会周期性刷新 user/me：宝箱消耗与点击数都来自服务器，只靠本地累加会逐渐对不上。
    /// 这里每 60s 拉一次，只同步运行时字段，不动本地外观。
    /// </summary>
    async Task RefreshClicksAsync()
    {
        try
        {
            if (!Auth.IsLoggedIn) return;
            await Auth.RefreshClicksAsync();
            Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
            Pet.BeginInvoke(new Action(() => { Pet.RefreshHud(); }));
            try { if (Panel != null && Panel.IsHandleCreated) Panel.BeginInvoke(new Action(PushAuth)); } catch { }
        }
        catch (Exception ex) { Log.Write("刷新点击数失败 " + ex.Message); }
    }

    void ApplySkinTestArg()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--set-skin");
        if (i < 0 || i + 1 >= args.Length) return;
        if (!long.TryParse(args[i + 1], out long skin)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            Cfg.CharacterAssetId = skin;
            Cfg.LocalAppearance = true;
            ConfigStore.Save(Cfg);
            Pet.BeginInvoke(new Action(() => Pet.ReloadAppearance()));
            await Room.AnnounceAppearanceAsync();
            Log.Write("维护命令：切换本地皮肤 " + skin);
        });
    }

    /// <summary>登录/启动后自动回到上次房间：优先服务器记录的 room_no，其次本地最近房间。</summary>
    public async Task ResumeLastRoomAsync()
    {
        string room = !string.IsNullOrEmpty(Auth.ServerRoomNo) ? Auth.ServerRoomNo : Cfg.LastRoom;
        // 服务器上的当前房间也补进历史，这样换机器登录后也能看到曾经待过的房间号。
        if (!string.IsNullOrEmpty(Auth.ServerRoomNo)) Room.RememberRoom(Auth.ServerRoomNo);
        if (string.IsNullOrEmpty(room)) return;
        try
        {
            await Room.JoinAsync(room);
            Log.Write("自动回到房间 " + room);
            PushRoom();
        }
        catch (Exception ex)
        {
            Log.Write("自动回房失败 " + room + " " + ex.Message);
        }
    }

    async Task AutoChestTickAsync()
    {
        try
        {
            if (!Auth.IsLoggedIn || Auth.UserInfo is not System.Text.Json.Nodes.JsonObject me) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long left = ApiClient.ReadLong(me["left_treasure_chest"]);
            long right = ApiClient.ReadLong(me["right_treasure_chest"]);

            // 宝箱到点（或即将到点）时云端很可能已刷新点击与消耗，先同步一次再决定是否领取。
            bool due = (left > 0 && now >= left - 5000) || (right > 0 && now >= right - 5000);
            if (due)
            {
                await Auth.RefreshClicksAsync();
                Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
                Pet.BeginInvoke(new Action(() => Pet.RefreshHud()));
                if (Auth.UserInfo is not System.Text.Json.Nodes.JsonObject fresh) return;
                me = fresh;
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                left = ApiClient.ReadLong(me["left_treasure_chest"]);
                right = ApiClient.ReadLong(me["right_treasure_chest"]);
            }

            if (!Cfg.AutoChest) return;
            await TryOpenChestAsync("left", left, now, false);
            await TryOpenChestAsync("right", right, now, true);
        }
        catch (Exception ex) { Log.Write("自动宝箱失败 " + ex.Message); }
    }

    async Task TryOpenChestAsync(string place, long unlockAt, long now, bool right)
    {
        var next = right ? _nextRightAttempt : _nextLeftAttempt;
        if (unlockAt <= 0 || now < unlockAt + 5000 || DateTime.UtcNow < next) return;
        Log.Write("自动宝箱尝试 " + place + " available=" + Room.AvailableClicks);
        try
        {
            await OpenChestAsync(place, true);
        }
        catch (Exception ex)
        {
            Log.Write("自动宝箱 " + place + " 未成功：" + ex.Message);
            if (right) _nextRightAttempt = DateTime.UtcNow.AddSeconds(60);
            else _nextLeftAttempt = DateTime.UtcNow.AddSeconds(60);
        }
    }

    async Task OpenChestSafeAsync(string place)
    {
        try { await OpenChestAsync(place, false); }
        catch (Exception ex)
        {
            Log.Write("开宝箱失败 " + place + "：" + ex.Message);
            Pet.Notify("开宝箱失败：" + ex.Message);
            Toast("开宝箱失败：" + ex.Message, "err");
        }
    }

    /// <summary>开宝箱：手动点击桌宠锚点或自动领取都走这里。返回原始响应供面板弹奖励。</summary>
    public async Task<System.Text.Json.Nodes.JsonNode> OpenChestAsync(string place, bool auto)
    {
        if (!Auth.IsLoggedIn) throw new ApiException(-1, "请先登录");
        if (Interlocked.Exchange(ref _chestBusy, 1) == 1) throw new ApiException(-1, "正在领取宝箱，请稍候");
        try
        {
            // 原版开箱前先按可用余额（总额 − 已消耗）判断，再发送累计总额。
            const long ChestCost = 1000;
            if (Room.AvailableClicks < ChestCost)
                throw new ApiException(-1, $"可用点击数不足（需要 {ChestCost}，当前 {Room.AvailableClicks}）");
            Log.Write("开宝箱 " + place + " auto=" + auto +
                " click_num=" + Room.DisplayClicks + " available=" + Room.AvailableClicks);
            var resp = await Auth.Api.PostAsync("api/frontend/chests/open", new { place, click_num = Room.DisplayClicks });
            long consume = ApiClient.ReadLong(resp?["click_consume"]);
            if (consume > 0) Room.AddConsume(consume);
            // 开箱请求已把本地未上报的点击包含在 click_num 里，服务器总额会吸收它们。
            Room.ResetPendingClicks();
            await Auth.WhoAmIAsync();
            PushAuth();
            long id = ApiClient.ReadLong(resp?["asset_id"]);
            string type = ApiClient.ReadString(resp?["asset_type"]);
            Log.Write("开宝箱成功 " + place + " asset=" + id + " type=" + type + " consume=" + consume);
            if (id > 0)
            {
                if (string.IsNullOrEmpty(type)) type = GuessAssetType(id);
                Pet.BeginInvoke(new Action(() => Pet.ShowPrize(type, id)));
            }
            Panel?.Push("chest", resp);
            // 通知只是反馈：任何 UI 异常都不能让它把“已领取”当成失败，否则会重复重试并吃掉冷却。
            try
            {
                if (auto)
                {
                    string label = place == "left" ? "左" : "右";
                    Toast("自动领取了" + label + "侧宝箱");
                    Pet.Notify("自动领取了" + label + "侧宝箱");
                }
            }
            catch (Exception ex) { Log.Write("宝箱提示失败 " + ex.Message); }
            return resp;
        }
        finally { Interlocked.Exchange(ref _chestBusy, 0); }
    }

    string GuessAssetType(long id)
    {
        if (Catalog.Find("character_skin", id) != null) return "character_skin";
        if (Catalog.Find("accessory", id) != null) return "accessory";
        if (Catalog.Find("emote", id) != null) return "emote";
        return "emote";
    }

    async Task BootAsync()
    {
        Catalog.LoadOffline();
        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
        }
        catch (Exception ex) { Log.Write("目录加载异常 " + ex); }

        try
        {
            bool ok = await Auth.TryRestoreAsync();
            Log.Write("恢复登录 " + ok);
            if (ok)
            {
                Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--leave-room") >= 0)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(Cfg.RoomNo)) await Room.JoinAsync(Cfg.RoomNo);
                        await Room.LeaveAsync();
                        Log.Write("维护命令：已退出房间");
                    }
                    catch (Exception ex) { Log.Write("维护命令退出房间失败 " + ex.Message); }
                    Pet.BeginInvoke(new Action(() => Pet.Close()));
                    return;
                }
                await Room.EnsureLobbyAsync();
                // 压测模式不进真实房间，否则 rm 快照会把伪造成员清掉。
                if (!_stressMode) await ResumeLastRoomAsync();
                ApplySkinTestArg();
                Pet.BeginInvoke(new Action(() => Pet.ApplySettings()));
                PushAuth();
            }
            else
            {
                Log.Write("尚未登录");
            }
        }
        catch (Exception ex) { Log.Write("启动登录失败 " + ex); }
    }

    public void OpenPanel()
    {
        if (Panel == null || Panel.IsDisposed)
        {
            Panel = new PanelWindow(this);
            Panel.FormClosed += (s, e) => Panel = null;
        }
        Panel.Show();
        Panel.BringToFront();
        Panel.Activate();
    }

    public void ApplyKeyboardMode()
    {
        var mode = Cfg.KeyboardMode switch
        {
            "letters" => KeyboardWindow.Mode.Letters,
            "wuwa" => KeyboardWindow.Mode.Wuwa,
            "controller" => KeyboardWindow.Mode.Controller,
            _ => KeyboardWindow.Mode.Hidden
        };
        Keyboard.SetMode(mode);
        LayoutKeyboard();
    }

    /// <summary>键盘始终在角色下方；下方空间不足时优先把角色往上挪，仍放不下才贴屏幕底边。</summary>
    void LayoutKeyboard()
    {
        if (Keyboard.CurrentMode == KeyboardWindow.Mode.Hidden || !Pet.Visible) return;
        double scale = Cfg.Scale * Pet.DeviceDpi / 96.0;
        Keyboard.FollowPet(Pet.Location, Pet.Size, scale);
        if (Pet.IsDragging) return;
        int lift = Keyboard.ComputeLift(Pet.Bounds, scale);
        if (lift > 0)
        {
            Pet.Location = new Point(Pet.Left, Pet.Top - lift);
            Cfg.PetX = Pet.Left;
            Cfg.PetY = Pet.Top;
            ConfigStore.Save(Cfg);
            Keyboard.FollowPet(Pet.Location, Pet.Size, scale);
        }
    }

    public void PushAuth()
    {
        Panel?.Push("auth", new
        {
            loggedIn = Auth.IsLoggedIn,
            nickname = Auth.Nickname,
            cuid = Auth.Cuid,
            uid = Auth.UserId,
            clicks = Room.AvailableClicks,
            clickTotal = Room.DisplayClicks,
            clickConsume = Room.ServerConsume,
            roomNo = Room.RoomNo,
            chestLeft = ApiClient.ReadLong(Auth.UserInfo?["left_treasure_chest"]),
            chestRight = ApiClient.ReadLong(Auth.UserInfo?["right_treasure_chest"]),
        });
    }

    public void PushRoom() => Panel?.Push("room", RoomState());
    public void PushCatalog() => Panel?.Push("catalog", new { skins = Catalog.Count("character_skin"), accessories = Catalog.Count("accessory"), emotes = Catalog.Count("emote") });

    public object RoomState()
    {
        var members = new System.Collections.Generic.List<object>();
        members.Add(new { id = Auth.UserId, nickname = Auth.Nickname, owner = true, online = true, self = true, visible = true, skin = Cfg.CharacterAssetId, part = Cfg.AccessoryAssetId });
        foreach (var m in Room.Members.Values)
            members.Add(new { id = m.Id, nickname = m.Nickname, owner = m.IsOwner, online = m.Online, self = false, visible = m.Visible, skin = m.RoleKey, part = m.PartKey });
        return new
        {
            state = Room.State,
            roomNo = Room.RoomNo,
            connected = Room.IsConnected,
            members,
            hiddenAll = Cfg.HideOtherPets,
            history = Cfg.RoomHistory,
        };
    }

    public void Toast(string text, string kind = "info") => Panel?.Push("toast", new { text, kind });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _autoChestTimer?.Dispose(); } catch { }
            try { _clickRefreshTimer?.Dispose(); } catch { }
            try { Room.Dispose(); } catch { }
            try { Store.Dispose(); } catch { }
            try { Auth.Api.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
