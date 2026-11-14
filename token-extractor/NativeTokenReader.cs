using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WuwatappoTokenExtractor;

public sealed class NativeTokenException : Exception
{
    public NativeTokenException(string message) : base(message) { }
    public NativeTokenException(string message, Exception inner) : base(message, inner) { }
}

public sealed class NativeTokenResult
{
    public NativeTokenResult(string token, string configPath)
    {
        Token = token;
        ConfigPath = configPath;
    }

    public string Token { get; }
    public string ConfigPath { get; }
}

/// <summary>
/// 只读取 Wuwatappo Native 已保存的配置，不启动、不修改桌宠进程/文件。
/// 生产路径固定为 %LOCALAPPDATA%\WuwatappoNative\config.json；测试可传入临时路径。
/// </summary>
public static class NativeTokenReader
{
    const int ReadAttempts = 6;

    public static string DefaultConfigPath
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new NativeTokenException("无法定位本机应用数据目录（%LOCALAPPDATA%）。");
            return Path.Combine(local, "WuwatappoNative", "config.json");
        }
    }

    /// <summary>
    /// 读取 config.json 的 Token 字段。文件可能正被桌宠写入，因此使用
    /// FileShare.ReadWrite | FileShare.Delete 并做短暂重试；JSON 属性名不区分大小写。
    /// </summary>
    public static NativeTokenResult Read(string? configPath = null)
    {
        string path = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : Path.GetFullPath(configPath);
        if (!File.Exists(path))
            throw new NativeTokenException(
                "Native 尚未登录或没有保存 Token。\r\n\r\n请先启动 Wuwatappo Native 并完成登录，然后重试。");

        string json = ReadAllTextWithRetry(path);
        string? token;
        try
        {
            using var doc = JsonDocument.Parse(json);
            token = FindToken(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new NativeTokenException(
                "Native 配置文件格式不正确，无法读取 Token。\r\n\r\n请确认文件未被手动改坏，或在桌宠中重新登录。", ex);
        }

        token = token?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new NativeTokenException(
                "Native 尚未登录或没有保存 Token。\r\n\r\n请先启动 Wuwatappo Native 并完成登录，然后重试。");

        return new NativeTokenResult(token, path);
    }

    static string? FindToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "Token", StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }

    static string ReadAllTextWithRetry(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            if (attempt < ReadAttempts - 1)
                Thread.Sleep(100 * (attempt + 1));
        }

        throw new NativeTokenException(
            "读取 Native 配置失败：文件正被占用或暂时无法访问。请稍后重试。", last ?? new IOException("unknown"));
    }
}
