using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌面键盘 / 手柄反馈。布局对齐 Helper：
/// 200px 宽奶油木板；26 键为 15×19 键帽；鸣潮专属 1/2/3 + Q/E/R + 空格 + 鼠标；
/// 手柄为 200×174 面板（仅手柄本体，不再套占位的奶油底板），使用 XInput 轮询。
/// </summary>
public sealed class KeyboardWindow : Form
{
    public enum Mode { Hidden, Letters, Wuwa, Rhythm, Controller }

    const int W = 200;
    const int ToolbarH = 24;
    const int LettersH = ToolbarH + 121;
    const int WuwaH = ToolbarH + 116;
    const int RhythmH = ToolbarH + 86;
    const int PadH = ToolbarH + 156;
    const float KeyW = 15, KeyH = 19, Gap = 3;

    readonly Dictionary<string, RectangleF> _keys = new();
    readonly Dictionary<string, RectangleF> _buttons = new();
    readonly List<RectangleF> _rhythmLanes = new();
    readonly HashSet<string> _down = new();
    bool _rhythmEnabled;
    string[] _rhythmKeys = RhythmKeys.DefaultsFor(4);
    // 原版给每次按下约 70ms 的可见高亮：极快的点按也能看见，不会“一闪就没”。
    readonly Dictionary<string, long> _pulseUntil = new();
    System.Windows.Forms.Timer _pulseTimer;
    System.Windows.Forms.Timer _poll;
    Mode _mode = Mode.Hidden;
    float _scale = 1f;
    bool _painted;
    readonly LayeredSurface _surface = new();
    // 主题调色板只在 ApplyTheme/PreviewTheme 时解析一次；隐藏/未建句柄时也保留状态，显示时直接使用。
    // 所有模式共用同一套：底栏跟随标准时用面板主题，关闭跟随时用独立底栏配色。
    KeyboardTheme _theme;
    KeyboardTheme _themePreview;
    KeyboardTheme ActiveTheme => _themePreview ?? _theme;

    // 手柄状态
    ushort _padButtons;
    byte _lt, _rt;
    short _lx, _ly, _rx, _ry;
    bool _padConnected, _padUnavailable;
    bool _padKeystrokesReady, _padKeystrokeUnavailable;
    uint _padPacket;
    int _padUserIndex = -1;
    System.Windows.Forms.Timer _padReleaseTimer;
    bool _padReleasePending;
    readonly PadInputTracker _padInput = new();
    readonly List<string> _padTriggerLabels = new();

    public Mode CurrentMode => _mode;
    public event Action<Mode> ModeRequested;
    public event Action CloseRequested;
    /// <summary>键盘/手柄画面变化，绿幕采集窗口据此重绘。</summary>
    public event Action VisualChanged;
    public int LogicalHeightForCapture => LogicalHeight;
    public int CaptureWidth => W;
    /// <summary>手柄有新按键按下：驱动主角挥手与点击数（摇杆不参与）。</summary>
    public event Action PadButtonDown;
    /// <summary>手柄具体按键按下：用于按键气泡，每个新按下的键各触发一次。</summary>
    public event Action<string> PadButtonPressed;
    /// <summary>手柄所有按键都松开。</summary>
    public event Action PadButtonUp;

    static readonly (ushort Flag, string Label)[] PadButtonLabels =
    {
        (0x0001, "↑"), (0x0002, "↓"), (0x0004, "←"), (0x0008, "→"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"),
        (0x4000, "X"), (0x8000, "Y"),
    };

    public KeyboardWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, LettersH);
        _theme = KeyboardTheme.Resolve(new Settings());
        BuildLayout(Mode.Letters);
        _poll = new System.Windows.Forms.Timer { Interval = 16 };
        _poll.Tick += (s, e) => Poll();
        // 始终轮询：手柄按键即使不展开键盘界面也要驱动主角。
        _poll.Start();
    }

    /// <summary>逻辑高度：布局以 200px 宽为基准，实际尺寸 = 逻辑 × 缩放。绝不能用当前 Height 反推。</summary>
    int LogicalHeight => _mode switch
    {
        Mode.Letters => LettersH,
        Mode.Wuwa => WuwaH,
        Mode.Rhythm => RhythmH,
        Mode.Controller => PadH,
        _ => LettersH
    };

    void BuildLayout(Mode mode)
    {
        _keys.Clear();
        _buttons.Clear();
        _rhythmLanes.Clear();
        _buttons["26键"] = new RectangleF(2, 2, 44, 18);
        _buttons[_rhythmEnabled ? "音游模式" : "鸣潮专属"] = new RectangleF(50, 2, 64, 18);
        _buttons["手柄"] = new RectangleF(118, 2, 40, 18);
        _buttons["×"] = new RectangleF(W - 22, 2, 20, 18);

        if (mode == Mode.Letters)
        {
            string[] rows = { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            float y = ToolbarH + 10;
            for (int r = 0; r < rows.Length; r++)
            {
                float rowW = rows[r].Length * KeyW + (rows[r].Length - 1) * Gap;
                float x = (W - rowW) / 2f;
                for (int c = 0; c < rows[r].Length; c++)
                    _keys[rows[r][c].ToString()] = new RectangleF(x + c * (KeyW + Gap), y, KeyW, KeyH);
                y += KeyH + 6;
            }
            float ew = 43, sw = 70, ew2 = 43, egap = 3;
            float total = ew + egap + sw + egap + ew2;
            float ex = (W - total) / 2f;
            _keys["MOUSE_L"] = new RectangleF(ex, y, ew, 22);
            _keys["SPACE"] = new RectangleF(ex + ew + egap, y, sw, 22);
            _keys["MOUSE_R"] = new RectangleF(ex + ew + egap + sw + egap, y, ew2, 22);
            ApplyLogicalSize(LettersH);
        }
        else if (mode == Mode.Wuwa)
        {
            float leftX = 10, top = ToolbarH + 10, kw = 33, kh = 30, kg = 5;
            string[] topKeys = { "1", "2", "3" };
            string[] midKeys = { "Q", "E", "R" };
            for (int i = 0; i < 3; i++)
            {
                _keys[topKeys[i]] = new RectangleF(leftX + i * (kw + kg), top, kw, kh);
                _keys[midKeys[i]] = new RectangleF(leftX + i * (kw + kg), top + kh + kg, kw, kh);
            }
            _keys["SPACE"] = new RectangleF(leftX, top + 2 * (kh + kg), 109, 23);
            float mx = leftX + 109 + 10, my = top + 8;
            _keys["MOUSE_L"] = new RectangleF(mx, my, 26, 59);
            _keys["MOUSE_R"] = new RectangleF(mx + 26, my, 26, 59);
            ApplyLogicalSize(WuwaH);
        }
        else if (mode == Mode.Rhythm)
        {
            // 4K/5K/6K 音游指示器：键帽、高亮和轨道文字统一取当前主题调色板。
            int count = Math.Clamp(_rhythmKeys.Length, 4, 6);
            float gap = 4, kh = 42;
            float kw = Math.Min(42f, (W - 12 - (count - 1) * gap) / count);
            float totalW = count * kw + (count - 1) * gap;
            float startX = (W - totalW) / 2f;
            float y = ToolbarH + 12;
            string[] defaults = RhythmKeys.DefaultsFor(count);
            for (int i = 0; i < count; i++)
            {
                string label = i < _rhythmKeys.Length && _rhythmKeys[i].Length > 0
                    ? _rhythmKeys[i]
                    : defaults[i];
                var rect = new RectangleF(startX + i * (kw + gap), y, kw, kh);
                _keys[label] = rect;
                _rhythmLanes.Add(rect);
            }
            ApplyLogicalSize(RhythmH);
        }
        else if (mode == Mode.Controller)
        {
            ApplyLogicalSize(PadH);
        }
    }

    void ApplyLogicalSize(int logicalHeight)
    {
        Size = new Size(
            Math.Max(1, (int)Math.Round(W * _scale)),
            Math.Max(1, (int)Math.Round(logicalHeight * _scale)));
    }

    public void SetMode(Mode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (mode == Mode.Hidden)
        {
            Hide();
            VisualChanged?.Invoke();
            return;
        }
        _down.Clear();
        _pulseUntil.Clear();
        BuildLayout(mode);
        Show();
        if (!_poll.Enabled) _poll.Start();
        Render();
    }

    /// <summary>切换音游模式并更新 4/5/6 个触发键；已显示时立即重建键帽。</summary>
    public void SetRhythm(bool enabled, string[] keys)
    {
        _rhythmEnabled = enabled;
        _rhythmKeys = RhythmKeys.Normalize(keys, keys?.Length ?? 4);
        if (_mode == Mode.Hidden) return;
        _down.Clear();
        _pulseUntil.Clear();
        BuildLayout(_mode);
        Render();
    }

    /// <summary>
    /// 应用正式主题配置。窗口隐藏或句柄尚未创建时也保存调色板，
    /// 下次 SetMode/Show 时会直接用新配色绘制。
    /// </summary>
    public void ApplyTheme(Settings cfg)
    {
        if (cfg == null) return;
        _theme = KeyboardTheme.Resolve(cfg);
        _themePreview = null;
        RefreshTheme();
    }

    /// <summary>取色器拖动时的临时预览；不写配置，取消后由 ClearThemePreview 恢复。</summary>
    public void PreviewTheme(Settings cfg)
    {
        if (cfg == null) return;
        _themePreview = KeyboardTheme.Resolve(cfg);
        RefreshTheme();
    }

    /// <summary>取消取色预览，恢复最近一次 ApplyTheme 的正式配色。</summary>
    public void ClearThemePreview()
    {
        if (_themePreview == null) return;
        _themePreview = null;
        RefreshTheme();
    }

    void RefreshTheme()
    {
        if (IsDisposed) return;
        if (IsHandleCreated) Render();
        else VisualChanged?.Invoke();
    }

    /// <summary>键盘需要上移多少像素才能塞进屏幕（用于优先上移角色）。</summary>
    public int ComputeLift(Rectangle petBounds, double scale)
    {
        if (_mode == Mode.Hidden) return 0;
        float s = (float)scale;
        var screen = Screen.FromPoint(new Point(petBounds.Left + petBounds.Width / 2, petBounds.Top + petBounds.Height / 2)).WorkingArea;
        int gap = (int)(4 * s);
        int overflow = petBounds.Bottom + gap + Height - screen.Bottom;
        if (overflow <= 0) return 0;
        int availableAbove = petBounds.Top - screen.Top;
        return Math.Min(overflow, Math.Max(0, availableAbove));
    }

    /// <summary>
    /// 让 200px 逻辑宽的键盘/手柄窗口与桌宠底部信息条（数字显示板）水平对齐，
    /// 而不是按整个 272px 窗口居中。桌面窗口和 OBS 采集拼接共用这套几何。
    /// </summary>
    public static int AlignedLeftForPet(int petLeft, int petWidth, int keyboardWidth)
    {
        if (petWidth <= 0) return petLeft;
        float scale = petWidth / (float)PetComposer.WindowW;
        int infoLeft = (int)Math.Round(PetComposer.CounterX * scale);
        int infoRight = (int)Math.Round((PetComposer.ToolbarX + PetComposer.ToolbarW) * scale);
        return petLeft + infoLeft + (infoRight - infoLeft - keyboardWidth) / 2;
    }

    public void FollowPet(Point petLocation, Size petSize, double scale)
    {
        if (_mode == Mode.Hidden || petSize.Width <= 0) return;
        float s = (float)Math.Clamp(scale, 0.25, 3.0);
        _scale = s;
        // 目标尺寸始终由“逻辑尺寸 × 缩放”得出；用 Height × scale 会让窗口每布局一次就放大一圈。
        var target = new Size(Math.Max(1, (int)Math.Round(W * s)), Math.Max(1, (int)Math.Round(LogicalHeight * s)));
        bool resized = Size != target;
        if (resized) Size = target;
        var screen = Screen.FromPoint(new Point(petLocation.X + petSize.Width / 2, petLocation.Y)).WorkingArea;
        int x = AlignedLeftForPet(petLocation.X, petSize.Width, Width);
        int y = petLocation.Y + petSize.Height + (int)(4 * s);
        // 始终在角色下方；放不下就贴住屏幕底边（弹回），不再自动放到上方
        if (y + Height > screen.Bottom) y = screen.Bottom - Height;
        if (y < screen.Top) y = screen.Top;
        if (x < screen.Left) x = screen.Left;
        if (x + Width > screen.Right) x = screen.Right - Width;
        Location = new Point(x, y);
        // 拖动角色时这里每秒会被调用上百次；内容没变就不重画（只移动窗口位置）。
        if (resized || !_painted) Render();
    }

    void Poll()
    {
        // 手柄始终轮询：即使键盘界面没展开，按手柄也要驱动主角挥手和点击数。
        // 键盘和鼠标按钮都由 Raw Input 边沿驱动（OnKeyEdge），16ms 轮询会漏掉快速点击。
        PollPad(_mode == Mode.Controller);
    }

    /// <summary>
    /// Raw Input 按键边沿：按下立刻高亮，不等 33ms 轮询，也不会漏掉快速点击。
    /// 渲染按 8ms 合并，连打时不会把 UI 线程塞满。
    /// </summary>
    public void OnKeyEdge(int vk, bool down)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<int, bool>(OnKeyEdge), vk, down); } catch { }
            return;
        }
        if (_mode != Mode.Letters && _mode != Mode.Wuwa && _mode != Mode.Rhythm) return;
        string key = _mode == Mode.Rhythm
            ? RhythmKeys.FromVirtualKey(vk)
            : vk switch
            {
                0x01 => "MOUSE_L",
                0x02 => "MOUSE_R",
                0x20 => "SPACE",
                >= 0x30 and <= 0x5A => ((char)vk).ToString(),
                _ => null
            };
        if (key == null || !_keys.ContainsKey(key)) return;
        if (down) { if (!_down.Add(key)) return; }
        else { if (!_down.Remove(key)) return; }
        if (down) Pulse(key);
        RequestRender();
    }

    bool IsLit(string key)
    {
        if (_down.Contains(key)) return true;
        return _pulseUntil.TryGetValue(key, out long until) && Environment.TickCount64 < until;
    }

    void Pulse(string key)
    {
        // 只给按下边沿一个很短的可见脉冲；松开不再延长，快速连打时不会一直亮。
        _pulseUntil[key] = Environment.TickCount64 + 35;
        if (_pulseTimer == null)
        {
            _pulseTimer = new System.Windows.Forms.Timer { Interval = 10 };
            _pulseTimer.Tick += (s, e) =>
            {
                long now = Environment.TickCount64;
                var expired = new List<string>();
                foreach (var pair in _pulseUntil) if (pair.Value <= now) expired.Add(pair.Key);
                foreach (var k in expired) _pulseUntil.Remove(k);
                RequestRender();
                if (_pulseUntil.Count == 0) _pulseTimer.Stop();
            };
        }
        if (!_pulseTimer.Enabled) _pulseTimer.Start();
    }

    void RequestRender()
    {
        // 键盘窗口很小，直接重绘即可；不再用 8ms 合并延迟，音游连打也能立刻看到按键变化。
        Render();
    }

    void PollPad(bool render)
    {
        if (_padUnavailable) return;
        _padInput.BeginPoll();
        _padTriggerLabels.Clear();
        try
        {
            var state = new XINPUT_STATE();
            bool connected = _padUserIndex >= 0 && XInputGetState((uint)_padUserIndex, ref state) == 0;
            if (!connected)
            {
                // 手柄不一定在 0 号槽位；依次找第一个已连接的手柄。
                // 这样 2P/3P 槽位、或先连了其他设备时也能正常识别。
                _padUserIndex = -1;
                _padKeystrokesReady = false;
                for (uint i = 0; i < 4; i++)
                {
                    var candidate = new XINPUT_STATE();
                    if (XInputGetState(i, ref candidate) != 0) continue;
                    _padUserIndex = (int)i;
                    state = candidate;
                    connected = true;
                    // 换了槽位/重新连接：旧状态作废，第一帧只同步，不伪造按键边沿。
                    _padConnected = false;
                    _padButtons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0;
                    ResetPadInputState();
                    break;
                }
            }
            if (!connected)
            {
                if (_padConnected)
                {
                    _padConnected = false;
                    _padKeystrokesReady = false;
                    _padButtons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0;
                    ResetPadInputState();
                    if (render) Render();
                }
                return;
            }

            var g = state.Gamepad;
            // 摇杆只用于显示，不参与计数：按“按键是否按下”做边沿检测，
            // 这样转摇杆不会被视为拍桌宠，也避免轴抖动引发高频重绘。
            bool buttonsChanged = g.wButtons != _padButtons;
            bool triggersChanged = Math.Abs(g.bLeftTrigger - _lt) > 4 || Math.Abs(g.bRightTrigger - _rt) > 4;
            bool axesMoved = Math.Abs(g.sThumbLX - _lx) > 900 || Math.Abs(g.sThumbLY - _ly) > 900
                || Math.Abs(g.sThumbRX - _rx) > 900 || Math.Abs(g.sThumbRY - _ry) > 900;
            if (!_padConnected || buttonsChanged || triggersChanged || axesMoved)
            {
                bool wasConnected = _padConnected;
                ushort prev = _padButtons;
                byte prevLt = _lt, prevRt = _rt;
                _padConnected = true;
                _padPacket = state.dwPacketNumber;
                _padButtons = g.wButtons;
                _lt = g.bLeftTrigger;
                _rt = g.bRightTrigger;
                _lx = g.sThumbLX;
                _ly = g.sThumbLY;
                _rx = g.sThumbRX;
                _ry = g.sThumbRY;

                // 刚连接时只同步状态，不算一次按键；只有真正的按下边沿才触发计数。
                // 同时按多个键时，每个新按下的键都算一次气泡，但手势只计一次。
                if (!wasConnected)
                {
                    SyncPadInputState();
                }
                else
                {
                    ApplyPadButtonStateEdges(prev, _padButtons);
                    if (_lt > 4 && prevLt <= 4) _padTriggerLabels.Add("LT");
                    if (_rt > 4 && prevRt <= 4) _padTriggerLabels.Add("RT");
                }
                if (render) Render();
            }

            // XInput 的 keystroke 队列保留两次轮询之间发生的按键事件。
            // 16ms 状态采样会漏掉极短的点按；队列可以把这些按键补回来。
            // 两条路径共享 PadInputTracker，同一次物理按下不会被重复计数。
            if (_padKeystrokesReady) PollPadKeystrokes();
            else _padKeystrokesReady = true;
            FlushPadInput();
        }
        catch (Exception ex)
        {
            // 系统没有 xinput1_4.dll 时不必每 33ms 抛一次异常。
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException) _padUnavailable = true;
            if (!_padConnected)
            {
                ResetPadInputState();
                return;
            }
            _padConnected = false;
            _padKeystrokesReady = false;
            _padButtons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0;
            ResetPadInputState();
            if (render) Render();
        }
    }

    /// <summary>连接/换槽位后的首帧：把当前按住的键同步进去，不产生按下边沿。</summary>
    void SyncPadInputState()
    {
        var labels = new List<string>();
        foreach (var (flag, label) in PadButtonLabels)
            if ((_padButtons & flag) != 0) labels.Add(label);
        _padInput.SyncDown(labels);
    }

    void ApplyPadButtonStateEdges(ushort prev, ushort current)
    {
        foreach (var (flag, label) in PadButtonLabels)
        {
            bool nowDown = (current & flag) != 0;
            bool wasDown = (prev & flag) != 0;
            if (nowDown == wasDown) continue;
            _padInput.ObserveStateButton(label, nowDown);
        }
    }

    /// <summary>把本轮轮询里由状态边沿或 keystroke 队列发现的新按下统一成一次事件。</summary>
    void FlushPadInput()
    {
        if (_padInput.PressedAny)
        {
            PadButtonDown?.Invoke();
            foreach (string label in _padInput.PressedLabels) RaisePadButtonPressed(label);
        }
        if (_padTriggerLabels.Count > 0)
        {
            foreach (string label in _padTriggerLabels) RaisePadButtonPressed(label);
            _padTriggerLabels.Clear();
        }
        if (_padInput.ReleasedAll)
        {
            _padReleasePending = false;
            _padReleaseTimer?.Stop();
            PadButtonUp?.Invoke();
        }
    }

    void ResetPadInputState()
    {
        _padInput.Reset();
        _padTriggerLabels.Clear();
        _padReleasePending = false;
        _padReleaseTimer?.Stop();
    }

    void PollPadKeystrokes()
    {
        if (_padKeystrokeUnavailable) return;
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var keystroke = new XINPUT_KEYSTROKE();
                // XUSER_INDEX_ANY：任意槽位的手柄按键都收，不再只认 0 号手柄。
                if (XInputGetKeystroke(XUSER_INDEX_ANY, 0, out keystroke) != 0) break;
                string label = PadInputLogic.LabelForVirtualKey(keystroke.VirtualKey);
                if (label == null) continue;

                // 长按产生的 KEYDOWN | REPEAT 必须丢弃，否则会周期性触发新的按键。
                if (PadInputLogic.IsFreshKeyDown(keystroke.Flags))
                {
                    // 只有队列补发的、状态轮询还没计过的按下才需要补释放。
                    if (_padInput.ObserveKeystrokeDown(label)) SchedulePadRelease();
                }
                else if (PadInputLogic.IsKeyUp(keystroke.Flags))
                {
                    _padInput.ObserveKeystrokeUp(label);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            _padKeystrokeUnavailable = true;
        }
    }

    /// <summary>
    /// 队列补发的极快点按可能只有 KEYDOWN，状态轮询永远看不到按下，
    /// 因此补一个短释放定时器，避免主角一直保持“手按下”的姿势。
    /// 定时器只在物理状态确实没有按键/扳机时收尾，长按不会提前触发 PadButtonUp。
    /// </summary>
    void SchedulePadRelease()
    {
        if (_padReleaseTimer == null)
        {
            _padReleaseTimer = new System.Windows.Forms.Timer { Interval = 90 };
            _padReleaseTimer.Tick += (s, e) =>
            {
                _padReleaseTimer.Stop();
                bool pending = _padReleasePending;
                _padReleasePending = false;
                if (!pending) return;
                // 物理上还有按键/扳机时不能提前释放；长按的后续轮询会把这里清掉。
                if (_padButtons != 0 || _lt > 4 || _rt > 4) return;
                // 清掉没有被状态轮询确认过的队列按下；仍被按住（或状态未同步）的键不受影响。
                _padInput.ReleaseUnconfirmed();
                if (!_padInput.AnyActive) PadButtonUp?.Invoke();
            };
        }
        _padReleaseTimer.Stop();
        _padReleasePending = true;
        _padReleaseTimer.Start();
    }

    void RaisePadButtonPressed(string label)
    {
        if (string.IsNullOrEmpty(label)) return;
        PadButtonPressed?.Invoke(label);
    }

    void Render()
    {
        if (!IsHandleCreated) return;
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var surface = _surface.Begin(w, h);
        var theme = ActiveTheme;
        using (var g = Graphics.FromImage(surface))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = Width / (float)W;
            DrawToolbar(g, s, theme);
            // 手柄模式只显示手柄本体，不再画占面积很大的奶油底板。
            if (_mode != Mode.Controller) DrawPlate(g, s, LogicalHeight, theme);
            if (_mode == Mode.Controller) DrawPad(g, s, theme);
            else
            {
                foreach (var pair in _keys) DrawKey(g, s, pair.Key, pair.Value, IsLit(pair.Key), theme);
                DrawRhythmLaneNumbers(g, s, theme);
            }
        }
        _surface.Apply(this);
        _painted = true;
        VisualChanged?.Invoke();
    }

    /// <summary>把当前键盘/手柄 GUI 画到绿幕采集窗口的指定区域（不画自己的背景）。</summary>
    public void DrawTo(Graphics g, Rectangle dest)
    {
        if (_mode == Mode.Hidden || dest.Width <= 0 || dest.Height <= 0) return;
        var state = g.Save();
        try
        {
            var theme = ActiveTheme;
            g.TranslateTransform(dest.X, dest.Y);
            float s = dest.Width / (float)W;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            DrawToolbar(g, s, theme);
            if (_mode != Mode.Controller) DrawPlate(g, s, LogicalHeight, theme);
            if (_mode == Mode.Controller) DrawPad(g, s, theme);
            else
            {
                foreach (var pair in _keys) DrawKey(g, s, pair.Key, pair.Value, IsLit(pair.Key), theme);
                DrawRhythmLaneNumbers(g, s, theme);
            }
        }
        finally { g.Restore(state); }
    }

    void DrawToolbar(Graphics g, float s, KeyboardTheme theme)
    {
        using var font = new Font("Microsoft YaHei UI", 10f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.ToolbarText);
        foreach (var pair in _buttons)
        {
            var r = new RectangleF(pair.Value.X * s, pair.Value.Y * s, pair.Value.Width * s, pair.Value.Height * s);
            bool active = (pair.Key == "26键" && _mode == Mode.Letters)
                || (pair.Key == "鸣潮专属" && _mode == Mode.Wuwa)
                || (pair.Key == "音游模式" && _mode == Mode.Rhythm)
                || (pair.Key == "手柄" && _mode == Mode.Controller);
            using (var path = PetComposer.Rounded(r, 9 * s))
            using (var brush = new SolidBrush(active ? theme.ToolbarActive : theme.ToolbarBackground))
            using (var pen = new Pen(theme.ToolbarBorder, 1f * s))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(pair.Key, font, text, r, fmt);
        }
    }

    void DrawPlate(Graphics g, float s, int logicalHeight, KeyboardTheme theme)
    {
        var plate = new RectangleF(2 * s, 22 * s, (W - 4) * s, logicalHeight * s - 24 * s);
        using (var shadow = PetComposer.Rounded(new RectangleF(plate.X, plate.Y + 3 * s, plate.Width, plate.Height), 16 * s))
        using (var brush = new SolidBrush(theme.PlateShadow))
            g.FillPath(brush, shadow);
        using (var path = PetComposer.Rounded(plate, 16 * s))
        using (var brush = new LinearGradientBrush(plate, theme.PlateTop, theme.PlateBottom, 160f))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(plate, 16 * s))
        using (var pen = new Pen(theme.PlateBorder, 2f * s))
            g.DrawPath(pen, path);
        using (var edge = PetComposer.Rounded(new RectangleF(plate.X + 2 * s, plate.Bottom - 5 * s, plate.Width - 4 * s, 5 * s), 3 * s))
        using (var brush = new SolidBrush(theme.PlateEdge))
            g.FillPath(brush, edge);
        using (var foot = PetComposer.Rounded(new RectangleF(17 * s, (22 - 5) * s, 14 * s, 5 * s), 2 * s))
        using (var brush = new SolidBrush(theme.PlateFoot))
            g.FillPath(brush, foot);
        using (var led = new SolidBrush(theme.Led))
            g.FillEllipse(led, plate.Right - 17 * s, plate.Bottom - 15 * s, 7 * s, 7 * s);
    }

    void DrawKey(Graphics g, float s, string key, RectangleF logical, bool down, KeyboardTheme theme)
    {
        float dy = down ? 2 * s : 0;
        var r = new RectangleF(logical.X * s, logical.Y * s + dy, logical.Width * s, logical.Height * s);
        bool rhythm = _mode == Mode.Rhythm;
        bool wuwa = _mode == Mode.Wuwa;
        float radius = (rhythm ? 9 : wuwa ? 8 : (key.StartsWith("MOUSE") || key == "SPACE") ? 6 : 5) * s;
        var shadow = new RectangleF(r.X, r.Y + (down ? 0 : 2 * s), r.Width, r.Height);
        using (var path = PetComposer.Rounded(shadow, radius))
        using (var brush = new SolidBrush(theme.KeyShadow))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(r, radius))
        using (var brush = new SolidBrush(down ? theme.KeyPressed : theme.KeyTop))
            g.FillPath(brush, path);
        if (down)
        {
            using var glow = new Pen(theme.KeyGlow, 2f * s);
            using var path = PetComposer.Rounded(r, radius);
            g.DrawPath(glow, path);
        }
        string label = key switch { "MOUSE_L" => "左键", "MOUSE_R" => "右键", "SPACE" => "空格", _ => key };
        float fs = rhythm
            ? (_rhythmKeys.Length >= 6 ? 16f : _rhythmKeys.Length == 5 ? 18f : 22f)
            : wuwa ? 14f : (key.StartsWith("MOUSE") || key == "SPACE") ? 9f : 9f;
        fs *= s;
        using var font = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(down ? theme.KeyTextPressed : theme.KeyText);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (rhythm && key == RhythmKeys.Space)
        {
            // 空格用旋转 90° 的 "[" 作为键帽标记。
            var state = g.Save();
            g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
            g.RotateTransform(90);
            var box = new RectangleF(-r.Width / 2, -r.Height / 2, r.Width, r.Height);
            g.DrawString("[", font, text, box, fmt);
            g.Restore(state);
        }
        else
        {
            g.DrawString(label, font, text, r, fmt);
        }
    }

    void DrawRhythmLaneNumbers(Graphics g, float s, KeyboardTheme theme)
    {
        if (_mode != Mode.Rhythm) return;
        using var font = new Font("Microsoft YaHei UI", 8f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.LaneNumber);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < _rhythmLanes.Count; i++)
        {
            var r = _rhythmLanes[i];
            g.DrawString((i + 1).ToString(), font, text,
                new RectangleF(r.X * s, (r.Bottom + 3) * s, r.Width * s, 10 * s), fmt);
        }
    }

    // ---------------- 手柄 ----------------

    bool PadPressed(ushort flag) => (_padButtons & flag) != 0;

    void DrawPad(Graphics g, float s, KeyboardTheme theme)
    {
        float top = (ToolbarH + 2) * s;
        var board = new RectangleF(4 * s, top, 192 * s, 146 * s);
        if (!_padConnected)
        {
            using var font = new Font("Microsoft YaHei UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(theme.PadText);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("未连接手柄（XInput）", font, brush, board, fmt);
            return;
        }

        // 手柄本体：参考图的扁平线稿轮廓（双肩、握把、底部中央内凹），只画本体。
        // 整体下移 8px，给顶部 LT/RT 深度条留出空间。
        float bodyOffsetY = 8 * s;
        var state = g.Save();
        try
        {
            g.TranslateTransform(board.X, board.Y + bodyOffsetY);
            g.ScaleTransform(s, s);
            using var body = BuildPadBodyPath();
            using (var brush = new SolidBrush(theme.PadBody))
                g.FillPath(brush, body);
            using (var pen = new Pen(theme.PadOutline, 2.0f))
                g.DrawPath(pen, body);
        }
        finally { g.Restore(state); }

        // 顶部扳机深度条与肩键：使用设计器导出的坐标。
        DrawPadTrigger(g, s, board.X + 38 * s, board.Y + 4 * s, 30 * s, "LT", _lt, theme);
        DrawPadTrigger(g, s, board.X + 124 * s, board.Y + 4 * s, 30 * s, "RT", _rt, theme);
        DrawPadBumper(g, s, board.X + 40 * s, board.Y + 14 * s, 26 * s, "LB", PadPressed(0x0100), theme);
        DrawPadBumper(g, s, board.X + 126 * s, board.Y + 14 * s, 26 * s, "RB", PadPressed(0x0200), theme);

        // 参考图布局：十字键左上、Guide 上方中间、面键右上、双摇杆下方中间。
        DrawDpad(g, s, board.X + 53 * s, board.Y + 47 * s,
            PadPressed(0x0001), PadPressed(0x0002), PadPressed(0x0004), PadPressed(0x0008), theme);

        var guide = new RectangleF(board.X + 88 * s, board.Y + 32 * s, 18 * s, 18 * s);
        using (var brush = new SolidBrush(PadPressed(0x0400) ? theme.PadPressedFor(theme.PadBody) : theme.PadBody))
            g.FillEllipse(brush, guide);
        using (var pen = new Pen(theme.PadOutline, 1.8f * s))
            g.DrawEllipse(pen, guide);
        using (var pen = new Pen(PetWindow.BlendColor(theme.PadOutline, theme.PadBody, 0.35f), 1.2f * s))
            g.DrawEllipse(pen, guide.X + 5 * s, guide.Y + 5 * s, guide.Width - 10 * s, guide.Height - 10 * s);

        float fx = board.X + 139 * s, fy = board.Y + 49 * s;
        DrawFace(g, s, fx, fy - 11.5f * s, "Y", PadPressed(0x8000), Color.FromArgb(255, 252, 226, 156), theme);
        DrawFace(g, s, fx - 11.5f * s, fy, "X", PadPressed(0x4000), Color.FromArgb(255, 214, 233, 248), theme);
        DrawFace(g, s, fx + 11.5f * s, fy, "B", PadPressed(0x2000), Color.FromArgb(255, 250, 217, 212), theme);
        DrawFace(g, s, fx, fy + 11.5f * s, "A", PadPressed(0x1000), Color.FromArgb(255, 215, 237, 208), theme);

        // 两个短横：Menu / View，使用设计器导出的坐标。
        DrawPadDash(g, s, board.X + 80 * s, board.Y + 56 * s, 10 * s, 6 * s, PadPressed(0x0020), theme);
        DrawPadDash(g, s, board.X + 104 * s, board.Y + 56 * s, 10 * s, 6 * s, PadPressed(0x0010), theme);

        // 双摇杆。
        DrawStick(g, s, board.X + 74 * s, board.Y + 94 * s, _lx, _ly, PadPressed(0x0040), "LS", theme);
        DrawStick(g, s, board.X + 118 * s, board.Y + 94 * s, _rx, _ry, PadPressed(0x0080), "RS", theme);
    }

    /// <summary>hy4 按参考图给出的 192×146 逻辑坐标闭合轮廓（P0|C1|C2|P3，顺序绘制）。</summary>
    static GraphicsPath BuildPadBodyPath()
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(78.5f, 20.6f, 88f, 20.6f, 104f, 20.6f, 113.5f, 20.6f);
        path.AddBezier(113.5f, 20.6f, 120f, 20.6f, 130f, 6f, 137.7f, 6f);
        path.AddBezier(137.7f, 6f, 146f, 6f, 155f, 13f, 159f, 24f);
        path.AddBezier(159f, 24f, 165f, 40f, 173f, 58f, 179f, 78f);
        path.AddBezier(179f, 78f, 183f, 92f, 187f, 102f, 188f, 112f);
        path.AddBezier(188f, 112f, 190f, 119f, 190f, 121f, 189.5f, 126f);
        path.AddBezier(189.5f, 126f, 189.5f, 135f, 183f, 141f, 174f, 141f);
        path.AddBezier(174f, 141f, 166f, 141f, 159f, 135f, 154f, 128f);
        path.AddBezier(154f, 128f, 146f, 121f, 138f, 116f, 130f, 112f);
        path.AddBezier(130f, 112f, 118f, 108f, 104f, 103f, 96f, 103f);
        path.AddBezier(96f, 103f, 88f, 103f, 74f, 108f, 62f, 112f);
        path.AddBezier(62f, 112f, 54f, 116f, 46f, 121f, 38f, 128f);
        path.AddBezier(38f, 128f, 33f, 135f, 26f, 141f, 18f, 141f);
        path.AddBezier(18f, 141f, 9f, 141f, 2.5f, 135f, 2.5f, 126f);
        path.AddBezier(2.5f, 126f, 2f, 121f, 2f, 119f, 4f, 112f);
        path.AddBezier(4f, 112f, 5f, 102f, 9f, 92f, 13f, 78f);
        path.AddBezier(13f, 78f, 19f, 58f, 27f, 40f, 33f, 24f);
        path.AddBezier(33f, 24f, 37f, 13f, 46f, 6f, 55.7f, 6f);
        path.AddBezier(55.7f, 6f, 64f, 6f, 72f, 20.6f, 78.5f, 20.6f);
        path.CloseFigure();
        return path;
    }

    void DrawDpad(Graphics g, float s, float cx, float cy, bool up, bool down, bool left, bool right, KeyboardTheme theme)
    {
        float t = 12 * s;
        float arm = 17 * s;
        using var cross = BuildDpadPath(cx, cy, arm, t);
        using (var brush = new SolidBrush(theme.PadBody))
            g.FillPath(brush, cross);
        using (var pen = new Pen(theme.PadOutline, 1.8f * s) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, cross);
        if (!up && !down && !left && !right) return;

        var state = g.Save();
        try
        {
            g.SetClip(cross);
            using var press = new SolidBrush(theme.PadPressedFor(theme.PadBody));
            if (up) g.FillRectangle(press, cx - t / 2, cy - arm, t, arm);
            if (down) g.FillRectangle(press, cx - t / 2, cy, t, arm);
            if (left) g.FillRectangle(press, cx - arm, cy - t / 2, arm, t);
            if (right) g.FillRectangle(press, cx, cy - t / 2, arm, t);
        }
        finally { g.Restore(state); }
    }

    static GraphicsPath BuildDpadPath(float cx, float cy, float arm, float t)
    {
        float h = t / 2;
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(cx - h, cy - arm), new PointF(cx + h, cy - arm),
            new PointF(cx + h, cy - h), new PointF(cx + arm, cy - h),
            new PointF(cx + arm, cy + h), new PointF(cx + h, cy + h),
            new PointF(cx + h, cy + arm), new PointF(cx - h, cy + arm),
            new PointF(cx - h, cy + h), new PointF(cx - arm, cy + h),
            new PointF(cx - arm, cy - h), new PointF(cx - h, cy - h),
        });
        path.CloseFigure();
        return path;
    }

    void DrawPadDash(Graphics g, float s, float x, float y, float w, float h, bool down, KeyboardTheme theme)
    {
        var rect = new RectangleF(x, y, w, h);
        using (var path = PetComposer.Rounded(rect, Math.Min(h, 3.5f * s)))
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.Cream2) : theme.Cream2))
        using (var pen = new Pen(theme.PadOutline, 1.1f * s))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
    }

    void DrawPadBumper(Graphics g, float s, float x, float y, float w, string label, bool down, KeyboardTheme theme)
    {
        var rect = new RectangleF(x, y, w, 10 * s);
        using (var path = PetComposer.Rounded(rect, 5 * s))
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadBody) : theme.PadBody))
        using (var pen = new Pen(theme.PadOutline, 1.2f * s))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        using var font = new Font("Microsoft YaHei UI", 5.8f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.PadTextFor(down ? theme.PadPressedFor(theme.PadBody) : theme.PadBody));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, text, rect, fmt);
    }

    void DrawPadTrigger(Graphics g, float s, float x, float y, float w, string label, byte value, KeyboardTheme theme)
    {
        var rect = new RectangleF(x, y, w, 9.5f * s);
        bool down = value > 10;
        using (var path = PetComposer.Rounded(rect, 4.75f * s))
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadTriggerTrack) : theme.PadTriggerTrack))
        using (var pen = new Pen(theme.PadOutline, 1.1f * s))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        var bar = new RectangleF(rect.X + 4 * s, rect.Bottom - 2.8f * s, (rect.Width - 8 * s) * (value / 255f), 1.6f * s);
        using (var brush = new SolidBrush(theme.PadTriggerFill))
            g.FillRectangle(brush, bar);
        using var font = new Font("Microsoft YaHei UI", 5.8f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.PadTextFor(down ? theme.PadPressedFor(theme.PadTriggerTrack) : theme.PadTriggerTrack));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label + " " + (int)(value / 255f * 100) + "%", font, text, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height - 1.5f * s), fmt);
    }

    void DrawTrigger(Graphics g, float s, float x, float y, float w, string label, byte value, KeyboardTheme theme)
    {
        var box = new RectangleF(x, y, w, 14 * s);
        bool down = value > 10;
        using (var path = PetComposer.Rounded(box, 6 * s))
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadTriggerTrack) : theme.PadTriggerTrack))
        using (var pen = new Pen(theme.PadOutline, 1.1f * s))
        { g.FillPath(brush, path); g.DrawPath(pen, path); }
        var bar = new RectangleF(box.X + 4 * s, box.Bottom - 5 * s, (box.Width - 8 * s) * (value / 255f), 2.5f * s);
        using (var brush = new SolidBrush(theme.PadTriggerFill))
            g.FillRectangle(brush, bar);
        using var font = new Font("Microsoft YaHei UI", 8f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.PadTextFor(down ? theme.PadPressedFor(theme.PadTriggerTrack) : theme.PadTriggerTrack));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label + " " + (int)(value / 255f * 100) + "%", font, text, new RectangleF(box.X, box.Y, box.Width, box.Height - 4 * s), fmt);
    }

    void DrawFace(Graphics g, float s, float cx, float cy, string label, bool down, Color fill, KeyboardTheme theme)
    {
        float r = 9.5f * s;
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        // ABXY 保留功能辨识色，但按下态仍向主题高亮色靠拢，外框和文字使用主题色。
        Color face = down ? PetWindow.BlendColor(fill, theme.PadPressed, 0.45f) : fill;
        using (var brush = new SolidBrush(face))
            g.FillEllipse(brush, rect);
        using (var pen = new Pen(theme.PadFaceOutline, 1.3f * s))
            g.DrawEllipse(pen, rect);
        using var font = new Font("Microsoft YaHei UI", 9.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.PadFaceTextFor(face));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, text, rect, fmt);
    }

    void DrawStick(Graphics g, float s, float cx, float cy, short ax, short ay, bool down, string label, KeyboardTheme theme)
    {
        float r = 15.5f * s;
        var well = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadStickWell) : theme.PadStickWell))
            g.FillEllipse(brush, well);
        using (var pen = new Pen(theme.PadOutline, 1.8f * s))
            g.DrawEllipse(pen, well);

        float capR = r * 0.62f;
        float ox = ax / 32767f * 4 * s, oy = -ay / 32767f * 4 * s;
        var cap = new RectangleF(cx - capR + ox, cy - capR + oy, capR * 2, capR * 2);
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadStickCap) : theme.PadStickCap))
            g.FillEllipse(brush, cap);
        using (var pen = new Pen(theme.PadOutline, 1.5f * s))
            g.DrawEllipse(pen, cap);
    }

    void DrawPadKey(Graphics g, float s, float x, float y, float w, float h, string label, bool down, float radius, KeyboardTheme theme)
    {
        var r = new RectangleF(x, y, w, h);
        using (var path = PetComposer.Rounded(r, radius))
        using (var brush = new SolidBrush(down ? theme.PadPressedFor(theme.Cream2) : theme.Cream2))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(r, radius))
        using (var pen = new Pen(theme.Border, 1f * s))
            g.DrawPath(pen, path);
        using var font = new Font("Microsoft YaHei UI", 7.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.PadTextFor(down ? theme.PadPressedFor(theme.Cream2) : theme.Cream2));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, text, r, fmt);
    }

    // ---------------- 窗口 ----------------

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = unchecked((int)0x80000000L) | 0x10000000;
            cp.ExStyle = 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        float s = Width / (float)W;
        var p = new PointF(e.X / s, e.Y / s);
        foreach (var pair in _buttons)
        {
            if (!pair.Value.Contains(p)) continue;
            if (pair.Key == "×") CloseRequested?.Invoke();
            else ModeRequested?.Invoke(pair.Key switch
            {
                "26键" => Mode.Letters,
                "鸣潮专属" => Mode.Wuwa,
                "音游模式" => Mode.Rhythm,
                _ => Mode.Controller
            });
            return;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll?.Dispose();
        _pulseTimer?.Dispose();
        _padReleaseTimer?.Dispose();
        _surface.Dispose();
        base.OnClosed(e);
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetKeystroke(uint dwUserIndex, uint dwReserved, out XINPUT_KEYSTROKE pKeystroke);

    const uint XUSER_INDEX_ANY = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_KEYSTROKE
    {
        public ushort VirtualKey;
        public ushort Unicode;
        public ushort Flags;
        public byte UserIndex;
        public byte HidCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }
}
