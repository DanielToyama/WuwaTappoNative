using System;
using System.Drawing;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// OBS 采集窗口：带标准可调整标题栏、纯绿背景，绘制主角完整 GUI（数字/计数条/宝箱/昵称/按钮），
/// 并在键盘或手柄模式开启时把对应 GUI 拼在下方。OBS 窗口采集选择 “Wuwatappo Capture”。
/// </summary>
public sealed class CaptureWindow : Form
{
    readonly PetWindow _pet;
    readonly Screen _screen;
    bool _detached;

    /// <summary>由 PetWindow 主动关闭时置 true，避免把设置开关误关掉。</summary>
    public bool ClosingFromOwner { get; set; }

    public CaptureWindow(PetWindow pet)
    {
        _pet = pet;
        _screen = Screen.FromControl(pet);
        Text = "Wuwatappo Capture";
        Icon = AppIcon.Panel;
        // 不要用 SizableToolWindow：它会带 WS_EX_TOOLWINDOW，OBS 的窗口列表会直接过滤掉。
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = false;
        DoubleBuffered = true;
        BackColor = Color.Lime;
        MinimumSize = new Size(160, 160);
        ClientSize = new Size(400, 700);
        Location = new Point(_screen.WorkingArea.Left + 40, _screen.WorkingArea.Top + 40);

        var menu = new ContextMenuStrip();
        menu.Items.Add("重置到左上角", null, (s, e) =>
        {
            Location = new Point(_screen.WorkingArea.Left + 40, _screen.WorkingArea.Top + 40);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭绿幕窗口", null, (s, e) => _pet.DisableCaptureMode());
        ContextMenuStrip = menu;

        _pet.CaptureChanged += OnPetChanged;
        if (_pet.KeyboardUI != null) _pet.KeyboardUI.VisualChanged += OnPetChanged;
        ResizeEnd += (s, e) => _pet.SetCaptureSizeFromWindow(ClientSize.Width, ClientSize.Height);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 双重保证：普通顶层窗口 + 任务栏/Alt+Tab 可见，OBS 才会枚举到标题。
            cp.ExStyle &= ~0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00040000;  // WS_EX_APPWINDOW
            return cp;
        }
    }

    public void SetCaptureSize(int width, int height)
    {
        width = Math.Clamp(width, 160, 1600);
        height = Math.Clamp(height, 160, 1600);
        if (ClientSize.Width == width && ClientSize.Height == height) return;
        ClientSize = new Size(width, height);
        Invalidate();
    }

    void OnPetChanged()
    {
        if (!IsDisposed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Lime);
        var keyboard = _pet.KeyboardUI;
        bool showKeyboard = keyboard != null && keyboard.CurrentMode != KeyboardWindow.Mode.Hidden;
        int logicalW = PetComposer.WindowW;
        int keyboardH = showKeyboard ? keyboard.LogicalHeightForCapture : 0;
        int logicalH = PetComposer.WindowH + (showKeyboard ? 4 : 0) + keyboardH;
        if (logicalH <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        float scale = Math.Min(ClientSize.Width / (float)logicalW, ClientSize.Height / (float)logicalH);
        int contentW = Math.Max(1, (int)Math.Round(logicalW * scale));
        int contentH = Math.Max(1, (int)Math.Round(logicalH * scale));
        int x = (ClientSize.Width - contentW) / 2;
        int y = (ClientSize.Height - contentH) / 2;
        var petDest = new Rectangle(x, y, contentW, Math.Max(1, (int)Math.Round(PetComposer.WindowH * scale)));
        _pet.DrawCapture(e.Graphics, petDest);

        if (showKeyboard)
        {
            int kbW = Math.Max(1, (int)Math.Round(keyboard.CaptureWidth * scale));
            int kbH = Math.Max(1, (int)Math.Round(keyboardH * scale));
            int kx = x + (contentW - kbW) / 2;
            int ky = petDest.Bottom + Math.Max(1, (int)Math.Round(4 * scale));
            keyboard.DrawTo(e.Graphics, new Rectangle(kx, ky, kbW, kbH));
        }
        base.OnPaint(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Detach();
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
