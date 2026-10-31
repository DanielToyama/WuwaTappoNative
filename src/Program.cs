using System;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Paths.EnsureDirs();
        Log.Init(args);
        // 单实例必须在提权/创建窗口之前判断；第二个实例只负责请求已有实例显示面板。
        if (!SingleInstance.Acquire())
        {
            SingleInstance.SignalExisting();
            Log.Write("已有实例运行，已请求显示面板");
            SingleInstance.Dispose();
            return;
        }
        // 管理员模式是显式设置。Raw Input 无法越过 UIPI：当前台程序权限更高时，
        // 普通权限实例收不到按键，只能以管理员权限重新启动自身。
        var startupCfg = ConfigStore.Load();
        if (startupCfg.RunAsAdmin && !Elevation.IsElevated &&
            Array.IndexOf(args, "--no-elevate") < 0)
        {
            SingleInstance.ReleaseForRelaunch();
            if (Elevation.TryRestartElevated()) return;
            // UAC 被取消：重新拿回锁，继续以普通权限运行。
            if (!SingleInstance.Acquire())
            {
                SingleInstance.SignalExisting();
                SingleInstance.Dispose();
                return;
            }
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("UNHANDLED " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Log.Write("THREAD " + e.Exception);

        var app = new App();
        SingleInstance.SetActivationHandler(() => app.RequestPanelFromOtherInstance());
        try
        {
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Write("FATAL " + ex);
            throw;
        }
        finally
        {
            SingleInstance.Dispose();
        }
    }
}
