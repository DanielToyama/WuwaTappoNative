using System;
using System.IO;
using System.Text;

namespace WuwatappoNative.Core;

public static class Log
{
    static readonly object Gate = new();
    static bool _verbose;

    public static void Init(string[] args = null)
    {
        _verbose = args != null && Array.IndexOf(args, "--verbose") >= 0;
        try { if (File.Exists(Paths.LogFile) && new FileInfo(Paths.LogFile).Length > 2 * 1024 * 1024) File.Delete(Paths.LogFile); } catch { }
        Write("---- start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ----");
    }

    public static void Write(string message)
    {
        if (!_verbose && message.StartsWith("DBG ")) return;
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
        lock (Gate)
        {
            try { File.AppendAllText(Paths.LogFile, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}
