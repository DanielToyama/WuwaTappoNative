using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
    bool _maximized;
    Rectangle _restoreBounds;
    bool _resizing;
    string _resizeDir = "";
    Point _resizeStartCursor;
    Rectangle _resizeStartBounds;
    /// <summary>
    /// Win11 使用 DWM 原生圆角（平滑抗锯齿）；旧系统才回退到 GDI Region。
    /// Region 是二值像素遮罩，和 WebView2 的 DirectComposition 圆角无法对齐，是四周锯齿/断开的根因。
    /// </summary>
    bool _dwmCorners = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    readonly System.Windows.Forms.Timer _resizeSaveTimer;
    bool _detached;
    string _pendingPage;
    ReleaseInfo _updateInfo;
    bool _updateChecked;
    readonly Dictionary<string, HashSet<long>> _ownedAssets = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource _updateDownloadCts;
    string _updateDownloadPath = "";
    string _updateDownloadTag = "";
    bool _updateDownloading;
    bool _updateInstalling;
    DateTimeOffset _ownedAssetsAt = DateTimeOffset.MinValue;
    string _ownedAssetsCuid = "";

    public PanelWindow(App app)
    {
        _app = app;
        // 高级合成按“面板会话”处理：每次重新打开面板都重置为锁定/关闭，
        // 必须重新输入隐藏指令并再次确认警告。
        if (app.Cfg.AnyItemSynthesisUnlocked || app.Cfg.AnyItemSynthesisEnabled)
        {
            app.Cfg.AnyItemSynthesisUnlocked = false;
            app.Cfg.AnyItemSynthesisEnabled = false;
            ConfigStore.Save(app.Cfg);
        }
        _maximized = app.Cfg.PanelMaximized;
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
        _resizeSaveTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _resizeSaveTimer.Tick += (s, e) =>
        {
            _resizeSaveTimer.Stop();
            SavePanelSize();
        };
        BuildWebView();

        app.Room.StateChanged += OnRoomStateChanged;
        // 人数多时名册/外观事件是成批来的：合并到下一次消息循环再推一次，
        // 否则 40 人的房间每次按键都会序列化 40 份完整房间状态发给 WebView。
        app.Room.MemberUpdated += OnMemberListChanged;
        app.Room.MemberRemoved += OnMemberListChanged;
        app.Room.RosterChanged += QueuePushRoom;
        // 拒绝/强制断开/断线以前只进托盘；面板房间页也要立即显示持久错误和重试入口。
        app.Room.JoinRejected += OnRoomJoinRejected;
        app.Room.ForcedDisconnect += OnRoomForcedDisconnect;
        app.Room.SocketClosed += OnRoomSocketClosed;
        app.Auth.Changed += OnAuthChanged;
    }

    void OnRoomJoinRejected(string message) => Safe(() =>
    {
        Push("room.error", new { kind = "rejected", message });
        Push("room", _app.RoomState());
    });

    void OnRoomForcedDisconnect(int code) => Safe(() =>
    {
        string message = code == 90002 ? "账号已在其他设备登录，房间已断开" : $"房间已关闭（{code}）";
        Push("room.error", new { kind = "forced", message });
        Push("room", _app.RoomState());
    });

    void OnRoomSocketClosed(string reason) => Safe(() =>
    {
        // 大厅/未加入房间的普通断线不打扰房间页；只有实际房间或已有错误时才提示。
        if (_app.Room.IsLeavingIntentionally) return;
        if (_app.Room.State != "joined" && string.IsNullOrEmpty(_app.Room.LastError)) return;
        string message = _app.Room.LastError;
        if (string.IsNullOrWhiteSpace(message)) message = "连接断开：" + (string.IsNullOrWhiteSpace(reason) ? "网络异常" : reason);
        Push("room.error", new { kind = "socket", message });
        Push("room", _app.RoomState());
    });

    void OnRoomStateChanged() => SafePushRoom();
    void OnMemberListChanged(long _) => QueuePushRoom();
    void OnAuthChanged() => SafePushAuth();

    /// <summary>关闭面板时解除 App 的事件订阅并释放 WebView2，否则 Hide() 会一直占着整个浏览器实例。</summary>
    void Detach()
    {
        if (_detached) return;
        _detached = true;
        _ready = false;
        try { _resizeSaveTimer?.Stop(); _resizeSaveTimer?.Dispose(); } catch { }
        try { _app.Room.StateChanged -= OnRoomStateChanged; } catch { }
        try { _app.Room.MemberUpdated -= OnMemberListChanged; } catch { }
        try { _app.Room.MemberRemoved -= OnMemberListChanged; } catch { }
        try { _app.Room.RosterChanged -= QueuePushRoom; } catch { }
        try { _app.Room.JoinRejected -= OnRoomJoinRejected; } catch { }
        try { _app.Room.ForcedDisconnect -= OnRoomForcedDisconnect; } catch { }
        try { _app.Room.SocketClosed -= OnRoomSocketClosed; } catch { }
        try { _app.Auth.Changed -= OnAuthChanged; } catch { }
        // 面板关闭时丢弃未保存的取色预览，避免桌宠和键盘停在临时配色。
        try { _app.Pet.ClearThemePreview(); } catch { }
        try { _app.Keyboard.ClearThemePreview(); } catch { }
        try { _web?.Dispose(); } catch { }
        _web = null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SavePanelSize();
        base.OnFormClosed(e);
        Detach();
    }

    /// <summary>把当前普通状态尺寸换算成 DIP 后写入 state.json，供下次打开面板恢复。</summary>
    void SavePanelSize()
    {
        if (_maximized || !IsHandleCreated || WindowState != FormWindowState.Normal) return;
        if (Width <= 0 || Height <= 0) return;
        float scale = Math.Max(1, DeviceDpi) / 96f;
        int width = Math.Max(1, (int)Math.Round(Width / scale));
        int height = Math.Max(1, (int)Math.Round(Height / scale));
        if (width == _app.Cfg.PanelWidth && height == _app.Cfg.PanelHeight) return;
        _app.Cfg.PanelWidth = width;
        _app.Cfg.PanelHeight = height;
        ConfigStore.Save(_app.Cfg);
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
        if (_maximized)
        {
            // 最大化期间屏幕分辨率/DPI 变化时继续铺满工作区。
            var maxArea = (screen ?? Screen.FromControl(this)).WorkingArea;
            if (Bounds != maxArea) Bounds = maxArea;
            ApplyRegion();
            return;
        }
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
            int designW = _app?.Cfg?.PanelWidth > 0 ? _app.Cfg.PanelWidth : 1120;
            int designH = _app?.Cfg?.PanelHeight > 0 ? _app.Cfg.PanelHeight : 780;
            int targetW = Math.Min((int)Math.Round(designW * scale), availW);
            int targetH = Math.Min((int)Math.Round(designH * scale), availH);
            // 保留一个能正常操作的下限，但不得超出当前屏幕。
            int minW = Math.Min(availW, (int)Math.Round(640 * scale));
            int minH = Math.Min(availH, (int)Math.Round(480 * scale));
            MinimumSize = new Size(minW, minH);
            targetW = Math.Max(targetW, minW);
            targetH = Math.Max(targetH, minH);
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
    /// 面板最大化/还原。这里不使用 FormWindowState.Maximized，因为无边框窗口的
    /// 最大化会覆盖任务栏区域；直接铺满当前屏幕工作区，并保留还原前的窗口位置。
    /// </summary>
    void ToggleMaximize()
    {
        if (!IsHandleCreated) return;
        if (_maximized)
        {
            _maximized = false;
            if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
                Bounds = _restoreBounds;
            else
                FitToWorkingArea(center: true);
        }
        else
        {
            SavePanelSize();
            _restoreBounds = Bounds;
            _maximized = true;
            Bounds = Screen.FromControl(this).WorkingArea;
        }
        ApplyRegion();
        // 最大化状态写入 state.json，下次打开面板保持相同的窗口形态。
        _app.Cfg.PanelMaximized = _maximized;
        ConfigStore.Save(_app.Cfg);
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
    /// <summary>认证变化只推 auth 字段，避免前端同时收到 auth+boot 后整页重建两次。</summary>
    void SafePushAuth() => Safe(() => _app.PushAuth());
    void SafePushBoot() => Safe(() => Push("boot", BootPayload()));

    /// <summary>让面板重新拉取 boot（设置/可见性等被快捷键改变时用）。</summary>
    public void PushBoot() => SafePushBoot();

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

    /// <summary>打开面板后切到指定页面；WebView 尚未加载时先记下，boot 完成后再推送。</summary>
    public void NavigateTo(string page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        if (_ready) Push("navigate", new { page });
        else _pendingPage = page;
    }

    /// <summary>切换区服/账号后强制重载 WebView，清空上一会话的背包、图鉴等页面缓存。</summary>
    public void ReloadPage()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            Safe(ReloadPage);
            return;
        }
        try
        {
            _ready = false;
            _web?.CoreWebView2?.Reload();
        }
        catch (Exception ex)
        {
            Log.Write("重载面板失败：" + ex.Message);
        }
    }

    async Task CheckUpdateAsync()
    {
        if (_updateChecked) return;
        _updateChecked = true;
        var current = typeof(PanelWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var info = await UpdateChecker.CheckAsync(current, false, _app.Cfg.UpdateUseProxy ? _app.Cfg.UpdateHttpProxy : null);
        if (info == null) return;
        _updateInfo = info;
        Push("update", info);
    }

    async Task DownloadUpdateAsync(ReleaseAsset installer, string tag, string source, string proxy, CancellationToken token)
    {
        try
        {
            var result = await UpdateDownloader.DownloadAsync(
                installer,
                source,
                proxy,
                token,
                progress => Push("update.progress", new
                {
                    tag,
                    phase = progress.Phase,
                    source = progress.Source,
                    downloaded = progress.Downloaded,
                    total = progress.Total,
                    speed = progress.SpeedBytesPerSecond,
                    message = progress.Message,
                    path = progress.Path,
                }));
            if (!token.IsCancellationRequested)
            {
                _updateDownloadPath = result.Path;
                _updateDownloadTag = tag;
            }
        }
        catch (OperationCanceledException)
        {
            Push("update.progress", new { tag, phase = "cancelled", message = "已取消下载" });
        }
        catch (Exception ex)
        {
            Log.Write("更新下载失败 " + ex.Message);
            Push("update.progress", new { tag, phase = "error", message = ex.Message });
        }
        finally
        {
            _updateDownloading = false;
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = null;
        }
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
            // 主题背景图单独映射到用户数据目录，BootPayload 只发小体积 URL，不把图片塞进消息。
            core.SetVirtualHostNameToFolderMapping("wuwatappo.theme", Paths.ThemeDir, CoreWebView2HostResourceAccessKind.Allow);
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
                if (_ready)
                {
                    if (!_dwmCorners)
                    {
                        try { core.ExecuteScriptAsync("document.documentElement.classList.remove('dwm-rounded')"); } catch { }
                    }
                    Push("boot", BootPayload());
                    if (!string.IsNullOrEmpty(_pendingPage))
                    {
                        Push("navigate", new { page = _pendingPage });
                        _pendingPage = null;
                    }
                    _ = CheckUpdateAsync();
                }
            };
            // 带版本号查询串，避免升级后 WebView2 仍使用旧 index.html/app.js 缓存。
            string panelVersion = typeof(PanelWindow).Assembly.GetName().Version?.ToString(3) ?? "0";
            string url = "https://wuwatappo.local/index.html?v=" + Uri.EscapeDataString(panelVersion);
            if (_dwmCorners) url += "&dwm=1";
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

    string RecentChestScope => (_app.Cfg.ServerRegion == "global" ? "global" : "cn") + ":" + (_app.Auth.Cuid ?? "");

    object BootPayload()
    {
        var barTheme = PetWindow.ResolveThemeBarColors(_app.Cfg);
        var accTransform = _app.Pet.AccessoryTransformFor(_app.Cfg.AccessoryAssetId);
        return new
        {
        auth = new
        {
            loggedIn = _app.Auth.IsLoggedIn,
            nickname = _app.Auth.Nickname,
            cuid = _app.Auth.Cuid,
            uid = _app.Auth.PlayerId > 0 ? _app.Auth.PlayerId : _app.Auth.UserId,
            clicks = _app.Room.AvailableClicks,
            clickTotal = _app.Room.DisplayClicks,
            clickConsume = _app.Room.ServerConsume,
            chestLeft = ApiClient.ReadLong(_app.Auth.UserInfo?["left_treasure_chest"]),
            chestRight = ApiClient.ReadLong(_app.Auth.UserInfo?["right_treasure_chest"]),
            serverOffsetMs = _app.Auth.ServerTimeOffsetMs,
        },
        room = _app.RoomState(),
        disclaimerShown = _app.Cfg.DisclaimerShown,
        uacPromptShown = _app.Cfg.UacPromptShown,
        update = _updateInfo,
        compatibility = _app.Compatibility,
        announcements = _app.Announcements.Visible,
        // 面板关闭时自动领取的宝箱奖励；前端合并到「最近获得」后 ack。
        pendingRecentChestIds = RecentChestStore.Pending(RecentChestScope),
        screens = Screen.AllScreens.Select((screen, index) => new
        {
            deviceName = screen.DeviceName,
            label = PetWindow.ScreenLabel(screen, index),
            primary = screen.Primary,
        }).ToArray(),
        settings = new
        {
            scale = _app.Cfg.Scale,
            opacity = _app.Cfg.Opacity,
            remoteScale = _app.Cfg.RemoteScale,
            remoteFollowMain = _app.Cfg.RemoteFollowMainScale,
            remoteOpacity = _app.Cfg.RemoteOpacity,
            remoteFollowMainOpacity = _app.Cfg.RemoteFollowMainOpacity,
            remoteNameScale = _app.Cfg.RemoteNameScale,
            keyBubbles = _app.Cfg.KeyBubbles,
            animationFps = _app.Cfg.AnimationFps,
            topmost = _app.Cfg.Topmost,
            clickThrough = _app.Cfg.ClickThrough,
            hoverFade = _app.Cfg.HoverFade,
            hoverFadeOpacity = _app.Cfg.HoverFadeOpacity,
            keyboardMode = _app.Cfg.KeyboardMode,
            showMappingControls = _app.Cfg.ShowMappingControls,
            hideOtherPets = _app.Cfg.HideOtherPets,
            hideMainBar = _app.Cfg.HideMainBar,
            hideRemoteBars = _app.Cfg.HideRemoteBars,
            separateBarHotkeys = _app.Cfg.SeparateBarHotkeys,
            rhythmMode = _app.Cfg.RhythmMode,
            rhythmKeyCount = _app.Cfg.RhythmKeyCount,
            rhythmKeys = _app.Cfg.RhythmKeys,
            allowOverlap = _app.Cfg.AllowOverlap,
            catLayout = _app.Cfg.CatLayout,
            petScreen = _app.Cfg.PetScreen,
            screenSwitchMode = _app.Cfg.ScreenSwitchMode,
            themeAccent = _app.Cfg.ThemeAccentColor,
            themeFont = _app.Cfg.ThemeFontColor,
            themeAdvanced = _app.Cfg.ThemeAdvanced,
            // 底栏独立配色：存储值 + 实际生效值，前端颜色选择器直接显示实际颜色。
            themeBarFollowStandard = _app.Cfg.ThemeBarFollowStandard,
            themeBarColor = _app.Cfg.ThemeBarColor,
            themeBarTextFollowTheme = _app.Cfg.ThemeBarTextFollowTheme,
            themeBarTextColor = _app.Cfg.ThemeBarTextColor,
            themeBarEffectiveColor = PetWindow.ThemeColorHex(barTheme.bar),
            themeBarEffectiveTextColor = PetWindow.ThemeColorHex(barTheme.text),
            // 只发文件名解析出的小 URL；1MB+ 的 data URL 不再进入 boot。
            themeBackgroundImage = ThemeBackgroundStore.DisplayUrl(_app.Cfg.ThemeBackgroundImage),
            themeBackgroundImageSet = ThemeBackgroundStore.HasImage(_app.Cfg.ThemeBackgroundImage),
            themeBackgroundOpacity = _app.Cfg.ThemeBackgroundOpacity,
            themeBackgroundBlur = _app.Cfg.ThemeBackgroundBlur,
            synthCheckMode = _app.Cfg.SynthCheckMode,
            anyItemSynthesisUnlocked = _app.Cfg.AnyItemSynthesisUnlocked,
            anyItemSynthesisEnabled = _app.Cfg.AnyItemSynthesisEnabled,
            autoClickBoost = _app.Cfg.AutoClickBoost,
            captureMode = _app.Cfg.CaptureMode,
            captureWidth = _app.Cfg.CaptureWidth,
            captureHeight = _app.Cfg.CaptureHeight,
            captureColor = _app.Cfg.CaptureColor,
            captureTransparent = _app.Cfg.CaptureTransparent,
            captureObsNoticeShown = _app.Cfg.CaptureObsNoticeShown,
            captureCompatibilityMode = _app.Cfg.CaptureCompatibilityMode,
            captureStopRenderingMain = _app.Cfg.CaptureStopRenderingMain,
            alwaysShowChest = _app.Cfg.AlwaysShowChest,
            autoChest = _app.Cfg.AutoChest,
            autoEmoteEnabled = _app.Cfg.AutoEmoteEnabled,
            autoEmoteMaxPerSecond = _app.Cfg.AutoEmoteMaxPerSecond,
            autoEmoteMode = _app.Cfg.AutoEmoteMode,
            autoEmoteId = _app.Cfg.AutoEmoteId,
            autoClickEnabled = _app.Cfg.AutoClickEnabled,
            autoClickPerSecond = _app.Cfg.AutoClickPerSecond,
            autoClickAnimate = _app.Cfg.AutoClickAnimate,
            notifyChestClaimed = _app.Cfg.NotifyChestClaimed,
            notifyHotkeys = _app.Cfg.NotifyHotkeys,
            launchAtLogin = AutoStart.IsEnabled,
            runAsAdmin = _app.Cfg.RunAsAdmin,
            gameModeRules = _app.Cfg.GameModeRules,
            isElevated = Elevation.IsElevated,
            roomNo = _app.Cfg.RoomNo,
            lastRoom = _app.Cfg.LastRoom,
            serverRegion = _app.Cfg.ServerRegion,
            forcedOnlineAcceptedVersion = _app.Cfg.ForcedOnlineAcceptedVersion,
            updateDownloadSource = _app.Cfg.UpdateDownloadSource,
            updateUseProxy = _app.Cfg.UpdateUseProxy,
            updateHttpProxy = _app.Cfg.UpdateHttpProxy,
            hotkeys = Hotkeys.FromSettings(_app.Cfg.Hotkeys),
        },
        appearance = new
        {
            characterAssetId = _app.Cfg.CharacterAssetId,
            characterRoleId = _app.Catalog.Find("character_skin", _app.Cfg.CharacterAssetId)?.GameRoleId ?? 0,
            accessoryAssetId = _app.Cfg.AccessoryAssetId,
            accessoryOffsetX = accTransform.X,
            accessoryOffsetY = accTransform.Y,
            accessoryScalePercent = accTransform.ScalePercent,
            accessoryRotationDegrees = accTransform.RotationDegrees,
            // 预设预览需要任意头饰的本地微调，而不只是当前佩戴的这一件。
            accessoryOffsets = _app.Cfg.AccessoryOffsets,
            emoteAssetIds = _app.Cfg.EmoteAssetIds,
            cloudCharacterAssetId = _app.Cfg.CloudCharacterAssetId,
            cloudAccessoryAssetId = _app.Cfg.CloudAccessoryAssetId,
            cloudEmoteAssetIds = _app.Cfg.CloudEmoteAssetIds,
        },
        catalog = new
        {
            assetsHost = _app.Catalog.AssetsHost,
            skins = _app.Catalog.Count("character_skin"),
            accessories = _app.Catalog.Count("accessory"),
            emotes = _app.Catalog.Count("emote"),
        },
        locks = AssetLockStore.All(),
        // Boot 与 save/rename 共用同一个映射，避免再次出现 PascalCase / camelCase 分叉。
        themePresets = MapThemePresets(_app.ThemePresets.Presets),
        themeRotation = MapThemeRotation(_app.ThemePresets.Rotation),
        panelMaximized = _maximized,
        version = typeof(PanelWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.1",
    };
    }

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

    static bool IsNetworkGuardedCommand(string cmd, JsonObject data)
    {
        return cmd switch
        {
            "synthesize" or "redeem" or "appearance.sync" or "presets.create" or "presets.delete"
                or "favorite" or "emote.send" => true,
            // 只有显式上传云端的表情槽位会改服务器；仅本地槽位在安全模式下仍可用。
            "appearance.setSlot" => ApiClient.ReadBool(data?["upload"]),
            _ => false,
        };
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

    /// <summary>构造只用于桌宠底栏实时预览的临时设置副本，不写入配置。</summary>
    static Settings ThemePreviewSettings(Settings cfg, JsonObject data)
    {
        var preview = new Settings
        {
            ThemeAccentColor = cfg.ThemeAccentColor,
            ThemeFontColor = cfg.ThemeFontColor,
            ThemeAdvanced = cfg.ThemeAdvanced,
            ThemeBarFollowStandard = cfg.ThemeBarFollowStandard,
            ThemeBarColor = cfg.ThemeBarColor,
            ThemeBarTextFollowTheme = cfg.ThemeBarTextFollowTheme,
            ThemeBarTextColor = cfg.ThemeBarTextColor,
        };
        if (data.ContainsKey("themeAccent"))
            preview.ThemeAccentColor = ConfigStore.NormalizeThemeColor(ApiClient.ReadString(data["themeAccent"]), "#E7B77C");
        if (data.ContainsKey("themeFont"))
            preview.ThemeFontColor = ConfigStore.NormalizeThemeColor(ApiClient.ReadString(data["themeFont"]), "#8E5E43");
        if (data.ContainsKey("themeAdvanced"))
            preview.ThemeAdvanced = ApiClient.ReadString(data["themeAdvanced"]) ?? "";
        if (data.ContainsKey("themeBarFollowStandard"))
            preview.ThemeBarFollowStandard = ApiClient.ReadBool(data["themeBarFollowStandard"]);
        if (data.ContainsKey("themeBarColor"))
            preview.ThemeBarColor = ConfigStore.NormalizeOptionalThemeColor(ApiClient.ReadString(data["themeBarColor"]));
        if (data.ContainsKey("themeBarTextFollowTheme"))
            preview.ThemeBarTextFollowTheme = ApiClient.ReadBool(data["themeBarTextFollowTheme"]);
        if (data.ContainsKey("themeBarTextColor"))
            preview.ThemeBarTextColor = ConfigStore.NormalizeOptionalThemeColor(ApiClient.ReadString(data["themeBarTextColor"]));
        return preview;
    }

    async Task<object> DispatchAsync(string cmd, JsonObject data)
    {
        var auth = _app.Auth;
        var cfg = _app.Cfg;
        // 统一兼容性门禁：killSwitch/unsafe 时所有会改动服务器状态的命令直接失败，
        // 不再出现“状态横幅说不安全、操作却照样成功”的不一致。
        if (IsNetworkGuardedCommand(cmd, data))
        {
            string blocked = _app.OnlineBlockedReason;
            if (!string.IsNullOrEmpty(blocked)) throw new ApiException(-1, "联网操作已暂停：" + blocked);
        }
        switch (cmd)
        {
            case "win.drag":
                if (_maximized) return true;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 2, 0);
                return true;
            case "win.min":
                WindowState = FormWindowState.Minimized;
                return true;
            case "win.max":
                ToggleMaximize();
                return new { maximized = _maximized };
            case "win.resizeStart":
            {
                if (_maximized || _resizing) return false;
                string dir = ApiClient.ReadString(data["dir"]);
                if (dir is not ("left" or "right" or "top" or "bottom"
                    or "topleft" or "topright" or "bottomleft" or "bottomright")) return false;
                _resizeDir = dir;
                _resizing = true;
                _resizeStartCursor = System.Windows.Forms.Cursor.Position;
                _resizeStartBounds = Bounds;
                Capture = true;
                Cursor = dir switch
                {
                    "topleft" or "bottomright" => Cursors.SizeNWSE,
                    "topright" or "bottomleft" => Cursors.SizeNESW,
                    "left" or "right" => Cursors.SizeWE,
                    _ => Cursors.SizeNS,
                };
                return true;
            }
            case "win.resizeEnd":
                EndResize();
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

            case "ui.uacPromptResult":
            {
                bool granted = ApiClient.ReadBool(data["granted"]);
                cfg.UacPromptShown = true;
                if (!granted)
                {
                    cfg.RunAsAdmin = false;
                    ConfigStore.Save(cfg);
                    Log.Write("首次启动未授予管理员权限");
                    return new { granted = false, elevated = false, restarting = false, cancelled = false };
                }

                cfg.RunAsAdmin = true;
                ConfigStore.Save(cfg);
                if (Elevation.IsElevated)
                {
                    AutoStart.Apply(cfg.LaunchAtLogin, true);
                    return new { granted = true, elevated = true, restarting = false, cancelled = false };
                }

                // 先释放单实例锁，提权后的新进程才能正常启动并读取上面的配置。
                SingleInstance.ReleaseForRelaunch();
                if (!Elevation.TryRestartElevated(reopenPanel: true, panelPage: "bag"))
                {
                    cfg.RunAsAdmin = false;
                    ConfigStore.Save(cfg);
                    if (SingleInstance.Acquire())
                        SingleInstance.SetActivationHandler(() => _app.RequestPanelFromOtherInstance());
                    Log.Write("首次启动 UAC 被取消");
                    return new { granted = true, elevated = false, restarting = false, cancelled = true };
                }
                _app.ScheduleRestartExit();
                return new { granted = true, elevated = false, restarting = true, cancelled = false };
            }

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

            case "login.phone.sendCode":
                await auth.SendPhoneCodeAsync(ApiClient.ReadString(data["phone"]));
                return new { sent = true };

            case "login.phone.submit":
                await auth.LoginWithPhoneAsync(ApiClient.ReadString(data["phone"]), ApiClient.ReadString(data["code"]));
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
                    return OfficialTokenReader.Extract(Log.Write, preferredRegion: _app.Cfg.ServerRegion);
                });

                var token = extracted.PreferredToken;
                string detectedRegion = string.IsNullOrEmpty(token?.Region) ? extracted.Region : token.Region;

                if (!string.IsNullOrEmpty(detectedRegion) && !string.Equals(_app.Cfg.ServerRegion, detectedRegion, StringComparison.OrdinalIgnoreCase))
                {
                    // 这是登录流程内部的区服校正，不应冒出一个“请重新登录”的切换提示。
                    await _app.SwitchServerRegionAsync(detectedRegion, announce: false);
                }

                bool restored = false;
                if (token != null && !string.IsNullOrWhiteSpace(token.Value))
                {
                    auth.SetTokenManually(token.Value);
                    restored = await auth.TryRestoreAsync();
                }

                // 若 Cookie token 失效/过期，但提取到了海外通行证会话，则自动调用接口重新签发新 Token
                if (!restored && extracted.ForeignSession != null)
                {
                    try
                    {
                        var fs = extracted.ForeignSession;
                        await auth.LoginWithForeignSessionAsync(fs.Cuid, fs.Puid, fs.UserId, fs.UserName, fs.AccessToken);
                        restored = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("使用官方海外通行证刷新登录失败：" + ex.Message);
                    }
                }

                if (restored)
                {
                    _app.Pet.BeginInvoke(new Action(() => _app.Pet.ApplySettings()));
                    await _app.Room.EnsureLobbyAsync();
                    await _app.ResumeLastRoomAsync();
                    _app.CheckAutoChestNow();
                    return new { loggedIn = true, nickname = auth.Nickname };
                }

                if (token == null && extracted.ForeignSession == null)
                {
                    if (extracted.SawV20)
                        throw new ApiException(-1, "官方客户端使用了 v20 App-Bound Encryption，无法直接解密 Cookie。");
                    if (extracted.TokenDecryptFailed)
                        throw new ApiException(-1, "找到登录数据但解密失败；请确认官方客户端与本程序使用同一 Windows 用户，并在客户端重新登录后再试。");
                    throw new ApiException(-1, "官方客户端中没有找到登录凭据，请先在官方客户端完成登录。");
                }

                throw new ApiException(-1, "读取到登录凭据，但服务器验证失败；请在官方客户端内重新登录一次后再试。");
            }

            case "logout":
                await auth.LogoutAsync();
                await _app.Room.LeaveAsync(ownerConfirmed: true);
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

            case "recent.ack":
            {
                if (data["ids"] is JsonArray ids)
                    RecentChestStore.Ack(ids.Select(x => ApiClient.ReadLong(x)), RecentChestScope);
                return true;
            }

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
                if (slot < 0 || slot >= 5) throw new ApiException(-1, "槽位无效");
                if (ApiClient.ReadBool(data["upload"]))
                    return await UploadEmoteSlotAsync(slot, id);
                var slots = cfg.EmoteAssetIds ?? new long[5];
                slots[slot] = id;
                cfg.EmoteAssetIds = slots;
                cfg.LocalEmoteSlots = true;
                ConfigStore.Save(cfg);
                Log.Write($"表情槽位 {slot} = {id}（仅本地）");
                _app.Pet.BeginInvoke(new Action(() => _app.Pet.OnEmoteSlotsChanged()));
                return BootPayload();
            }

            case "appearance.emoteStatus":
            {
                long id = ApiClient.ReadLong(data["id"]);
                await EnsureOwnedAssetsAsync();
                var emote = _app.Catalog.Find("emote", id);
                long currentRole = CurrentCharacterRoleId();
                long emoteRole = emote?.GameRoleId ?? 0;
                bool owned = IsOwned("emote", id);
                bool characterOwned = IsOwned("character_skin", cfg.CharacterAssetId);
                bool roleMatch = emoteRole <= 0 || emoteRole == 2000 || (currentRole > 0 && emoteRole == currentRole);
                string reason = !owned ? "未拥有该表情"
                    : !characterOwned ? "当前角色未拥有"
                    : !roleMatch ? "表情属于其他角色"
                    : "";
                return new
                {
                    owned,
                    characterOwned,
                    currentRole,
                    emoteRole,
                    roleMatch,
                    canUpload = owned && characterOwned && roleMatch,
                    reason,
                };
            }

            case "appearance.sync":
            {
                var (uploadChar, uploadAcc, characterBlocked, accessoryBlocked) = await ResolveCloudAppearanceAsync();
                if (characterBlocked || accessoryBlocked)
                    Log.Write($"云端外观使用安全值 character={uploadChar} accessory={uploadAcc} blocked={characterBlocked}/{accessoryBlocked}");

                // 服务器返回的旧表情可能来自其他账号/旧状态，背包里已经不存在。
                // 直接原样回传会让整次 appearance 更新失败并报 50006，必须先用当前背包校验。
                var serverEmotes = _app.Auth.UserInfo is JsonObject me
                    ? ReadServerEmoteIds(me)
                    : NormalizeEmoteSlots(cfg.CloudEmoteAssetIds).Where(id => id > 0).ToArray();
                var cloudEmotes = SanitizeCloudEmotes(uploadChar, serverEmotes);
                LogCloudAppearanceRepair(serverEmotes, uploadChar, uploadAcc);

                var appliedEmotes = await PutCloudAppearanceAsync(uploadChar, uploadAcc, cloudEmotes, clearEmotesOnReject: true);

                cfg.CloudCharacterAssetId = uploadChar;
                cfg.CloudAccessoryAssetId = uploadAcc;
                cfg.CloudEmoteAssetIds = appliedEmotes;
                // 本地装扮与云端安全值一致时才解除本地覆盖；未拥有的本地装扮继续保留。
                bool localMatches = (cfg.CharacterAssetId == uploadChar || cfg.CharacterAssetId == 0)
                    && cfg.AccessoryAssetId == uploadAcc;
                if (localMatches)
                {
                    cfg.CharacterAssetId = uploadChar;
                    cfg.AccessoryAssetId = uploadAcc;
                    cfg.LocalAppearance = false;
                }
                else
                {
                    cfg.LocalAppearance = true;
                }
                ConfigStore.Save(cfg);
                await auth.WhoAmIAsync();
                await _app.Room.AnnounceAppearanceAsync();
                return new { synced = true, characterBlocked, accessoryBlocked };
            }

            case "room.create":
                await _app.Room.CreateAsync();
                return _app.RoomState();
            case "room.join":
                await _app.Room.JoinAsync(ApiClient.ReadString(data["roomNo"]), ApiClient.ReadBool(data["ownerConfirmed"]));
                return _app.RoomState();
            case "room.leave":
                await _app.Room.LeaveAsync(ownerConfirmed: ApiClient.ReadBool(data["ownerConfirmed"]));
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
                // 按成员 id 持久化；重启/重新进房后由 RoomManager 创建成员时恢复。
                _app.Room.SetMemberVisible(memberId, !hide);
                _app.Pet.BeginInvoke(new Action(() => _app.Pet.RefreshMembers()));
                return _app.RoomState();
            }

            case "room.resetOrder":
                _app.Pet.BeginInvoke(new Action(_app.Pet.ResetCatOrder));
                return new { ok = true };

            case "emote.send":
            {
                long emoteId = ApiClient.ReadLong(data["id"]);
                // 走桌宠本地表情入口：同时显示本地表情气泡并发送到房间。
                _app.Pet.BeginInvoke(new Action(() => _app.Pet.SendEmoteById(emoteId)));
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
                // 设置页只提交当前模式可见的行；先与已有绑定合并，
                // 保证切换单/双底栏快捷键模式时另一模式的绑定不会被空值覆盖。
                var merged = Hotkeys.Merge(_app.Cfg.Hotkeys, next);
                var normalized = Hotkeys.FromSettings(merged);
                // 冲突检查只看当前实际启用的动作，隐藏模式的重复绑定不报错。
                string conflict = Hotkeys.FindConflict(normalized, _app.Cfg.SeparateBarHotkeys);
                if (conflict != null) return new { ok = false, error = conflict };
                _app.Cfg.Hotkeys = normalized;
                ConfigStore.Save(_app.Cfg);
                Log.Write("快捷键已保存 " + string.Join(" ", normalized.Select(kv => kv.Key + "=" + kv.Value)));
                _app.Pet.BeginInvoke(new Action(_app.Pet.RebindHotkeys));
                return new { ok = true, hotkeys = normalized };
            }

            case "locks.toggle":
            {
                string type = ApiClient.ReadString(data["type"]);
                long id = ApiClient.ReadLong(data["id"]);
                bool locked = AssetLockStore.Toggle(type, id);
                return new { locked, items = AssetLockStore.All() };
            }

            case "theme.preview":
            {
                var preview = ThemePreviewSettings(cfg, data);
                _app.Pet.BeginInvoke(new Action(() =>
                {
                    _app.Pet.PreviewTheme(preview);
                    _app.Keyboard.PreviewTheme(preview);
                }));
                return true;
            }

            case "theme.preview.clear":
                _app.Pet.BeginInvoke(new Action(() =>
                {
                    _app.Pet.ClearThemePreview();
                    _app.Keyboard.ClearThemePreview();
                }));
                return true;

            case "theme.image.begin":
                return new { uploadId = ThemeBackgroundStore.BeginUpload(ApiClient.ReadString(data["mime"])) };

            case "theme.image.chunk":
                return new
                {
                    received = ThemeBackgroundStore.AppendUpload(
                        ApiClient.ReadString(data["uploadId"]),
                        ApiClient.ReadString(data["data"])),
                };

            case "theme.image.commit":
            {
                string oldName = cfg.ThemeBackgroundImage;
                string name = ThemeBackgroundStore.CommitUpload(ApiClient.ReadString(data["uploadId"]));
                cfg.ThemeBackgroundImage = name;
                ConfigStore.Save(cfg);
                _app.ThemePresets.ClearCurrentPreset();
                _app.ThemePresets.CleanupImage(oldName);
                return new { name, url = ThemeBackgroundStore.DisplayUrl(name) };
            }

            case "theme.image.cancel":
                ThemeBackgroundStore.CancelUpload(ApiClient.ReadString(data["uploadId"]));
                return new { ok = true };

            case "theme.image.clear":
            {
                string oldName = cfg.ThemeBackgroundImage;
                cfg.ThemeBackgroundImage = "";
                ConfigStore.Save(cfg);
                _app.ThemePresets.ClearCurrentPreset();
                _app.ThemePresets.CleanupImage(oldName);
                _app.ThemePresets.CleanupOrphans();
                return new { ok = true };
            }

            case "theme.image.get":
            {
                // 兜底：如果启动迁移没有跑到（例如运行中手动替换了 config），这里再尝试一次。
                if (ThemeBackgroundStore.MigrateLegacy(cfg)) ConfigStore.Save(cfg);
                string url = ThemeBackgroundStore.DisplayUrl(cfg.ThemeBackgroundImage);
                return new { url, isSet = url.Length > 0 };
            }

            case "theme.presets.save":
            {
                var preset = _app.ThemePresets.SaveCurrent(
                    ApiClient.ReadString(data["name"]),
                    ApiClient.ReadString(data["id"]));
                return new { preset = MapThemePreset(preset) };
            }

            case "theme.presets.apply":
            {
                _app.ThemePresets.Apply(ApiClient.ReadString(data["id"]));
                return new { ok = true };
            }

            case "theme.presets.delete":
            {
                bool removed = _app.ThemePresets.Delete(ApiClient.ReadString(data["id"]));
                return new { ok = removed };
            }

            case "theme.presets.rename":
            {
                var preset = _app.ThemePresets.Rename(
                    ApiClient.ReadString(data["id"]),
                    ApiClient.ReadString(data["name"]));
                return new { preset = MapThemePreset(preset) };
            }

            case "theme.presets.move":
            {
                string id = ApiClient.ReadString(data["id"]);
                int direction = (int)ApiClient.ReadLong(data["direction"]);
                bool moved = _app.ThemePresets.Move(id, direction);
                return new { ok = moved, id, direction };
            }

            case "theme.rotation.set":
            {
                var rotation = _app.ThemePresets.SetRotation(
                    ApiClient.ReadBool(data["enabled"]),
                    (int)ApiClient.ReadLong(data["intervalMinutes"]));
                return new { rotation = new { enabled = rotation.Enabled, intervalMinutes = rotation.IntervalMinutes } };
            }

            case "settings.set":
            case "settings.update":
            {
                bool hideOthersChanged = false;
                bool barHotkeyModeChanged = false;
                bool themeChanged = false;
                foreach (var kv in data)
                {
                    switch (kv.Key)
                    {
                        case "scale": cfg.Scale = ReadDouble(kv.Value); break;
                        case "opacity": cfg.Opacity = ReadDouble(kv.Value); break;
                        case "topmost": cfg.Topmost = ApiClient.ReadBool(kv.Value); break;
                        case "clickThrough": cfg.ClickThrough = ApiClient.ReadBool(kv.Value); break;
                        case "hoverFade": cfg.HoverFade = ApiClient.ReadBool(kv.Value); break;
                        case "hoverFadeOpacity": cfg.HoverFadeOpacity = Math.Clamp(ReadDouble(kv.Value), 0.1, 1.0); break;
                        case "keyboardMode":
                        {
                            string mode = ApiClient.ReadString(kv.Value);
                            if (mode == "rhythm" && !cfg.RhythmMode) mode = "wuwa";
                            cfg.KeyboardMode = mode;
                            break;
                        }
                        case "showMappingControls": cfg.ShowMappingControls = ApiClient.ReadBool(kv.Value); break;
                        case "rhythmMode":
                        {
                            bool on = ApiClient.ReadBool(kv.Value);
                            cfg.RhythmMode = on;
                            if (on) cfg.KeyboardMode = "rhythm";
                            else if (cfg.KeyboardMode == "rhythm") cfg.KeyboardMode = "wuwa";
                            break;
                        }
                        case "rhythmKeyCount":
                        {
                            int nextCount = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 4, 6);
                            if (nextCount != cfg.RhythmKeyCount)
                            {
                                // 切换键数前先把当前模式的键位存回它自己的配置。
                                cfg.SetRhythmKeys(cfg.RhythmKeyCount, cfg.RhythmKeys);
                                cfg.RhythmKeyCount = nextCount;
                                cfg.RhythmKeys = cfg.GetRhythmKeys(nextCount);
                            }
                            break;
                        }
                        case "rhythmKeys":
                        {
                            var arr = kv.Value as JsonArray;
                            cfg.RhythmKeys = RhythmKeys.Normalize(arr?.Select(x => ApiClient.ReadString(x)), cfg.RhythmKeyCount);
                            cfg.SetRhythmKeys(cfg.RhythmKeyCount, cfg.RhythmKeys);
                            break;
                        }
                        case "hideOtherPets":
                            cfg.HideOtherPets = ApiClient.ReadBool(kv.Value);
                            hideOthersChanged = true;
                            break;
                        case "hideMainBar": cfg.HideMainBar = ApiClient.ReadBool(kv.Value); break;
                        case "hideRemoteBars": cfg.HideRemoteBars = ApiClient.ReadBool(kv.Value); break;
                        case "separateBarHotkeys":
                            cfg.SeparateBarHotkeys = ApiClient.ReadBool(kv.Value);
                            barHotkeyModeChanged = true;
                            break;
                        case "allowOverlap": cfg.AllowOverlap = ApiClient.ReadBool(kv.Value); break;
                        case "remoteFollowMain": cfg.RemoteFollowMainScale = ApiClient.ReadBool(kv.Value); break;
                        case "remoteScale":
                            cfg.RemoteScale = Math.Clamp(ReadDouble(kv.Value), 0.25, 2.0);
                            // 在联机面板/远程菜单主动调整大小时，退出“跟随主角”。
                            cfg.RemoteFollowMainScale = false;
                            break;
                        case "remoteFollowMainOpacity": cfg.RemoteFollowMainOpacity = ApiClient.ReadBool(kv.Value); break;
                        case "remoteOpacity":
                            cfg.RemoteOpacity = Math.Clamp(ReadDouble(kv.Value), 0.2, 1.0);
                            // 在联机面板/远程菜单主动调整透明度时，退出“跟随主角”。
                            cfg.RemoteFollowMainOpacity = false;
                            break;
                        case "remoteNameScale": cfg.RemoteNameScale = Math.Clamp(ReadDouble(kv.Value), 0.5, 2.0); break;
                        case "keyBubbles": cfg.KeyBubbles = ApiClient.ReadBool(kv.Value); break;
                        case "animationFps":
                            cfg.AnimationFps = Settings.NormalizeAnimationFps((int)Math.Clamp(ApiClient.ReadLong(kv.Value), 0, 1000));
                            AnimationTimer.FrameRate = cfg.AnimationFps;
                            break;
                        case "catLayout":
                            cfg.CatLayout = string.Equals(ApiClient.ReadString(kv.Value), "vertical", StringComparison.OrdinalIgnoreCase)
                                ? "vertical" : "horizontal";
                            break;
                        case "petScreen":
                            cfg.PetScreen = ApiClient.ReadString(kv.Value);
                            break;
                        case "screenSwitchMode":
                            cfg.ScreenSwitchMode = ApiClient.ReadString(kv.Value) switch
                            {
                                "main" => "main",
                                "all" => "all",
                                _ => "default",
                            };
                            break;
                        case "themeAccent":
                            cfg.ThemeAccentColor = ConfigStore.NormalizeThemeColor(ApiClient.ReadString(kv.Value), "#E7B77C");
                            break;
                        case "themeFont":
                            cfg.ThemeFontColor = ConfigStore.NormalizeThemeColor(ApiClient.ReadString(kv.Value), "#8E5E43");
                            break;
                        case "themeAdvanced":
                            cfg.ThemeAdvanced = ApiClient.ReadString(kv.Value) ?? "";
                            break;
                        case "themeBarFollowStandard":
                            cfg.ThemeBarFollowStandard = ApiClient.ReadBool(kv.Value);
                            break;
                        case "themeBarColor":
                            cfg.ThemeBarColor = ConfigStore.NormalizeOptionalThemeColor(ApiClient.ReadString(kv.Value));
                            break;
                        case "themeBarTextFollowTheme":
                            cfg.ThemeBarTextFollowTheme = ApiClient.ReadBool(kv.Value);
                            break;
                        case "themeBarTextColor":
                            cfg.ThemeBarTextColor = ConfigStore.NormalizeOptionalThemeColor(ApiClient.ReadString(kv.Value));
                            break;
                        case "themeBackgroundImage":
                        {
                            // 兼容旧前端/旧命令：能走到这里的 data URL 通常小于 WebView2 消息上限。
                            string value = ApiClient.ReadString(kv.Value) ?? "";
                            string oldName = cfg.ThemeBackgroundImage;
                            if (value.Length == 0)
                            {
                                cfg.ThemeBackgroundImage = "";
                            }
                            else if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                            {
                                cfg.ThemeBackgroundImage = ThemeBackgroundStore.SaveDataUrl(value);
                            }
                            else
                            {
                                cfg.ThemeBackgroundImage = Path.GetFileName(value);
                            }
                            if (!string.Equals(oldName, cfg.ThemeBackgroundImage, StringComparison.OrdinalIgnoreCase))
                                _app.ThemePresets.CleanupImage(oldName);
                            break;
                        }
                        case "themeBackgroundOpacity":
                            cfg.ThemeBackgroundOpacity = Math.Clamp(ApiClient.ReadLong(kv.Value) / 100.0, 0, 1);
                            break;
                        case "themeBackgroundBlur":
                            cfg.ThemeBackgroundBlur = Math.Clamp(ApiClient.ReadLong(kv.Value), 0, 24);
                            break;
                        case "synthCheckMode":
                        {
                            string mode = ApiClient.ReadString(kv.Value);
                            cfg.SynthCheckMode = mode == "same" || mode == "off" ? mode : "up";
                            break;
                        }
                        case "anyItemSynthesisUnlocked": cfg.AnyItemSynthesisUnlocked = ApiClient.ReadBool(kv.Value); break;
                        case "anyItemSynthesisEnabled": cfg.AnyItemSynthesisEnabled = ApiClient.ReadBool(kv.Value); break;
                        case "autoClickBoost": cfg.AutoClickBoost = ApiClient.ReadBool(kv.Value); break;
                        case "captureMode": cfg.CaptureMode = ApiClient.ReadBool(kv.Value); break;
                        case "captureWidth": cfg.CaptureWidth = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 160, 1600); break;
                        case "captureHeight": cfg.CaptureHeight = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 160, 1600); break;
                        case "captureColor": cfg.CaptureColor = ConfigStore.NormalizeCaptureColor(ApiClient.ReadString(kv.Value)); break;
                        case "captureTransparent": cfg.CaptureTransparent = ApiClient.ReadBool(kv.Value); break;
                        case "captureCompatibilityMode": cfg.CaptureCompatibilityMode = ApiClient.ReadBool(kv.Value); break;
                        case "captureStopRenderingMain": cfg.CaptureStopRenderingMain = ApiClient.ReadBool(kv.Value); break;
                        case "alwaysShowChest": cfg.AlwaysShowChest = ApiClient.ReadBool(kv.Value); break;
                        case "autoChest":
                            cfg.AutoChest = ApiClient.ReadBool(kv.Value);
                            if (cfg.AutoChest) _app.CheckAutoChestNow();
                            break;
                        case "autoEmoteEnabled": cfg.AutoEmoteEnabled = ApiClient.ReadBool(kv.Value); break;
                        case "autoEmoteMaxPerSecond": cfg.AutoEmoteMaxPerSecond = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 1, 20); break;
                        case "autoEmoteMode":
                        {
                            string mode = ApiClient.ReadString(kv.Value);
                            if (mode != "specific" && mode != "library") mode = "quickbar";
                            cfg.AutoEmoteMode = mode;
                            break;
                        }
                        case "autoEmoteId": cfg.AutoEmoteId = ApiClient.ReadLong(kv.Value); break;
                        case "autoClickEnabled": cfg.AutoClickEnabled = ApiClient.ReadBool(kv.Value); break;
                        case "autoClickPerSecond": cfg.AutoClickPerSecond = Math.Clamp((int)ApiClient.ReadLong(kv.Value), 1, 10); break;
                        case "autoClickAnimate": cfg.AutoClickAnimate = ApiClient.ReadBool(kv.Value); break;
                        case "notifyChestClaimed": cfg.NotifyChestClaimed = ApiClient.ReadBool(kv.Value); break;
                        case "notifyHotkeys": cfg.NotifyHotkeys = ApiClient.ReadBool(kv.Value); break;
                        case "gameModeRules":
                        {
                            var arr = kv.Value as JsonArray;
                            cfg.GameModeRules = GameModeRules.Normalize(arr?.Select(x => ApiClient.ReadString(x)));
                            break;
                        }
                        case "launchAtLogin":
                            cfg.LaunchAtLogin = AutoStart.Apply(ApiClient.ReadBool(kv.Value), cfg.RunAsAdmin);
                            break;
                        case "updateDownloadSource":
                        {
                            string source = ApiClient.ReadString(kv.Value).Trim().ToLowerInvariant();
                            cfg.UpdateDownloadSource = source is "github" or "ghproxy" ? source : "auto";
                            break;
                        }
                        case "updateUseProxy":
                            cfg.UpdateUseProxy = ApiClient.ReadBool(kv.Value);
                            break;
                        case "updateHttpProxy":
                            cfg.UpdateHttpProxy = ApiClient.ReadString(kv.Value).Trim();
                            break;
                        case "serverRegion":
                        {
                            string reg = ApiClient.ReadString(kv.Value).Trim().ToLowerInvariant();
                            if (reg != "cn" && reg != "global") reg = "cn";
                            if (cfg.ServerRegion != reg)
                            {
                                bool announce = !data.ContainsKey("announce") || ApiClient.ReadBool(data["announce"]);
                                await _app.SwitchServerRegionAsync(reg, announce);
                            }
                            break;
                        }
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
                            ConfigStore.Save(cfg);
                            AutoStart.Apply(cfg.LaunchAtLogin, cfg.RunAsAdmin);
                            break;
                        }
                    }
                }
                themeChanged = data.ContainsKey("themeAccent") || data.ContainsKey("themeFont")
                    || data.ContainsKey("themeAdvanced") || data.ContainsKey("themeBarFollowStandard")
                    || data.ContainsKey("themeBarColor") || data.ContainsKey("themeBarTextFollowTheme")
                    || data.ContainsKey("themeBarTextColor") || data.ContainsKey("themeBackgroundImage")
                    || data.ContainsKey("themeBackgroundOpacity") || data.ContainsKey("themeBackgroundBlur");
                // 手动改主题后清掉“当前预设”标记，避免 UI 仍显示旧预设为当前。
                if (themeChanged) _app.ThemePresets.ClearCurrentPreset();
                cfg.Opacity = Math.Clamp(cfg.Opacity, 0.2, 1.0);
                cfg.Scale = Math.Clamp(cfg.Scale, 0.25, 2.0);
                cfg.CaptureWidth = Math.Clamp(cfg.CaptureWidth, 160, 1600);
                cfg.CaptureHeight = Math.Clamp(cfg.CaptureHeight, 160, 1600);
                ConfigStore.Save(cfg);
                _app.ApplyAutoSettings();
                _app.Pet.BeginInvoke(new Action(() =>
                {
                    // 主题相关设置变化时先刷新键盘调色板，隐藏状态下也会保存，下次显示直接生效。
                    if (themeChanged) _app.Keyboard.ApplyTheme(cfg);
                    _app.Pet.ApplySettings();
                    _app.Pet.ApplyCaptureMode();
                    // 单/双底栏快捷键模式变化后立即换用对应的注册集合。
                    if (barHotkeyModeChanged) _app.Pet.RebindHotkeys();
                    if (data.ContainsKey("petScreen")) _app.Pet.MoveToScreen(cfg.PetScreen);
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
            {
                // 官方接口的 data 不保证包含奖励明细；前端会结合兑换前后背包 diff 显示实际到账物品。
                var resp = await auth.Api.PostAsync("api/frontend/redeems/use", new { code = ApiClient.ReadString(data["code"]) });
                return new { ok = true, data = resp };
            }

            case "presets.list":
                return await auth.Api.GetAsync("api/frontend/presets");

            case "presets.create":
            {
                bool includeEmotes = data.ContainsKey("includeEmotes") && ApiClient.ReadBool(data["includeEmotes"]);
                var cloud = await BuildCloudPresetAsync(data, includeEmotes);
                return await auth.Api.PostAsync("api/frontend/presets", new
                {
                    character_asset_id = cloud.character,
                    accessory_asset_id = cloud.accessory,
                    emote_asset_ids = cloud.emotes,
                });
            }

            case "presets.local.list":
                return MapLocalPresets(cfg.LocalPresets);

            case "presets.local.save":
            {
                string id = ApiClient.ReadString(data["id"]).Trim();
                string name = ApiClient.ReadString(data["name"]).Trim();
                if (string.IsNullOrEmpty(name)) name = "本地预设";
                long character = ApiClient.ReadLong(data["characterAssetId"]);
                long accessory = ApiClient.ReadLong(data["accessoryAssetId"]);
                var emotes = ReadEmoteSlots(data["emoteAssetIds"]);
                var preset = string.IsNullOrEmpty(id)
                    ? null
                    : cfg.LocalPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (preset == null)
                {
                    preset = new LocalPreset { Id = Guid.NewGuid().ToString("N") };
                    cfg.LocalPresets.Add(preset);
                }
                preset.Name = name;
                preset.CharacterAssetId = character;
                preset.AccessoryAssetId = accessory;
                preset.EmoteAssetIds = emotes;
                ConfigStore.Save(cfg);
                return MapLocalPresets(cfg.LocalPresets);
            }

            case "presets.local.delete":
            {
                string id = ApiClient.ReadString(data["id"]);
                cfg.LocalPresets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                ConfigStore.Save(cfg);
                return MapLocalPresets(cfg.LocalPresets);
            }

            case "presets.delete":
                return await auth.Api.DeleteAsync("api/frontend/presets", new { preset_id = ApiClient.ReadLong(data["id"]) });

            case "update.check":
            {
                var current = typeof(PanelWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
                var info = await UpdateChecker.CheckAsync(current, true, _app.Cfg.UpdateUseProxy ? _app.Cfg.UpdateHttpProxy : null);
                _updateInfo = info;
                Push("update", info);
                return new { available = info != null, update = info, currentVersion = current.ToString(3) };
            }

            case "update.download":
            {
                if (_updateDownloading) return new { started = true, already = true };
                var info = _updateInfo;
                if (info?.Installer == null)
                    throw new ApiException(-1, "当前发布没有可自动下载的安装包，请手动前往 Release");
                string source = cfg.UpdateDownloadSource;
                string proxy = cfg.UpdateUseProxy ? cfg.UpdateHttpProxy : null;
                string tag = info.Tag;
                var installer = info.Installer;
                _updateDownloading = true;
                _updateDownloadPath = "";
                _updateDownloadTag = tag;
                _updateDownloadCts = new CancellationTokenSource();
                var token = _updateDownloadCts.Token;
                _ = Task.Run(() => DownloadUpdateAsync(installer, tag, source, proxy, token));
                return new { started = true, tag, source };
            }

            case "update.cancel":
                _updateDownloadCts?.Cancel();
                return new { cancelled = true };

            case "update.install":
            {
                if (_updateInstalling)
                    return new { started = true, already = true };
                var info = _updateInfo;
                if (info?.Installer == null || string.IsNullOrEmpty(_updateDownloadPath))
                    throw new ApiException(-1, "请先下载更新安装包");
                if (!string.Equals(_updateDownloadTag, info.Tag, StringComparison.Ordinal))
                    throw new ApiException(-1, "下载的安装包与当前版本不匹配，请重新下载");
                if (!UpdateDownloader.VerifyFile(_updateDownloadPath, info.Installer.Digest, out string actual))
                {
                    try { File.Delete(_updateDownloadPath); } catch { }
                    throw new ApiException(-1, "安装包 SHA-256 校验失败，已删除，请重新下载（实际 " + actual + "）");
                }
                _updateInstalling = true;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _updateDownloadPath,
                        UseShellExecute = true,
                    });
                    Log.Write("已启动更新安装包 " + _updateDownloadPath);
                    return new { started = true, path = _updateDownloadPath };
                }
                catch
                {
                    _updateInstalling = false;
                    throw;
                }
            }

            case "compatibility.acceptForcedOnline":
                await _app.AcceptForcedOnlineAsync(ApiClient.ReadString(data["version"]));
                return BootPayload();

            case "compatibility.clearForcedOnline":
                _app.ClearForcedOnline();
                return BootPayload();

            case "compatibility.check":
                await _app.RefreshCompatibilityAsync(true);
                return _app.Compatibility;

            case "announcements.check":
                await _app.RefreshAnnouncementsAsync(true);
                return _app.Announcements.Visible;

            case "announcement.read":
            {
                var remaining = _app.Announcements.MarkRead(ApiClient.ReadString(data["id"]));
                Push("announcements", remaining);
                return remaining;
            }

            case "appearance.offset":
            {
                // 滑条传绝对 x/y/scale/rotation；方向按钮传相对 dx/dy；reset 一次复位全部参数。
                bool reset = ApiClient.ReadBool(data["reset"]);
                AccessoryTransform p;
                if (reset)
                {
                    p = _app.Pet.SetAccessoryTransform(AccessoryTransform.Default);
                }
                else if (data.ContainsKey("x") || data.ContainsKey("y") || data.ContainsKey("scale") || data.ContainsKey("rotation"))
                {
                    var current = _app.Pet.AccessoryTransformFor(_app.Cfg.AccessoryAssetId);
                    int x = data.ContainsKey("x") ? (int)ApiClient.ReadLong(data["x"]) : current.X;
                    int y = data.ContainsKey("y") ? (int)ApiClient.ReadLong(data["y"]) : current.Y;
                    int scale = data.ContainsKey("scale") ? (int)ApiClient.ReadLong(data["scale"]) : current.ScalePercent;
                    int rotation = data.ContainsKey("rotation") ? (int)ApiClient.ReadLong(data["rotation"]) : current.RotationDegrees;
                    p = _app.Pet.SetAccessoryTransform(x, y, scale, rotation);
                }
                else
                {
                    int dx = (int)ApiClient.ReadLong(data["dx"]);
                    int dy = (int)ApiClient.ReadLong(data["dy"]);
                    p = _app.Pet.NudgeAccessory(dx, dy);
                }
                var boot = BootPayload();
                return new
                {
                    x = p.X,
                    y = p.Y,
                    scale = p.ScalePercent,
                    rotation = p.RotationDegrees,
                    accessoryId = _app.Cfg.AccessoryAssetId,
                    boot,
                };
            }

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
        _ownedAssets.Clear();
        var list = new List<object>();
        if (data is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                long id = ApiClient.ReadLong(o["asset_id"]);
                string type = ApiClient.ReadString(o["asset_type"]);
                string assetName = ApiClient.ReadString(o["asset_name"]);
                if (id > 0 && !string.IsNullOrEmpty(type))
                {
                    if (!_ownedAssets.TryGetValue(type, out var owned))
                        _ownedAssets[type] = owned = new HashSet<long>();
                    owned.Add(id);
                }
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
        _ownedAssetsCuid = auth.Cuid;
        _ownedAssetsAt = DateTimeOffset.UtcNow;
        return new { items = list, count = list.Count };
    }

    async Task EnsureOwnedAssetsAsync()
    {
        var auth = _app.Auth;
        if (!auth.IsLoggedIn) throw new ApiException(-1, "请先登录");
        string cuid = auth.Cuid ?? "";
        if (string.Equals(_ownedAssetsCuid, cuid, StringComparison.Ordinal) &&
            _ownedAssets.Count > 0 &&
            DateTimeOffset.UtcNow - _ownedAssetsAt < TimeSpan.FromMinutes(2))
            return;
        await LoadBagAsync();
    }

    bool IsOwned(string type, long id)
    {
        if (id <= 0) return true;
        return _ownedAssets.TryGetValue(type, out var owned) && owned.Contains(id);
    }

    long CurrentCharacterRoleId()
        => _app.Catalog.Find("character_skin", _app.Cfg.CharacterAssetId)?.GameRoleId ?? 0;

    bool EmoteMatchesCharacter(long characterId, long emoteId)
    {
        var character = _app.Catalog.Find("character_skin", characterId);
        var emote = _app.Catalog.Find("emote", emoteId);
        long characterRole = character?.GameRoleId ?? 0;
        long emoteRole = emote?.GameRoleId ?? 0;
        return emoteRole <= 0 || emoteRole == 2000 || (characterRole > 0 && characterRole == emoteRole);
    }

    async Task<object> UploadEmoteSlotAsync(int slot, long id)
    {
        await EnsureOwnedAssetsAsync();
        var cfg = _app.Cfg;
        if (!IsOwned("emote", id))
            throw new ApiException(-1, "未拥有该表情，不能上传云端");
        if (!IsOwned("character_skin", cfg.CharacterAssetId))
            throw new ApiException(-1, "当前角色未拥有，不能上传云端表情");
        if (!EmoteMatchesCharacter(cfg.CharacterAssetId, id))
            throw new ApiException(-1, "该表情属于其他角色，不能上传云端");

        var cloudSlots = NormalizeEmoteSlots(cfg.CloudEmoteAssetIds);
        cloudSlots[slot] = id;
        var (uploadChar, uploadAcc, _, _) = await ResolveCloudAppearanceAsync();
        // 清掉旧账号/旧状态遗留的无效表情，否则服务器会以 50006 拒绝整个请求。
        var uploadEmotes = SanitizeCloudEmotes(uploadChar, cloudSlots.Where(x => x > 0));
        var appliedEmotes = await PutCloudAppearanceAsync(uploadChar, uploadAcc, uploadEmotes, clearEmotesOnReject: false);

        var localSlots = NormalizeEmoteSlots(cfg.EmoteAssetIds);
        localSlots[slot] = id;
        cfg.CloudCharacterAssetId = uploadChar;
        cfg.CloudAccessoryAssetId = uploadAcc;
        cfg.CloudEmoteAssetIds = NormalizeEmoteSlots(appliedEmotes);
        cfg.EmoteAssetIds = localSlots;
        cfg.LocalEmoteSlots = true;
        ConfigStore.Save(cfg);
        try { await _app.Auth.WhoAmIAsync(); } catch (Exception ex) { Log.Write("上传表情后刷新账号失败 " + ex.Message); }
        await _app.Room.AnnounceAppearanceAsync();
        _app.Pet.BeginInvoke(new Action(() => _app.Pet.OnEmoteSlotsChanged()));
        Log.Write($"表情槽位 {slot} = {id}（已上传云端）");
        return BootPayload();
    }

    async Task<(long character, long accessory, long[] emotes)> BuildCloudPresetAsync(JsonObject data, bool includeEmotes)
    {
        await EnsureOwnedAssetsAsync();
        var cfg = _app.Cfg;
        long character = data.ContainsKey("characterAssetId")
            ? ApiClient.ReadLong(data["characterAssetId"])
            : cfg.CharacterAssetId;
        long accessory = data.ContainsKey("accessoryAssetId")
            ? ApiClient.ReadLong(data["accessoryAssetId"])
            : cfg.AccessoryAssetId;

        if (character <= 0 || !IsOwned("character_skin", character))
            throw new ApiException(-1, "预设中的角色未拥有，不能上传云端");
        if (accessory > 0 && !IsOwned("accessory", accessory))
            throw new ApiException(-1, "预设中的配件未拥有，不能上传云端");

        var slots = data.ContainsKey("emoteAssetIds")
            ? ReadEmoteSlots(data["emoteAssetIds"])
            : NormalizeEmoteSlots(cfg.EmoteAssetIds);
        var emotes = includeEmotes ? slots.Where(x => x > 0).ToArray() : Array.Empty<long>();
        foreach (long emoteId in emotes)
        {
            if (!IsOwned("emote", emoteId))
                throw new ApiException(-1, "预设中的表情未拥有，不能上传云端");
            if (!EmoteMatchesCharacter(character, emoteId))
                throw new ApiException(-1, "预设中的表情与角色不匹配，不能上传云端");
        }
        return (character, accessory, emotes);
    }

    static long[] NormalizeEmoteSlots(long[] values)
    {
        var slots = new long[5];
        if (values != null) Array.Copy(values, slots, Math.Min(5, values.Length));
        return slots;
    }

    static long[] ReadEmoteSlots(JsonNode node)
    {
        var slots = new long[5];
        if (node is JsonArray arr)
            for (int i = 0; i < 5 && i < arr.Count; i++) slots[i] = ApiClient.ReadLong(arr[i]);
        return slots;
    }

    static long[] ReadServerEmoteIds(JsonObject me)
    {
        var ids = new List<long>();
        if (me?["emote"] is JsonArray arr)
            foreach (var entry in arr)
            {
                long id = ApiClient.ReadLong(entry?["id"]);
                if (id > 0 && !ids.Contains(id)) ids.Add(id);
            }
        return ids.ToArray();
    }

    /// <summary>
    /// 云端表情必须是当前背包拥有、且与目标角色匹配的资源。
    /// 旧账号/旧状态残留的 ID 不能原样回传，否则服务器会用 50006 拒绝整次外观更新。
    /// </summary>
    long[] SanitizeCloudEmotes(long characterId, IEnumerable<long> source)
    {
        var result = new List<long>();
        foreach (long id in source ?? Enumerable.Empty<long>())
        {
            if (id <= 0 || result.Contains(id)) continue;
            if (!IsOwned("emote", id)) continue;
            if (!EmoteMatchesCharacter(characterId, id)) continue;
            result.Add(id);
            if (result.Count >= 5) break;
        }
        return result.ToArray();
    }

    void LogCloudAppearanceRepair(long[] serverEmotes, long uploadChar, long uploadAcc)
    {
        long serverChar = ApiClient.ReadLong(_app.Auth.UserInfo?["character"]?["id"]);
        long serverAcc = ApiClient.ReadLong(_app.Auth.UserInfo?["accessory"]?["id"]);
        var unknown = serverEmotes.Where(id => !IsOwned("emote", id)).ToArray();
        var incompatible = serverEmotes.Where(id => IsOwned("emote", id) && !EmoteMatchesCharacter(uploadChar, id)).ToArray();
        bool invalid = (serverChar > 0 && !IsOwned("character_skin", serverChar))
            || (serverAcc > 0 && !IsOwned("accessory", serverAcc))
            || unknown.Length > 0;
        if (invalid)
            Log.Write($"检测到云端外观异常，自动复位：character={serverChar}->{uploadChar} accessory={serverAcc}->{uploadAcc} 无效表情=[{string.Join(",", unknown)}]");
        else if (incompatible.Length > 0)
            Log.Write($"云端表情与新角色不匹配，自动移除=[{string.Join(",", incompatible)}]");
    }

    async Task<long[]> PutCloudAppearanceAsync(long character, long accessory, long[] emotes, bool clearEmotesOnReject)
    {
        try
        {
            await _app.Auth.Api.PutAsync("api/frontend/user/me/appearance", new
            {
                character_asset_id = character,
                accessory_asset_id = accessory,
                emote_asset_ids = emotes,
            });
            return emotes;
        }
        catch (ApiException ex) when (clearEmotesOnReject && ex.Code == 50006 && emotes.Length > 0)
        {
            // 即使按背包过滤后服务器仍拒绝，说明服务端表情状态本身不可用；
            // 清空云端表情先把外观恢复为可保存状态，本地表情槽位不受影响。
            Log.Write($"云端外观仍被拒绝，自动清空云端表情后重试：emotes=[{string.Join(",", emotes)}]");
            await _app.Auth.Api.PutAsync("api/frontend/user/me/appearance", new
            {
                character_asset_id = character,
                accessory_asset_id = accessory,
                emote_asset_ids = Array.Empty<long>(),
            });
            return Array.Empty<long>();
        }
    }

    static object[] MapLocalPresets(List<LocalPreset> presets)
    {
        return (presets ?? new List<LocalPreset>()).Select(p => new
        {
            id = p.Id,
            name = p.Name,
            characterAssetId = p.CharacterAssetId,
            accessoryAssetId = p.AccessoryAssetId,
            emoteAssetIds = NormalizeEmoteSlots(p.EmoteAssetIds),
        }).ToArray();
    }

    /// <summary>主题预设的前端 DTO。所有字段必须保持 camelCase，Boot 与命令返回共用。</summary>
    internal static object[] MapThemePresets(IEnumerable<ThemePreset> presets)
    {
        return (presets ?? Array.Empty<ThemePreset>())
            .Where(p => p != null)
            .Select(MapThemePreset)
            .ToArray();
    }

    internal static object MapThemePreset(ThemePreset preset) => new
    {
        id = preset.Id,
        name = preset.Name,
        accentColor = preset.AccentColor,
        fontColor = preset.FontColor,
        advanced = preset.Advanced,
        barFollowStandard = preset.BarFollowStandard,
        barColor = preset.BarColor,
        barTextFollowTheme = preset.BarTextFollowTheme,
        barTextColor = preset.BarTextColor,
        backgroundImage = ThemeBackgroundStore.DisplayUrl(preset.BackgroundImage),
        backgroundImageSet = ThemeBackgroundStore.HasImage(preset.BackgroundImage),
        backgroundOpacity = preset.BackgroundOpacity,
        backgroundBlur = preset.BackgroundBlur,
        createdAtMs = preset.CreatedAtMs,
    };

    internal static object MapThemeRotation(ThemeRotationState rotation) => new
    {
        enabled = rotation != null && rotation.Enabled,
        intervalMinutes = rotation?.IntervalMinutes ?? 30,
        currentPresetId = rotation?.CurrentPresetId ?? "",
        lastAppliedMs = rotation?.LastAppliedUtcMs ?? 0,
    };

    async Task<(long character, long accessory, bool characterBlocked, bool accessoryBlocked)> ResolveCloudAppearanceAsync()
    {
        var cfg = _app.Cfg;
        if (cfg.CloudCharacterAssetId <= 0)
        {
            try { await _app.Auth.WhoAmIAsync(); }
            catch (Exception ex) { Log.Write("同步前读取云端外观失败 " + ex.Message); }
        }
        await EnsureOwnedAssetsAsync();

        long localChar = cfg.CharacterAssetId;
        long localAcc = cfg.AccessoryAssetId;
        bool characterBlocked = localChar > 0 && !IsOwned("character_skin", localChar);
        bool accessoryBlocked = localAcc > 0 && !IsOwned("accessory", localAcc);
        long uploadChar = characterBlocked ? cfg.CloudCharacterAssetId : localChar;
        long uploadAcc = accessoryBlocked ? cfg.CloudAccessoryAssetId : localAcc;
        if (uploadChar <= 0) uploadChar = cfg.CloudCharacterAssetId;
        if (uploadChar <= 0)
            throw new ApiException(-1, "当前角色未拥有，无法同步云端；请先切换为已拥有角色");
        return (uploadChar, uploadAcc, characterBlocked, accessoryBlocked);
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

        var items = catalog.All(type).Select(e =>
        {
            string assetName = catalog.AssetNameById.TryGetValue(e.Id, out var an) ? an : "";
            string name = catalog.Names.TryGetValue(e.Id, out var n) && !string.IsNullOrEmpty(n)
                ? n
                : catalog.DisplayName(type, assetName);
            if (string.IsNullOrEmpty(name)) name = e.Id.ToString();
            string series = catalog.Series.TryGetValue(e.Id, out var s) && !string.IsNullOrEmpty(s)
                ? s
                : catalog.SeriesName(type, assetName);
            return new
            {
                id = e.Id,
                type = e.Type,
                assetName,
                name,
                series,
                rarity = catalog.Rarities.TryGetValue(e.Id, out var r) ? r : "common",
                thumb = atlasByMain.TryGetValue(e.Id, out var t) ? t : catalog.CdnUrl(e.Url),
                // 预设预览需要和桌宠一样使用完整 200×200 资源，并带上配件目录偏移。
                previewUrl = catalog.CdnUrl(e.Url),
                offsetX = e.OffsetX,
                offsetY = e.OffsetY,
                gameRoleId = e.GameRoleId,
            };
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
        // Win11 DWM 圆角需要 WS_THICKFRAME；该样式会给窗口加一圈非客户区边框。
        // 返回 0 让客户区继续铺满整窗，边框由网页 CSS 绘制。
        if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) // WM_NCCALCSIZE
        {
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x007E) // WM_DISPLAYCHANGE：分辨率/缩放布局改变后重新夹紧
        {
            try { BeginInvoke(new Action(() => FitToWorkingArea(Screen.FromControl(this), DeviceDpi, center: false))); } catch { }
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_dwmCorners)
            {
                // DWM 只对带 WS_THICKFRAME 的顶层窗口应用 Win11 圆角。
                cp.Style |= 0x00040000; // WS_THICKFRAME
            }
            return cp;
        }
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
        if (!_fitting && !_maximized && !_resizing && WindowState == FormWindowState.Normal)
        {
            _resizeSaveTimer?.Stop();
            _resizeSaveTimer?.Start();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_resizing) return;
        var b = _resizeStartBounds;
        var cur = System.Windows.Forms.Cursor.Position;
        int dx = cur.X - _resizeStartCursor.X;
        int dy = cur.Y - _resizeStartCursor.Y;
        int minW = Math.Max(1, MinimumSize.Width);
        int minH = Math.Max(1, MinimumSize.Height);
        int left = b.Left, top = b.Top, width = b.Width, height = b.Height;

        if (_resizeDir.Contains("left"))
        {
            left = b.Left + dx;
            width = b.Right - left;
            if (width < minW) { left = b.Right - minW; width = minW; }
        }
        else if (_resizeDir.Contains("right"))
        {
            width = b.Width + dx;
            if (width < minW) width = minW;
        }
        if (_resizeDir.Contains("top"))
        {
            top = b.Top + dy;
            height = b.Bottom - top;
            if (height < minH) { top = b.Bottom - minH; height = minH; }
        }
        else if (_resizeDir.Contains("bottom"))
        {
            height = b.Height + dy;
            if (height < minH) height = minH;
        }

        var wa = Screen.FromPoint(cur).WorkingArea;
        if (left < wa.Left) { width -= wa.Left - left; left = wa.Left; }
        if (top < wa.Top) { height -= wa.Top - top; top = wa.Top; }
        if (left + width > wa.Right) width = wa.Right - left;
        if (top + height > wa.Bottom) height = wa.Bottom - top;
        width = Math.Max(width, minW);
        height = Math.Max(height, minH);
        Bounds = new Rectangle(left, top, width, height);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_resizing && e.Button == MouseButtons.Left) EndResize();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_resizing && !Capture) EndResize();
    }

    void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        _resizeDir = "";
        Capture = false;
        Cursor = Cursors.Default;
        _resizeSaveTimer?.Stop();
        SavePanelSize();
    }

    void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        if (_maximized)
        {
            if (_dwmCorners && TrySetDwmCornerPreference(DwmCornerDoNotRound))
            {
                SetWindowRegion(null);
                return;
            }
            SetWindowRegion(new Region(new Rectangle(0, 0, Width, Height)));
            return;
        }
        if (_dwmCorners)
        {
            if (TrySetDwmCornerPreference(DwmCornerRound))
            {
                // Win11：交给 DWM 做抗锯齿圆角，和网页 CSS 的 8px 外圆角一致。
                SetWindowRegion(null);
                return;
            }
            _dwmCorners = false;
            try { _web?.CoreWebView2?.ExecuteScriptAsync("document.documentElement.classList.remove('dwm-rounded')"); } catch { }
            Log.Write("DWM 圆角不可用，回退到 Region 裁剪");
        }
        // Win10 回退：Region 是像素级二值遮罩，只能尽量和 CSS 外圆角对齐。
        float radius = 18f * Math.Max(1, DeviceDpi) / 96f;
        using var path = PetComposer.Rounded(new RectangleF(0, 0, Width, Height), radius);
        SetWindowRegion(new Region(path));
    }

    void SetWindowRegion(Region region)
    {
        var previous = Region;
        Region = region;
        previous?.Dispose();
    }

    const int DwmCornerRound = 2;
    const int DwmCornerDoNotRound = 1;

    bool TrySetDwmCornerPreference(int preference)
    {
        try
        {
            int value = preference;
            int hr = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref value, sizeof(int));
            // 去掉 DWM 默认的 1px 边框；圆角与描边都由网页负责。
            int noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(Handle, DwmWindowBorderColor, ref noBorder, sizeof(int));
            return hr == 0;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    const int DwmWindowCornerPreference = 33;
    const int DwmWindowBorderColor = 34;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
