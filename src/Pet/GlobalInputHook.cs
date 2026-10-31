using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 全局键盘监听：用 Raw Input（RIDEV_INPUTSINK）被动读取按键，不安装键盘钩子、不拦截按键，
/// 因此不会吞掉任何软件的输入，也不在输入路径上增加延迟。
/// 按下/抬起分别通知，用于“按下一只手落下、抬起手抬起”的双手机制；
/// 全局快捷键也在这条事件流里匹配，效果与直接监听相同但不会独占按键。
/// 自身面板输入框聚焦时不计数、不触发快捷键（避免打字误触和录入快捷键时自触发）。
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIM_TYPEKEYBOARD = 1;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;

    readonly object _gate = new();
    Thread _thread;
    volatile bool _running;
    IntPtr _hwnd;
    IntPtr _buffer;
    int _pid = Environment.ProcessId;
    readonly HashSet<int> _pressed = new();

    public event Action<int> KeyDown;
    public event Action<int> KeyUp;
    /// <summary>非修饰键按下（含当时按住的 Ctrl/Alt/Shift）。快捷键匹配用，不消耗按键。</summary>
    public event Action<int, bool, bool, bool> HotkeyKeyDown;
    /// <summary>任意按键的按下/抬起边沿（不含长按重复）。只用于界面高亮，不拦截、不计数。</summary>
    public event Action<int, bool> KeyEdge;

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlobalInputHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Run()
    {
        var window = new SinkWindow();
        try
        {
            window.Create();
            _hwnd = window.Handle;
            _buffer = Marshal.AllocHGlobal(128);
            window.Wnd = OnWindowMessage;

            var device = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_INPUTSINK,
                Target = _hwnd,
            };
            if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                Log.Write("Raw Input 注册失败 " + Marshal.GetLastWin32Error());

            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex) { Log.Write("键盘监听异常 " + ex.Message); }
        finally
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            try { window.DestroyHandle(); } catch { }
            _hwnd = IntPtr.Zero;
        }
    }

    IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) { HandleRawInput(lParam); return IntPtr.Zero; }
        return IntPtr.Zero;
    }

    void HandleRawInput(IntPtr lParam)
    {
        if (_buffer == IntPtr.Zero) return;
        uint size = 128;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint got = GetRawInputData(lParam, RID_INPUT, _buffer, ref size, headerSize);
        if (got == unchecked((uint)-1) || got < headerSize) return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.Type != RIM_TYPEKEYBOARD) return;

        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(_buffer, (int)headerSize));
        int vk = kb.VKey;
        if (vk == 0 || vk == 0xFF) return; // 无虚拟键的媒体键等
        bool down = (kb.Flags & 1) == 0;   // RI_KEY_BREAK = 1

        bool ownForeground = IsOwnProcessForeground();
        bool blocked = ownForeground || App.UiInputFocused;

        if (down)
        {
            bool first;
            lock (_gate) first = _pressed.Add(vk);
            if (!first) return; // 长按的自动重复

            // 高亮走边沿事件：不等轮询，按下立刻亮。
            Safe(() => KeyEdge?.Invoke(vk, true));

            if (!App.UiInputFocused)
            {
                bool ctrl, alt, shift;
                lock (_gate)
                {
                    ctrl = _pressed.Contains(0x11) || _pressed.Contains(0xA2) || _pressed.Contains(0xA3);
                    alt = _pressed.Contains(0x12) || _pressed.Contains(0xA4) || _pressed.Contains(0xA5);
                    shift = _pressed.Contains(0x10) || _pressed.Contains(0xA0) || _pressed.Contains(0xA1);
                }
                if (!IsModifier(vk)) Safe(() => HotkeyKeyDown?.Invoke(vk, ctrl, alt, shift));
            }

            if (!blocked) Safe(() => KeyDown?.Invoke(vk));
        }
        else
        {
            bool was;
            lock (_gate) was = _pressed.Remove(vk);
            if (was) Safe(() => KeyEdge?.Invoke(vk, false));
            if (was && !blocked) Safe(() => KeyUp?.Invoke(vk));
        }
    }

    static void Safe(Action a)
    {
        try { a(); } catch { }
    }

    bool IsOwnProcessForeground()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out int pid);
            return pid == _pid;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            if (_hwnd != IntPtr.Zero && _thread != null && _thread.IsAlive)
                PostMessage(_hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        try { _thread?.Join(800); } catch { }
    }

    sealed class SinkWindow : NativeWindow
    {
        public Func<IntPtr, int, IntPtr, IntPtr, IntPtr> Wnd;

        public void Create()
        {
            CreateHandle(new CreateParams
            {
                Caption = "WuwatappoInputSink",
                Style = unchecked((int)0x80000000), // WS_POPUP，不显示
                ExStyle = 0x00000080,               // WS_EX_TOOLWINDOW，不进 Alt+Tab
                X = 0, Y = 0, Width = 0, Height = 0,
                Parent = IntPtr.Zero,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0010) // WM_CLOSE：退出消息循环
            {
                PostQuitMessage(0);
                return;
            }
            try { Wnd?.Invoke(m.HWnd, m.Msg, m.WParam, m.LParam); } catch { }
            // WM_INPUT 交给默认过程做内部清理。
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
}
