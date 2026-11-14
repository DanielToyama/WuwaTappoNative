using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// OBS 采集窗口：带可拖动/缩放的标题栏，绘制主角完整 GUI（数字/计数条/宝箱/昵称/按钮），
/// 并在键盘或手柄模式开启时把对应 GUI 拼在下方。背景可以是自定义色号或透明。
/// 透明模式使用分层窗口提交 ARGB 位图，保留角色、表情气泡等半透明内容的原始 alpha。
/// OBS 窗口采集选择 “Wuwatappo Capture”；透明模式需在 OBS 中允许透明。
/// </summary>
public sealed class CaptureWindow : Form
{
    const int TitleBarH = 28;
    const int ResizeEdge = 6;
    const int WM_NCHITTEST = 0x0084;
    const int HTCLIENT = 1;
    const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    readonly PetWindow _pet;
    readonly Screen _screen;
    readonly LayeredSurface _surface = new();
    readonly bool _compatibility;
    bool _detached;
    bool _updatingSize;
    Color _backgroundColor = Color.Lime;
    bool _transparent;
    bool _dragging;
    bool _rendering;
    Point _dragScreenStart;
    Point _dragWindowStart;

    /// <summary>由 PetWindow 主动关闭时置 true，避免把设置开关误关掉。</summary>
    public bool ClosingFromOwner { get; set; }
    public bool Compatibility => _compatibility;

    public CaptureWindow(PetWindow pet, bool compatibility = false)
    {
        _pet = pet;
        _compatibility = compatibility;
        _screen = Screen.FromControl(pet);
        Text = "Wuwatappo Capture";
        Icon = AppIcon.Panel;
        // 现代模式使用分层窗口和自绘标题栏；兼容模式回退到普通 Sizable 窗体。
        FormBorderStyle = compatibility ? FormBorderStyle.Sizable : FormBorderStyle.None;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = false;
        DoubleBuffered = true;
        MinimumSize = new Size(160, 160);
        ClientSize = new Size(400, 700);
        Location = new Point(_screen.WorkingArea.Left + 40, _screen.WorkingArea.Top + 40);
        if (compatibility) BackColor = _backgroundColor;

        var menu = new ContextMenuStrip();
        menu.Items.Add("重置到左上角", null, (s, e) =>
        {
            Location = new Point(_screen.WorkingArea.Left + 40, _screen.WorkingArea.Top + 40);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭采集窗口", null, (s, e) => _pet.DisableCaptureMode());
        ContextMenuStrip = menu;

        _pet.CaptureChanged += OnPetChanged;
        if (_pet.KeyboardUI != null) _pet.KeyboardUI.VisualChanged += OnPetChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_compatibility)
            {
                // 兼容模式：保留普通顶层窗口，不用分层窗口。
                cp.ExStyle &= ~0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00040000;  // WS_EX_APPWINDOW
                return cp;
            }
            // WS_POPUP + WS_VISIBLE；分层窗口用 UpdateLayeredWindow 提交。
            cp.Style = unchecked((int)0x80000000L) | 0x10000000;
            // WS_EX_LAYERED + WS_EX_APPWINDOW：OBS 能枚举到窗口标题。
            cp.ExStyle = 0x00080000 | 0x00040000;
            return cp;
        }
    }

    public void SetCaptureSize(int width, int height)
    {
        width = Math.Clamp(width, 160, 1600);
        height = Math.Clamp(height, 160, 1600);
        if (ClientSize.Width == width && ClientSize.Height == height) return;
        _updatingSize = true;
        try { ClientSize = new Size(width, height); }
        finally { _updatingSize = false; }
        Render();
    }

    public void SetBackground(string colorHex, bool transparent)
    {
        _transparent = transparent;
        _backgroundColor = ParseColor(colorHex);
        if (_compatibility)
        {
            // 兼容模式不支持逐像素 alpha，透明设置退化为纯色背景，避免 OBS 黑影。
            _transparent = false;
            BackColor = _backgroundColor;
            TransparencyKey = Color.Empty;
        }
        Render();
    }

    static Color ParseColor(string value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var color = ColorTranslator.FromHtml(value.Trim());
                if (color.A != 0) return color;
            }
        }
        catch { }
        return Color.Lime;
    }

    void OnPetChanged()
    {
        if (!IsDisposed) Render();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_compatibility)
        {
            e.Graphics.Clear(_backgroundColor);
            DrawContent(e.Graphics, 0, ClientSize.Height);
            base.OnPaint(e);
            return;
        }
        Render();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Render();
    }

    void Render()
    {
        if (_rendering || IsDisposed || !IsHandleCreated) return;
        if (_compatibility)
        {
            Invalidate();
            return;
        }
        _rendering = true;
        try
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            var bitmap = _surface.Begin(ClientSize.Width, ClientSize.Height);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(_transparent ? Color.Transparent : _backgroundColor);
            DrawTitleBar(g);
            DrawContent(g, TitleBarH, Math.Max(1, ClientSize.Height - TitleBarH));
            _surface.Apply(this);
        }
        catch (Exception ex) { Log.Write("采集窗口绘制失败 " + ex); }
        finally { _rendering = false; }
    }

    void DrawContent(Graphics g, int top, int height)
    {
        var keyboard = _pet.KeyboardUI;
        bool showKeyboard = keyboard != null && keyboard.CurrentMode != KeyboardWindow.Mode.Hidden;
        int logicalW = PetComposer.WindowW;
        int keyboardH = showKeyboard ? keyboard.LogicalHeightForCapture : 0;
        int logicalH = PetComposer.WindowH + (showKeyboard ? 4 : 0) + keyboardH;
        if (logicalH <= 0 || height <= 0) return;

        float scale = Math.Min(ClientSize.Width / (float)logicalW, height / (float)logicalH);
        int contentW = Math.Max(1, (int)Math.Round(logicalW * scale));
        int contentH = Math.Max(1, (int)Math.Round(logicalH * scale));
        int x = (ClientSize.Width - contentW) / 2;
        int y = top + (height - contentH) / 2;
        var petDest = new Rectangle(x, y, contentW, Math.Max(1, (int)Math.Round(PetComposer.WindowH * scale)));
        _pet.DrawCapture(g, petDest);

        if (showKeyboard)
        {
            int kbW = Math.Max(1, (int)Math.Round(keyboard.CaptureWidth * scale));
            int kbH = Math.Max(1, (int)Math.Round(keyboardH * scale));
            // 与桌宠底部数字显示板对齐，而不是按整个桌宠窗口居中。
            int kx = KeyboardWindow.AlignedLeftForPet(x, contentW, kbW);
            int ky = petDest.Bottom + Math.Max(1, (int)Math.Round(4 * scale));
            keyboard.DrawTo(g, new Rectangle(kx, ky, kbW, kbH));
        }
    }

    void DrawTitleBar(Graphics g)
    {
        using var background = new SolidBrush(Color.FromArgb(_transparent ? 235 : 255, PetComposer.Cream));
        g.FillRectangle(background, 0, 0, ClientSize.Width, TitleBarH);
        using var border = new Pen(Color.FromArgb(90, PetComposer.Border), 1);
        g.DrawLine(border, 0, TitleBarH - 1, ClientSize.Width, TitleBarH - 1);
        using var font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(PetComposer.InkStrong);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.DrawString(Text, font, text, new PointF(10, 6));
        using var closeFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("×", closeFont, text, new RectangleF(ClientSize.Width - 34, 0, 28, TitleBarH), format);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_updatingSize && ClientSize.Width >= MinimumSize.Width && ClientSize.Height >= MinimumSize.Height)
            _pet.SetCaptureSizeFromWindow(ClientSize.Width, ClientSize.Height);
    }

    protected override void WndProc(ref Message m)
    {
        if (!_compatibility && m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                var p = PointToClient(new Point(x, y));
                bool left = p.X < ResizeEdge;
                bool right = p.X >= ClientSize.Width - ResizeEdge;
                bool top = p.Y < ResizeEdge;
                bool bottom = p.Y >= ClientSize.Height - ResizeEdge;
                if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_compatibility || e.Button != MouseButtons.Left || e.Y >= TitleBarH) return;
        if (e.X >= ClientSize.Width - 36)
        {
            _pet.DisableCaptureMode();
            return;
        }
        _dragging = true;
        _dragScreenStart = Cursor.Position;
        _dragWindowStart = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = Cursor.Position;
        Location = new Point(_dragWindowStart.X + p.X - _dragScreenStart.X,
            _dragWindowStart.Y + p.Y - _dragScreenStart.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
        }
        base.OnMouseUp(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Detach();
        _surface.Dispose();
        if (!ClosingFromOwner) _pet.OnCaptureWindowUserClosed();
        base.OnFormClosed(e);
    }

    void Detach()
    {
        if (_detached) return;
        _detached = true;
        try { _pet.CaptureChanged -= OnPetChanged; } catch { }
        try { if (_pet.KeyboardUI != null) _pet.KeyboardUI.VisualChanged -= OnPetChanged; } catch { }
        try { ContextMenuStrip?.Dispose(); } catch { }
    }
}
