using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WuwatappoNative.Net;

namespace WuwatappoTokenExtractor;

public sealed class MainForm : Form
{
    static readonly Color Cream = Color.FromArgb(251, 237, 213);
    static readonly Color CreamDark = Color.FromArgb(244, 225, 200);
    static readonly Color Ink = Color.FromArgb(110, 69, 45);
    static readonly Color Muted = Color.FromArgb(142, 123, 106);
    static readonly Color Accent = Color.FromArgb(254, 210, 145);
    static readonly Color Border = Color.FromArgb(231, 183, 124);
    static readonly Color Ok = Color.FromArgb(64, 132, 82);
    static readonly Color Bad = Color.FromArgb(170, 65, 54);

    readonly TextBox _tokenBox;
    readonly TextBox _logBox;
    readonly Label _status;
    readonly Label _source;
    readonly Button _global;
    readonly Button _cn;
    readonly Button _native;
    readonly Button _copy;
    bool _busy;

    public MainForm()
    {
        Text = "Wuwatappo Token 提取器";
        ClientSize = new Size(800, 700);
        MinimumSize = new Size(660, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Cream;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;
        MaximizeBox = false;
        // 桌宠/联机成员是置顶窗口；工具窗口置顶，避免被猫盖住。
        TopMost = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 9,
            BackColor = Cream,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Wuwatappo Token 提取器",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(title);

        var steps = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(750, 0),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 12),
            Text =
                "用途：获取登录 token，填入桌宠（Wuwatappo Native）的「设置 → 导入 token」。\r\n" +
                "• 「获取国服 Token」：从本机官方国服客户端读取；若客户端正在运行，会先询问是否关闭。\r\n" +
                "• 「获取国际服 Token」：从本机官方国际服客户端读取；若客户端正在运行，会先询问是否关闭。\r\n" +
                "• 「提取 Native Token」：只读取桌宠已保存的 config.json，不会启动或修改桌宠。\r\n" +
                "• 成功后请先阅读安全警告，关闭弹窗后再点「复制 Token」。",
        };
        root.Controls.Add(steps);

        var tokenHeader = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4),
            BackColor = Cream,
        };
        var section = new Label
        {
            Text = "Token",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 14, 0),
        };
        _source = new Label
        {
            Text = "来源：未选择",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 2, 0, 0),
        };
        tokenHeader.Controls.Add(section);
        tokenHeader.Controls.Add(_source);
        root.Controls.Add(tokenHeader);

        _tokenBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = Color.FromArgb(255, 253, 246),
            ForeColor = Ink,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10),
        };
        root.Controls.Add(_tokenBox);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Cream,
        };
        _global = MakeButton("获取国际服 Token", 150, Accent);
        _global.Click += OnGlobalClick;
        _cn = MakeButton("获取国服 Token", 150, Accent);
        _cn.Click += OnCnClick;
        _native = MakeButton("提取 Native Token", 150, Accent);
        _native.Click += OnNativeClick;
        _copy = MakeButton("复制 Token", 120, CreamDark);
        _copy.Enabled = false;
        _copy.Click += OnCopyClick;
        _tokenBox.TextChanged += (s, e) => _copy.Enabled = !_busy && !string.IsNullOrWhiteSpace(_tokenBox.Text);
        var close = MakeButton("退出", 90, CreamDark);
        close.Click += (s, e) => Close();
        buttons.Controls.Add(_cn);
        buttons.Controls.Add(_global);
        buttons.Controls.Add(_native);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(close);
        root.Controls.Add(buttons);

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(750, 0),
            ForeColor = Muted,
            Margin = new Padding(0, 0, 0, 6),
            Text = "准备就绪。请选择「获取国服 Token」「获取国际服 Token」或「提取 Native Token」。",
        };
        root.Controls.Add(_status);

        var logTitle = new Label
        {
            Text = "运行日志",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(logTitle);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Color.FromArgb(255, 253, 246),
            ForeColor = Ink,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(_logBox);

        var security = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(750, 0),
            ForeColor = Bad,
            Margin = new Padding(0),
            Text = "安全提示：Token 等同于账号访问权。本工具不联网、不上传；日志不会打印 Token；" +
                   "官方客户端的临时 cookie 副本会在读取后立即删除。",
        };
        root.Controls.Add(security);

        AppendLog("准备就绪。国际服入口只读取官方国际服客户端数据；Native 入口只读取本机已保存的 config.json。");
    }

    static Button MakeButton(string text, int width, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(width, 38),
            BackColor = backColor,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 10, 0),
        };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }

    async void OnGlobalClick(object? sender, EventArgs e)
        => await RunOfficialExtractionAsync("global", "官方国际服", "com.kuro.wuwatappo.global");

    async void OnCnClick(object? sender, EventArgs e)
        => await RunOfficialExtractionAsync("cn", "官方国服", "com.kuro.wuwatappo.cn");

    async Task RunOfficialExtractionAsync(string region, string sourceLabel, string dataDir)
    {
        if (_busy) return;
        string regionName = string.Equals(region, "global", StringComparison.OrdinalIgnoreCase) ? "国际服" : "国服";
        BeginExtraction(sourceLabel);
        SetBusy(true);

        try
        {
            _status.Text = $"正在检查{regionName}客户端数据…";
            AppendLog($"正在查找{regionName}客户端数据（{dataDir}）…");

            // 严格只认目标区服目录，避免没有该区服数据时静默读到另一个区服。
            if (OfficialTokenReader.FindDataRoot(region, allowFallback: false) == null)
            {
                Fail(
                    $"未检测到{regionName}客户端数据。\r\n\r\n" +
                    $"请确认已安装并登录过{regionName} WuwaTappo 客户端，且本工具与客户端使用同一个 Windows 用户。\r\n\r\n" +
                    $"{regionName}数据目录：%LOCALAPPDATA%\\{dataDir}\\EBWebView");
                return;
            }
            AppendLog($"已找到{regionName}数据目录。");

            var clients = OfficialTokenReader.FindClientProcesses();
            try
            {
                if (clients.Count > 0)
                {
                    string ids = string.Join(", ", clients.Select(p => p.Id));
                    var answer = MessageBox.Show(this,
                        "检测到官方 WuwaTappo 客户端正在运行。\r\n\r\n读取" + regionName + "登录数据前需要完全退出客户端。" +
                        "是否现在由本工具关闭它？（未保存的游戏内操作可能丢失）\r\n\r\nPID: " + ids,
                        "需要关闭官方客户端",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        AppendLog($"已取消。请手动退出官方客户端后再点击「获取{regionName} Token」。");
                        _status.ForeColor = Muted;
                        _status.Text = "等待官方客户端退出。";
                        return;
                    }

                    Action<string> progress = AppendLog;
                    await Task.Run(() => OfficialTokenReader.CloseClientProcesses(clients, progress));
                    AppendLog("官方客户端已关闭。");
                }
                else
                {
                    AppendLog("官方客户端当前未运行。");
                }
            }
            finally
            {
                foreach (var p in clients) p.Dispose();
            }

            _status.Text = $"正在读取并解密{regionName}登录数据…";
            Action<string> logProgress = AppendLog;
            var result = await Task.Run(() =>
                OfficialTokenReader.Extract(logProgress, preferredRegion: region, requirePreferredRegion: true));

            if (result.PreferredToken is { } token && !string.IsNullOrWhiteSpace(token.Value))
            {
                ShowExtractedToken(token.Value, sourceLabel, token.Name);
                if (!result.TempCleaned)
                    AppendLog("提示：临时目录未能自动删除，可在 %TEMP% 中查找 WuwatappoTokenExtractor-* 手动清理。");
            }
            else if (result.SawV20)
            {
                Fail("检测到新版 App-Bound Encryption（v20）cookie，当前方案无法解密。\r\n\r\n请改用桌宠的 UID + 验证码登录。");
            }
            else if (result.TokenDecryptFailed)
            {
                Fail("找到了 token cookie，但解密失败。\r\n\r\n请确认官方客户端和本工具运行在同一个 Windows 用户下，并在客户端重新登录后再试。");
            }
            else
            {
                Fail($"没有在{regionName}客户端中找到可用的 token。\r\n\r\n请先在{regionName} WuwaTappo 客户端完成登录，再关闭客户端并重新提取。");
            }
        }
        catch (TokenExtractionException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail($"获取{regionName} Token 失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void OnNativeClick(object? sender, EventArgs e)
    {
        if (_busy) return;
        BeginExtraction("Wuwatappo Native");
        SetBusy(true);

        try
        {
            _status.Text = "正在读取 Native 已保存的配置…";
            AppendLog("正在读取 Native 配置（%LOCALAPPDATA%\\WuwatappoNative\\config.json）…");
            var result = await Task.Run(() => NativeTokenReader.Read());

            if (string.IsNullOrWhiteSpace(result.Token))
            {
                Fail("Native 尚未登录或没有保存 Token。");
                return;
            }

            ShowExtractedToken(result.Token, "Wuwatappo Native", "Token");
        }
        catch (NativeTokenException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail("读取 Native Token 失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void BeginExtraction(string source)
    {
        // 切换来源时先清空旧 token/日志/状态，避免把上一个来源的内容误当成新结果。
        _tokenBox.Clear();
        _logBox.Clear();
        _status.ForeColor = Muted;
        _status.Text = "正在准备…";
        _source.ForeColor = Ink;
        _source.Text = "来源：" + source;
    }

    void ShowExtractedToken(string rawToken, string sourceLabel, string tokenName)
    {
        string token = rawToken.Trim();
        if (token.Length == 0)
        {
            Fail("获取失败：token 为空。");
            return;
        }

        _tokenBox.Clear();
        _status.ForeColor = Ok;
        _status.Text = $"获取成功（{sourceLabel}）：{tokenName}。请先阅读弹出的安全警告。";
        AppendLog($"获取成功：来源 {sourceLabel}，字段 {tokenName}。token 内容未写入日志。");

        MessageBox.Show(this,
            "已成功获取 Token。请仔细阅读以下安全提示：\r\n\r\n" + TokenSafety.CredentialWarning,
            "安全警告 - 登录凭据",
            MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

        // 警告关闭后才把 token 放进可复制的文本框，避免用户在阅读警告前误复制。
        _tokenBox.Text = token;
        _tokenBox.SelectAll();
        _status.Text = $"获取成功（{sourceLabel}）：{tokenName}。请确认你信任使用渠道，再点「复制 Token」。";
    }

    void OnCopyClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tokenBox.Text)) return;
        try
        {
            Clipboard.SetText(_tokenBox.Text.Trim());
            _status.ForeColor = Ok;
            _status.Text = "Token 已复制。请粘贴到桌宠的「设置 → 导入 token」，并确认你信任该使用渠道。";
            AppendLog("Token 已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Fail("复制到剪贴板失败：" + ex.Message);
        }
    }

    void Fail(string message)
    {
        _status.ForeColor = Bad;
        _status.Text = "获取失败，请查看下方说明。";
        AppendLog("[失败] " + message.Replace("\r\n", "\n").Replace("\n", " "));
        MessageBox.Show(this, message, "获取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _global.Enabled = !busy;
        _cn.Enabled = !busy;
        _native.Enabled = !busy;
        _copy.Enabled = !busy && !string.IsNullOrWhiteSpace(_tokenBox.Text);
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UseWaitCursor = busy;
    }

    void AppendLog(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), text); } catch { }
            return;
        }
        if (_logBox.TextLength > 0) _logBox.AppendText("\r\n");
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}");
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToScreen();
    }

    void FitToScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int maxW = Math.Max(MinimumSize.Width, wa.Width - 40);
        int maxH = Math.Max(MinimumSize.Height, wa.Height - 40);
        int w = Math.Min(Width, maxW);
        int h = Math.Min(Height, maxH);
        if (Size.Width != w || Size.Height != h) Size = new Size(w, h);
        if (Left < wa.Left || Top < wa.Top || Right > wa.Right || Bottom > wa.Bottom)
            Location = new Point(
                wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "正在读取 Token，请等待操作完成。", "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        base.OnFormClosing(e);
    }
}
