using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
    public AuthService Auth { get; }
    public Catalog Catalog { get; } = new();
    public AssetStore Store { get; } = new();
    public RoomManager Room { get; }
    public PetWindow Pet { get; }
    public KeyboardWindow Keyboard { get; }
    public PanelWindow Panel { get; private set; }
    /// <summary>GitHub Pages 上发布的版本兼容状态；读取失败时为 null。</summary>
    public CompatibilityInfo Compatibility { get; private set; }
    /// <summary>只读公告服务；只在设置页渲染，不打扰其他页面。</summary>
    public AnnouncementService Announcements { get; }
    System.Threading.Timer _autoChestTimer;
    System.Threading.Timer _clickRefreshTimer;
    System.Threading.Timer _autoClickTimer;
    System.Threading.Timer _gameModeRuleTimer;
    System.Threading.Timer _compatibilityTimer;
    System.Threading.Timer _announcementTimer;
    readonly object _gameModeRuleGate = new();
    bool _gameModeRuleMatched;
    bool _gameModeRuleAuto;
    long _clickUserId;
    readonly object _autoEmoteGate = new();
    readonly Queue<long> _autoEmoteTimes = new();
    DateTime _nextLeftAttempt = DateTime.MinValue, _nextRightAttempt = DateTime.MinValue;
    int _chestBusy;
    int _autoChestTickBusy;

    public App()
    {
        Cfg = ConfigStore.Load();
        // 先迁移/加载独立状态文件，再保存 config.json 以移除旧的状态字段。
        StateStore.Load(Cfg);
        ConfigStore.Save(Cfg);
        Announcements = new AnnouncementService(Cfg);
        // 配置与注册表对齐：用户手动删了 Run 项、或换过安装目录，都在启动时修正。
        Cfg.LaunchAtLogin = AutoStart.Sync(Cfg.LaunchAtLogin, Cfg.RunAsAdmin);
        AnimationTimer.FrameRate = Cfg.AnimationFps;
        Auth = new AuthService(Cfg);
        _clickUserId = Auth.UserId;
        Room = new RoomManager(Auth, Cfg);
        Pet = new PetWindow(Cfg, Auth, Catalog, Store, Room);
        Keyboard = new KeyboardWindow();
        Pet.KeyboardUI = Keyboard;

        Pet.OpenPanelRequested += OpenPanel;
        Pet.OpenMainPanelRequested += OpenMainPanel;
        Pet.AutoSettingsChanged += ApplyAutoSettings;
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
            if (!Auth.IsLoggedIn)
            {
                Room.ResetClickState();
                _clickUserId = 0;
                return;
            }
            // 切换账号不能继承上一账号的累计点击。
            if (_clickUserId != 0 && _clickUserId != Auth.UserId) Room.ResetClickState();
            _clickUserId = Auth.UserId;
            Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
        };
        // 点击数变化很频繁（每次按键一次）。面板可能没开，开了也不能每次都走一趟
        // WebView2 序列化 + postMessage；这里合并成 100ms 一次。
        Room.ClicksChanged += QueueClicksPush;
        Room.LocalClick += OnLocalClick;
        Room.ServiceUnavailableChanged += down =>
        {
            try
            {
                Panel?.Push("toast", new
                {
                    text = down ? "联机服务暂不可用，正在自动重试…" : "联机服务已恢复",
                    kind = down ? "err" : "ok",
                });
            }
            catch { }
        };
        // 键盘显示直接吃 Raw Input 边沿：按下立刻亮，不再等 16ms 轮询，也不会漏掉快速点击。
        Pet.Input.KeyEdge += (vk, down) => Keyboard.OnKeyEdge(vk, down);
        Pet.GeometryChanged += LayoutKeyboard;
        Pet.RefreshRequested += () => _ = RefreshEverythingAsync();
        // 快捷键/托盘菜单改了窗口设置后，面板里的开关要跟着变，否则界面显示和实际状态不一致。
        Pet.SettingsChanged += () => { try { Panel?.PushBoot(); } catch { } };
        // 手柄按键和键盘按键走同一条链路：计数、广播、挥手。
        Keyboard.PadButtonDown += () => Pet.BeginInvoke(new Action(Pet.GamepadTap));
        Keyboard.PadButtonUp += () => Pet.BeginInvoke(new Action(Pet.GamepadRelease));
        Keyboard.PadButtonPressed += label => Pet.BeginInvoke(new Action(() => Pet.PushGamepadBubble(label)));
        Pet.FormClosed += (s, e) => ExitThread();

        Pet.Show();
        Pet.ApplyCaptureMode();
        if (!Cfg.AllowOverlap) Pet.ResolveOverlaps();
        ApplyKeyboardMode();
        Keyboard.ModeRequested += mode =>
        {
            Cfg.KeyboardMode = mode switch
            {
                KeyboardWindow.Mode.Letters => "letters",
                KeyboardWindow.Mode.Wuwa => "wuwa",
                KeyboardWindow.Mode.Rhythm => "rhythm",
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

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--panel") >= 0 || !Cfg.DisclaimerShown)
            Pet.BeginInvoke(new Action(OpenPanel));

        _ = BootAsync();
        // 宝箱到点后最多等 5 秒就开始尝试，而不是等下一个 30 秒周期。
        _autoChestTimer = new System.Threading.Timer(_ => _ = AutoChestTickAsync(), null, 5000, 5000);
        // 网络恢复后最多 30 秒重新同步一次点击/宝箱等服务器状态。
        _clickRefreshTimer = new System.Threading.Timer(_ => _ = RefreshClicksAsync(), null, 30000, 30000);
        _gameModeRuleTimer = new System.Threading.Timer(_ => CheckGameModeRules(), null, 1500, 2000);
        // 启动后异步读取 GitHub Pages 上的兼容状态，之后每 30 分钟刷新；失败不阻塞启动。
        _compatibilityTimer = new System.Threading.Timer(_ => _ = RefreshCompatibilityAsync(), null, 2500, 30 * 60 * 1000);
        // 公告独立拉取；只在设置页显示，失败静默忽略。
        _announcementTimer = new System.Threading.Timer(_ => _ = RefreshAnnouncementsAsync(), null, 3500, 30 * 60 * 1000);
        ApplyAutoClickTimer();
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

    /// <summary>刷新 GitHub Pages 上的版本兼容状态；失败时保留上次结果，不影响本地功能。</summary>
    public async Task RefreshCompatibilityAsync(bool force = false)
    {
        var info = await CompatibilityChecker.CheckAsync(force);
        // 官方版本由客户端直接读取官方清单；即使 compatibility.json 暂时不可用，
        // 也能提示“官方已更新但 Native 尚未验证/已被官方强制更新”。
        var official = await OfficialVersionChecker.FetchAsync(Cfg.ServerRegion, force);
        var officialCheck = OfficialVersionChecker.Evaluate(official);
        if (info == null)
        {
            info = new CompatibilityInfo
            {
                Status = "unknown",
                Message = "暂时无法读取版本状态。",
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
        }
        info.OfficialCheck = officialCheck;
        Compatibility = info;
        Log.Write($"版本状态：{info.Status} {info.Message}；官方客户端：{officialCheck.State} {officialCheck.OfficialVersion}");
        try { Panel?.Push("compatibility", info); } catch { }
    }

    /// <summary>拉取公告并把未读队列推给面板；请求失败时保留上次结果。</summary>
    public async Task RefreshAnnouncementsAsync(bool force = false)
    {
        if (!await Announcements.RefreshAsync(force)) return;
        try { Panel?.Push("announcements", Announcements.Visible); } catch { }
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
            if (!await Auth.RefreshClicksAsync(Auth.CaptureSession())) return;
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

    /// <summary>切换服务器区服（cn / global）：登出账号、换 BaseUrl、重载目录与外观。</summary>
    /// <param name="announce">是否在面板提示“已切换至…”。登录流程内部的自动切换应传 false。</param>
    public async Task SwitchServerRegionAsync(string region, bool announce = true)
    {
        region = string.Equals(region, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "cn";
        if (Cfg.ServerRegion == region && Auth.Api.BaseUrl == ApiClient.ResolveBaseUrl(region))
            return;

        Log.Write($"正在切换服务器区服至：{region}");
        Cfg.ServerRegion = region;
        ConfigStore.Save(Cfg);

        // 不能在这里回到大厅：此时 Auth 仍然指向旧区服，会留下旧区服的 socket。
        try { await Room.LeaveAsync(reconnectLobby: false); } catch { }
        try { await Auth.LogoutAsync(); } catch { }

        Auth.UpdateBaseUrl(ApiClient.ResolveBaseUrl(region));

        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
            Pet.BeginInvoke(new Action(() => { Pet.ReloadAppearance(); Pet.RefreshMembers(); }));
        }
        catch (Exception ex)
        {
            Log.Write("切换区服后加载目录失败：" + ex.Message);
        }

        try
        {
            if (Panel != null && Panel.IsHandleCreated)
            {
                Panel.BeginInvoke(new Action(() =>
                {
                    Panel.PushBoot();
                    if (announce)
                        Panel.Push("toast", new { text = $"已切换至{(region == "global" ? "国际服" : "国服")}，请重新登录", kind = "ok" });
                }));
            }
        }
        catch { }
        // 旧会话的背包/图鉴缓存在 WebView 里，仅 PushBoot 不会清空；切服后强制重载页面。
        try { Panel?.ReloadPage(); } catch { }
        _ = RefreshCompatibilityAsync(force: true);
    }

    /// <summary>自动敲击定时器：按设置频率产生一次本地敲击，由 RoomManager 批量上传。</summary>
    public void ApplyAutoSettings() => ApplyAutoClickTimer();

    void ApplyAutoClickTimer()
    {
        if (_autoClickTimer == null)
            _autoClickTimer = new System.Threading.Timer(_ => AutoClickTick(), null, Timeout.Infinite, Timeout.Infinite);
        int rate = Math.Clamp(Cfg.AutoClickPerSecond, 1, 10);
        if (Cfg.AutoClickEnabled)
        {
            int period = Math.Max(50, (int)Math.Round(1000.0 / rate));
            _autoClickTimer.Change(50, period);
        }
        else
        {
            _autoClickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _ = Room.FlushClicksNowAsync();
        }
    }

    /// <summary>
    /// 每 2 秒扫描一次进程名；命中规则时自动开启鼠标穿透，进程全部退出后恢复。
    /// 只在匹配状态发生边沿变化时操作，避免反复覆盖用户手动切换的开关。
    /// </summary>
    void CheckGameModeRules()
    {
        try
        {
            string[] rules = Cfg.GameModeRules ?? Array.Empty<string>();
            bool matched = false;
            string matchedName = null;
            if (rules.Length > 0)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (GameModeRules.Matches(p.ProcessName, rules))
                        {
                            matched = true;
                            matchedName = p.ProcessName;
                            break;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }

            // 用户手动关掉穿透后不再把这次开关算作规则接管，避免游戏退出时误关。
            if (matched && _gameModeRuleAuto && !Cfg.ClickThrough)
                _gameModeRuleAuto = false;

            bool becameMatched, becameUnmatched;
            lock (_gameModeRuleGate)
            {
                becameMatched = matched && !_gameModeRuleMatched;
                becameUnmatched = !matched && _gameModeRuleMatched;
                _gameModeRuleMatched = matched;
            }

            if (becameMatched)
            {
                Log.Write("游戏模式规则命中：" + (matchedName ?? "?"));
                Pet.BeginInvoke(new Action(() =>
                {
                    if (Cfg.ClickThrough) return;
                    _gameModeRuleAuto = true;
                    Pet.SetClickThrough(true);
                }));
            }
            else if (becameUnmatched && _gameModeRuleAuto)
            {
                Log.Write("游戏模式规则进程已退出，恢复鼠标交互");
                Pet.BeginInvoke(new Action(() =>
                {
                    if (!_gameModeRuleAuto) return;
                    _gameModeRuleAuto = false;
                    Pet.SetClickThrough(false);
                }));
            }
        }
        catch (Exception ex) { Log.Write("游戏模式规则检测失败 " + ex.Message); }
    }

    void AutoClickTick()
    {
        try
        {
            if (!Cfg.AutoClickEnabled || !Auth.IsLoggedIn || !Room.IsConnected) return;
            Room.AddClicks(1);
            Room.NotifyLocalAction();
            if (Cfg.AutoClickAnimate)
            {
                try { Pet.BeginInvoke(new Action(Pet.VirtualTap)); } catch { }
            }
            // 点击上传由 RoomManager 每 5 秒批量执行，避免自动敲击时高频 cr 被服务端限流。
        }
        catch (Exception ex) { Log.Write("自动敲击失败 " + ex.Message); }
    }

    /// <summary>每次本地敲击（真实或自动）按每滚动一秒上限发送一个自动表情。</summary>
    void OnLocalClick()
    {
        try
        {
            if (!Cfg.AutoEmoteEnabled || !Auth.IsLoggedIn || !Room.IsConnected) return;
            int max = Math.Clamp(Cfg.AutoEmoteMaxPerSecond, 1, 20);
            long now = Environment.TickCount64;
            lock (_autoEmoteGate)
            {
                while (_autoEmoteTimes.Count > 0 && now - _autoEmoteTimes.Peek() >= 1000)
                    _autoEmoteTimes.Dequeue();
                if (_autoEmoteTimes.Count >= max) return;
                _autoEmoteTimes.Enqueue(now);
            }
            long id = PickAutoEmoteId();
            if (id > 0) Pet.BeginInvoke(new Action(() => Pet.SendEmoteById(id)));
        }
        catch (Exception ex) { Log.Write("自动表情失败 " + ex.Message); }
    }

    long PickAutoEmoteId()
    {
        try
        {
            if (Cfg.AutoEmoteMode == "specific") return Cfg.AutoEmoteId;
            long[] ids;
            if (Cfg.AutoEmoteMode == "library")
                ids = Catalog.All("emote").Select(e => e.Id).Where(id => id > 0).ToArray();
            else
                ids = (Cfg.EmoteAssetIds ?? Array.Empty<long>()).Where(id => id > 0).ToArray();
            return ids.Length == 0 ? 0 : ids[Random.Shared.Next(ids.Length)];
        }
        catch { return 0; }
    }

    /// <summary>登录/启动/重新开启自动宝箱后立刻检查一次，不等 30 秒周期。</summary>
    public void CheckAutoChestNow() => _ = AutoChestTickAsync();

    async Task AutoChestTickAsync()
    {
        if (Interlocked.Exchange(ref _autoChestTickBusy, 1) == 1) return;
        try
        {
            var session = Auth.CaptureSession();
            if (session == null || !Cfg.AutoChest || Auth.UserInfo is not System.Text.Json.Nodes.JsonObject me) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long left = ApiClient.ReadLong(me["left_treasure_chest"]);
            long right = ApiClient.ReadLong(me["right_treasure_chest"]);

            // 宝箱到点（或即将到点）时云端很可能已刷新点击与消耗，先同步一次再决定是否领取。
            bool due = (left > 0 && now >= left - 5000) || (right > 0 && now >= right - 5000);
            if (due)
            {
                if (!await Auth.RefreshClicksAsync(session) || !Cfg.AutoChest) return;
                Pet.BeginInvoke(new Action(() => Pet.RefreshHud()));
                if (Auth.UserInfo is not System.Text.Json.Nodes.JsonObject fresh) return;
                me = fresh;
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                left = ApiClient.ReadLong(me["left_treasure_chest"]);
                right = ApiClient.ReadLong(me["right_treasure_chest"]);
            }

            await TryOpenChestAsync(session, "left", left, now, false);
            await TryOpenChestAsync(session, "right", right, now, true);
        }
        catch (Exception ex) { Log.Write("自动宝箱失败 " + ex.Message); }
        finally { Interlocked.Exchange(ref _autoChestTickBusy, 0); }
    }

    async Task TryOpenChestAsync(ApiClient session, string place, long unlockAt, long now, bool right)
    {
        var next = right ? _nextRightAttempt : _nextLeftAttempt;
        if (unlockAt <= 0 || now < unlockAt + 5000 || DateTime.UtcNow < next) return;
        Log.Write("自动宝箱尝试 " + place + " available=" + Room.AvailableClicks);
        try
        {
            await OpenChestAsync(place, true, session);
        }
        catch (Exception ex)
        {
            Log.Write("自动宝箱 " + place + " 未成功：" + ex.Message);
            Auth.TryUseSession(session, () =>
            {
                if (right) _nextRightAttempt = DateTime.UtcNow.AddSeconds(60);
                else _nextLeftAttempt = DateTime.UtcNow.AddSeconds(60);
            });
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
    public Task<System.Text.Json.Nodes.JsonNode> OpenChestAsync(string place, bool auto)
    {
        var session = Auth.CaptureSession() ?? throw new ApiException(-1, "请先登录");
        return OpenChestAsync(place, auto, session);
    }

    async Task<System.Text.Json.Nodes.JsonNode> OpenChestAsync(string place, bool auto, ApiClient session)
    {
        if (Interlocked.Exchange(ref _chestBusy, 1) == 1) throw new ApiException(-1, "正在领取宝箱，请稍候");
        try
        {
            System.Text.Json.Nodes.JsonNode resp;
            long consume;
            await Room.EnterClickUploadAsync();
            try
            {
                RoomManager.ChestClicks sent = default;
                Task<System.Text.Json.Nodes.JsonNode> request = null;
                if (!Auth.TryUseSession(session, () =>
                {
                    if (auto && !Cfg.AutoChest) return;
                    sent = Room.CaptureChestClicks();
                    const long ChestCost = 1000;
                    if (sent.Available < ChestCost)
                        throw new ApiException(-1, $"可用点击数不足（需要 {ChestCost}，当前 {sent.Available}）");
                    Log.Write("开宝箱 " + place + " auto=" + auto + " click_num=" + sent.Total + " available=" + sent.Available);
                    request = session.PostAsync("api/frontend/chests/open", new { place, click_num = sent.Total });
                }) || request == null) return null;

                try { resp = await request; }
                catch (OperationCanceledException) when (Auth.CaptureSession() != session) { return null; }
                consume = Math.Max(0, ApiClient.ReadLong(resp?["click_consume"]));
                if (!Auth.TryUseSession(session, () =>
                {
                    Room.CompleteChestClicks(sent, consume);
                    Auth.MarkChestOpened(place);
                })) return null;
            }
            finally { Room.ExitClickUpload(); }

            if (Auth.CaptureSession() != session) return null;
            PushAuth();
            try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
            _ = RefreshAfterChestAsync(session);
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
                    if (Cfg.NotifyChestClaimed) Pet.Notify("自动领取了" + label + "侧宝箱");
                }
            }
            catch (Exception ex) { Log.Write("宝箱提示失败 " + ex.Message); }
            return resp;
        }
        finally { Interlocked.Exchange(ref _chestBusy, 0); }
    }

    /// <summary>开箱成功后后台校准服务器时间戳；失败只记日志，不影响已经到手的宝箱结果。</summary>
    async Task RefreshAfterChestAsync(ApiClient session)
    {
        try
        {
            if (!await Auth.WhoAmIAsync(session)) return;
            PushAuth();
            try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
        }
        catch (Exception ex) { Log.Write("开宝箱后刷新账号失败 " + ex.Message); }
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
                // 启动恢复登录后立刻检查一次，别让已经可领的宝箱干等 30 秒首个定时器。
                CheckAutoChestNow();
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
        Panel.ShowAndFocus();
    }

    /// <summary>快捷键“打开主面板”：无论面板上次停在哪一页，都回到主页面并置前。</summary>
    public void OpenMainPanel()
    {
        OpenPanel();
        try { Panel?.BeginInvoke(new Action(() => Panel.NavigateTo("bag"))); } catch { }
    }

    /// <summary>另一个进程再次启动时，由单实例事件线程转回 UI 线程打开面板。</summary>
    public void RequestPanelFromOtherInstance()
    {
        try { Pet.BeginInvoke(new Action(OpenPanel)); } catch { }
    }

    /// <summary>提权重启时先让当前进程把设置响应送回面板，再退出，避免两个实例长时间并存。</summary>
    public void ScheduleRestartExit()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 900 };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            timer.Dispose();
            try { Pet.Close(); } catch { }
        };
        timer.Start();
    }

    public void ApplyKeyboardMode()
    {
        Keyboard.SetRhythm(Cfg.RhythmMode, Cfg.RhythmKeys);
        var mode = Cfg.KeyboardMode switch
        {
            "letters" => KeyboardWindow.Mode.Letters,
            "wuwa" => KeyboardWindow.Mode.Wuwa,
            "rhythm" => KeyboardWindow.Mode.Rhythm,
            "controller" => KeyboardWindow.Mode.Controller,
            _ => KeyboardWindow.Mode.Hidden
        };
        if (Cfg.RhythmMode && mode == KeyboardWindow.Mode.Wuwa) mode = KeyboardWindow.Mode.Rhythm;
        if (!Cfg.RhythmMode && mode == KeyboardWindow.Mode.Rhythm) mode = KeyboardWindow.Mode.Wuwa;
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

    int _clicksPending;
    System.Threading.Timer _clicksPushTimer;

    /// <summary>
    /// 点击数每次按键都会变。面板没开时直接丢弃；开着时按 100ms 节流推送，
    /// 避免高频输入把 WebView2 的消息队列塞满（表现出来就是界面“卡住不刷新”）。
    /// </summary>
    void QueueClicksPush()
    {
        if (Panel == null) return;
        Interlocked.Exchange(ref _clicksPending, 1);
        if (_clicksPushTimer == null)
        {
            _clicksPushTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref _clicksPending, 0) == 0) return;
                PushClicksNow();
            }, null, 100, Timeout.Infinite);
            return;
        }
        _clicksPushTimer.Change(100, Timeout.Infinite);
    }

    void PushClicksNow() => Panel?.Push("clicks", new
    {
        available = Room.AvailableClicks,
        total = Room.DisplayClicks,
        consume = Room.ServerConsume
    });

    public void PushAuth()
    {
        Panel?.Push("auth", new
        {
            loggedIn = Auth.IsLoggedIn,
            nickname = Auth.Nickname,
            cuid = Auth.Cuid,
            uid = Auth.PlayerId > 0 ? Auth.PlayerId : Auth.UserId,
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
        members.Add(new { id = Auth.UserId, nickname = Auth.Nickname, owner = Room.SelfIsOwner, online = true, self = true, visible = true, skin = Cfg.CharacterAssetId, part = Cfg.AccessoryAssetId });
        foreach (var m in Room.Members.Values)
            members.Add(new { id = m.Id, nickname = m.Nickname, owner = m.IsOwner, online = m.Online, self = false, visible = m.Visible, skin = m.RoleKey, part = m.PartKey });
        return new
        {
            state = Room.State,
            roomNo = Room.RoomNo,
            connected = Room.IsConnected,
            serviceUnavailable = Room.ServiceUnavailable,
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
            try { _autoClickTimer?.Dispose(); } catch { }
            try { _gameModeRuleTimer?.Dispose(); } catch { }
            try { _clicksPushTimer?.Dispose(); } catch { }
            try { Room.Dispose(); } catch { }
            try { Store.Dispose(); } catch { }
            try { Auth.Api.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
