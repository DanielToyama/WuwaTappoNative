using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌面键盘 / 手柄反馈。布局对齐 Helper：
/// 200px 宽奶油木板；26 键为 15×19 键帽；鸣潮专属 1/2/3 + Q/E/R + 空格 + 鼠标；
/// 手柄为 200×160 面板（十字键/面键/肩键/摇杆/扳机），使用 XInput 轮询。
/// </summary>
public sealed class KeyboardWindow : Form
{
    public enum Mode { Hidden, Letters, Wuwa, Controller }

    const int W = 200;
    const int ToolbarH = 24;
    const int LettersH = ToolbarH + 121;
    const int WuwaH = ToolbarH + 116;
    const int PadH = ToolbarH + 182;
    const float KeyW = 15, KeyH = 19, Gap = 3;

    readonly Dictionary<string, RectangleF> _keys = new();
    readonly Dictionary<string, RectangleF> _buttons = new();
    readonly HashSet<string> _down = new();
    // 原版给每次按下约 70ms 的可见高亮：极快的点按也能看见，不会“一闪就没”。
    readonly Dictionary<string, long> _pulseUntil = new();
    System.Windows.Forms.Timer _pulseTimer;
    System.Windows.Forms.Timer _poll;
    Mode _mode = Mode.Hidden;
    float _scale = 1f;
    bool _painted;
    readonly LayeredSurface _surface = new();

    // 手柄状态
    ushort _padButtons;
    byte _lt, _rt;
    short _lx, _ly, _rx, _ry;
    bool _padConnected, _padUnavailable;
    uint _padPacket;

    public Mode CurrentMode => _mode;
    public event Action<Mode> ModeRequested;
    public event Action CloseRequested;
    /// <summary>键盘/手柄画面变化，绿幕采集窗口据此重绘。</summary>
    public event Action VisualChanged;
    public int LogicalHeightForCapture => LogicalHeight;
    public int CaptureWidth => W;
    /// <summary>手柄有新按键按下：驱动主角挥手与点击数（摇杆不参与）。</summary>
    public event Action PadButtonDown;
    /// <summary>手柄所有按键都松开。</summary>
    public event Action PadButtonUp;

    public KeyboardWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, LettersH);
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
        Mode.Controller => PadH,
        _ => LettersH
    };

    void BuildLayout(Mode mode)
    {
        _keys.Clear();
        _buttons.Clear();
        _buttons["26键"] = new RectangleF(4, 2, 44, 18);
        _buttons["鸣潮专属"] = new RectangleF(52, 2, 64, 18);
        _buttons["手柄"] = new RectangleF(120, 2, 40, 18);
        _buttons["×"] = new RectangleF(W - 24, 2, 20, 18);

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
        int x = petLocation.X + (petSize.Width - Width) / 2;
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
        if (_mode != Mode.Letters && _mode != Mode.Wuwa) return;
        string key = vk switch
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
        Pulse(key);
        RequestRender();
    }

    long _lastRenderMs;
    bool _renderPending;
    System.Windows.Forms.Timer _renderTimer;

    bool IsLit(string key)
    {
        if (_down.Contains(key)) return true;
        return _pulseUntil.TryGetValue(key, out long until) && Environment.TickCount64 < until;
    }

    void Pulse(string key)
    {
        _pulseUntil[key] = Environment.TickCount64 + 70;
        if (_pulseTimer == null)
        {
            _pulseTimer = new System.Windows.Forms.Timer { Interval = 40 };
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
        long now = Environment.TickCount64;
        if (now - _lastRenderMs >= 8)
        {
            Render();
            return;
        }
        _renderPending = true;
        if (_renderTimer == null)
        {
            _renderTimer = new System.Windows.Forms.Timer { Interval = 8 };
            _renderTimer.Tick += (s, e) =>
            {
                _renderTimer.Stop();
                if (!_renderPending) return;
                _renderPending = false;
                Render();
            };
        }
        if (!_renderTimer.Enabled) _renderTimer.Start();
    }

    void PollPad(bool render)
    {
        if (_padUnavailable) return;
        try
        {
            var state = new XINPUT_STATE();
            uint res = XInputGetState(0, ref state);
            bool connected = res == 0;
            if (!connected)
            {
                if (_padConnected)
                {
                    _padConnected = false;
                    _padButtons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0;
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
                // 同时按多个键时，每个新按下的键都算一次（与键盘一致）。
                if (wasConnected)
                {
                    ushort newlyPressed = (ushort)(_padButtons & ~prev);
                    if (newlyPressed != 0) PadButtonDown?.Invoke();
                    else if (prev != 0 && _padButtons == 0) PadButtonUp?.Invoke();
                }
                if (render) Render();
            }
        }
        catch (Exception ex)
        {
            // 系统没有 xinput1_4.dll 时不必每 33ms 抛一次异常。
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException) _padUnavailable = true;
            if (!_padConnected) return;
            _padConnected = false;
            _padButtons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0;
            if (render) Render();
        }
    }

    void Render()
    {
        if (!IsHandleCreated) return;
        _lastRenderMs = Environment.TickCount64;
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var surface = _surface.Begin(w, h);
        using (var g = Graphics.FromImage(surface))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = Width / (float)W;
            DrawToolbar(g, s);
            DrawPlate(g, s, LogicalHeight);
            if (_mode == Mode.Controller) DrawPad(g, s);
            else foreach (var pair in _keys) DrawKey(g, s, pair.Key, pair.Value, IsLit(pair.Key));
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
            g.TranslateTransform(dest.X, dest.Y);
            float s = dest.Width / (float)W;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            DrawToolbar(g, s);
            DrawPlate(g, s, LogicalHeight);
            if (_mode == Mode.Controller) DrawPad(g, s);
            else foreach (var pair in _keys) DrawKey(g, s, pair.Key, pair.Value, IsLit(pair.Key));
        }
        finally { g.Restore(state); }
    }

    void DrawToolbar(Graphics g, float s)
    {
        using var font = new Font("Microsoft YaHei UI", 10f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(PetComposer.InkStrong);
        foreach (var pair in _buttons)
        {
            var r = new RectangleF(pair.Value.X * s, pair.Value.Y * s, pair.Value.Width * s, pair.Value.Height * s);
            bool active = (pair.Key == "26键" && _mode == Mode.Letters)
                || (pair.Key == "鸣潮专属" && _mode == Mode.Wuwa)
                || (pair.Key == "手柄" && _mode == Mode.Controller);
            using (var path = PetComposer.Rounded(r, 9 * s))
            using (var brush = new SolidBrush(active ? Color.FromArgb(254, 210, 145) : Color.FromArgb(255, 243, 221)))
            using (var pen = new Pen(Color.FromArgb(220, 231, 183, 124), 1f * s))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(pair.Key, font, text, r, fmt);
        }
    }

    void DrawPlate(Graphics g, float s, int logicalHeight)
    {
        var plate = new RectangleF(2 * s, 22 * s, (W - 4) * s, logicalHeight * s - 24 * s);
        using (var shadow = PetComposer.Rounded(new RectangleF(plate.X, plate.Y + 3 * s, plate.Width, plate.Height), 16 * s))
        using (var brush = new SolidBrush(Color.FromArgb(160, 234, 211, 183)))
            g.FillPath(brush, shadow);
        using (var path = PetComposer.Rounded(plate, 16 * s))
        using (var brush = new LinearGradientBrush(plate, Color.FromArgb(255, 255, 248, 233), Color.FromArgb(255, 255, 220, 170), 160f))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(plate, 16 * s))
        using (var pen = new Pen(Color.FromArgb(255, 184, 135, 100), 2f * s))
            g.DrawPath(pen, path);
        using (var edge = PetComposer.Rounded(new RectangleF(plate.X + 2 * s, plate.Bottom - 5 * s, plate.Width - 4 * s, 5 * s), 3 * s))
        using (var brush = new SolidBrush(Color.FromArgb(255, 184, 135, 100)))
            g.FillPath(brush, edge);
        using (var foot = PetComposer.Rounded(new RectangleF(17 * s, (22 - 5) * s, 14 * s, 5 * s), 2 * s))
        using (var brush = new SolidBrush(Color.FromArgb(255, 231, 183, 124)))
            g.FillPath(brush, foot);
        using (var led = new SolidBrush(Color.FromArgb(255, 200, 220, 196)))
            g.FillEllipse(led, plate.Right - 17 * s, plate.Bottom - 15 * s, 7 * s, 7 * s);
    }

    void DrawKey(Graphics g, float s, string key, RectangleF logical, bool down)
    {
        float dy = down ? 2 * s : 0;
        var r = new RectangleF(logical.X * s, logical.Y * s + dy, logical.Width * s, logical.Height * s);
        bool wuwa = _mode == Mode.Wuwa;
        float radius = (wuwa ? 8 : (key.StartsWith("MOUSE") || key == "SPACE") ? 6 : 5) * s;
        var shadow = new RectangleF(r.X, r.Y + (down ? 0 : 2 * s), r.Width, r.Height);
        using (var path = PetComposer.Rounded(shadow, radius))
        using (var brush = new SolidBrush(Color.FromArgb(255, 190, 152, 121)))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(r, radius))
        using (var brush = new SolidBrush(down ? Color.FromArgb(255, 255, 201, 119) : Color.FromArgb(255, 255, 250, 240)))
            g.FillPath(brush, path);
        if (down)
        {
            using var glow = new Pen(Color.FromArgb(180, 255, 227, 145), 2f * s);
            using var path = PetComposer.Rounded(r, radius);
            g.DrawPath(glow, path);
        }
        string label = key switch { "MOUSE_L" => "左键", "MOUSE_R" => "右键", "SPACE" => "空格", _ => key };
        float fs = (wuwa ? 14f : (key.StartsWith("MOUSE") || key == "SPACE") ? 9f : 9f) * s;
        using var font = new Font("Microsoft YaHei UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(down ? Color.FromArgb(255, 110, 69, 45) : PetComposer.Ink);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (key == "MOUSE_L" && !wuwa)
        {
            var state = g.Save();
            g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
            g.RotateTransform(-90);
            g.DrawString("左键", font, text, new RectangleF(-r.Height / 2, -r.Width / 2, r.Height, r.Width), fmt);
            g.Restore(state);
        }
        else g.DrawString(label, font, text, r, fmt);
    }

    // ---------------- 手柄 ----------------

    bool PadPressed(ushort flag) => (_padButtons & flag) != 0;

    void DrawPad(Graphics g, float s)
    {
        float top = (ToolbarH + 8) * s;
        var board = new RectangleF(4 * s, top, 192 * s, 150 * s);
        if (!_padConnected)
        {
            using var font = new Font("Microsoft YaHei UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(200, PetComposer.Ink));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("未连接手柄（XInput）", font, brush, board, fmt);
            return;
        }

        // 手柄轮廓
        using (var body = new GraphicsPath())
        {
            var r = new RectangleF(board.X + 16 * s, board.Y + 34 * s, board.Width - 32 * s, 72 * s);
            body.AddArc(r.X, r.Y, 28 * s, 28 * s, 180, 90);
            body.AddArc(r.Right - 28 * s, r.Y, 28 * s, 28 * s, 270, 90);
            body.AddBezier(r.Right, r.Y + 40 * s, r.Right + 4 * s, r.Bottom + 20 * s, r.Right - 26 * s, r.Bottom + 26 * s, r.Right - 34 * s, r.Bottom - 2 * s);
            body.AddArc(board.X + board.Width / 2 - 14 * s, r.Bottom - 6 * s, 28 * s, 22 * s, 0, 180);
            body.AddBezier(r.X + 34 * s, r.Bottom - 2 * s, r.X + 26 * s, r.Bottom + 26 * s, r.X - 4 * s, r.Bottom + 20 * s, r.X, r.Y + 40 * s);
            body.CloseFigure();
            using (var brush = new LinearGradientBrush(board, Color.FromArgb(255, 255, 250, 238), Color.FromArgb(255, 244, 225, 200), 90f))
                g.FillPath(brush, body);
            using (var pen = new Pen(Color.FromArgb(255, 184, 135, 100), 1.6f * s))
                g.DrawPath(pen, body);
        }

        // 扳机
        DrawTrigger(g, s, board.X + 28 * s, board.Y + 6 * s, 64 * s, "LT", _lt);
        DrawTrigger(g, s, board.Right - 92 * s, board.Y + 6 * s, 64 * s, "RT", _rt);

        // 肩键
        DrawPadKey(g, s, board.X + 32 * s, board.Y + 24 * s, 44 * s, 13 * s, "LB", PadPressed(0x0100), 6 * s);
        DrawPadKey(g, s, board.Right - 76 * s, board.Y + 24 * s, 44 * s, 13 * s, "RB", PadPressed(0x0200), 6 * s);

        // 十字键
        float dx = board.X + 42 * s, dy = board.Y + 66 * s, ds = 14 * s;
        DrawPadKey(g, s, dx, dy - ds * 1.5f, ds * 1.7f, ds * 1.5f, "▲", PadPressed(0x0001), 3 * s);
        DrawPadKey(g, s, dx, dy + ds * 1.5f, ds * 1.7f, ds * 1.5f, "▼", PadPressed(0x0002), 3 * s);
        DrawPadKey(g, s, dx - ds * 1.5f, dy, ds * 1.5f, ds * 1.7f, "◀", PadPressed(0x0004), 3 * s);
        DrawPadKey(g, s, dx + ds * 1.5f, dy, ds * 1.5f, ds * 1.7f, "▶", PadPressed(0x0008), 3 * s);

        // 面键
        float fx = board.Right - 58 * s, fy = board.Y + 66 * s;
        DrawFace(g, s, fx, fy - 16 * s, "Y", PadPressed(0x8000), Color.FromArgb(255, 255, 241, 197), Color.FromArgb(255, 165, 138, 65));
        DrawFace(g, s, fx - 16 * s, fy, "X", PadPressed(0x4000), Color.FromArgb(255, 234, 242, 236), Color.FromArgb(255, 99, 135, 148));
        DrawFace(g, s, fx + 16 * s, fy, "B", PadPressed(0x2000), Color.FromArgb(255, 255, 237, 228), Color.FromArgb(255, 183, 117, 102));
        DrawFace(g, s, fx, fy + 16 * s, "A", PadPressed(0x1000), Color.FromArgb(255, 239, 244, 219), Color.FromArgb(255, 100, 131, 90));

        // 摇杆
        DrawStick(g, s, board.X + 66 * s, board.Y + 104 * s, _lx, _ly, PadPressed(0x0040), "LS");
        DrawStick(g, s, board.Right - 66 * s, board.Y + 104 * s, _rx, _ry, PadPressed(0x0080), "RS");

        // 中间功能键
        DrawPadKey(g, s, board.X + 78 * s, board.Y + 48 * s, 18 * s, 11 * s, "≡", PadPressed(0x0010), 4 * s);
        DrawPadKey(g, s, board.X + 102 * s, board.Y + 48 * s, 18 * s, 11 * s, "−", PadPressed(0x0020), 4 * s);
        DrawPadKey(g, s, board.X + 90 * s, board.Y + 66 * s, 16 * s, 12 * s, "⏺", PadPressed(0x0400), 4 * s);

        using (var font = new Font("Microsoft YaHei UI", 8f * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.FromArgb(170, PetComposer.Ink)))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far })
            g.DrawString("XInput 手柄", font, brush, new RectangleF(board.X, board.Bottom - 2 * s, board.Width, 10 * s), fmt);
    }

    void DrawTrigger(Graphics g, float s, float x, float y, float w, string label, byte value)
    {
        var box = new RectangleF(x, y, w, 14 * s);
        using (var path = PetComposer.Rounded(box, 6 * s))
        using (var brush = new SolidBrush(value > 10 ? Color.FromArgb(255, 255, 209, 142) : Color.FromArgb(255, 245, 230, 202)))
        using (var pen = new Pen(Color.FromArgb(255, 191, 155, 116), 1f * s))
        { g.FillPath(brush, path); g.DrawPath(pen, path); }
        var bar = new RectangleF(box.X + 4 * s, box.Bottom - 5 * s, (box.Width - 8 * s) * (value / 255f), 2.5f * s);
        using (var brush = new SolidBrush(Color.FromArgb(255, 199, 210, 172)))
            g.FillRectangle(brush, bar);
        using var font = new Font("Microsoft YaHei UI", 8f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(PetComposer.InkStrong);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label + " " + (int)(value / 255f * 100) + "%", font, text, new RectangleF(box.X, box.Y, box.Width, box.Height - 4 * s), fmt);
    }

    void DrawFace(Graphics g, float s, float cx, float cy, string label, bool down, Color fill, Color ink)
    {
        float r = 11 * s;
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var brush = new SolidBrush(down ? Color.FromArgb(255, 255, 199, 120) : fill))
            g.FillEllipse(brush, rect);
        using (var pen = new Pen(down ? Color.FromArgb(255, 162, 106, 66) : Color.FromArgb(255, 184, 148, 113), 1.2f * s))
            g.DrawEllipse(pen, rect);
        using var font = new Font("Microsoft YaHei UI", 9f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(ink);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, text, rect, fmt);
    }

    void DrawStick(Graphics g, float s, float cx, float cy, short ax, short ay, bool down, string label)
    {
        float r = 17 * s;
        using (var brush = new SolidBrush(Color.FromArgb(255, 227, 203, 165)))
            g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(Color.FromArgb(255, 177, 139, 104), 1.2f * s))
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        float ox = ax / 32767f * 6 * s, oy = -ay / 32767f * 6 * s;
        using (var brush = new SolidBrush(down ? Color.FromArgb(255, 255, 199, 120) : Color.FromArgb(255, 237, 220, 193)))
            g.FillEllipse(brush, cx - r * 0.55f + ox, cy - r * 0.55f + oy, r * 1.1f, r * 1.1f);
        using var font = new Font("Microsoft YaHei UI", 7f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(200, 133, 104, 76));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        g.DrawString(label, font, text, new RectangleF(cx - r, cy + r - 8 * s, r * 2, 8 * s), fmt);
    }

    void DrawPadKey(Graphics g, float s, float x, float y, float w, float h, string label, bool down, float radius)
    {
        var r = new RectangleF(x, y, w, h);
        using (var path = PetComposer.Rounded(r, radius))
        using (var brush = new SolidBrush(down ? Color.FromArgb(255, 255, 199, 120) : Color.FromArgb(255, 246, 234, 210)))
            g.FillPath(brush, path);
        using (var path = PetComposer.Rounded(r, radius))
        using (var pen = new Pen(Color.FromArgb(255, 176, 140, 106), 1f * s))
            g.DrawPath(pen, path);
        using var font = new Font("Microsoft YaHei UI", 7.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(PetComposer.InkStrong);
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
                _ => Mode.Controller
            });
            return;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll?.Dispose();
        _renderTimer?.Dispose();
        _pulseTimer?.Dispose();
        _surface.Dispose();
        base.OnClosed(e);
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);

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
