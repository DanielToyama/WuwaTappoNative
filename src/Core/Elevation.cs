using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace WuwatappoNative.Core;

/// <summary>
/// 管理员权限启动。Raw Input 的 RIDEV_INPUTSINK 仍然受 UIPI 限制：
/// 当前台窗口属于更高完整性级别（例如“以管理员身份运行”的游戏或终端）时，
/// 普通权限进程收不到按键。这是 Windows 的安全边界，没有普通用户态绕法，
/// 只能让本进程和目标程序处于同一权限级别。这里提供显式、可取消的提权启动。
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>以管理员权限重新启动当前程序。返回 true 表示新进程已经启动。</summary>
    public static bool TryRestartElevated(bool reopenPanel = false)
    {
        if (IsElevated) return false;
        try
        {
            string exe = ResolveExecutable();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Log.Write("请求管理员权限失败：找不到可执行文件");
                return false;
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            if (reopenPanel)
            {
                if (!args.Contains("--panel")) args.Add("--panel");
                int page = args.IndexOf("--panel-page");
                if (page >= 0 && page + 1 < args.Count) args[page + 1] = "settings";
                else { args.Add("--panel-page"); args.Add("settings"); }
            }
            if (args.Count > 0) psi.Arguments = string.Join(" ", args.Select(Quote));
            Process.Start(psi);
            Log.Write("已启动管理员权限实例");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Write("用户取消了管理员授权");
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("请求管理员权限失败 " + ex.Message);
            return false;
        }
    }

    static string ResolveExecutable()
    {
        string process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) &&
            !string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            return process;
        string local = Path.Combine(AppContext.BaseDirectory, "WuwatappoNative.exe");
        return File.Exists(local) ? local : process;
    }

    static string Quote(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        // 参数都是本地路径/开关，不含需要复杂反斜杠转义的引号；这里只保证带空格的路径完整传过去。
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
