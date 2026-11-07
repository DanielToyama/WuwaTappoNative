using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Pet;

/// <summary>联机成员桌宠：200×(211+净空) 分层窗口，昵称条 + 表情气泡。上方留出气泡上浮空间，避免被窗口裁掉。</summary>
public sealed class RemoteCatWindow : Form
{
    public const int CatW = 200, CatH = 211, Headroom = 130;
    public const int W = CatW, H = CatH + Headroom;
    public const int CatSize = 200;
    readonly RoomMember _member;
    readonly PetImageLoader _loader;
    readonly Catalog _catalog;
    readonly AssetStore _store;

    PetFrames _frames;
    readonly EmoteBubbleLayer _bubbles = new(6);
    ContextMenuStrip _menu;
    ToolStripMenuItem _scaleMenu, _opacityMenu;
    double _remoteScale = 1.0, _remoteOpacity = 1.0, _remoteNameScale = 1.0;
    public event Action<double> RemoteScaleRequested;
    public event Action<double> RemoteOpacityRequested;
    System.Windows.Forms.Timer _handReset, _hoverFadeTimer, _hoverFadeAnimTimer;
    int _bodyFrame = 1;
    string _lastAction = "";
    long _lastSkin = -1, _lastPart = -1;
    string _lastNickname;
    readonly LayeredSurface _surface = new();
    Bitmap _baseFrame;
    (Size Size, Image Head, Image Body, Image Accessory, double X, double Y, float Scale, double NameScale, string Nickname) _baseFrameKey;
    float _scale = 1f;
    bool _dragging;
    Point _dragCursor, _dragOrigin;
    bool _movedDuringDrag;

    public event Action<Point> Moved;

    public RemoteCatWindow(RoomMember member, PetImageLoader loader, Catalog catalog, AssetStore store)
    {
        _member = member;
        _loader = loader;
        _catalog = catalog;
        _store = store;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        BuildRemoteMenu();
        _handReset = new System.Windows.Forms.Timer { Interval = 220 };
        _handReset.Tick += (s, e) => { _handReset.Stop(); SetBody(1); };
        _hoverFadeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _hoverFadeTimer.Tick += (s, e) =>
        {
            _hoverFadeTimer.Stop();
            if (!_hovered || !_hoverFade) return;
            StartHoverFade(true);
        };
        _hoverFadeAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _hoverFadeAnimTimer.Tick += (s, e) =>
        {
            double target = _hoverFadeOn ? 1.0 : 0.0;
            double diff = target - _hoverFadeAmount;
            if (Math.Abs(diff) < 0.06)
            {
                _hoverFadeAmount = target;
                _hoverFadeAnimTimer.Stop();
            }
            else _hoverFadeAmount += Math.Sign(diff) * 0.08;
            UpdateHoverOpacity();
        };
        Reload();
    }

    public long MemberId => _member.Id;

    void BuildRemoteMenu()
    {
        _menu = new ContextMenuStrip();
        _scaleMenu = new ToolStripMenuItem("联机角色大小");
        foreach (var v in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) => RemoteScaleRequested?.Invoke(value);
            _scaleMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(_scaleMenu);

        _opacityMenu = new ToolStripMenuItem("联机角色透明度");
        foreach (var v in new[] { 1.0, 0.9, 0.75, 0.5 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) => RemoteOpacityRequested?.Invoke(value);
            _opacityMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(_opacityMenu);
        _menu.Opening += (s, e) => RefreshRemoteMenu();
    }

    public void SetRemoteStyle(double scale, double opacity, double nameScale = 1.0)
    {
        double oldNameScale = _remoteNameScale;
        _remoteScale = Math.Clamp(scale, 0.25, 2.0);
        _remoteOpacity = Math.Clamp(opacity, 0.2, 1.0);
        _remoteNameScale = Math.Clamp(nameScale, 0.5, 2.0);
        RefreshRemoteMenu();
        if (Math.Abs(oldNameScale - _remoteNameScale) > 0.001 && IsHandleCreated)
            Compose();
    }

    void RefreshRemoteMenu()
    {
        if (_menu == null) return;
        _scaleMenu.Text = $"联机角色大小（{(int)Math.Round(_remoteScale * 100)}%）";
        _opacityMenu.Text = $"联机角色透明度（{(int)Math.Round(_remoteOpacity * 100)}%）";
        foreach (ToolStripItem sub in _scaleMenu.DropDownItems)
        {
            if (sub is ToolStripMenuItem item && item.Tag is double preset)
                item.Checked = Math.Abs(_remoteScale - preset) < 0.005;
        }
        foreach (ToolStripItem sub in _opacityMenu.DropDownItems)
        {
            if (sub is ToolStripMenuItem item && item.Tag is double preset)
                item.Checked = Math.Abs(_remoteOpacity - preset) < 0.005;
        }
    }

    double _opacity = 1.0, _opacityBase = 1.0;
    bool _clickThrough, _hoverFade, _hovered, _hoverFadeOn, _topmost = true;
    double _hoverFadeOpacity = 0.4, _hoverFadeAmount;

    /// <summary>
    /// 与主桌宠保持一致的窗口选项：不透明度 / 悬停淡出 / 鼠标穿透 / 置顶。
    /// 原版里这些是全局设置，联机成员的窗口也必须跟着变，否则会出现“只有主角能穿透”的割裂感。
    /// </summary>
    public void SetWindowOptions(double opacity, bool hoverFade, bool clickThrough, bool topmost, double hoverFadeOpacity = 0.4)
    {
        _opacityBase = Math.Clamp(opacity, 0.1, 1.0);
        _hoverFadeOpacity = Math.Clamp(hoverFadeOpacity, 0.1, 1.0);
        bool fadeWasOn = _hoverFade;
        _hoverFade = hoverFade;
        if (!_hoverFade)
        {
            _hoverFadeTimer?.Stop();
            _hoverFadeAnimTimer?.Stop();
            _hoverFadeOn = false;
            _hoverFadeAmount = 0;
        }
        else if (!fadeWasOn && _hovered && !_hoverFadeOn)
        {
            // 刚打开“悬停半透明”时也遵守 500ms 停留时间。
            _hoverFadeTimer.Stop();
            _hoverFadeTimer.Start();
        }
        bool stylesChanged = clickThrough != _clickThrough || topmost != _topmost;
        _clickThrough = clickThrough;
        _topmost = topmost;
        if (stylesChanged && IsHandleCreated) ApplyWindowStyles();
        UpdateHoverOpacity();
    }

    /// <summary>鼠标进入/离开：停留 500ms 后才淡出，离开时平滑恢复。</summary>
    public void SetHovered(bool hovered)
    {
        if (hovered == _hovered) return;
        _hovered = hovered;
        _hoverFadeTimer?.Stop();
        if (!_hoverFade)
        {
            _hoverFadeOn = false;
            _hoverFadeAmount = 0;
            UpdateHoverOpacity();
            return;
        }
        if (hovered)
        {
            _hoverFadeOn = false;
            _hoverFadeTimer.Start();
            return;
        }
        _hoverFadeOn = false;
        StartHoverFade(false);
    }

    void StartHoverFade(bool on)
    {
        _hoverFadeOn = on;
        if (!_hoverFadeAnimTimer.Enabled) _hoverFadeAnimTimer.Start();
    }

    void UpdateHoverOpacity()
    {
        double factor = 1.0 - _hoverFadeAmount * (1.0 - _hoverFadeOpacity);
        double next = Math.Clamp(_opacityBase * factor, 0.1, 1.0);
        if (Math.Abs(next - _opacity) < 0.001) return;
        _opacity = next;
        // 只更新窗口常量 alpha，不重画整只猫。
        if (_surface.Bitmap != null) _surface.Apply(this, _opacity);
    }

    /// <summary>鼠标穿透时由主窗口轮询光标位置，判断是否命中可见猫像素；透明气泡净空不算。</summary>
    public bool ContainsScreenPoint(Point p)
    {
        if (!Visible || IsDisposed || !IsHandleCreated) return false;
        var pt = PointToClient(p);
        if (pt.X < 0 || pt.Y < 0 || pt.X >= Width || pt.Y >= Height) return false;
        int head = (int)Math.Round(Headroom * _scale);
        int catH = (int)Math.Round(CatH * _scale);
        if (pt.Y < head || pt.Y >= head + catH) return false;
        var bmp = _surface.Bitmap;
        if (bmp == null) return false;
        try { return bmp.GetPixel(pt.X, pt.Y).A > 8; } catch { return false; }
    }

    void ApplyWindowStyles()
    {
        if (!IsHandleCreated) return;
        long ex = GetWindowLongPtr(Handle, -20).ToInt64();
        if (_clickThrough) ex |= 0x00000020L;
        else ex &= ~0x00000020L;
        if (_topmost) ex |= 0x00000008L;
        else ex &= ~0x00000008L;
        SetWindowLongPtr(Handle, -20, new IntPtr(ex));
        SetWindowPos(Handle, _topmost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
    }

    static readonly IntPtr HwndTopMost = new(-1);
    static readonly IntPtr HwndNoTopMost = new(-2);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public void SetScale(float scale)
    {
        scale = Math.Clamp(scale, 0.25f, 2.5f);
        var target = new Size(Math.Max(1, (int)Math.Round(W * scale)), Math.Max(1, (int)Math.Round(H * scale)));
        if (Math.Abs(scale - _scale) < 0.001f && Size == target) return;
        _scale = scale;
        if (Size != target) Size = target;
        Compose();
    }

    public void RefreshMember()
    {
        Reload();
        ApplyAction(_member.Action);
    }

    /// <summary>远端按键包走这里：只切换手部帧，不重建外观。</summary>
    public void ApplyMemberAction(string action) => ApplyAction(action);

    void ApplyAction(string action)
    {
        if (action == _lastAction) return;
        _lastAction = action;
        if (action == "left") SetBody(2);
        else if (action == "right") SetBody(3);
        else SetBody(1);
        if (action == "left" || action == "right")
        {
            _handReset.Stop();
            _handReset.Start();
        }
    }

    void SetBody(int f)
    {
        if (_bodyFrame == f) return;
        _bodyFrame = f;
        CatPainter.Request(this);
    }

    /// <summary>立即重绘（首帧、换装、目录加载完成等一次性场景用）。</summary>
    internal void PaintNow()
    {
        if (IsDisposed || !IsHandleCreated) return;
        Compose();
    }

    void Reload()
    {
        long skin = _catalog.ResolveId("character_skin", _member.RoleKey);
        long part = _catalog.ResolveId("accessory", _member.PartKey);
        // 动作包会高频触发 MemberUpdated。只要 loader 返回的还是同一个帧对象，就不必重建；
        // 目录加载完成后 loader.Clear() 会产出新对象，这里能自然识别并重新订阅。
        var frames = _loader.Get(skin, part);
        bool framesChanged = !ReferenceEquals(frames, _frames);
        if (framesChanged)
        {
            if (_frames != null) _frames.Changed -= OnFramesChanged;
            _frames = frames;
            _frames.Changed += OnFramesChanged;
        }
        // 外观与昵称都没变就不要重画：人多时每条动作包都全量重绘是最主要的开销。
        bool appearanceChanged = framesChanged || skin != _lastSkin || part != _lastPart;
        bool nicknameChanged = !string.Equals(_lastNickname, _member.Nickname);
        _lastSkin = skin;
        _lastPart = part;
        _lastNickname = _member.Nickname;
        if (appearanceChanged || nicknameChanged) Compose();
    }
    void OnFramesChanged()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Compose)); } catch { }
    }

    public void ShowEmote(string name)
    {
        Image img = ResolveEmote(name);
        if (img != null) PushBubble(img);
    }

    void PushBubble(Image image)
    {
        _bubbles.Add(image);
        // 多个远程桌宠共用一个表情动画时钟；只有第一次入队时请求重绘。
        if (RemoteEmoteAnimator.Add(this)) CatPainter.Request(this);
    }

    internal bool TickEmote()
    {
        bool active = _bubbles.Tick();
        CatPainter.Request(this);
        return active;
    }

    Image ResolveEmote(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (long.TryParse(name, out var id)) return EmoteImage(id);
        string fileKey = name.Replace("emote_", "").Replace(".png", "");
        foreach (var e in _catalog.All("emote"))
        {
            if (e.Url.IndexOf(fileKey, StringComparison.OrdinalIgnoreCase) >= 0) return EmoteImage(e.Id);
        }
        return null;
    }

    Image EmoteImage(long id)
    {
        var e = _catalog.Find("emote", id);
        if (e == null) return null;
        string url = _catalog.CdnUrl(e.Url);
        var img = _store.GetBubbleImage(url, onReady: () =>
        {
            var loaded = _store.GetBubbleImage(url, allowDownload: false);
            if (loaded != null && IsHandleCreated) BeginInvoke(new Action(() => PushBubble(loaded)));
        });
        return img;
    }

    void Compose()
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        // 复用同一张后缓冲：人多时每个成员每秒都会重画，反复分配 150KB 位图会造成明显的 GC 压力。
        var surface = _surface.Begin(w, h);
        ComposeInto(surface);
        // 这里是全不透明内容；悬停/窗口透明度交给 UpdateLayeredWindow 的常量 alpha。
        _surface.Apply(this, _opacity);
    }

    internal void ComposeInto(Bitmap surface)
    {
        bool ready = _frames != null && _frames.Ready;
        Image selectedBody = _frames == null ? null : _bodyFrame switch
        {
            2 => _frames.Body2 ?? _frames.Body1,
            3 => _frames.Body3 ?? _frames.Body1,
            _ => _frames.Body1
        };
        var key = (surface.Size, _frames?.Head1, selectedBody, _frames?.Accessory,
            _frames?.AccOffsetX ?? 0, _frames?.AccOffsetY ?? 0, _scale, _remoteNameScale, _member.Nickname);
        if (ready && (_baseFrame == null || _baseFrameKey != key))
        {
            if (_baseFrame == null || _baseFrame.Size != surface.Size)
            {
                _baseFrame?.Dispose();
                _baseFrame = new Bitmap(surface.Width, surface.Height, PixelFormat.Format32bppPArgb);
            }
            using (var g = Graphics.FromImage(_baseFrame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float s = _scale;
                float top = Headroom * s;
                float cat = CatSize * s;
                if (_frames.Head1 != null) g.DrawImage(_frames.Head1, 0, top, cat, cat);
                Image body = _bodyFrame switch
                {
                    2 => _frames.Body2 ?? _frames.Body1,
                    3 => _frames.Body3 ?? _frames.Body1,
                    _ => _frames.Body1
                };
                if (body != null) g.DrawImage(body, 0, top, cat, cat);
                if (_frames.Accessory != null)
                    g.DrawImage(_frames.Accessory, (float)(_frames.AccOffsetX * s), (float)(_frames.AccOffsetY * s) + top, cat, cat);

                float nameFontPx = 13.5f * s * (float)_remoteNameScale;
                float pillH = Math.Max(32f * s, nameFontPx * 1.35f);
                var pill = new RectangleF(10 * s, (Headroom + 179) * s - (pillH - 32 * s) / 2f, 180 * s, pillH);
                using (var path = PetComposer.Rounded(pill, 16 * s))
                using (var brush = new SolidBrush(PetComposer.Cream))
                    g.FillPath(brush, path);
                using (var font = new Font("Microsoft YaHei UI", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(PetComposer.InkStrong))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(string.IsNullOrEmpty(_member.Nickname) ? "漂泊者" : _member.Nickname, font, brush, pill, fmt);

                // 手/手臂要压住昵称条；角色头部和身体其余部分仍在 UI 后面。
                if (body != null)
                {
                    var state = g.Save();
                    try
                    {
                        using var clip = PetComposer.Rounded(pill, 16 * s);
                        g.SetClip(clip, CombineMode.Intersect);
                        g.DrawImage(body, 0, top, cat, cat);
                    }
                    finally { g.Restore(state); }
                }

                _baseFrameKey = key;
            }
        }
        using var target = Graphics.FromImage(surface);
        target.Clear(Color.Transparent);
        if (ready)
        {
            target.SmoothingMode = SmoothingMode.AntiAlias;
            target.InterpolationMode = InterpolationMode.HighQualityBicubic;
            target.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            target.DrawImageUnscaled(_baseFrame, 0, 0);
            _bubbles.Draw(target, _scale, 100 * _scale, (Headroom + 100) * _scale);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = unchecked((int)0x80000000L) | 0x10000000;
            cp.ExStyle = 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008;
            if (_clickThrough) cp.ExStyle |= 0x00000020;
            if (!_topmost) cp.ExStyle &= ~0x00000008;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowStyles();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // 悬停半透明按窗口各自生效：鼠标在谁身上谁变淡，而不是一悬停全体一起淡。
        SetHovered(true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _movedDuringDrag = false;
        _dragCursor = Cursor.Position;
        _dragOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = Cursor.Position;
        int dx = p.X - _dragCursor.X, dy = p.Y - _dragCursor.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 2) _movedDuringDrag = true;
        // 先算好贴边后的目标位置，再一次性移动，避免先移出边缘再吸回来造成左右闪烁。
        Location = SnapToWorkingArea(new Point(_dragOrigin.X + dx, _dragOrigin.Y + dy), p);
    }

    /// <summary>联机成员桌宠与主角一样，拖到屏幕/任务栏边缘附近时自动贴边。</summary>
    Point SnapToWorkingArea(Point target, Point cursor)
    {
        var wa = Screen.FromPoint(cursor).WorkingArea;
        float dpi = DeviceDpi / 96f;
        int threshold = Math.Max(8, (int)Math.Round(28 * dpi));
        int left = target.X, top = target.Y;
        if (Math.Abs(left - wa.Left) <= threshold) left = wa.Left;
        else if (Math.Abs(left + Width - wa.Right) <= threshold) left = wa.Right - Width;
        if (Math.Abs(top - wa.Top) <= threshold) top = wa.Top;
        else if (Math.Abs(top + Height - wa.Bottom) <= threshold) top = wa.Bottom - Height;
        return new Point(left, top);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            // 只在真正可见的猫/昵称区域弹菜单，透明气泡净空不响应。
            if (ContainsScreenPoint(Cursor.Position))
            {
                RefreshRemoteMenu();
                _menu?.Show(Cursor.Position);
            }
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_movedDuringDrag) Location = SnapToWorkingArea(Location, Cursor.Position);
        _dragging = false;
        if (_movedDuringDrag) Moved?.Invoke(Location);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_frames != null) _frames.Changed -= OnFramesChanged;
        RemoteEmoteAnimator.Remove(this);
        CatPainter.Remove(this);
        _bubbles.Clear();
        _menu?.Dispose();
        _handReset.Dispose();
        _hoverFadeTimer.Dispose();
        _hoverFadeAnimTimer.Dispose();
        _baseFrame?.Dispose();
        _surface.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>
/// 分层窗口的后缓冲。用 CreateDIBSection 直接拿到像素内存，绘制和 UpdateLayeredWindow
/// 共用同一块 DIB，省掉每次重绘 GetHbitmap 的整幅拷贝；人多时这个拷贝是主要的热点之一。
/// </summary>
public sealed class LayeredSurface : IDisposable
{
    Bitmap _bitmap;
    IntPtr _dc, _dib, _old;
    int _w, _h;
    bool _fallback;

    public Bitmap Bitmap => _bitmap;

    /// <summary>确保后缓冲尺寸，返回可直接绘制的位图。</summary>
    public Bitmap Begin(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_bitmap != null && _w == width && _h == height) return _bitmap;
        Release();
        if (!_fallback)
        {
            try
            {
                _dc = Layered.CreateCompatibleDC(IntPtr.Zero);
                var info = new Layered.BITMAPINFO
                {
                    header = new Layered.BITMAPINFOHEADER
                    {
                        size = 40,
                        width = width,
                        height = -height, // top-down
                        planes = 1,
                        bitCount = 32,
                        compression = 0,
                    }
                };
                _dib = Layered.CreateDIBSection(_dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (_dc != IntPtr.Zero && _dib != IntPtr.Zero && bits != IntPtr.Zero)
                {
                    _old = Layered.SelectObject(_dc, _dib);
                    _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
                    _w = width;
                    _h = height;
                    return _bitmap;
                }
            }
            catch (Exception ex) { Log.Write("分层后缓冲创建失败，回退到普通位图 " + ex.Message); }
            // DIB 失败就退回普通位图，绘制路径不变，只是每次 Apply 多一次拷贝。
            Release();
            _fallback = true;
        }
        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _w = width;
        _h = height;
        return _bitmap;
    }

    /// <summary>
    /// 提交窗口。opacity 通过 UpdateLayeredWindow 的 SourceConstantAlpha 完成，
    /// 不再为了淡出效果重画整张位图，鼠标频繁悬停时能明显降低 GDI+ 占用。
    /// </summary>
    public void Apply(Form form, double opacity = 1.0)
    {
        if (_bitmap == null) return;
        byte alpha = (byte)Math.Round(255 * Math.Clamp(opacity, 0.0, 1.0));
        Layered.Commit(form, _bitmap, _dc, _w, _h, alpha);
    }

    void Release()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (_old != IntPtr.Zero) { Layered.SelectObject(_dc, _old); _old = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { Layered.DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_dc != IntPtr.Zero) { Layered.DeleteDC(_dc); _dc = IntPtr.Zero; }
        _w = _h = 0;
    }

    public void Dispose() => Release();
}

/// <summary>
/// 多个远程桌宠共用的活动动画时钟。避免每个成员各自持有一个高频计时器，
/// 人多时大量 WM_TIMER 消息和重复整窗重绘会抢占 UI 线程。
/// </summary>
static class RemoteEmoteAnimator
{
    static readonly AnimationTimer Timer = new();
    static readonly HashSet<RemoteCatWindow> Active = new();

    static RemoteEmoteAnimator()
    {
        Timer.Tick += (s, e) => Pump();
    }

    /// <summary>加入动画；返回 true 表示这是该窗口本轮第一次加入，需要立即请求一帧。</summary>
    public static bool Add(RemoteCatWindow window)
    {
        if (window == null || window.IsDisposed) return false;
        bool added = Active.Add(window);
        if (!Timer.Enabled) Timer.Start();
        return added;
    }

    public static void Remove(RemoteCatWindow window)
    {
        Active.Remove(window);
        if (Active.Count == 0) Timer.Stop();
    }

    static void Pump()
    {
        if (Active.Count == 0)
        {
            Timer.Stop();
            return;
        }
        foreach (var window in new List<RemoteCatWindow>(Active))
        {
            if (window.IsDisposed || !window.IsHandleCreated)
            {
                Active.Remove(window);
                continue;
            }
            if (!window.TickEmote()) Active.Remove(window);
        }
        if (Active.Count == 0) Timer.Stop();
    }
}

/// <summary>
/// 远端桌宠的重绘调度器：同一帧内被反复标记的窗口只画一次，且每 16ms 只处理固定数量。
/// 几十人同时按键时，如果每个事件都立即重绘，UI 线程会在一帧里堆积几十次整窗绘制，
/// 表现就是拖角色卡顿、动画掉帧。这里把绘制摊到多帧里做。
/// </summary>
static class CatPainter
{
    // 原来每 16ms 只画 6 个；40 人房间、10Hz 按键时产能不足，动作会排队 1~2 秒。
    // 现在用时间预算+单帧即时绘制：稀疏房间零排队，人多时把一帧内能画完的都画掉。
    const int PerTick = 64;
    const int BudgetMs = 10;
    const long ImmediateGapMs = 8;
    static readonly List<RemoteCatWindow> Queue = new();
    static readonly HashSet<RemoteCatWindow> Pending = new();
    static readonly AnimationTimer Timer = new();
    static long _lastPaintMs;

    static CatPainter()
    {
        Timer.Tick += (s, e) => Pump();
    }

    public static void Request(RemoteCatWindow window)
    {
        if (window.IsDisposed || !window.IsHandleCreated) return;
        if (window.InvokeRequired)
        {
            try { window.BeginInvoke(new Action<RemoteCatWindow>(Request), window); } catch { }
            return;
        }

        // 低负载时当前动作立即上屏，不再固定等下一帧。
        long now = Environment.TickCount64;
        if (Queue.Count == 0 && now - _lastPaintMs >= ImmediateGapMs)
        {
            _lastPaintMs = now;
            window.PaintNow();
            return;
        }

        if (!Pending.Add(window)) return;
        Queue.Add(window);
        if (!Timer.Enabled) Timer.Start();
    }

    internal static void Remove(RemoteCatWindow window)
    {
        Pending.Remove(window);
        Queue.Remove(window);
        if (Queue.Count == 0) Timer.Stop();
    }

    static void Pump()
    {
        long start = Environment.TickCount64;
        int budget = PerTick;
        while (budget-- > 0 && Queue.Count > 0)
        {
            var w = Queue[0];
            Queue.RemoveAt(0);
            Pending.Remove(w);
            if (!w.IsDisposed)
            {
                _lastPaintMs = Environment.TickCount64;
                w.PaintNow();
            }
            if (Environment.TickCount64 - start >= BudgetMs) break;
        }
        if (Queue.Count == 0) Timer.Stop();
    }
}

public static class Layered
{
    const int UlwAlpha = 2;
    const byte AcSrcOver = 0, AcSrcAlpha = 1;

    [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFOHEADER
    {
        public int size, width, height;
        public short planes, bitCount;
        public int compression, sizeImage, xPelsPerMeter, yPelsPerMeter, clrUsed, clrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER header;
        public int colors;
    }

    [StructLayout(LayoutKind.Sequential)] struct BlendFunction { public byte Op, Flags, ConstantAlpha, Format; }

    public static void Apply(Form form, Bitmap surface) => Commit(form, surface, IntPtr.Zero, surface.Width, surface.Height);

    internal static void Commit(Form form, Bitmap surface, IntPtr cachedDc, int width, int height, byte constantAlpha = 255)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        bool ownDc = cachedDc == IntPtr.Zero;
        IntPtr memDc = ownDc ? CreateCompatibleDC(screenDc) : cachedDc;
        IntPtr hBitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (ownDc)
            {
                hBitmap = surface.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memDc, hBitmap);
            }
            var topLeft = new Point(form.Left, form.Top);
            var size = new Size(width, height);
            var source = new Point(0, 0);
            var blend = new BlendFunction { Op = AcSrcOver, Flags = 0, ConstantAlpha = constantAlpha, Format = AcSrcAlpha };
            UpdateLayeredWindow(form.Handle, screenDc, ref topLeft, ref size, memDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (ownDc) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dstTopLeft,
        ref Size dstSize, IntPtr srcDc, ref Point srcTopLeft, int key, ref BlendFunction blend, int flags);
}
