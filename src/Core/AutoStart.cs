using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WuwatappoNative.Core;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run（无需管理员，只影响当前用户）。
/// 记录的是 exe 的绝对路径；换位置运行时会自动更新，卸载后残留条目由系统忽略。
/// </summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WuwatappoNative";

    /// <summary>当前进程的 exe 路径（自包含发布时就是发布目录里的那个）。</summary>
    public static string ExecutablePath
    {
        get
        {
            try
            {
                string path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "WuwatappoNative.exe");
        }
    }

    /// <summary>注册表里实际是否已启用（读实际值，不读配置）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch (Exception ex)
            {
                Log.Write("读取开机自启失败 " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>把注册表同步到指定状态；返回最终是否启用。</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return IsEnabled;
            if (enabled)
                key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);
            return IsEnabled;
        }
        catch (Exception ex)
        {
            Log.Write("设置开机自启失败 " + ex.Message);
            return IsEnabled;
        }
    }

    /// <summary>启动时对齐：配置为开启就补写，关闭就清掉，避免手动删了注册表但配置还是开。</summary>
    public static bool Sync(bool savedEnabled)
    {
        if (savedEnabled) return Apply(true);
        return IsEnabled ? Apply(false) : false;
    }
}
