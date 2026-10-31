using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 全局键盘钩子：只读，不拦截输入。
/// 按下/抬起分别通知，用于“按下一只手落下、抬起手抬起”的双手机制。
/// 自身面板在前台时不发送（避免打字时桌宠乱动、点击数被面板输入污染）。
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYUP = 0x0105;

    // 关键：委托必须存字段，否则 GC 回收后回调跳野指针，进程随机 0xC000041D 崩溃。
    delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
    readonly LowLevelProc _proc;
    readonly HashSet<int> _pressed = new();
    IntPtr _hook;
    Thread _thread;
    volatile bool _running;
    int _pid = Environment.ProcessId;

    public event Action<int> KeyDown;
    public event Action<int> KeyUp;

    public GlobalInputHook()
    {
        _proc = HookProc;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(() =>
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Write("键盘钩子安装失败 " + Marshal.GetLastWin32Error());
                return;
            }
            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "GlobalInputHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)info.vkCode;
                if (!IsOwnProcessForeground() && !App.UiInputFocused)
                {
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    if (down)
                    {
                        if (_pressed.Add(vk)) Safe(() => KeyDown?.Invoke(vk));
                    }
                    else
                    {
                        if (_pressed.Remove(vk)) Safe(() => KeyUp?.Invoke(vk));
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
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
        try { if (_thread != null && _thread.IsAlive) PostThreadMessage(_thread.ManagedThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero); } catch { }
        try { _thread?.Join(500); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string lpModuleName);
}
