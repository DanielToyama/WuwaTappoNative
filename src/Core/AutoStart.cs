using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WuwatappoNative.Core;

/// <summary>
/// 开机自启：普通权限写 HKCU Run；开启管理员启动时改用当前用户登录触发的最高权限计划任务，
/// 避免 Windows 在登录时弹 UAC 导致自动启动被取消。记录的是 exe 绝对路径，换目录运行会重新同步。
/// </summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WuwatappoNative";
    const string TaskName = "WuwatappoNative";

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

    /// <summary>注册表或计划任务实际是否已启用（读实际状态，不读配置）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                if (!string.IsNullOrEmpty(key?.GetValue(ValueName) as string)) return true;
            }
            catch (Exception ex) { Log.Write("读取开机自启注册表失败 " + ex.Message); }
            return TaskExists();
        }
    }

    /// <summary>把自启同步到指定状态；runAsAdmin=true 时使用最高权限计划任务。</summary>
    public static bool Apply(bool enabled, bool runAsAdmin = false)
    {
        try
        {
            if (!enabled)
            {
                DeleteTask();
                RemoveRunValue();
                return IsEnabled;
            }

            if (runAsAdmin)
            {
                if (CreateHighestTask())
                {
                    RemoveRunValue();
                    return true;
                }
                // 当前进程可能没有提权（例如用户取消了本次 UAC），但之前已注册好的任务仍然可用。
                // 此时不要删除它，否则会把正常的开机自启一起弄丢。
                if (TaskExists())
                {
                    Log.Write("最高权限计划任务已存在，当前进程未提权，保留原任务");
                    RemoveRunValue();
                    return true;
                }
            }

            // 任务不可用（例如当前不是管理员）时退回 HKCU Run；
            // 这种情况下登录时可能还会请求 UAC，至少不会完全失去自动启动。
            Log.Write("最高权限计划任务不可用，回退到 HKCU Run");
            DeleteTask();
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return IsEnabled;
            key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
            return IsEnabled;
        }
        catch (Exception ex)
        {
            Log.Write("设置开机自启失败 " + ex.Message);
            return IsEnabled;
        }
    }

    /// <summary>启动时对齐：配置为开启就补写，关闭就清掉，避免手动删了注册表但配置还是开。</summary>
    public static bool Sync(bool savedEnabled, bool runAsAdmin = false)
        => Apply(savedEnabled, runAsAdmin);

    static bool CreateHighestTask()
    {
        string exe = ExecutablePath;
        string workDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        string userId = "";
        string userName = "";
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            userId = identity.User?.Value ?? "";
            userName = identity.Name ?? "";
        }
        catch (Exception ex) { Log.Write("读取当前用户失败 " + ex.Message); }
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userName)) return false;

        // 用 XML 注册，避免 schtasks 默认把“仅交流电时启动”设为 true。
        // 计划任务以当前交互用户身份运行，并请求最高权限，登录时不会弹 UAC。
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Wuwatappo Native 开机自动启动（最高权限，无 UAC 提示）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{XmlEscape(userName)}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{XmlEscape(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{XmlEscape(exe)}</Command>
      <WorkingDirectory>{XmlEscape(workDir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        string temp = Path.Combine(Path.GetTempPath(), "WuwatappoNative-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(temp, xml, Encoding.Unicode);
            if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{temp}\" /F"))
            {
                Log.Write("已创建最高权限开机计划任务");
                return true;
            }
        }
        catch (Exception ex) { Log.Write("写入计划任务 XML 失败 " + ex.Message); }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        Log.Write("创建最高权限计划任务失败");
        return false;
    }

    static string XmlEscape(string value)
        => System.Security.SecurityElement.Escape(value ?? "") ?? "";

    static void DeleteTask()
    {
        try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); } catch { }
    }

    static bool TaskExists()
    {
        try
        {
            return RunSchtasks($"/Query /TN \"{TaskName}\"");
        }
        catch { return false; }
    }

    static bool RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Write("schtasks 调用失败 " + ex.Message);
            return false;
        }
    }

    static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Write("移除开机自启注册表失败 " + ex.Message); }
    }
}
