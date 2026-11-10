using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WuwatappoNative.Core;

public sealed class AssetLockEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("id")]
    public long Id { get; set; }
}

/// <summary>
/// 高级合成的锁定物资。刻意独立于 config.json/state.json 保存，
/// 避免版本升级或状态迁移时把用户锁定的图鉴物资清掉。
/// </summary>
public static class AssetLockStore
{
    static readonly object Gate = new();
    static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);
    static bool _loaded;

    static string Key(string type, long id) => (type ?? "").Trim().ToLowerInvariant() + ":" + id;

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(Paths.LocksFile)) return;
            var root = JsonNode.Parse(File.ReadAllText(Paths.LocksFile)) as JsonObject;
            if (root?["items"] is not JsonArray items) return;
            foreach (var node in items)
            {
                if (node is not JsonObject item) continue;
                string type = (item["type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                long id = ReadLong(item["id"]);
                if (!string.IsNullOrEmpty(type) && id > 0) Keys.Add(Key(type, id));
            }
        }
        catch (Exception ex) { Log.Write("锁定物资读取失败 " + ex.Message); }
    }

    static long ReadLong(JsonNode node)
    {
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
            }
        }
        catch { }
        return 0;
    }

    public static bool IsLocked(string type, long id)
    {
        if (string.IsNullOrWhiteSpace(type) || id <= 0) return false;
        lock (Gate)
        {
            EnsureLoaded();
            return Keys.Contains(Key(type, id));
        }
    }

    public static AssetLockEntry[] All()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return Keys.Select(key =>
            {
                int split = key.LastIndexOf(':');
                long.TryParse(key.AsSpan(split + 1), out long id);
                return new AssetLockEntry { Type = key[..split], Id = id };
            }).ToArray();
        }
    }

    /// <summary>切换锁定状态并立即落盘；返回切换后的状态。</summary>
    public static bool Toggle(string type, long id)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type) || id <= 0) return false;
        lock (Gate)
        {
            EnsureLoaded();
            string key = Key(type, id);
            bool locked;
            if (Keys.Contains(key))
            {
                Keys.Remove(key);
                locked = false;
            }
            else
            {
                Keys.Add(key);
                locked = true;
            }
            Save();
            return locked;
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var items = new JsonArray();
            foreach (string key in Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                int split = key.LastIndexOf(':');
                long.TryParse(key.AsSpan(split + 1), out long id);
                items.Add(new JsonObject
                {
                    ["type"] = key[..split],
                    ["id"] = id,
                });
            }
            var root = new JsonObject { ["schema"] = 1, ["items"] = items };
            string tmp = Paths.LocksFile + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString());
            File.Move(tmp, Paths.LocksFile, true);
        }
        catch (Exception ex) { Log.Write("锁定物资写入失败 " + ex.Message); }
    }
}
