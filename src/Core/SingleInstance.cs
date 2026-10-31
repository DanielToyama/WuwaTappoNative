using System;
using System.Threading;

namespace WuwatappoNative.Core;

/// <summary>
/// 单实例保护。命名 Mutex 防止重复进程，命名事件用于让第二个实例
/// 请求第一个实例把面板带到前台；两者都放在 Local\ 会话命名空间。
/// </summary>
public static class SingleInstance
{
    const string MutexName = @"Local\WuwatappoNative.SingleInstance.v1";
    const string ActivateEventName = @"Local\WuwatappoNative.Activate.v1";

    static Mutex _mutex;
    static EventWaitHandle _activate;
    static RegisteredWaitHandle _wait;
    static bool _first;
    static Action _handler;

    /// <summary>返回 true 表示当前进程是唯一实例；false 表示已经有一个实例在运行。</summary>
    public static bool Acquire()
    {
        Dispose();
        try
        {
            _mutex = Mutex.OpenExisting(MutexName);
            _first = false;
            return false;
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception ex) { Log.Write("单实例检查失败 " + ex.Message); return true; }

        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _first = createdNew;
            if (!createdNew) return false;
            try
            {
                _activate = new EventWaitHandle(false, EventResetMode.AutoReset,
                    ActivateEventName, out _);
            }
            catch (Exception ex) { Log.Write("单实例激活事件创建失败 " + ex.Message); }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception ex)
        {
            Log.Write("单实例锁创建失败 " + ex.Message);
            return true;
        }
    }

    /// <summary>通知已有实例显示面板；没有可通知的实例时返回 false。</summary>
    public static bool SignalExisting()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
            ev.Set();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("通知已有实例失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>第一个实例收到“再启动一次”的信号时，在 UI 线程打开面板。</summary>
    public static void SetActivationHandler(Action handler)
    {
        _handler = handler;
        if (_activate == null || _wait != null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (state, timedOut) =>
        {
            if (timedOut) return;
            try { _handler?.Invoke(); } catch { }
        }, null, Timeout.Infinite, false);
    }

    /// <summary>提权重启前释放命名对象，否则新的管理员实例会把自己当成第二个实例。</summary>
    public static void ReleaseForRelaunch()
    {
        _wait?.Unregister(null);
        _wait = null;
        _handler = null;
        _activate?.Dispose();
        _activate = null;
        if (_first)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _mutex = null;
        _first = false;
    }

    public static void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _handler = null;
        _activate?.Dispose();
        _activate = null;
        if (_first)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        _mutex = null;
        _first = false;
    }
}
