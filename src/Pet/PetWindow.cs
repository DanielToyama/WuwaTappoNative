using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌宠主窗口。几何完全按原版 CSS：272×331 逻辑像素，猫 200×200 位于 (24,120)，
/// 信息条 y=299（猫底边上方 21px），左下计数条 136×32，右侧工具栏 61×32。
/// </summary>
public sealed class PetWindow : Form
{
    readonly Settings _cfg;
    readonly AuthService _auth;
    readonly Catalog _catalog;
    readonly AssetStore _store;
    readonly PetImageLoader _loader;
    readonly RoomManager _room;
    readonly GlobalInputHook _hook = new();
    readonly Dictionary<long, RemoteCatWindow> _cats = new();

    PetFrames _frames;
    readonly LayeredSurface _surface = new();
    readonly PetComposer.FrameCache _frameCache = new();
    AnimationTimer _composeTimer;
    int _bodyFrame = 1, _headFrame = 1;
    bool _dragging, _moved;
    bool _handIsLeft;
    long _lastGeometryMs;
    Point _dragCursor, _dragOrigin;
    readonly EmoteBubbleLayer _bubbles = new(10);
    readonly KeyBubbleLayer _keyBubbles = new(10);
    readonly EmoteWheelLayer _wheel = new();
    System.Windows.Forms.Timer _blink, _blinkOff, _moveReset, _handReset, _countdownTimer, _wheelCloseTimer, _wheelShowTimer, _hoverFadeTimer, _loadingTimer, _hoverFadeAnimTimer, _hoverPollTimer;
    long _lastSkin = -1, _lastAcc = -1;
    Image _chestBtnArt, _chestBgArt;
    ContextMenuStrip _menu;
    NotifyIcon _tray;
    ToolStripMenuItem _topmostItem, _passthroughItem, _passthroughOffItem, _hoverItem, _scaleMenuItem, _opacityMenuItem;
    ToolStripMenuItem _layoutHorizontalItem, _layoutVerticalItem;
    Icon _icon;

    bool _hiddenByHotkey;
    CaptureWindow _capture;
    Bitmap _captureFrame;

    /// <summary>全局输入监听（App 用它给键盘显示转发按键高亮）。</summary>
    public GlobalInputHook Input => _hook;

    public event Action OpenPanelRequested;
    public event Action OpenMainPanelRequested;
    /// <summary>自动敲击/自动表情开关变化后，让 App 立即启停相关定时器。</summary>
    public event Action AutoSettingsChanged;
    public event Action<string> ChestOpenRequested;
    public event Action GeometryChanged;
    public event Action<bool> PetVisibilityChanged;
    /// <summary>F5：重新拉取目录/外观/房间等界面数据。</summary>
    public event Action RefreshRequested;
    /// <summary>窗口设置被快捷键/菜单改动后触发，面板据此刷新开关状态。</summary>
    public event Action SettingsChanged;
    /// <summary>主角帧/手部/眨眼变化时通知绿幕采集窗口重绘。</summary>
    public event Action CaptureChanged;
    /// <summary>绿幕窗口需要一起绘制当前键盘/手柄 GUI。</summary>
    public KeyboardWindow KeyboardUI { get; set; }

    public PetWindow(Settings cfg, AuthService auth, Catalog catalog, AssetStore store, RoomManager room)
    {
        _cfg = cfg;
        _auth = auth;
        _catalog = catalog;
        _store = store;
        _room = room;
        _loader = new PetImageLoader(catalog, store);
        _chestBtnArt = store.LoadLocal(System.IO.Path.Combine(Paths.AppDir, "assets", "ui", "chest-btn.png"));
        _chestBgArt = store.LoadLocal(System.IO.Path.Combine(Paths.AppDir, "assets", "ui", "chest-countdown.png"));

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Wuwatappo";
        CreateTimers();
        BuildMenu();
        BuildTray();

        _room.MemberUpdated += id => Safe(() => UpdateCat(id));
        // 按键动作只驱动对应桌宠的手部动画；不要走 UpdateCat（那会重算外观、缩放、位置）。
        _room.MemberAction += (id, action) => Safe(() => { if (_cats.TryGetValue(id, out var c)) c.ApplyMemberAction(action); });
        _room.RosterChanged += () => Safe(RefreshCats);
        _room.MemberRemoved += id => Safe(() => RemoveCat(id));
        _room.MemberEmote += (id, name) => Safe(() => { if (_cats.TryGetValue(id, out var c)) c.ShowEmote(name); });
        _room.StateChanged += () => Safe(RefreshCats);
        _room.ForcedDisconnect += code => Safe(() => Notify(code == 90002 ? "账号已在其他设备登录" : "房间已关闭"));
        _room.SocketClosed += reason => Safe(() => { if (_room.State == "joined") Notify("连接断开：" + reason); });
        _room.JoinRejected += message => Safe(() => Notify(message));
        _room.ServiceUnavailableChanged += down => Safe(() =>
            Notify(down ? "联机服务暂不可用，正在自动重试…" : "联机服务已恢复"));

        _auth.Changed += () => Safe(OnAuthChanged);
        _room.ClicksChanged += () => Safe(() => { CaptureChanged?.Invoke(); RequestCompose(); });
        _catalog.Loaded += () => Safe(() => { _loader.Clear(); ReloadFrames(); RefreshCats(); });

        ReloadFrames();
        Location = cfg.PetX >= 0 ? new Point(cfg.PetX, cfg.PetY) : DefaultLocation();
        _hook.KeyDown += vk => Safe(() =>
        {
            _room.AddClicks(1);
            _room.NotifyLocalAction();
            if (!_dragging) HandDown();
        });
        _hook.KeyUp += vk => Safe(() => { if (!_dragging) HandUp(); });
        _hook.KeyEdge += (vk, down) => Safe(() => { if (down) PushKeyBubble(vk); });
        _hook.HotkeyKeyDown += OnHotkeyKeyDown;
        RebuildHotkeyMap();
        _hook.Start();
    }

    // ---------- 尺寸 / 渲染 ----------

    float DpiScale => DeviceDpi / 96f;
    double EffectiveRemoteScale => _cfg.RemoteFollowMainScale ? _cfg.Scale : _cfg.RemoteScale;
    double EffectiveRemoteOpacity => _cfg.RemoteFollowMainOpacity ? _cfg.Opacity : _cfg.RemoteOpacity;
    float RemoteScaleFactor => DpiScale * (float)EffectiveRemoteScale;

    Size WindowSize()
    {
        float s = DpiScale * (float)_cfg.Scale;
        return new Size(
            Math.Max(40, (int)Math.Round(PetComposer.WindowW * s)),
            Math.Max(40, (int)Math.Round(PetComposer.WindowH * s)));
    }

    void ReloadFrames()
    {
        if (_frames != null) _frames.Changed -= OnFramesChanged;
        _frames = _loader.Get(_cfg.CharacterAssetId, _cfg.AccessoryAssetId);
        _frames.Changed += OnFramesChanged;
        UpdateLoadingAnimation();
        Log.Write($"DBG reload frames cfg={_cfg.CharacterAssetId}/{_cfg.AccessoryAssetId} ready={_frames?.Ready} loading={_frames?.Loading}");
        if (Size != WindowSize()) Size = WindowSize();
        Compose();
    }

    void OnFramesChanged() => Safe(() =>
    {
        UpdateLoadingAnimation();
        Compose();
    });

    void UpdateLoadingAnimation()
    {
        if (_frames?.Loading == true)
        {
            if (!_loadingTimer.Enabled) _loadingTimer.Start();
        }
        else _loadingTimer.Stop();
    }

    /// <summary>换装后清缓存并立即重绘（本地与云端装扮变更都走这里）。</summary>
    public void ReloadAppearance()
    {
        Log.Write($"刷新本地外观 skin={_cfg.CharacterAssetId} accessory={_cfg.AccessoryAssetId}");
        _loader.Clear();
        _wheel.Close();
        _lastSkin = _cfg.CharacterAssetId;
        _lastAcc = _cfg.AccessoryAssetId;
        ReloadFrames();
    }

    void OnAuthChanged()
    {
        if (_lastSkin != _cfg.CharacterAssetId || _lastAcc != _cfg.AccessoryAssetId)
        {
            _lastSkin = _cfg.CharacterAssetId;
            _lastAcc = _cfg.AccessoryAssetId;
            ReloadFrames();
        }
        else Compose();
    }

    void Compose()
    {
        if (!IsHandleCreated) return;
        if (_frames == null || (!_frames.Ready && !_frames.Loading)) return;
        var info = BuildInfo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        info.Loading = _frames.Loading;
        info.LoadingPhase = (Environment.TickCount64 % 1000) / 1000.0;
        // 高频重绘（每次按键、每次计数变化）复用同一张后缓冲。
        var surface = _surface.Begin(Size.Width, Size.Height);
        if (_frames.Ready)
        {
            // 窗口内容始终按全不透明绘制；实际透明度交给 UpdateLayeredWindow 常量 alpha。
            // 这样悬停淡出不用每帧重画整张位图，鼠标频繁移动时 CPU 占用会低很多。
            PetComposer.ComposeInto(surface, _frames, _bodyFrame, _headFrame, info, _frameCache);
        }
        else
        {
            using var g = Graphics.FromImage(surface);
            g.Clear(Color.Transparent);
            float s = surface.Width / (float)PetComposer.WindowW;
            PetComposer.DrawLoading(g, s, info.LoadingPhase,
                PetComposer.CatX + PetComposer.CatSize / 2f,
                PetComposer.CatY + PetComposer.CatSize / 2f);
        }
        _surface.Apply(this, EffectiveOpacity);
        CaptureChanged?.Invoke();
    }

    double EffectiveOpacity
    {
        get
        {
            double baseOpacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);
            if (!_cfg.HoverFade) return baseOpacity;
            double hoverFactor = 1.0 - _hoverFadeAmount * (1.0 - Math.Clamp(_cfg.HoverFadeOpacity, 0.1, 1.0));
            return Math.Clamp(baseOpacity * hoverFactor, 0.01, 1.0);
        }
    }

    /// <summary>只更新窗口常量 alpha，不重新绘制内容。</summary>
    void ApplyOpacityOnly()
    {
        if (IsHandleCreated && !IsDisposed && _surface.Bitmap != null)
            _surface.Apply(this, EffectiveOpacity);
    }

    PetComposer.Info BuildInfo(long now)
    {
        long leftTs = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
        long rightTs = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
        var accOffset = AccessoryOffsetFor(_cfg.AccessoryAssetId);
        return new PetComposer.Info
        {
            Clicks = _room.AvailableClicks,
            KeyboardOn = _cfg.KeyboardMode != "hidden",
            Nickname = _auth.Nickname,
            Bubbles = _bubbles,
            KeyBubbles = _keyBubbles,
            Wheel = _wheel,
            ChestLeftTs = leftTs,
            ChestRightTs = rightTs,
            NowMs = now,
            AccOffsetX = accOffset.x,
            AccOffsetY = accOffset.y,
            // 关闭"总是显示宝箱"时：可领取按钮常显；鼠标悬停桌宠时像表情 UI 一样露出倒计时
            ShowChestLeft = leftTs > 0 && (_cfg.AlwaysShowChest || _hovering || leftTs <= now),
            ShowChestRight = rightTs > 0 && (_cfg.AlwaysShowChest || _hovering || rightTs <= now),
            ChestButton = _chestBtnArt,
            ChestCountdown = _chestBgArt,
        };
    }

    /// <summary>取当前头饰的用户微调偏移（逻辑像素）。</summary>
    public (int x, int y) AccessoryOffsetFor(long accessoryId)
    {
        if (accessoryId <= 0) return (0, 0);
        if (_cfg.AccessoryOffsets.TryGetValue(accessoryId.ToString(), out var arr) &&
            arr != null && arr.Length >= 2)
            return (Math.Clamp(arr[0], -160, 160), Math.Clamp(arr[1], -160, 160));
        return (0, 0);
    }

    /// <summary>微调当前头饰位置；reset=true 时回到 0,0。返回调整后的逻辑坐标。</summary>
    public (int x, int y) NudgeAccessory(int dx, int dy, bool reset = false)
    {
        long id = _cfg.AccessoryAssetId;
        if (id <= 0) return (0, 0);
        var current = AccessoryOffsetFor(id);
        int x = reset ? 0 : Math.Clamp(current.x + dx, -160, 160);
        int y = reset ? 0 : Math.Clamp(current.y + dy, -160, 160);
        _cfg.AccessoryOffsets[id.ToString()] = new[] { x, y };
        ConfigStore.Save(_cfg);
        Compose();
        return (x, y);
    }

    bool _hovering, _hoverFadeTarget;
    double _hoverFadeAmount;

    /// <summary>统一用光标轮询判断悬停：穿透/非穿透行为一致，也避免窗口透明区域造成误触发。</summary>
    void SetHovering(bool hovering)
    {
        if (_hovering == hovering) return;
        _hovering = hovering;
        _hoverFadeTimer.Stop();
        if (hovering)
        {
            // 停留 500ms 才淡出：只是划过桌宠时不应该闪一下透明度。
            _wheelCloseTimer.Stop();
            if (_cfg.HoverFade) _hoverFadeTimer.Start();
            // 轮盘也延迟出现；鼠标只是在角色身上快速划过时，不反复启动展开动画。
            if (!_cfg.ClickThrough)
            {
                _wheelShowTimer.Stop();
                _wheelShowTimer.Start();
            }
            return;
        }
        _wheelShowTimer.Stop();
        _wheelCloseTimer.Start();
        StartHoverFade(false);
    }

    void PollHover()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var p = Cursor.Position;
        SetHovering(Visible && !_hiddenByHotkey && ContainsPetPoint(p));
        foreach (var cat in _cats.Values)
            cat.SetHovered(cat.Visible && !_hiddenByHotkey && cat.ContainsScreenPoint(p));
    }

    /// <summary>按当前合成位图的 alpha 判断光标是否落在实际可见内容上，透明留白不触发悬停。</summary>
    bool ContainsPetPoint(Point p)
    {
        if (!Visible || IsDisposed || !IsHandleCreated) return false;
        var pt = PointToClient(p);
        if (pt.X < 0 || pt.Y < 0 || pt.X >= Width || pt.Y >= Height) return false;
        var bmp = _surface.Bitmap;
        if (bmp == null) return false;
        try { return bmp.GetPixel(pt.X, pt.Y).A > 8; } catch { return false; }
    }

    void StartHoverFade(bool on)
    {
        if (!_cfg.HoverFade)
        {
            _hoverFadeAmount = 0;
            _hoverFadeAnimTimer.Stop();
            ApplyOpacityOnly();
            return;
        }
        _hoverFadeTarget = on;
        if (!_hoverFadeAnimTimer.Enabled) _hoverFadeAnimTimer.Start();
    }

    // ---------- 动画 ----------

    void CreateTimers()
    {
        _blink = new System.Windows.Forms.Timer { Interval = 5000 };
        _blink.Tick += (s, e) => { SetHead(2); _blinkOff.Start(); };
        _blinkOff = new System.Windows.Forms.Timer { Interval = 250 };
        _blinkOff.Tick += (s, e) => { _blinkOff.Stop(); SetHead(1); };
        _moveReset = new System.Windows.Forms.Timer { Interval = 220 };
        _moveReset.Tick += (s, e) => { _moveReset.Stop(); SetBody(1); };
        _handReset = new System.Windows.Forms.Timer { Interval = 220 };
        _handReset.Tick += (s, e) => { _handReset.Stop(); SetBody(1); };
        // 按键、计数、气泡和轮盘统一绘制；没有活动动画或新请求时立即停钟。
        _composeTimer = new AnimationTimer();
        _composeTimer.Tick += (s, e) =>
        {
            bool active = _bubbles.Tick();
            bool keyActive = _keyBubbles.Tick();
            Compose();
            if (!active && !keyActive && !_wheel.Animating) _composeTimer.Stop();
        };
        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdownTimer.Tick += (s, e) =>
        {
            if (!_auth.IsLoggedIn) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long left = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
            long right = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
            // 宝箱倒计时没显示时不需要每秒重画整只桌宠。
            bool showLeft = left > 0 && (_cfg.AlwaysShowChest || _hovering || left <= now);
            bool showRight = right > 0 && (_cfg.AlwaysShowChest || _hovering || right <= now);
            if (showLeft || showRight) RequestCompose();
        };
        _countdownTimer.Start();
        _wheelShowTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _wheelShowTimer.Tick += (s, e) =>
        {
            _wheelShowTimer.Stop();
            if (_hovering && !_cfg.ClickThrough && !_hiddenByHotkey) ShowWheel();
        };
        _wheelCloseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _wheelCloseTimer.Tick += (s, e) =>
        {
            if (!_hovering)
            {
                bool wasOpen = _wheel.Open;
                _wheel.Close();
                if (wasOpen) Compose();
            }
            // 已经重新悬停到实际角色上（或轮盘已关闭）就停止，避免空转。
            _wheelCloseTimer.Stop();
        };
        _hoverFadeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _hoverFadeTimer.Tick += (s, e) =>
        {
            _hoverFadeTimer.Stop();
            if (!_hovering || !_cfg.HoverFade) return;
            StartHoverFade(true);
        };
        _hoverFadeAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _hoverFadeAnimTimer.Tick += (s, e) =>
        {
            double target = _hoverFadeTarget ? 1.0 : 0.0;
            double diff = target - _hoverFadeAmount;
            if (Math.Abs(diff) < 0.06)
            {
                _hoverFadeAmount = target;
                _hoverFadeAnimTimer.Stop();
            }
            else _hoverFadeAmount += Math.Sign(diff) * 0.08;
            ApplyOpacityOnly();
        };
        // 悬停有 500ms 停留判定，150ms 轮询足够；降低轮询频率可减少多开联机成员时的 UI 线程开销。
        _hoverPollTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _hoverPollTimer.Tick += (s, e) => PollHover();
        _hoverPollTimer.Start();
        _loadingTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _loadingTimer.Tick += (s, e) =>
        {
            if (_frames?.Loading != true)
            {
                _loadingTimer.Stop();
                Compose();
                return;
            }
            Compose();
        };
        _blink.Start();
    }

    void SetHead(int f) { if (_headFrame == f) return; _headFrame = f; CaptureChanged?.Invoke(); RequestCompose(); }
    void SetBody(int f) { if (_bodyFrame == f) return; _bodyFrame = f; CaptureChanged?.Invoke(); RequestCompose(); }

    /// <summary>同一帧内的按键、计数和动画请求只触发一次绘制，不积压 UI 消息。</summary>
    void RequestCompose()
    {
        if (IsDisposed || !IsHandleCreated) return;
        _composeTimer.Start();
    }

    /// <summary>每次按下：左右手交替落下，220ms 后若未抬起自动回位。</summary>
    void HandDown(bool log = true)
    {
        _handIsLeft = !_handIsLeft;
        if (log) Log.Write("DBG hand down " + (_handIsLeft ? "left" : "right"));
        SetBody(_handIsLeft ? 2 : 3);
        _handReset.Stop();
        _handReset.Start();
    }

    void HandUp()
    {
        Log.Write("DBG hand up");
        _handReset.Stop();
        SetBody(1);
    }

    /// <summary>自动敲击的桌宠动作；只驱动本地手部动画，不点亮键位显示器。</summary>
    public void VirtualTap()
    {
        if (_hiddenByHotkey || _dragging) return;
        HandDown(false);
        Compose();
    }

    /// <summary>手柄按键：与键盘按下完全一致——计数 + 广播 + 挥手。</summary>
    public void GamepadTap()
    {
        if (_hiddenByHotkey) return;
        _room.AddClicks(1);
        _room.NotifyLocalAction();
        if (!_dragging) HandDown();
        Compose();
    }

    public void GamepadRelease()
    {
        if (_hiddenByHotkey) return;
        if (!_dragging) HandUp();
    }

    // ---------- 鼠标 ----------

    sealed record HitZone(bool Cat, bool Counter, bool Keyboard, bool Menu, string Chest, bool ChestReady);

    HitZone HitTest(Point p)
    {
        float s = Size.Width / (float)PetComposer.WindowW;
        int x = (int)Math.Round(p.X / s), y = (int)Math.Round(p.Y / s);
        bool kb = PetComposer.KeyboardRect.Contains(x, y);
        bool menu = PetComposer.MenuRect.Contains(x, y);
        bool counter = PetComposer.CounterRect.Contains(x, y) && !kb && !menu;
        bool cat = PetComposer.CatRect.Contains(x, y);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long leftTs = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
        long rightTs = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
        string chest = null;
        bool chestReady = false;
        if (leftTs > 0 && (_cfg.AlwaysShowChest || _hovering || leftTs <= now) && PetComposer.ChestLeftRect.Contains(x, y))
        { chest = "left"; chestReady = leftTs <= now; }
        else if (rightTs > 0 && (_cfg.AlwaysShowChest || _hovering || rightTs <= now) && PetComposer.ChestRightRect.Contains(x, y))
        { chest = "right"; chestReady = rightTs <= now; }
        return new HitZone(cat, counter, kb, menu, chest, chestReady);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { RefreshMenu(); _menu.Show(this, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;

        var zone = HitTest(e.Location);

        float scale = Size.Width / (float)PetComposer.WindowW;
        var logical = new PointF(e.X / scale, e.Y / scale);
        if (_wheel.Open && _wheel.Hit(logical, PetComposer.CatX, PetComposer.CatY, out long emoteId))
        {
            Log.Write("DBG wheel click emote=" + emoteId + " at " + logical.X + "," + logical.Y);
            SendEmoteById(emoteId);
            Compose();
            return;
        }

        if (!string.IsNullOrEmpty(zone.Chest))
        {
            if (!zone.ChestReady) return;
            Log.Write("DBG chest click " + zone.Chest);
            ChestOpenRequested?.Invoke(zone.Chest);
            return;
        }
        if (zone.Keyboard) { CycleKeyboard(); return; }
        if (zone.Menu || zone.Counter)
        {
            _room.AddClicks(1);
            Compose();
            OpenPanelRequested?.Invoke();
            return;
        }
        if (!zone.Cat) return;

        _room.AddClicks(1);
        _room.NotifyLocalAction();
        HandDown();
        Compose();
        _dragging = true;
        _moved = false;
        _dragCursor = Cursor.Position;
        _dragOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }
        var now = Cursor.Position;
        int dx = now.X - _dragCursor.X, dy = now.Y - _dragCursor.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        _moved = true;
        Location = new Point(_dragOrigin.X + dx, _dragOrigin.Y + dy);
        SnapToWorkingArea(now);
        if (dx < 0) SetBody(2); else if (dx > 0) SetBody(3);
        _moveReset.Stop();
        _moveReset.Start();
        // 高刷新率鼠标每秒能产生上千次移动；键盘跟随最多按 60Hz 更新即可，
        // 否则每次移动都重排键盘窗口，拖动时会吃掉一个核。
        long tick = Environment.TickCount64;
        if (tick - _lastGeometryMs >= 16)
        {
            _lastGeometryMs = tick;
            GeometryChanged?.Invoke();
        }
    }

    /// <summary>拖动到屏幕/任务栏边缘附近时自动贴边，松手后位置照常保存。</summary>
    void SnapToWorkingArea(Point cursor)
    {
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int threshold = Math.Max(8, (int)Math.Round(28 * DpiScale));
        float s = Width / (float)PetComposer.WindowW;
        int contentLeftOffset = (int)Math.Round(PetComposer.CatX * s);
        int contentRightOffset = (int)Math.Round(Math.Max(PetComposer.ToolbarX + PetComposer.ToolbarW, PetComposer.ChestRightRect.Right) * s);
        int left = Left, top = Top;

        // 左右按角色/HUD 的可见范围判断，避免窗口透明边距导致看起来没有贴住。
        if (Math.Abs(Left + contentLeftOffset - wa.Left) <= threshold)
            left = wa.Left - contentLeftOffset;
        else if (Math.Abs(Left + contentRightOffset - wa.Right) <= threshold)
            left = wa.Right - contentRightOffset;

        // 下方窗口底边就是计数条底边，正好落在任务栏上沿；上方保留气泡净空。
        if (Math.Abs(Top - wa.Top) <= threshold) top = wa.Top;
        else if (Math.Abs(Top + Height - wa.Bottom) <= threshold) top = wa.Bottom - Height;

        if (left != Left || top != Top) Location = new Point(left, top);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_moved) SnapToWorkingArea(Cursor.Position);
            _dragging = false;
            _cfg.PetX = Left;
            _cfg.PetY = Top;
            ConfigStore.Save(_cfg);
            GeometryChanged?.Invoke();
            if (!_cfg.AllowOverlap) ResolveOverlaps();
        }
    }

    // ---------- 窗口样式 ----------

    bool StyleDirty;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = unchecked((int)0x80000000L) | 0x10000000;
            cp.ExStyle = 0x00080000 | 0x00000080 | 0x08000000
                | ((_cfg == null || _cfg.Topmost) ? 0x00000008 : 0)
                | ((_cfg != null && _cfg.ClickThrough) ? 0x00000020 : 0);
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>供游戏模式规则/外部控制切换鼠标穿透；会同步保存并刷新菜单与面板。</summary>
    public void SetClickThrough(bool enabled)
    {
        if (_cfg.ClickThrough == enabled) return;
        _cfg.ClickThrough = enabled;
        Save();
        ApplySettings();
        NotifySettingsChanged();
    }

    public void ApplySettings()
    {
        if (!_cfg.HoverFade)
        {
            _hoverFadeTimer?.Stop();
            _hoverFadeAnimTimer?.Stop();
            _hoverFadeTarget = false;
            _hoverFadeAmount = 0;
        }
        else if (_hovering && !_hoverFadeTarget)
        {
            // 运行中打开悬停半透明时，也按正常停留时间开始淡出。
            _hoverFadeTimer.Stop();
            _hoverFadeTimer.Start();
        }
        if (_cfg.ClickThrough)
        {
            _wheelShowTimer?.Stop();
            _wheel.Close();
        }
        Size = WindowSize();
        ApplyWindowStyles();
        Compose();
        UpdateCatScales();
        ApplyCaptureMode();
        if (!_cfg.AllowOverlap) ResolveOverlaps();
        RefreshMenu();
    }

    /// <summary>打开/关闭/调整 OBS 绿幕窗口。绿幕窗口只复制主角，不影响桌面桌宠。</summary>
    public void ApplyCaptureMode()
    {
        int w = Math.Clamp(_cfg.CaptureWidth, 160, 1600);
        int h = Math.Clamp(_cfg.CaptureHeight, 160, 1600);
        if (!_cfg.CaptureMode)
        {
            var old = _capture;
            _capture = null;
            if (old != null)
            {
                try { old.ClosingFromOwner = true; old.Close(); old.Dispose(); } catch { }
            }
            return;
        }

        if (_capture == null || _capture.IsDisposed)
        {
            _capture = new CaptureWindow(this);
            _capture.FormClosed += (s, e) => { if (ReferenceEquals(_capture, s)) _capture = null; };
            _capture.Show();
        }
        _capture.SetCaptureSize(w, h);
        _capture.Invalidate();
    }

    /// <summary>绿幕窗口右键关闭时同步关掉设置开关，避免下次 ApplySettings 又弹出来。</summary>
    public void DisableCaptureMode()
    {
        _cfg.CaptureMode = false;
        Save();
        ApplyCaptureMode();
        NotifySettingsChanged();
    }

    /// <summary>用户拖动绿幕窗口边框调整大小后回写设置，下次打开保持该尺寸。</summary>
    public void SetCaptureSizeFromWindow(int width, int height)
    {
        width = Math.Clamp(width, 160, 1600);
        height = Math.Clamp(height, 160, 1600);
        if (_cfg.CaptureWidth == width && _cfg.CaptureHeight == height) return;
        _cfg.CaptureWidth = width;
        _cfg.CaptureHeight = height;
        Save();
        NotifySettingsChanged();
    }

    /// <summary>用户从任务栏/Alt+F4 关闭绿幕窗口时，把设置开关同步关掉。</summary>
    public void OnCaptureWindowUserClosed()
    {
        if (!_cfg.CaptureMode) return;
        _cfg.CaptureMode = false;
        Save();
        NotifySettingsChanged();
    }

    /// <summary>
    /// 把主角完整 GUI（角色、数字、计数条、宝箱、昵称和按钮）画到 OBS 绿幕窗口。
    /// 键盘/手柄 GUI 由 CaptureWindow 另外调用 KeyboardWindow.DrawTo 拼接。
    /// </summary>
    public void DrawCapture(Graphics g, Rectangle dest)
    {
        if (_frames == null || dest.Width <= 0 || dest.Height <= 0) return;
        if (!_frames.Ready)
        {
            if (!_frames.Loading) return;
            var state = g.Save();
            try
            {
                g.TranslateTransform(dest.X, dest.Y);
                float ls = dest.Width / (float)PetComposer.WindowW;
                PetComposer.DrawLoading(g, ls, (Environment.TickCount64 % 1000) / 1000.0,
                    PetComposer.CatX + PetComposer.CatSize / 2f,
                    PetComposer.CatY + PetComposer.CatSize / 2f);
            }
            finally { g.Restore(state); }
            return;
        }
        // 按采集窗口的最终像素尺寸合成，而不是拿缩小后的桌宠窗口位图放大。
        // 桌宠 Scale 很小时窗口可能只有 136×165；直接放大到 OBS 绿幕尺寸会明显糊。
        int w = Math.Max(1, dest.Width), h = Math.Max(1, dest.Height);
        if (_captureFrame == null || _captureFrame.Width != w || _captureFrame.Height != h)
        {
            _captureFrame?.Dispose();
            _captureFrame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        var info = BuildInfo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        info.Loading = _frames.Loading;
        info.LoadingPhase = (Environment.TickCount64 % 1000) / 1000.0;
        PetComposer.ComposeInto(_captureFrame, _frames, _bodyFrame, _headFrame, info, 1.0);
        // 位图已经就是目标尺寸，一比一贴图，避免再次双三次插值把文字和边缘磨糊。
        g.DrawImageUnscaled(_captureFrame, dest.X, dest.Y);
    }

    void ApplyWindowStyles()
    {
        if (!IsHandleCreated) return;
        long ex = GetWindowLongPtr(Handle, -20).ToInt64();
        if (_cfg.ClickThrough) ex |= 0x00000020L;
        else ex &= ~0x00000020L;
        SetWindowLongPtr(Handle, -20, new IntPtr(ex));
        SetWindowPos(Handle, _cfg.Topmost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
    }

    static readonly IntPtr HwndTopMost = new(-1);
    static readonly IntPtr HwndNoTopMost = new(-2);

    public bool IsDragging => _dragging;

    public void RefreshHud() => Compose();

    /// <summary>表情栏本地槽位变更后刷新轮盘。</summary>
    public void OnEmoteSlotsChanged()
    {
        _wheel.Close();
        if (_hovering) ShowWheel();
        else Compose();
    }

    void CycleKeyboard()
    {
        _cfg.KeyboardMode = _cfg.KeyboardMode switch
        {
            "hidden" => "letters",
            "letters" => _cfg.RhythmMode ? "rhythm" : "wuwa",
            "wuwa" => "controller",
            "rhythm" => "controller",
            _ => "hidden"
        };
        ConfigStore.Save(_cfg);
        KeyboardModeChanged?.Invoke();
        Compose();
    }

    public event Action KeyboardModeChanged;

    // ---------- 表情轮盘 / 表情发送 ----------

    void ShowWheel()
    {
        if (_hiddenByHotkey || _wheel.Open) return;
        Log.Write("DBG wheel open");
        _wheel.Show(_cfg.EmoteAssetIds, id =>
        {
            var entry = _catalog.Find("emote", id);
            if (entry == null) return null;
            string url = _catalog.CdnUrl(entry.Url);
            return _store.Get(url, () => Safe(Compose));
        });
        RequestCompose();
    }

    /// <summary>宝箱奖励在头顶弹一个奖励图气泡（自动领取时也给用户可见反馈）。</summary>
    public void ShowPrize(string assetType, long assetId)
    {
        string url = null;
        var entry = _catalog.Find(assetType, assetId);
        if (entry != null) url = _catalog.CdnUrl(entry.Url);
        if (string.IsNullOrEmpty(url)) return;
        var img = _store.GetBubbleImage(url, onReady: () => Safe(() => { var loaded = _store.GetBubbleImage(url, allowDownload: false); if (loaded != null) PushBubble(loaded); }));
        if (img != null) PushBubble(img);
    }

    public async void SendEmoteById(long id)
    {
        if (id <= 0) return;
        var entry = _catalog.Find("emote", id);
        if (entry != null)
        {
            string url = _catalog.CdnUrl(entry.Url);
            var img = _store.GetBubbleImage(url, onReady: () => Safe(() => { var loaded = _store.GetBubbleImage(url, allowDownload: false); if (loaded != null) PushBubble(loaded); }));
            if (img != null) PushBubble(img);
        }
        try { await _room.SendEmoteAsync(id); } catch (Exception ex) { Log.Write("发送表情失败 " + ex.Message); }
    }

    void PushBubble(Image image)
    {
        _bubbles.Add(image);
        RequestCompose();
    }

    /// <summary>按键边沿生成一个键帽气泡；与表情和动作共享活动动画时钟。</summary>
    void PushKeyBubble(int vk)
    {
        if (!_cfg.KeyBubbles || _hiddenByHotkey || !Visible) return;
        if (vk == 0x01 || vk == 0x02) return; // 鼠标按键只计入点击，不生成键帽气泡
        if (!_keyBubbles.Add(vk)) return;
        RequestCompose();
    }

    /// <summary>手柄按键边沿生成一个键帽气泡；肩键/扳机用浅金色强调。</summary>
    public void PushGamepadBubble(string label)
    {
        if (!_cfg.KeyBubbles || _hiddenByHotkey || !Visible) return;
        bool accent = label is "LB" or "RB" or "LT" or "RT";
        if (!_keyBubbles.AddLabel(label, accent)) return;
        RequestCompose();
    }

    // ---------- 全局快捷键 ----------
    //
    // 不用 RegisterHotKey：那会独占按键，其他程序收不到 F5/F7/F8/F9，
    // 而且注册失败还会留下“按了没反应”的半残状态。
    // 改成在 Raw Input 的被动事件流里匹配：按键照常传给前台程序，只额外触发我们的动作。

    // Raw Input 线程会并发读取：重建时整体替换引用，避免字典写入时读线程抛异常。
    volatile Dictionary<(uint Mods, uint Vk), string> _hotkeyMap = new();

    /// <summary>
    /// 键位默认与原版 Helper 一致（F5/F7/F8/F9、Ctrl+Alt+1~6），
    /// 但全部取自配置，设置页改键后调用 RebindHotkeys 立即生效。
    /// </summary>
    void RebuildHotkeyMap()
    {
        var map = new Dictionary<(uint, uint), string>();
        var bound = new List<string>();
        foreach (var pair in Hotkeys.FromSettings(_cfg.Hotkeys))
        {
            if (!Hotkeys.TryParse(pair.Value, out uint mods, out uint vk) || vk == 0) continue;
            map[(mods, vk)] = pair.Key;
            bound.Add($"{pair.Key}={pair.Value}");
        }
        _hotkeyMap = map;
        if (bound.Count > 0) Log.Write("快捷键已启用（被动监听） " + string.Join(" ", bound));
    }

    /// <summary>设置页改键后重建匹配表。</summary>
    public void RebindHotkeys() => RebuildHotkeyMap();

    /// <summary>Raw Input 线程回调：按键照常下发给前台程序，这里只做匹配。</summary>
    void OnHotkeyKeyDown(int vk, bool ctrl, bool alt, bool shift)
    {
        uint mods = (ctrl ? Hotkeys.ModControl : 0) | (alt ? Hotkeys.ModAlt : 0) | (shift ? Hotkeys.ModShift : 0);
        var map = _hotkeyMap;
        if (map.TryGetValue((mods, (uint)vk), out string action))
            Safe(() => HandleHotkey(action));
    }

    void HandleHotkey(string action)
    {
        if (action == "reload")
        {
            RefreshRequested?.Invoke();
            NotifyHotkey("已刷新界面");
        }
        else if (action == "game")
        {
            // 原版“游戏模式”就是鼠标穿透：桌宠只作画面存在，不响应点击。
            _cfg.ClickThrough = !_cfg.ClickThrough;
            Save();
            ApplySettings();
            NotifyHotkey(_cfg.ClickThrough ? "已开启游戏模式（鼠标穿透）" : "已关闭游戏模式（鼠标穿透）");
            NotifySettingsChanged();
        }
        else if (action == "monitor")
        {
            var screens = Screen.AllScreens;
            if (screens.Length < 2) return;
            var current = Screen.FromControl(this);
            int idx = Array.IndexOf(screens, current);
            var next = screens[(idx + 1) % screens.Length];
            var wa = next.WorkingArea;
            Location = new Point(wa.Right - Width - 40, wa.Bottom - Height - 60);
            _cfg.PetX = Left;
            _cfg.PetY = Top;
            Save();
            GeometryChanged?.Invoke();
        }
        else if (action == "visibility")
        {
            _hiddenByHotkey = !_hiddenByHotkey;
            Visible = !_hiddenByHotkey;
            foreach (var c in _cats.Values) c.Visible = !_hiddenByHotkey;
            if (!_hiddenByHotkey) Compose();
            PetVisibilityChanged?.Invoke(!_hiddenByHotkey);
        }
        else if (action.StartsWith("emote", StringComparison.Ordinal) && int.TryParse(action[5..], out int slot))
        {
            var emotes = _cfg.EmoteAssetIds ?? Array.Empty<long>();
            if (slot >= 1 && slot <= emotes.Length) SendEmoteById(emotes[slot - 1]);
        }
        else if (action == "capture")
        {
            _cfg.CaptureMode = !_cfg.CaptureMode;
            Save();
            ApplyCaptureMode();
            NotifyHotkey(_cfg.CaptureMode ? "已开启 OBS 绿幕窗口" : "已关闭 OBS 绿幕窗口");
            NotifySettingsChanged();
        }
        else if (action == "mainPanel")
        {
            OpenMainPanelRequested?.Invoke();
        }
        else if (action == "catLayout")
        {
            ToggleCatLayout(true);
        }
        else if (action == "keyBubbles")
        {
            _cfg.KeyBubbles = !_cfg.KeyBubbles;
            if (!_cfg.KeyBubbles) _keyBubbles.Clear();
            Save();
            Compose();
            NotifyHotkey(_cfg.KeyBubbles ? "已开启按键气泡" : "已关闭按键气泡");
            NotifySettingsChanged();
        }
        else if (action == "rhythmMode")
        {
            _cfg.RhythmMode = !_cfg.RhythmMode;
            if (_cfg.RhythmMode) _cfg.KeyboardMode = "rhythm";
            else if (_cfg.KeyboardMode == "rhythm") _cfg.KeyboardMode = "wuwa";
            Save();
            KeyboardModeChanged?.Invoke();
            Compose();
            NotifyHotkey(_cfg.RhythmMode ? "已开启音游模式" : "已关闭音游模式");
            NotifySettingsChanged();
        }
        else if (action == "autoEmote")
        {
            _cfg.AutoEmoteEnabled = !_cfg.AutoEmoteEnabled;
            Save();
            AutoSettingsChanged?.Invoke();
            NotifyHotkey(_cfg.AutoEmoteEnabled ? "已开启自动表情" : "已关闭自动表情");
            NotifySettingsChanged();
        }
        else if (action == "autoClick")
        {
            _cfg.AutoClickEnabled = !_cfg.AutoClickEnabled;
            Save();
            AutoSettingsChanged?.Invoke();
            NotifyHotkey(_cfg.AutoClickEnabled ? "已开启自动敲击" : "已关闭自动敲击");
            NotifySettingsChanged();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---------- 菜单 / 托盘 ----------

    void BuildMenu()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开面板", null, (s, e) => OpenPanelRequested?.Invoke());
        _menu.Items.Add("表情轮盘", null, (s, e) => ShowWheel());
        _topmostItem = new ToolStripMenuItem("窗口置顶") { Checked = _cfg.Topmost };
        _topmostItem.Click += (s, e) => { _cfg.Topmost = _topmostItem.Checked = !_cfg.Topmost; Save(); ApplySettings(); NotifySettingsChanged(); };
        _menu.Items.Add(_topmostItem);
        _hoverItem = new ToolStripMenuItem("鼠标悬停时半透明") { Checked = _cfg.HoverFade };
        _hoverItem.Click += (s, e) => { _cfg.HoverFade = _hoverItem.Checked = !_hoverItem.Checked; Save(); ApplySettings(); NotifySettingsChanged(); };
        _menu.Items.Add(_hoverItem);
        _passthroughItem = new ToolStripMenuItem("鼠标穿透");
        _passthroughItem.Click += (s, e) => { _cfg.ClickThrough = true; Save(); ApplySettings(); NotifySettingsChanged(); };
        _menu.Items.Add(_passthroughItem);
        _passthroughOffItem = new ToolStripMenuItem("取消鼠标穿透", null, (s, e) => { _cfg.ClickThrough = false; Save(); ApplySettings(); NotifySettingsChanged(); });
        _menu.Items.Add(_passthroughOffItem);

        _scaleMenuItem = new ToolStripMenuItem("桌宠大小");
        foreach (var v in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) =>
            {
                _cfg.Scale = value;
                Save();
                ApplySettings();
                // 面板里的滑条要跟着右键菜单的预设档位更新。
                NotifySettingsChanged();
            };
            _scaleMenuItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_scaleMenuItem);

        _opacityMenuItem = new ToolStripMenuItem("桌宠透明度");
        foreach (var v in new[] { 1.0, 0.9, 0.75, 0.5 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) =>
            {
                _cfg.Opacity = value;
                Save();
                ApplySettings();
                NotifySettingsChanged();
            };
            _opacityMenuItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_opacityMenuItem);

        var layout = new ToolStripMenuItem("排列方向");
        _layoutHorizontalItem = new ToolStripMenuItem("横向排列", null, (s, e) => SetCatLayout("horizontal"));
        _layoutVerticalItem = new ToolStripMenuItem("纵向排列", null, (s, e) => SetCatLayout("vertical"));
        layout.DropDownItems.Add(_layoutHorizontalItem);
        layout.DropDownItems.Add(_layoutVerticalItem);
        _menu.Items.Add(layout);

        var kb = new ToolStripMenuItem("桌面键盘");
        foreach (var (key, label) in new[] { ("hidden", "不显示"), ("letters", "26 键"), ("wuwa", "鸣潮专属"), ("rhythm", "音游模式"), ("controller", "手柄") })
        {
            string k = key;
            var item = new ToolStripMenuItem(label) { Checked = _cfg.KeyboardMode == key };
            item.Click += (s, e) => { _cfg.KeyboardMode = k; Save(); KeyboardModeChanged?.Invoke(); Compose(); RefreshMenu(); };
            kb.DropDownItems.Add(item);
        }
        _menu.Items.Add(kb);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("重置位置", null, (s, e) => { Location = DefaultLocation(); _cfg.PetX = Left; _cfg.PetY = Top; Save(); GeometryChanged?.Invoke(); });
        _menu.Items.Add("重启桌宠", null, (s, e) => RestartPetApp());
        _menu.Items.Add("退出", null, (s, e) => Close());
        _menu.Opening += (s, e) => RefreshMenu();
    }

    void RefreshMenu()
    {
        if (_menu == null) return;
        _topmostItem.Checked = _cfg.Topmost;
        _hoverItem.Checked = _cfg.HoverFade;
        _passthroughOffItem.Visible = _cfg.ClickThrough;
        _passthroughItem.Enabled = !_cfg.ClickThrough;
        if (_scaleMenuItem != null)
        {
            int scalePct = (int)Math.Round(Math.Clamp(_cfg.Scale, 0.25, 2.0) * 100);
            _scaleMenuItem.Text = $"桌宠大小（{scalePct}%）";
            foreach (ToolStripItem sub in _scaleMenuItem.DropDownItems)
            {
                if (sub is ToolStripMenuItem scaleItem && scaleItem.Tag is double preset)
                    scaleItem.Checked = Math.Abs(_cfg.Scale - preset) < 0.005;
            }
        }
        if (_opacityMenuItem != null)
        {
            int opacityPct = (int)Math.Round(Math.Clamp(_cfg.Opacity, 0.2, 1.0) * 100);
            _opacityMenuItem.Text = $"桌宠透明度（{opacityPct}%）";
            foreach (ToolStripItem sub in _opacityMenuItem.DropDownItems)
            {
                if (sub is ToolStripMenuItem opacityItem && opacityItem.Tag is double preset)
                    opacityItem.Checked = Math.Abs(_cfg.Opacity - preset) < 0.005;
            }
        }
        bool verticalLayout = string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase);
        if (_layoutHorizontalItem != null) _layoutHorizontalItem.Checked = !verticalLayout;
        if (_layoutVerticalItem != null) _layoutVerticalItem.Checked = verticalLayout;
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is not ToolStripMenuItem mi || mi.Text != "桌面键盘") continue;
            string active = _cfg.KeyboardMode switch
            {
                "letters" => "26 键",
                "wuwa" => "鸣潮专属",
                "rhythm" => "音游模式",
                "controller" => "手柄",
                _ => "不显示"
            };
            foreach (ToolStripItem sub in mi.DropDownItems)
            {
                var subItem = (ToolStripMenuItem)sub;
                if (subItem.Text == "鸣潮专属") subItem.Visible = !_cfg.RhythmMode;
                else if (subItem.Text == "音游模式") subItem.Visible = _cfg.RhythmMode;
                subItem.Checked = subItem.Text == active;
            }
        }
    }

    void RestartPetApp()
    {
        bool started = false;
        try
        {
            string exe = Application.ExecutablePath;
            // 先释放单实例锁，否则新进程会被旧实例判断为第二个实例而直接退出。
            SingleInstance.ReleaseForRelaunch();
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            started = true;
        }
        catch (Exception ex)
        {
            Log.Write("重启桌宠失败 " + ex);
            Notify("重启桌宠失败，请查看日志");
            // 已经释放过单实例锁；启动失败时尽量重新拿回来，避免当前实例失去单实例保护。
            try
            {
                if (SingleInstance.Acquire())
                    SingleInstance.SetActivationHandler(() => BeginInvoke(new Action(() => OpenPanelRequested?.Invoke())));
            }
            catch { }
        }
        if (started) Close();
    }

    void Save() => ConfigStore.Save(_cfg);

    /// <summary>快捷键动作的托盘通知可关闭；不传按键本身的输入，只报告动作结果。</summary>
    void NotifyHotkey(string text)
    {
        if (_cfg.NotifyHotkeys) Notify(text);
    }

    void NotifySettingsChanged() => SettingsChanged?.Invoke();

    void BuildTray()
    {
        _icon = AppIcon.Tray;
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Wuwatappo",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (s, e) => OpenPanelRequested?.Invoke();
        _tray.BalloonTipClicked += (s, e) => OpenPanelRequested?.Invoke();
    }

    /// <summary>托盘气泡。后台线程（如自动领宝箱）也会调用，必须切回 UI 线程。</summary>
    public void Notify(string text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (InvokeRequired)
        {
            Safe(() => Notify(text));
            return;
        }
        try { _tray.ShowBalloonTip(3000, "Wuwatappo", text, ToolTipIcon.Info); } catch { }
    }

    // ---------- 成员桌宠 ----------

    void Safe(Action a)
    {
        try { if (IsHandleCreated) BeginInvoke(a); } catch { }
    }

    void UpdateCat(long id)
    {
        if (_room.Members.TryGetValue(id, out var m))
        {
            // 单个成员的显示开关与“隐藏其他成员”总开关互相独立：任一为隐藏就移除。
            if (!m.Online || !m.Visible || _cfg.HideOtherPets) { RemoveCat(id); return; }
            // 外观快照还没到（RoleKey 为空）时先不建；已有桌宠不要因为动作包清空外观而删掉
            if (string.IsNullOrEmpty(m.RoleKey) && !_cats.ContainsKey(id)) return;
            if (_cats.TryGetValue(id, out var existing))
            {
                existing.SetScale(RemoteScaleFactor);
                existing.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
                existing.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
                existing.RefreshMember();
            }
            else
            {
                var cat = new RemoteCatWindow(m, _loader, _catalog, _store);
                cat.SetScale(RemoteScaleFactor);
                cat.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
                cat.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
                cat.RemoteScaleRequested += ApplyRemoteScale;
                cat.RemoteOpacityRequested += ApplyRemoteOpacity;
                cat.Location = PlaceNewCat(id);
                cat.Moved += p =>
                {
                    _cfg.MemberPositions[CatKey(id)] = new[] { p.X, p.Y };
                    Save();
                    if (!_cfg.AllowOverlap) ResolveOverlaps(id);
                };
                _cats[id] = cat;
                cat.Show();
            }
        }
    }

    string CatKey(long id) => id.ToString();

    Point? StoredCatPoint(long id)
    {
        if (!_cfg.MemberPositions.TryGetValue(CatKey(id), out var a) || a == null || a.Length < 2) return null;
        var p = new Point(a[0], a[1]);
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        // 窗口上方 130px 是透明的气泡净空。靠近屏幕顶部时窗口 Y 本来就可以是负数，
        // 以前按整个 341px 窗口夹紧会把猫向下推 130px，看起来像坐标记错了。
        // 只要实际可见的猫/昵称条和任一屏幕工作区有交集，就原样恢复。
        var visible = new Rectangle(p.X, p.Y + head, w, catH);
        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(visible))) return p;

        // 屏幕布局变了或位置完全丢到屏幕外时才夹紧；夹的是可见区域，不是透明净空。
        var wa = Screen.FromPoint(new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2)).WorkingArea;
        int x = Math.Clamp(visible.Left, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = Math.Clamp(visible.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - catH));
        return new Point(x, y - head);
    }

    /// <summary>
    /// 新成员/回房成员位置判定：两张桌宠重叠面积超过新桌宠可见面积的 30% 才算冲突。
    /// 贴边或手动微调时允许少量重叠，避免因为一两像素就强制换位。
    /// </summary>
    const double CatOverlapTolerance = 0.30;

    static double OverlapRatio(Rectangle candidate, Rectangle obstacle)
    {
        var inter = Rectangle.Intersect(candidate, obstacle);
        if (inter.IsEmpty || candidate.Width <= 0 || candidate.Height <= 0) return 0;
        return (double)inter.Width * inter.Height / ((double)candidate.Width * candidate.Height);
    }

    static bool HasSignificantOverlap(Rectangle candidate, IEnumerable<Rectangle> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (OverlapRatio(candidate, obstacle) > CatOverlapTolerance) return true;
        }
        return false;
    }

    Rectangle CatVisibleRectAt(Point location)
    {
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        return new Rectangle(location.X, location.Y + head, w, catH);
    }

    List<Rectangle> VisibleCatRects()
    {
        var list = new List<Rectangle>();
        foreach (var cat in _cats.Values)
        {
            if (cat.Visible) list.Add(CatVisibleRect(cat));
        }
        var local = LocalVisibleRect();
        if (!local.IsEmpty) list.Add(local);
        return list;
    }

    Point PlaceNewCat(long id)
    {
        var stored = StoredCatPoint(id);
        if (stored.HasValue && !HasSignificantOverlap(CatVisibleRectAt(stored.Value), VisibleCatRects()))
            return stored.Value;

        var point = DefaultCatPoint();
        // 保存自动安排的位置；下次回房时优先恢复，若与后来的人冲突再重新安排。
        _cfg.MemberPositions[CatKey(id)] = new[] { point.X, point.Y };
        Save();
        return point;
    }

    /// <summary>
    /// 新成员默认从当前屏幕右下角开始摆放：横向时贴着工作区底边从右往左，排满后向上换行；
    /// 纵向时贴着工作区右边，第一列从右下往上，排满后向左换列并从顶部往下。
    /// 碰撞按桌宠实际可见区域判断，透明的气泡净空不占位置；本机桌宠也视为已占位。
    /// </summary>
    Point DefaultCatPoint()
    {
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int h = (int)Math.Round(RemoteCatWindow.H * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        int gap = Math.Max(4, (int)Math.Round(8 * s));
        int stepX = Math.Max(1, w + gap);
        int stepY = Math.Max(1, catH + gap);
        var wa = Screen.FromControl(this).WorkingArea;

        var taken = VisibleCatRects();

        if (string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase))
        {
            int columnRight = wa.Right;
            bool firstColumn = true;
            for (int col = 0; col < 32; col++)
            {
                int start = firstColumn ? wa.Bottom - catH : wa.Top;
                int end = firstColumn ? wa.Top : wa.Bottom - catH;
                for (int y = start; firstColumn ? y >= end : y <= end; y += firstColumn ? -stepY : stepY)
                {
                    int x = columnRight - w;
                    if (x < wa.Left) break;
                    var slot = new Rectangle(x, y, w, catH);
                    if (!HasSignificantOverlap(slot, taken)) return new Point(x, y - head);
                }
                firstColumn = false;
                columnRight -= stepX;
                if (columnRight - w < wa.Left) break;
            }
            return new Point(wa.Left, wa.Top);
        }

        int startX = wa.Right - w;
        int startY = wa.Bottom - h;
        for (int row = 0; row < 32; row++)
        {
            int y = startY - row * stepY;
            if (y < wa.Top) break;
            for (int x = startX; x >= wa.Left; x -= stepX)
            {
                var slot = new Rectangle(x, y + head, w, catH);
                if (!HasSignificantOverlap(slot, taken)) return new Point(x, y);
            }
        }
        return new Point(wa.Left, wa.Top);
    }

    /// <summary>托盘/右键菜单切换排列方向；切换后立即按新方向重排桌宠。</summary>
    public void SetCatLayout(string layout, bool announce = false)
    {
        string next = string.Equals(layout, "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";
        _cfg.CatLayout = next;
        Save();
        ResetCatOrder();
        RefreshMenu();
        NotifySettingsChanged();
        if (announce)
            NotifyHotkey(next == "vertical" ? "已切换为纵向排列" : "已切换为横向排列");
    }

    /// <summary>快捷键切换横纵排列。</summary>
    public void ToggleCatLayout(bool announce = false)
        => SetCatLayout(string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase) ? "horizontal" : "vertical", announce);

    /// <summary>
    /// 联机面板“重置排序”：横向时主角贴右下、其他成员从右往左排成底部横排；
    /// 纵向时沿屏幕右侧向下排列，排满后向左换列并从顶部继续。
    /// 锚点使用可见区域并贴住工作区底边/右边，和手动拖动贴边后的位置一致。
    /// </summary>
    public void ResetCatOrder()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        float s = RemoteScaleFactor;
        float ms = Width / (float)PetComposer.WindowW;
        int gap = Math.Max(6, (int)Math.Round(10 * s));

        // 主窗口右侧有透明边距和宝箱按钮；用实际内容右边界定位，避免主角看起来没贴住屏幕。
        int mainContentRight = (int)Math.Round(Math.Max(PetComposer.ToolbarX + PetComposer.ToolbarW, PetComposer.ChestRightRect.Right) * ms);
        int mainVisibleOffsetX = (int)Math.Round(PetComposer.CatX * ms);
        int mainVisibleW = (int)Math.Round(PetComposer.CatSize * ms);
        int mainVisibleRight = wa.Right - Math.Max(0, mainContentRight - (mainVisibleOffsetX + mainVisibleW));
        int localX = mainVisibleRight - mainVisibleOffsetX - mainVisibleW;
        int localY = Math.Max(wa.Top, wa.Bottom - Height);
        Location = new Point(localX, localY);
        _cfg.PetX = localX;
        _cfg.PetY = localY;

        // 只排列当前房间仍在显示的人；离线/隐藏/已离开的成员即使还残留窗口也不占位。
        var memberIds = _cats.Keys
            .Where(id => _room.Members.TryGetValue(id, out var m) && m.Online && m.Visible)
            .OrderBy(id => id)
            .ToList();
        if (memberIds.Count > 0)
        {
            int rw = Math.Max(1, (int)Math.Round(RemoteCatWindow.W * s));
            int catH = Math.Max(1, (int)Math.Round(RemoteCatWindow.CatH * s));
            int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
            int leftLimit = wa.Left;

            if (string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase))
            {
                // 第一列从主角上方往上排；排满后向左换列，从工作区顶部往下排。
                int columnRight = wa.Right;
                int cursorBottom = localY + (int)Math.Round(PetComposer.CatY * ms) - gap;
                int topCursor = wa.Top;
                bool firstColumn = true;

                foreach (long id in memberIds)
                {
                    int visibleTop;
                    if (firstColumn)
                    {
                        if (cursorBottom - catH >= wa.Top)
                        {
                            visibleTop = cursorBottom - catH;
                            cursorBottom -= catH + gap;
                        }
                        else
                        {
                            firstColumn = false;
                            columnRight -= rw + gap;
                            if (columnRight - rw < leftLimit) break;
                            visibleTop = wa.Top;
                            topCursor = wa.Top + catH + gap;
                        }
                    }
                    else
                    {
                        if (topCursor + catH > wa.Bottom)
                        {
                            columnRight -= rw + gap;
                            if (columnRight - rw < leftLimit) break;
                            topCursor = wa.Top;
                        }
                        visibleTop = topCursor;
                        topCursor += catH + gap;
                    }

                    int x = Math.Max(leftLimit, columnRight - rw);
                    int y = visibleTop - head;
                    if (_cats.TryGetValue(id, out var cat)) cat.Location = new Point(x, y);
                    _cfg.MemberPositions[CatKey(id)] = new[] { x, y };
                }
            }
            else
            {
                // 底部横排：主角贴右下，其他成员从右往左排；一行放不下时向上换行。
                int rowStep = Math.Max(1, catH + gap);
                int rightEdge = localX + mainVisibleOffsetX - gap;
                // 按主角实际猫体底边定位，避免远程猫窗口的 11px 底部留白和缩放取整造成 1px 错位。
                int mainCatBottom = localY + (int)Math.Round(PetComposer.CatY * ms) + (int)Math.Round(PetComposer.CatSize * ms);
                int remoteHead = (int)Math.Round(RemoteCatWindow.Headroom * s);
                int remoteCatSize = (int)Math.Round(RemoteCatWindow.CatSize * s);
                int baseY = Math.Max(wa.Top, mainCatBottom - remoteHead - remoteCatSize);
                int cursorRight = rightEdge;
                int row = 0;

                foreach (long id in memberIds)
                {
                    int rawX = cursorRight - rw;
                    if (rawX < leftLimit && cursorRight != rightEdge)
                    {
                        row++;
                        cursorRight = rightEdge;
                        rawX = cursorRight - rw;
                    }
                    int x = Math.Max(leftLimit, rawX);
                    int y = Math.Max(wa.Top, baseY - row * rowStep);
                    if (_cats.TryGetValue(id, out var cat)) cat.Location = new Point(x, y);
                    _cfg.MemberPositions[CatKey(id)] = new[] { x, y };
                    cursorRight = x - gap;
                }
            }
        }

        Save();
        GeometryChanged?.Invoke();
        if (!_cfg.AllowOverlap) ResolveOverlaps();
    }

    /// <summary>
    /// 关闭“允许重叠”后，把与主角/其他成员相交的联机桌宠向附近空位挪动。
    /// 本地主角位置保持不动，作为固定障碍物；刚被拖动的成员优先让位。
    /// </summary>
    public void ResolveOverlaps(long? priorityId = null)
    {
        if (_cfg.AllowOverlap) return;
        var cats = _cats.Where(kv => kv.Value.Visible).OrderBy(kv => kv.Key).ToList();
        if (priorityId.HasValue)
            cats = cats.OrderBy(kv => kv.Key == priorityId.Value ? 0 : 1).ThenBy(kv => kv.Key).ToList();

        bool changed = false;
        for (int pass = 0; pass < 8; pass++)
        {
            bool movedThisPass = false;
            foreach (var pair in cats)
            {
                var cat = pair.Value;
                var rect = CatVisibleRect(cat);
                var obstacles = new List<Rectangle>();
                var local = LocalVisibleRect();
                if (!local.IsEmpty) obstacles.Add(local);
                foreach (var other in cats)
                    if (other.Key != pair.Key) obstacles.Add(CatVisibleRect(other.Value));
                if (!HasSignificantOverlap(rect, obstacles)) continue;

                var target = FindFreeCatPoint(cat, obstacles);
                if (!target.HasValue) continue;
                cat.Location = target.Value;
                _cfg.MemberPositions[CatKey(pair.Key)] = new[] { target.Value.X, target.Value.Y };
                movedThisPass = true;
                changed = true;
            }
            if (!movedThisPass) break;
        }
        if (changed) Save();
    }

    Rectangle CatVisibleRect(RemoteCatWindow cat)
    {
        float s = RemoteScaleFactor;
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        return new Rectangle(cat.Left, cat.Top + head, cat.Width, catH);
    }

    Rectangle LocalVisibleRect()
    {
        if (!Visible || _hiddenByHotkey) return Rectangle.Empty;
        float s = Width / (float)PetComposer.WindowW;
        var cat = new Rectangle(
            Left + (int)Math.Round(PetComposer.CatX * s),
            Top + (int)Math.Round(PetComposer.CatY * s),
            (int)Math.Round(PetComposer.CatSize * s),
            (int)Math.Round(PetComposer.CatSize * s));
        var hud = new Rectangle(
            Left + (int)Math.Round(PetComposer.CatX * s),
            Top + (int)Math.Round(PetComposer.InfoY * s),
            (int)Math.Round(PetComposer.CatSize * s),
            (int)Math.Round(PetComposer.CounterH * s));
        return Rectangle.Union(cat, hud);
    }

    Point? FindFreeCatPoint(RemoteCatWindow cat, List<Rectangle> obstacles)
    {
        var current = CatVisibleRect(cat);
        var wa = Screen.FromPoint(new Point(current.Left + current.Width / 2, current.Top + current.Height / 2)).WorkingArea;
        float s = RemoteScaleFactor;
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int w = cat.Width;
        int h = (int)Math.Round(RemoteCatWindow.CatH * s);
        int stepX = Math.Max(24, (int)Math.Round(w * 0.5));
        int stepY = Math.Max(24, (int)Math.Round(h * 0.5));
        int maxX = Math.Max(wa.Left, wa.Right - w);
        int maxY = Math.Max(wa.Top, wa.Bottom - h);

        for (int row = 0; row < 8; row++)
        {
            int y = Math.Clamp(current.Top - row * stepY, wa.Top, maxY);
            for (int dist = 0; dist <= 8; dist++)
            {
                int[] signs = dist == 0 ? new[] { 0 } : new[] { -1, 1 };
                foreach (int sign in signs)
                {
                    int x = Math.Clamp(current.Left + sign * dist * stepX, wa.Left, maxX);
                    var candidate = new Rectangle(x, y, w, h);
                    if (candidate.Left < wa.Left || candidate.Top < wa.Top ||
                        candidate.Right > wa.Right || candidate.Bottom > wa.Bottom) continue;
                    if (!HasSignificantOverlap(candidate, obstacles))
                        return new Point(x, y - head);
                }
            }
        }
        return DefaultCatPoint();
    }

    void UpdateCatScales()
    {
        float s = RemoteScaleFactor;
        foreach (var pair in _cats)
        {
            var c = pair.Value;
            int oldWidth = c.Width;
            c.SetScale(s);
            c.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
            c.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
            // 缩放后窗口从左上角变大，贴边排列的成员会被顶出屏幕；按可见猫体修正回工作区。
            if (c.Width != oldWidth) ClampCatToScreen(pair.Key, c);
        }
    }

    void ClampCatToScreen(long id, RemoteCatWindow cat)
    {
        var rect = CatVisibleRect(cat);
        var wa = Screen.FromRectangle(rect).WorkingArea;
        int dx = 0, dy = 0;
        if (rect.Right > wa.Right) dx = wa.Right - rect.Right;
        if (rect.Left + dx < wa.Left) dx = wa.Left - rect.Left;
        if (rect.Bottom > wa.Bottom) dy = wa.Bottom - rect.Bottom;
        if (rect.Top + dy < wa.Top) dy = wa.Top - rect.Top;
        if (dx == 0 && dy == 0) return;
        cat.Location = new Point(cat.Left + dx, cat.Top + dy);
        _cfg.MemberPositions[CatKey(id)] = new[] { cat.Left, cat.Top };
    }

    void ApplyRemoteScale(double value)
    {
        _cfg.RemoteFollowMainScale = false;
        _cfg.RemoteScale = Math.Clamp(value, 0.25, 2.0);
        Save();
        UpdateCatScales();
        if (!_cfg.AllowOverlap) ResolveOverlaps();
        NotifySettingsChanged();
    }

    void ApplyRemoteOpacity(double value)
    {
        _cfg.RemoteFollowMainOpacity = false;
        _cfg.RemoteOpacity = Math.Clamp(value, 0.2, 1.0);
        Save();
        UpdateCatScales();
        NotifySettingsChanged();
    }

    void RemoveCat(long id)
    {
        if (_cats.TryGetValue(id, out var cat))
        {
            _cats.Remove(id);
            cat.Close();
            cat.Dispose();
        }
    }

    public void RefreshMembers() => RefreshCats();

    void RefreshCats()
    {
        foreach (var id in _cats.Keys.ToList())
            if (!_room.Members.ContainsKey(id)) RemoveCat(id);
        foreach (var id in _room.Members.Keys.ToList()) UpdateCat(id);
        if (!_cfg.AllowOverlap) ResolveOverlaps();
    }

    Point DefaultLocation()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        var size = WindowSize();
        return new Point(wa.Right - size.Width - 40, wa.Bottom - size.Height - 60);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Size = WindowSize();
        Compose();
        GeometryChanged?.Invoke();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Size = WindowSize();
        Compose();
        UpdateCatScales();
        if (!_cfg.AllowOverlap) ResolveOverlaps();
        GeometryChanged?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { if (_capture != null) { _capture.ClosingFromOwner = true; _capture.Close(); _capture.Dispose(); } } catch { }
        _captureFrame?.Dispose();
        _captureFrame = null;
        _capture = null;
        foreach (var t in new[] { _blink, _blinkOff, _moveReset, _handReset, _countdownTimer, _wheelCloseTimer, _wheelShowTimer, _hoverFadeTimer, _hoverFadeAnimTimer, _hoverPollTimer, _loadingTimer }) { try { t?.Stop(); t?.Dispose(); } catch { } }
        foreach (var c in _cats.Values.ToList()) { try { c.Close(); c.Dispose(); } catch { } }
        _cats.Clear();
        _composeTimer.Dispose();
        _bubbles.Clear();
        _keyBubbles.Dispose();
        _frameCache.Dispose();
        _surface.Dispose();
        _hook.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _cfg.PetX = Left;
        _cfg.PetY = Top;
        ConfigStore.Save(_cfg);
        base.OnClosed(e);
    }
}
