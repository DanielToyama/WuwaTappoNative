using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WuwatappoTokenExtractor;

public sealed class TokenEntry
{
    public TokenEntry(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

public sealed class ExtractionResult
{
    public List<TokenEntry> Tokens { get; } = new();
    public int CookieCount { get; set; }
    public bool SawV20 { get; set; }
    public bool TokenDecryptFailed { get; set; }
    public bool TempCleaned { get; set; }

    public TokenEntry? PreferredToken =>
        Tokens.FirstOrDefault(t => string.Equals(t.Name, "kuro_infoToken", StringComparison.OrdinalIgnoreCase))
        ?? Tokens.FirstOrDefault();
}

public sealed class TokenExtractionException : Exception
{
    public TokenExtractionException(string message) : base(message) { }
    public TokenExtractionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 只读取本机官方 WuwaTappo（WebView2）数据：关闭客户端 → 复制 Cookies/Local State →
/// DPAPI 解出 AES 密钥 → AES-256-GCM 解密 token。复制件在读取后立即删除。
/// </summary>
public static class TokenExtractor
{
    static readonly string[] ClientProcessNames = { "WuwaTappo" };

    /// <summary>清理以前异常退出留下的 cookie 副本，避免敏感数据长期留在临时目录。</summary>
    public static void CleanupStaleDumps(TimeSpan? olderThan = null)
    {
        var limit = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(1));
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "WuwatappoTokenExtractor-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < limit) Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    public static IReadOnlyList<Process> FindClientProcesses()
    {
        var seen = new HashSet<int>();
        var result = new List<Process>();
        foreach (var name in ClientProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (!seen.Add(p.Id)) { p.Dispose(); continue; }
                result.Add(p);
            }
        }
        return result;
    }

    /// <summary>关闭用户已确认关闭的官方客户端；客户端以管理员权限运行时普通权限无法结束，会返回明确错误。</summary>
    public static void CloseClientProcesses(IReadOnlyList<Process> processes, Action<string>? log = null)
    {
        foreach (var p in processes)
        {
            if (p.HasExited) continue;
            log?.Invoke($"正在关闭官方客户端（PID {p.Id}）…");
            try { p.CloseMainWindow(); } catch { }
            bool exited = false;
            try { exited = p.WaitForExit(4000); } catch { }
            if (!exited)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    throw new TokenExtractionException(
                        "无法关闭官方客户端。请手动完全退出它（包括托盘图标）后重试。" +
                        (ex is Win32Exception w && w.NativeErrorCode == 5
                            ? "如果客户端是以管理员身份运行的，普通权限工具无法结束它。"
                            : ""), ex);
                }
            }
        }

        Thread.Sleep(300); // 等 WebView2 子进程释放文件句柄
        var remaining = FindClientProcesses();
        try
        {
            if (remaining.Count > 0)
                throw new TokenExtractionException(
                    $"官方客户端仍在运行（PID {string.Join(", ", remaining.Select(p => p.Id))}）。请手动退出后重试。");
        }
        finally
        {
            foreach (var p in remaining) p.Dispose();
        }
    }

    /// <summary>
    /// 提取 token。传入 dataRoot 仅用于自动化测试（一个包含 Local State 和 Default\Network\Cookies
    /// 或直接包含 Cookies 的临时目录）；正常使用传 null。
    /// </summary>
    public static ExtractionResult Extract(Action<string>? log = null, string? dataRoot = null)
    {
        if (dataRoot == null)
        {
            var running = FindClientProcesses();
            try
            {
                if (running.Count > 0)
                    throw new TokenExtractionException("请先完全关闭官方客户端，再开始提取。");
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
        }

        string root = dataRoot ?? FindDataRoot()
            ?? throw new TokenExtractionException(
                "没有找到官方客户端数据。请先启动并登录官方 WuwaTappo 客户端一次，然后关闭它再试。");

        string cookies = FindCookiesFile(root)
            ?? throw new TokenExtractionException("找到了客户端数据目录，但缺少 Cookies 数据库。请登录客户端一次后重试。");
        string localState = Path.Combine(root, "Local State");
        if (!File.Exists(localState))
            throw new TokenExtractionException("缺少 Local State，无法解密。请确认使用的是同一 Windows 用户。");

        log?.Invoke("已找到官方客户端数据。");
        string dump = Path.Combine(Path.GetTempPath(), "WuwatappoTokenExtractor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dump);
        var result = new ExtractionResult();
        try
        {
            string cookieCopy = Path.Combine(dump, "Cookies");
            CopyFile(cookies, cookieCopy);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                string src = cookies + suffix;
                if (File.Exists(src)) CopyFile(src, cookieCopy + suffix);
            }
            CopyFile(localState, Path.Combine(dump, "Local State"));

            log?.Invoke("正在解密本地登录数据…");
            byte[] key = LoadKey(Path.Combine(dump, "Local State"));
            ReadTokens(cookieCopy, key, result);
            log?.Invoke($"读取到 {result.CookieCount} 条 cookie，找到 {result.Tokens.Count} 个 token。");
            return result;
        }
        finally
        {
            try
            {
                Directory.Delete(dump, true);
                result.TempCleaned = true;
            }
            catch
            {
                // 清理失败不影响已经得到的结果；下次提取会使用新的随机目录。
                result.TempCleaned = false;
            }
        }
    }

    public static string? FindDataRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string appDataDir)
        {
            string root = Path.Combine(appDataDir, "EBWebView");
            if (seen.Add(root) && FindCookiesFile(root) != null && File.Exists(Path.Combine(root, "Local State")))
                candidates.Add(root);
        }

        AddCandidate(Path.Combine(local, "com.kuro.wuwatappo.cn"));
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(local))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("com.kuro", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("wuwatappo", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("wuwa", StringComparison.OrdinalIgnoreCase))
                    AddCandidate(dir);
            }
        }
        catch { }

        return candidates.FirstOrDefault();
    }

    static string? FindCookiesFile(string root)
    {
        string network = Path.Combine(root, "Default", "Network", "Cookies");
        if (File.Exists(network)) return network;
        string legacy = Path.Combine(root, "Default", "Cookies");
        if (File.Exists(legacy)) return legacy;
        string direct = Path.Combine(root, "Cookies");
        return File.Exists(direct) ? direct : null;
    }

    static void CopyFile(string source, string destination)
    {
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Threading.Thread.Sleep(300);
            }
        }
        throw new TokenExtractionException("复制客户端数据失败，请确认已完全退出客户端。", last ?? new IOException("unknown"));
    }

    static byte[] LoadKey(string localStatePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            string? encoded = doc.RootElement
                .GetProperty("os_crypt")
                .GetProperty("encrypted_key")
                .GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                throw new TokenExtractionException("Local State 中没有加密密钥。");
            byte[] protectedKey = Convert.FromBase64String(encoded);
            if (protectedKey.Length <= 5 ||
                !Encoding.ASCII.GetString(protectedKey, 0, 5).Equals("DPAPI", StringComparison.Ordinal))
                throw new TokenExtractionException("不支持的密钥格式（不是 Windows DPAPI）。");
            byte[] raw = new byte[protectedKey.Length - 5];
            Buffer.BlockCopy(protectedKey, 5, raw, 0, raw.Length);
            return Dpapi.Unprotect(raw);
        }
        catch (TokenExtractionException) { throw; }
        catch (Exception ex)
        {
            throw new TokenExtractionException(
                "读取登录密钥失败。官方客户端数据必须来自当前 Windows 用户；请在登录客户端的同一用户下运行本工具。", ex);
        }
    }

    static void ReadTokens(string cookieDbPath, byte[] key, ExtractionResult result)
    {
        using var conn = new SqliteConnection("Data Source=" + cookieDbPath + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, value, encrypted_value FROM cookies";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.CookieCount++;
            string name = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (!name.Contains("token", StringComparison.OrdinalIgnoreCase)) continue;

            string? plain = reader.IsDBNull(1) ? null : reader.GetString(1);
            byte[]? encrypted = reader.IsDBNull(2) ? null : reader.GetValue(2) as byte[];
            if (encrypted == null || encrypted.Length == 0)
            {
                if (!string.IsNullOrEmpty(plain)) result.Tokens.Add(new TokenEntry(name, plain));
                continue;
            }

            if (!TryDecrypt(key, encrypted, out string? text, out bool v20) || string.IsNullOrEmpty(text))
            {
                result.SawV20 |= v20;
                result.TokenDecryptFailed |= !v20;
                continue;
            }
            result.Tokens.Add(new TokenEntry(name, text));
        }
    }

    static bool TryDecrypt(byte[] key, byte[] encrypted, out string? text, out bool v20)
    {
        text = null;
        v20 = false;
        if (encrypted.Length >= 3 && encrypted[0] == (byte)'v' && encrypted[1] == (byte)'1' &&
            (encrypted[2] == (byte)'0' || encrypted[2] == (byte)'1'))
        {
            if (encrypted.Length < 15 + 16) return false;
            try
            {
                byte[] nonce = encrypted.AsSpan(3, 12).ToArray();
                int payloadLength = encrypted.Length - 15;
                byte[] ciphertext = encrypted.AsSpan(15, payloadLength - 16).ToArray();
                byte[] tag = encrypted.AsSpan(encrypted.Length - 16, 16).ToArray();
                byte[] plain = new byte[ciphertext.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, ciphertext, tag, plain);
                // Chromium >= 24 在明文前加 SHA256(host_key)。
                if (plain.Length > 32) plain = plain.AsSpan(32).ToArray();
                text = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (encrypted.Length >= 3 && encrypted[0] == (byte)'v' && encrypted[1] == (byte)'2' &&
            encrypted[2] == (byte)'0')
        {
            v20 = true;
            return false;
        }

        text = Encoding.UTF8.GetString(encrypted);
        return true;
    }

    static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);

        public static byte[] Unprotect(byte[] data)
        {
            IntPtr input = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, input, data.Length);
                var blobIn = new DATA_BLOB { cbData = data.Length, pbData = input };
                var blobOut = new DATA_BLOB();
                if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blobOut))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new TokenExtractionException("CryptUnprotectData 失败（" + error + "）。请使用登录官方客户端的同一 Windows 用户。");
                }
                try
                {
                    byte[] result = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    LocalFree(blobOut.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input);
            }
        }
    }
}
