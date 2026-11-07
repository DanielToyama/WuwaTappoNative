using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 活动动画使用的 UI 时钟。高精度等待计时器不改变系统全局计时精度；
/// 后台只投递通知，绘制仍在 UI 线程，最多保留一个待处理通知。
/// </summary>
internal sealed class AnimationTimer : IDisposable
{
    const uint HighResolution = 0x2, TimerAllAccess = 0x1F0003;
    static int s_frameRate = 60, s_intervalMs = 16;
    readonly object _gate = new();
    readonly SynchronizationContext _context;
    readonly TimerWaitHandle _handle;
    RegisteredWaitHandle _registration;
    bool _enabled, _pending, _disposed;
    int _armedMs;
    long _generation;

    static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 动画进度用的单调毫秒时钟。Environment.TickCount64 约 15.6ms 才跳一次，
    /// 与 16ms 绘制节拍错位时会出现重复帧和跳帧。
    /// </summary>
    public static long NowMs() => (long)(Stopwatch.GetTimestamp() * TimestampToMs);

    /// <summary>
    /// 所有动画时钟共用的目标帧率（30/60/120，其他值按 60）。修改后正在运行的时钟在下一拍改用新间隔。
    /// 周期取整为 33 / 16 / 8ms；高精度计时器下实测约 30 / 61 / 120 次每秒，
    /// 回退到普通计时器的早期 Windows 10 上受系统时钟粒度限制，高档位达不到目标。
    /// </summary>
    public static int FrameRate
    {
        get => Volatile.Read(ref s_frameRate);
        set
        {
            int fps = Settings.NormalizeAnimationFps(value);
            Volatile.Write(ref s_intervalMs, IntervalFor(fps));
            Volatile.Write(ref s_frameRate, fps);
        }
    }

    internal static int IntervalFor(int fps) => fps switch { 30 => 33, 120 => 8, _ => 16 };
    internal int ArmedIntervalMs { get { lock (_gate) return _armedMs; } }

    public event EventHandler Tick;
    public bool Enabled { get { lock (_gate) return _enabled; } }

    public AnimationTimer()
    {
        _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var handle = CreateWaitableTimerEx(IntPtr.Zero, null, HighResolution, TimerAllAccess);
        // .NET 8 仍支持不具备高精度标志的早期 Windows 10，保留其运行兼容性。
        if (handle.IsInvalid && Marshal.GetLastWin32Error() == 87)
        {
            handle.Dispose();
            handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TimerAllAccess);
        }
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        _handle = new TimerWaitHandle(handle);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled) return;
            if (!TryArm(Volatile.Read(ref s_intervalMs)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _enabled = true;
            long generation = ++_generation;
            _registration = ThreadPool.RegisterWaitForSingleObject(_handle,
                (_, _) => QueueTick(generation), null, Timeout.Infinite, false);
        }
    }

    void QueueTick(long generation)
    {
        lock (_gate)
        {
            if (!_enabled || generation != _generation) return;
            int interval = Volatile.Read(ref s_intervalMs);
            // 线程池回调里不抛异常；改间隔失败就继续沿用原间隔。
            if (interval != _armedMs) TryArm(interval);
            if (_pending) return;
            _pending = true;
            // 与 Stop/Dispose 串行，避免退出时在 UI 上下文释放后继续投递。
            _context.Post(_ =>
            {
                lock (_gate)
                {
                    if (!_enabled || generation != _generation) return;
                    _pending = false;
                }
                Tick?.Invoke(this, EventArgs.Empty);
            }, null);
        }
    }

    bool TryArm(int interval)
    {
        long due = -interval * 10_000L;
        if (!SetWaitableTimer(_handle.SafeWaitHandle, ref due, interval, IntPtr.Zero, IntPtr.Zero, false)) return false;
        _armedMs = interval;
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _enabled = false;
            _pending = false;
            ++_generation;
            CancelWaitableTimer(_handle.SafeWaitHandle);
            _registration?.Unregister(null);
            _registration = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _handle.Dispose();
        }
    }

    sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr state,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
