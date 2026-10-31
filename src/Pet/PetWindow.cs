using System;
using System.Collections.Generic;
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
    int _bodyFrame = 1, _headFrame = 1;
    bool _dragging, _moved;
    bool _handIsLeft;
    long _lastGeometryMs;
    Point _dragCursor, _dragOrigin;
    readonly EmoteBubbleLayer _bubbles = new();
    readonly EmoteWheelLayer _wheel = new();
    System.Windows.Forms.Timer _blink, _blinkOff, _moveReset, _handReset, _emoteTimer, _wheelTimer, _countdownTimer, _wheelCloseTimer;
    long _lastSkin = -1, _lastAcc = -1;
    Image _chestBtnArt, _chestBgArt;
    ContextMenuStrip _menu;
    NotifyIcon _tray;
    ToolStripMenuItem _topmostItem, _passthroughItem, _passthroughOffItem, _hoverItem;
    Icon _icon;

    bool _hiddenByHotkey;

    public event Action OpenPanelRequested;
    public event Action<string> ChestOpenRequested;
    public event Action GeometryChanged;
    public event Action<bool> PetVisibilityChanged;
    /// <summary>F5：重新拉取目录/外观/房间等界面数据。</summary>
    public event Action RefreshRequested;
    /// <summary>窗口设置被快捷键/菜单改动后触发，面板据此刷新开关状态。</summary>
    public event Action SettingsChanged;

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

        _auth.Changed += () => Safe(OnAuthChanged);
        _room.ClicksChanged += () => Safe(Compose);
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
        _hook.Start();
    }

    // ---------- 尺寸 / 渲染 ----------

    float DpiScale => DeviceDpi / 96f;

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
        Log.Write($"DBG reload frames cfg={_cfg.CharacterAssetId}/{_cfg.AccessoryAssetId} ready={_frames?.Ready}");
        if (Size != WindowSize()) Size = WindowSize();
        Compose();
    }

    void OnFramesChanged() => Safe(Compose);

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
        if (_frames == null || !_frames.Ready) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long leftTs = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
        long rightTs = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
        var info = new PetComposer.Info
        {
            Clicks = _room.AvailableClicks,
            KeyboardOn = _cfg.KeyboardMode != "hidden",
            Nickname = _auth.Nickname,
            Bubbles = _bubbles,
            Wheel = _wheel,
            ChestLeftTs = leftTs,
            ChestRightTs = rightTs,
            NowMs = now,
            // 关闭"总是显示宝箱"时：可领取按钮常显；鼠标悬停桌宠时像表情 UI 一样露出倒计时
            ShowChestLeft = leftTs > 0 && (_cfg.AlwaysShowChest || _hovering || leftTs <= now),
            ShowChestRight = rightTs > 0 && (_cfg.AlwaysShowChest || _hovering || rightTs <= now),
            ChestButton = _chestBtnArt,
            ChestCountdown = _chestBgArt,
        };
        // 高频重绘（每次按键、每次计数变化）复用同一张后缓冲。
        var surface = _surface.Begin(Size.Width, Size.Height);
        PetComposer.ComposeInto(surface, _frames, _bodyFrame, _headFrame, info,
            _cfg.HoverFade && _hovering ? 0.4 : Math.Clamp(_cfg.Opacity, 0.1, 1.0));
        _surface.Apply(this);
    }

    bool _hovering;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;
        Compose();
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovering = false;
        Compose();
        _wheelCloseTimer.Start();
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
        _emoteTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _emoteTimer.Tick += (s, e) =>
        {
            bool active = _bubbles.Tick();
            Compose();
            if (!active) _emoteTimer.Stop();
        };
        _wheelTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _wheelTimer.Tick += (s, e) =>
        {
            Compose();
            if (!_wheel.Animating) _wheelTimer.Stop();
        };
        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdownTimer.Tick += (s, e) => { if (_auth.IsLoggedIn) Compose(); };
        _countdownTimer.Start();
        _wheelCloseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _wheelCloseTimer.Tick += (s, e) =>
        {
            if (!Bounds.Contains(Cursor.Position))
            {
                _wheel.Close();
                Compose();
                _wheelCloseTimer.Stop();
            }
        };
        MouseEnter += (s, e) => ShowWheel();
        _blink.Start();
    }

    void SetHead(int f) { if (_headFrame == f) return; _headFrame = f; Compose(); }
    void SetBody(int f) { if (_bodyFrame == f) return; _bodyFrame = f; Compose(); }

    /// <summary>每次按下：左右手交替落下，220ms 后若未抬起自动回位。</summary>
    void HandDown()
    {
        _handIsLeft = !_handIsLeft;
        Log.Write("DBG hand down " + (_handIsLeft ? "left" : "right"));
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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            _cfg.PetX = Left;
            _cfg.PetY = Top;
            ConfigStore.Save(_cfg);
            GeometryChanged?.Invoke();
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

    public void ApplySettings()
    {
        Size = WindowSize();
        ApplyWindowStyles();
        Compose();
        UpdateCatScales();
        RefreshMenu();
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
            "letters" => "wuwa",
            "wuwa" => "controller",
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
        Compose();
        _wheelTimer.Start();
    }

    /// <summary>宝箱奖励在头顶弹一个奖励图气泡（自动领取时也给用户可见反馈）。</summary>
    public void ShowPrize(string assetType, long assetId)
    {
        string url = null;
        var entry = _catalog.Find(assetType, assetId);
        if (entry != null) url = _catalog.CdnUrl(entry.Url);
        if (string.IsNullOrEmpty(url)) return;
        var img = _store.Get(url, () => Safe(() => { var loaded = _store.Get(url); if (loaded != null) PushBubble(loaded); }));
        if (img != null) PushBubble(img);
    }

    public async void SendEmoteById(long id)
    {
        if (id <= 0) return;
        var entry = _catalog.Find("emote", id);
        if (entry != null)
        {
            string url = _catalog.CdnUrl(entry.Url);
            var img = _store.Get(url, () => Safe(() => { var loaded = _store.Get(url); if (loaded != null) PushBubble(loaded); }));
            if (img != null) PushBubble(img);
        }
        try { await _room.SendEmoteAsync(id); } catch (Exception ex) { Log.Write("发送表情失败 " + ex.Message); }
    }

    void PushBubble(Image image)
    {
        _bubbles.Add(image);
        Compose();
        if (!_emoteTimer.Enabled) _emoteTimer.Start();
    }

    // ---------- 全局快捷键 ----------

    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    bool _hotkeysRegistered;
    readonly Dictionary<int, string> _hotkeyActions = new();

    /// <summary>
    /// 键位默认与原版 Helper 一致（F5/F7/F8/F9、Ctrl+Alt+1~6），
    /// 但全部取自配置，设置页改键后调用 RebindHotkeys 立即生效。
    /// </summary>
    void RegisterHotkeys()
    {
        if (_hotkeysRegistered) return;
        _hotkeysRegistered = true;
        _hotkeyHandle = Handle;
        _hotkeyActions.Clear();
        int id = 0x9100;
        var bound = new List<string>();
        foreach (var pair in Hotkeys.FromSettings(_cfg.Hotkeys))
        {
            if (!Hotkeys.TryParse(pair.Value, out uint mods, out uint vk)) continue;
            int atom = id++;
            if (RegisterHotKey(_hotkeyHandle, atom, mods | MOD_NOREPEAT, vk))
            {
                _hotkeyActions[atom] = pair.Key;
                bound.Add($"{pair.Key}={pair.Value}");
            }
            else
                Log.Write($"快捷键注册失败 {pair.Value}（{pair.Key}）");
        }
        if (bound.Count > 0) Log.Write("快捷键已注册 " + string.Join(" ", bound));
    }

    IntPtr _hotkeyHandle;

    void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered) return;
        _hotkeysRegistered = false;
        try
        {
            if (_hotkeyHandle != IntPtr.Zero)
                foreach (var atom in _hotkeyActions.Keys) UnregisterHotKey(_hotkeyHandle, atom);
        }
        catch { }
        _hotkeyActions.Clear();
        _hotkeyHandle = IntPtr.Zero;
    }

    /// <summary>设置页改键后重新注册。</summary>
    public void RebindHotkeys()
    {
        UnregisterHotkeys();
        RegisterHotkeys();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotkeys();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            if (_hotkeyActions.TryGetValue(m.WParam.ToInt32(), out string action)) HandleHotkey(action);
            return;
        }
        base.WndProc(ref m);
    }

    void HandleHotkey(string action)
    {
        if (action == "reload")
        {
            RefreshRequested?.Invoke();
            Notify("已刷新界面");
        }
        else if (action == "game")
        {
            // 原版“游戏模式”就是鼠标穿透：桌宠只作画面存在，不响应点击。
            _cfg.ClickThrough = !_cfg.ClickThrough;
            Save();
            ApplySettings();
            Notify(_cfg.ClickThrough ? "已开启游戏模式（鼠标穿透）" : "已关闭游戏模式（鼠标穿透）");
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
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
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

        var scale = new ToolStripMenuItem("桌宠大小");
        foreach (var v in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Checked = Math.Abs(_cfg.Scale - v) < 0.01 };
            item.Click += (s, e) => { _cfg.Scale = v; Save(); foreach (ToolStripItem x in scale.DropDownItems) ((ToolStripMenuItem)x).Checked = x == item; ApplySettings(); };
            scale.DropDownItems.Add(item);
        }
        _menu.Items.Add(scale);

        var opacity = new ToolStripMenuItem("桌宠透明度");
        foreach (var v in new[] { 1.0, 0.9, 0.75, 0.5 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Checked = Math.Abs(_cfg.Opacity - v) < 0.01 };
            item.Click += (s, e) => { _cfg.Opacity = v; Save(); foreach (ToolStripItem x in opacity.DropDownItems) ((ToolStripMenuItem)x).Checked = x == item; ApplySettings(); };
            opacity.DropDownItems.Add(item);
        }
        _menu.Items.Add(opacity);

        var kb = new ToolStripMenuItem("桌面键盘");
        foreach (var (key, label) in new[] { ("hidden", "不显示"), ("letters", "26 键"), ("wuwa", "鸣潮专属"), ("controller", "手柄") })
        {
            string k = key;
            var item = new ToolStripMenuItem(label) { Checked = _cfg.KeyboardMode == key };
            item.Click += (s, e) => { _cfg.KeyboardMode = k; Save(); KeyboardModeChanged?.Invoke(); Compose(); RefreshMenu(); };
            kb.DropDownItems.Add(item);
        }
        _menu.Items.Add(kb);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("重置位置", null, (s, e) => { Location = DefaultLocation(); _cfg.PetX = Left; _cfg.PetY = Top; Save(); GeometryChanged?.Invoke(); });
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
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is not ToolStripMenuItem mi || mi.Text != "桌面键盘") continue;
            string active = _cfg.KeyboardMode switch
            {
                "letters" => "26 键",
                "wuwa" => "鸣潮专属",
                "controller" => "手柄",
                _ => "不显示"
            };
            foreach (ToolStripItem sub in mi.DropDownItems)
                ((ToolStripMenuItem)sub).Checked = ((ToolStripMenuItem)sub).Text == active;
        }
    }

    void Save() => ConfigStore.Save(_cfg);

    void NotifySettingsChanged() => SettingsChanged?.Invoke();

    void BuildTray()
    {
        _icon = BuildIcon();
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

    Icon BuildIcon()
    {
        try
        {
            string head = System.IO.Path.Combine(Paths.DefaultAssets, "role_1501_1001_head_1.png");
            using var src = Image.FromFile(head);
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, 32, 32);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return SystemIcons.Application; }
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
                existing.SetScale(DpiScale * (float)_cfg.Scale);
                existing.SetWindowOptions(_cfg.Opacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost);
                existing.RefreshMember();
            }
            else
            {
                var cat = new RemoteCatWindow(m, _loader, _catalog, _store);
                cat.SetScale(DpiScale * (float)_cfg.Scale);
                cat.SetWindowOptions(_cfg.Opacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost);
                cat.Location = StoredCatPoint(id) ?? DefaultCatPoint();
                cat.Moved += p =>
                {
                    _cfg.MemberPositions[CatKey(id)] = new[] { p.X, p.Y };
                    Save();
                };
                _cats[id] = cat;
                cat.Show();
            }
        }
    }

    string CatKey(long id) => (_room.RoomNo ?? "") + ":" + id;

    Point? StoredCatPoint(long id)
    {
        if (!_cfg.MemberPositions.TryGetValue(CatKey(id), out var a) || a == null || a.Length < 2) return null;
        var p = new Point(a[0], a[1]);
        var wa = Screen.FromPoint(p).WorkingArea;
        float s = DpiScale * (float)_cfg.Scale;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int h = (int)Math.Round(RemoteCatWindow.H * s);
        int x = Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = Math.Clamp(p.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        return new Point(x, y);
    }

    /// <summary>
    /// 新成员默认摆在角色左侧；一排放不下就往上换行。人多时不能全往左挤，
    /// 否则除了最左边几个，其余都会叠在屏幕边缘。
    /// </summary>
    Point DefaultCatPoint()
    {
        float s = DpiScale * (float)_cfg.Scale;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int h = (int)Math.Round(RemoteCatWindow.H * s);
        int stepX = Math.Max(24, (int)Math.Round((RemoteCatWindow.CatW + 10) * s));
        int stepY = Math.Max(24, (int)Math.Round((RemoteCatWindow.CatH + 10) * s));
        var wa = Screen.FromControl(this).WorkingArea;
        var taken = _cats.Values.Select(c => c.Bounds).ToList();
        int baseY = Top - (int)Math.Round(10 * s);
        for (int row = 0; row < 8; row++)
        {
            int y = baseY - row * stepY;
            if (y < wa.Top) break;
            for (int col = 0; col < 24; col++)
            {
                int x = Left - (col + 1) * stepX;
                if (x < wa.Left) break;
                var r = new Rectangle(x, y, w, h);
                if (!taken.Any(t => t.IntersectsWith(r))) return new Point(x, y);
            }
        }
        return new Point(wa.Left, wa.Top);
    }

    void UpdateCatScales()
    {
        float s = DpiScale * (float)_cfg.Scale;
        foreach (var c in _cats.Values)
        {
            c.SetScale(s);
            c.SetWindowOptions(_cfg.Opacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost);
        }
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
        GeometryChanged?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var t in new[] { _blink, _blinkOff, _moveReset, _handReset, _emoteTimer, _wheelTimer, _countdownTimer, _wheelCloseTimer }) { try { t?.Stop(); t?.Dispose(); } catch { } }
        foreach (var c in _cats.Values.ToList()) { try { c.Close(); c.Dispose(); } catch { } }
        _cats.Clear();
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
