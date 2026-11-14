using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WuwatappoNative.Net;
using WuwatappoTokenExtractor;

internal static class Program
{
    // 只使用假 token，并在临时目录中构造假数据；绝不读取真实用户凭据。
    const string FakeNativeToken = "fake-native-token-for-isolated-test";
    const string FakeOfficialToken = "fake-official-token-for-isolated-test";
    const string FakeCnToken = "fake-cn-token-for-isolated-test";
    static string _temp = "";

    static int Main()
    {
        _temp = Path.Combine(Path.GetTempPath(), "WuwatappoTokenExtractorChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        try
        {
            TestSecurityWarning();
            TestNativeReader();
            TestOfficialReader();
            Console.WriteLine("PASS: Token 提取器隔离自检全部通过（仅使用临时文件和假 token）。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(_temp, true); } catch { }
        }
    }

    static void TestSecurityWarning()
    {
        Assert(TokenSafety.CredentialWarning.Contains("这是你的登录凭据", StringComparison.Ordinal),
            "安全警告缺少登录凭据说明。");
        Assert(TokenSafety.CredentialWarning.Contains("请不要在任何情况下公开展示此内容", StringComparison.Ordinal),
            "安全警告缺少公开展示警告。");
        Assert(TokenSafety.CredentialWarning.Contains("如果已泄露，可以通过重新通过在官方/改版中使用手机号/游戏uid重新登陆来更新token", StringComparison.Ordinal),
            "安全警告缺少泄露后的更新说明。");
    }

    static void TestNativeReader()
    {
        string dir = Path.Combine(_temp, "native");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");

        // 小写 token 字段也必须能读到（属性名大小写不敏感）。
        string original = "{\"token\":\"" + FakeNativeToken + "\"}";
        File.WriteAllText(path, original);
        var result = NativeTokenReader.Read(path);
        Assert(result.Token == FakeNativeToken, "Native token 读取结果不一致。");
        Assert(File.ReadAllText(path) == original, "读取 Native 配置不应修改文件。");
        Assert(string.Equals(result.ConfigPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
            "Native 配置路径不一致。");

        // 模拟桌宠正在写文件：独占句柄短时间占用后，读取应通过重试成功。
        File.WriteAllText(path, "{\"Token\":\"" + FakeNativeToken + "\"}");
        using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var readTask = Task.Run(() => NativeTokenReader.Read(path));
            Thread.Sleep(350);
            hold.Dispose();
            var lockedResult = readTask.GetAwaiter().GetResult();
            Assert(lockedResult.Token == FakeNativeToken, "文件占用重试后 token 读取结果不一致。");
        }

        // 空 token 与文件不存在都应给出明确提示。
        File.WriteAllText(path, "{\"Token\":\"   \"}");
        AssertThrows<NativeTokenException>(() => NativeTokenReader.Read(path),
            "Native 尚未登录或没有保存 Token");

        AssertThrows<NativeTokenException>(() => NativeTokenReader.Read(Path.Combine(dir, "missing.json")),
            "Native 尚未登录或没有保存 Token");
    }

    static void TestOfficialReader()
    {
        string local = Path.Combine(_temp, "localappdata");
        string globalRoot = Path.Combine(local, "com.kuro.wuwatappo.global", "EBWebView");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WriteLocalState(globalRoot, key);
        WriteCookies(globalRoot, key, FakeOfficialToken);

        var logs = new List<string>();
        var result = OfficialTokenReader.Extract(
            logs.Add, dataRoot: globalRoot, preferredRegion: "global", requirePreferredRegion: true);

        var preferred = result.PreferredToken;
        Assert(preferred != null, "没有从临时 official data root 中解出 token。");
        Assert(preferred!.Value == FakeOfficialToken, "official token 解密结果不一致。");
        Assert(string.Equals(result.Region, "global", StringComparison.OrdinalIgnoreCase), "区服应识别为 global。");
        Assert(result.TempCleaned, "临时 cookie 副本应已删除。");
        Assert(logs.Count > 0, "提取过程应产生日志。");
        Assert(!logs.Any(line => line.Contains(FakeOfficialToken, StringComparison.Ordinal)),
            "日志不得包含完整 token。");

        string? found = OfficialTokenReader.FindDataRoot("global", allowFallback: false, localAppDataRoot: local);
        Assert(found != null, "严格国际服定位应找到临时 global 数据目录。");
        Assert(string.Equals(Path.GetFullPath(found!), Path.GetFullPath(globalRoot), StringComparison.OrdinalIgnoreCase),
            "严格国际服定位返回了错误目录。");
        Assert(OfficialTokenReader.FindDataRoot("cn", allowFallback: false, localAppDataRoot: local) == null,
            "只有国际服数据时，严格国服定位不应回退到国际服。");

        // 只有国服数据时，国际服入口必须明确失败而不是静默回退。
        string cnLocal = Path.Combine(_temp, "localappdata-cn");
        string cnRoot = Path.Combine(cnLocal, "com.kuro.wuwatappo.cn", "EBWebView");
        WriteLocalState(cnRoot, key);
        WriteCookies(cnRoot, key, FakeCnToken);

        string? foundCn = OfficialTokenReader.FindDataRoot("cn", allowFallback: false, localAppDataRoot: cnLocal);
        Assert(foundCn != null, "严格国服定位应找到临时 cn 数据目录。");
        Assert(string.Equals(Path.GetFullPath(foundCn!), Path.GetFullPath(cnRoot), StringComparison.OrdinalIgnoreCase),
            "严格国服定位返回了错误目录。");

        Assert(OfficialTokenReader.FindDataRoot("global", allowFallback: false, localAppDataRoot: cnLocal) == null,
            "严格国际服定位不得回退到国服数据。");
        Assert(OfficialTokenReader.FindDataRoot("global", allowFallback: true, localAppDataRoot: cnLocal) != null,
            "允许回退时应能找到国服数据（用于主程序旧行为）。");
    }

    static void WriteLocalState(string root, byte[] key)
    {
        Directory.CreateDirectory(root);
        byte[] protectedKey = DpapiProtect(key);
        byte[] payload = new byte[5 + protectedKey.Length];
        Encoding.ASCII.GetBytes("DPAPI").CopyTo(payload, 0);
        protectedKey.CopyTo(payload, 5);
        string json = JsonSerializer.Serialize(new
        {
            os_crypt = new { encrypted_key = Convert.ToBase64String(payload) }
        });
        File.WriteAllText(Path.Combine(root, "Local State"), json);
    }

    static void WriteCookies(string root, byte[] key, string token)
    {
        string network = Path.Combine(root, "Default", "Network");
        Directory.CreateDirectory(network);
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(network, "Cookies") + ";Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE cookies (name TEXT, value TEXT, encrypted_value BLOB)";
            create.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO cookies (name, value, encrypted_value) VALUES ($name, NULL, $blob)";
        insert.Parameters.AddWithValue("$name", "kuro_infoToken");
        insert.Parameters.AddWithValue("$blob", EncryptCookie(key, token));
        insert.ExecuteNonQuery();
    }

    static byte[] EncryptCookie(byte[] key, string token)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        byte[] plain = new byte[32 + tokenBytes.Length]; // 前 32 字节模拟域名哈希
        tokenBytes.CopyTo(plain, 32);

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        return Encoding.ASCII.GetBytes("v10")
            .Concat(nonce)
            .Concat(cipher)
            .Concat(tag)
            .ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    static byte[] DpapiProtect(byte[] data)
    {
        IntPtr input = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, input, data.Length);
            var blobIn = new DATA_BLOB { cbData = data.Length, pbData = input };
            var blobOut = new DATA_BLOB();
            if (!CryptProtectData(ref blobIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blobOut))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData 失败。");
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

    static void AssertThrows<TException>(Action action, string messagePart) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            Assert(ex.Message.Contains(messagePart, StringComparison.Ordinal),
                $"{typeof(TException).Name} 提示缺少预期文案：{messagePart}");
            return;
        }
        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}，但没有抛出。");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
