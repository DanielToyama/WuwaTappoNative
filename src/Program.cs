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
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("UNHANDLED " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Log.Write("THREAD " + e.Exception);

        var app = new App();
        try
        {
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Write("FATAL " + ex);
            throw;
        }
    }
}
