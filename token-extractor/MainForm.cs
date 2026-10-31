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
    readonly Button _start;
    readonly Button _copy;
    bool _busy;

    public MainForm()
    {
        Text = "Wuwatappo Token 提取器";
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(620, 480);
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
            RowCount = 8,
            BackColor = Cream,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
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
            MaximumSize = new Size(710, 0),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 12),
            Text =
                "用途：从本机官方 WuwaTappo 客户端提取登录 token，填入桌宠（Wuwatappo Native）的「设置 → 导入 token」。\r\n" +
                "1. 先启动官方客户端并登录一次。\r\n" +
                "2. 本工具会询问是否关闭官方客户端；关闭后才会读取登录数据。\r\n" +
                "3. 点击「开始提取」，成功后点「复制 Token」。\r\n" +
                "4. 回到桌宠面板：设置 → 导入 token → 粘贴 → 导入并登录。",
        };
        root.Controls.Add(steps);

        var section = new Label
        {
            Text = "Token",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(section);

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
        _start = new Button
        {
            Text = "开始提取",
            AutoSize = false,
            Size = new Size(132, 38),
            BackColor = Accent,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 10, 0),
        };
        _start.FlatAppearance.BorderColor = Border;
        _start.Click += OnStartClick;
        _copy = new Button
        {
            Text = "复制 Token",
            Enabled = false,
            AutoSize = false,
            Size = new Size(132, 38),
            BackColor = CreamDark,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 10, 0),
        };
        _copy.FlatAppearance.BorderColor = Border;
        _copy.Click += OnCopyClick;
        _tokenBox.TextChanged += (s, e) => _copy.Enabled = !_busy && !string.IsNullOrWhiteSpace(_tokenBox.Text);
        var close = new Button
        {
            Text = "退出",
            AutoSize = false,
            Size = new Size(90, 38),
            BackColor = CreamDark,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
        };
        close.FlatAppearance.BorderColor = Border;
        close.Click += (s, e) => Close();
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(close);
        root.Controls.Add(buttons);

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(710, 0),
            ForeColor = Muted,
            Margin = new Padding(0, 0, 0, 6),
            Text = "准备就绪。",
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
            MaximumSize = new Size(710, 0),
            ForeColor = Bad,
            Margin = new Padding(0),
            Text = "安全提示：token 等同于账号访问权，请勿截图或发送给他人。临时 cookie 副本会在读取后自动删除。",
        };
        root.Controls.Add(security);

        AppendLog("准备就绪。请确认官方客户端已经登录，然后点击「开始提取」。");
    }

    async void OnStartClick(object? sender, EventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        _tokenBox.Clear();
        _logBox.Clear();
        _status.ForeColor = Muted;
        _status.Text = "正在准备…";

        try
        {
            var clients = OfficialTokenReader.FindClientProcesses();
            try
            {
                if (clients.Count > 0)
                {
                    string ids = string.Join(", ", clients.Select(p => p.Id));
                    var answer = MessageBox.Show(this,
                        "检测到官方 WuwaTappo 客户端正在运行。\r\n\r\n读取登录数据前需要完全退出客户端。" +
                        "是否现在由本工具关闭它？（未保存的游戏内操作可能丢失）\r\n\r\nPID: " + ids,
                        "需要关闭官方客户端",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        AppendLog("已取消。请手动退出官方客户端后再点击「开始提取」。");
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

            _status.Text = "正在读取并解密…";
            Action<string> logProgress = AppendLog;
            var result = await Task.Run(() => OfficialTokenReader.Extract(logProgress));

            if (result.PreferredToken is { } token)
            {
                _tokenBox.Text = token.Value;
                _tokenBox.SelectAll();
                _status.ForeColor = Ok;
                _status.Text = $"提取成功：{token.Name}。请点击「复制 Token」，再到桌宠设置中导入。";
                AppendLog("提取成功。token 已显示在上方；请勿把 token 发给他人。");
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
                Fail("没有找到可用的 token。\r\n\r\n请先在官方 WuwaTappo 客户端完成登录，再关闭客户端并重新提取。");
            }
        }
        catch (TokenExtractionException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail("提取失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void OnCopyClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tokenBox.Text)) return;
        try
        {
            Clipboard.SetText(_tokenBox.Text.Trim());
            _status.ForeColor = Ok;
            _status.Text = "Token 已复制。请到桌宠面板：设置 → 导入 token。";
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
        _status.Text = "提取失败，请查看下方说明。";
        AppendLog("[失败] " + message.Replace("\r\n", "\n").Replace("\n", " "));
        MessageBox.Show(this, message, "提取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _start.Enabled = !busy;
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
            MessageBox.Show(this, "正在提取，请等待操作完成。", "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        base.OnFormClosing(e);
    }
}
