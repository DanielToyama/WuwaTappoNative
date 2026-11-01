using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 全局键盘/鼠标监听：用 Raw Input（RIDEV_INPUTSINK）被动读取输入，不安装钩子、不拦截按键，
/// 因此不会吞掉任何软件的输入，也不在输入路径上增加延迟。
/// 按下/抬起分别通知，用于“按下一只手落下、抬起手抬起”的双手机制；
/// 全局快捷键也在这条事件流里匹配，效果与直接监听相同但不会独占按键。
/// 面板打开、输入框聚焦时也照常计数和匹配快捷键，监听没有“面板内忽略”的分支。
/// 点击本程序自己的窗口（面板、桌宠、成员桌宠）不重复计数：桌宠点击由窗口自己记账。
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIM_TYPEKEYBOARD = 1;
    const uint RIM_TYPEMOUSE = 0;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    const int VK_LBUTTON = 0x01;
    const int VK_RBUTTON = 0x02;

    readonly object _gate = new();
    Thread _thread;
    volatile bool _running;
    IntPtr _hwnd;
    IntPtr _buffer;
    int _pid = Environment.ProcessId;
    readonly HashSet<int> _pressed = new();
    // 只有真正计入统计的鼠标按下，才在松开时补发抬起事件；
    // 否则“在面板上按下、拖到桌面上松开”会让手一直抬着。
    readonly HashSet<int> _countedMouseButtons = new();

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

            var keyboard = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_INPUTSINK,
                Target = _hwnd,
            };
            var mouse = new RAWINPUTDEVICE
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_MOUSE,
                Flags = RIDEV_INPUTSINK,
                Target = _hwnd,
            };
            if (!RegisterRawInputDevices(new[] { keyboard, mouse }, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
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
        IntPtr payload = IntPtr.Add(_buffer, (int)headerSize);
        if (header.Type == RIM_TYPEKEYBOARD) HandleKeyboard(payload);
        else if (header.Type == RIM_TYPEMOUSE) HandleMouse(payload);
    }

    void HandleKeyboard(IntPtr payload)
    {
        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(payload);
        int vk = kb.VKey;
        if (vk == 0 || vk == 0xFF) return; // 无虚拟键的媒体键等
        bool down = (kb.Flags & 1) == 0;   // RI_KEY_BREAK = 1

        if (down)
        {
            bool first;
            lock (_gate) first = _pressed.Add(vk);
            if (!first) return; // 长按的自动重复

            // 高亮走边沿事件：不等轮询，按下立刻亮。
            Safe(() => KeyEdge?.Invoke(vk, true));

            bool ctrl, alt, shift;
            lock (_gate)
            {
                ctrl = _pressed.Contains(0x11) || _pressed.Contains(0xA2) || _pressed.Contains(0xA3);
                alt = _pressed.Contains(0x12) || _pressed.Contains(0xA4) || _pressed.Contains(0xA5);
                shift = _pressed.Contains(0x10) || _pressed.Contains(0xA0) || _pressed.Contains(0xA1);
            }
            if (!IsModifier(vk)) Safe(() => HotkeyKeyDown?.Invoke(vk, ctrl, alt, shift));

            Safe(() => KeyDown?.Invoke(vk));
        }
        else
        {
            bool was;
            lock (_gate) was = _pressed.Remove(vk);
            if (was) Safe(() => KeyEdge?.Invoke(vk, false));
            if (was) Safe(() => KeyUp?.Invoke(vk));
        }
    }

    void HandleMouse(IntPtr payload)
    {
        var mouse = Marshal.PtrToStructure<RAWMOUSE>(payload);
        ushort buttons = mouse.ButtonFlags;
        if ((buttons & 0x0001) != 0) HandleMouseButton(VK_LBUTTON, true);
        if ((buttons & 0x0002) != 0) HandleMouseButton(VK_LBUTTON, false);
        if ((buttons & 0x0004) != 0) HandleMouseButton(VK_RBUTTON, true);
        if ((buttons & 0x0008) != 0) HandleMouseButton(VK_RBUTTON, false);
    }

    void HandleMouseButton(int vk, bool down)
    {
        if (down)
        {
            // 点本程序自己的界面（设置面板、桌宠本身等）不计入全局统计；
            // 桌宠窗口的点击由 PetWindow 自己记一次，避免重复。
            bool skip = IsCursorOverOwnWindow();
            bool first, counted = false;
            lock (_gate)
            {
                first = _pressed.Add(vk);
                if (first && !skip) counted = _countedMouseButtons.Add(vk);
            }
            if (!first || !counted) return;
            Safe(() => KeyEdge?.Invoke(vk, true));
            Safe(() => KeyDown?.Invoke(vk));
        }
        else
        {
            bool was, counted;
            lock (_gate)
            {
                was = _pressed.Remove(vk);
                counted = _countedMouseButtons.Remove(vk);
            }
            if (!was || !counted) return;
            Safe(() => KeyEdge?.Invoke(vk, false));
            Safe(() => KeyUp?.Invoke(vk));
        }
    }

    static void Safe(Action a)
    {
        try { a(); } catch { }
    }

    bool IsCursorOverOwnWindow()
    {
        try
        {
            if (!GetCursorPos(out var p)) return false;
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            // WebView2 的可见内容属于 msedgewebview2.exe，直接看命中窗口的 PID 会把
            // 整个设置面板误判成外部程序。取顶层祖先后，面板/桌宠都会回到本进程。
            IntPtr root = GetAncestor(h, 2 /*GA_ROOT*/);
            if (root == IntPtr.Zero) root = h;
            GetWindowThreadProcessId(root, out int pid);
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

    // RAWMOUSE 里的按钮标志位于 ULONG 联合体的低 16 位；联合体按 4 字节对齐，
    // 因此 ButtonFlags 的偏移是 4 而不是 2。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
}
