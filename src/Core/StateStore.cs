using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace WuwatappoNative.Core;

/// <summary>
/// 运行状态存储：窗口位置、房间状态、成员位置、成员可见性和已读公告。
/// 与 config.json 分开，避免每次设置保存都重写越来越大的成员状态。
/// 成员数据按成员 id 保存，不按房间隔离；旧房间数据在迁移时丢弃。
/// </summary>
public static class StateStore
{
    static readonly object Gate = new();
    static bool _loaded;

    public static void Load(Settings cfg)
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(Paths.StateFile))
                {
                    Apply(JsonNode.Parse(File.ReadAllText(Paths.StateFile)), cfg);
                    return;
                }
                MigrateLegacy(cfg);
            }
            catch (Exception ex)
            {
                Log.Write("状态读取失败：" + ex.Message);
            }
        }
    }

    /// <summary>由 ConfigStore.Save 调用；状态未加载时不写，避免覆盖迁移前文件。</summary>
    public static void Save(Settings cfg)
    {
        if (!_loaded) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                var root = new JsonObject
                {
                    ["schema"] = 1,
                    ["petX"] = cfg.PetX,
                    ["petY"] = cfg.PetY,
                    ["roomNo"] = cfg.RoomNo ?? "",
                    ["lastRoom"] = cfg.LastRoom ?? "",
                    ["roomHistory"] = ToArray(cfg.RoomHistory),
                    ["memberPositions"] = ToPositions(cfg.MemberPositions),
                    ["memberVisibility"] = ToVisibility(cfg.MemberVisibility),
                    ["readAnnouncementIds"] = ToArray(cfg.ReadAnnouncementIds),
                };
                string tmp = Paths.StateFile + ".tmp";
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                };
                File.WriteAllText(tmp, root.ToJsonString(options));
                File.Move(tmp, Paths.StateFile, true);
            }
            catch (Exception ex)
            {
                Log.Write("状态写入失败：" + ex.Message);
            }
        }
    }

    static void Apply(JsonNode node, Settings cfg)
    {
        if (node is not JsonObject root) return;
        cfg.PetX = ReadInt(root["petX"], -1);
        cfg.PetY = ReadInt(root["petY"], -1);
        cfg.RoomNo = ReadString(root["roomNo"]);
        cfg.LastRoom = ReadString(root["lastRoom"]);
        cfg.RoomHistory = ReadStringArray(root["roomHistory"]);
        cfg.MemberPositions = ReadPositions(root["memberPositions"]);
        cfg.MemberVisibility = ReadVisibility(root["memberVisibility"]);
        cfg.ReadAnnouncementIds = ReadStringArray(root["readAnnouncementIds"]).ToList();
    }

    /// <summary>
    /// 从旧 config.json 提取状态。旧 MemberPositions 是“房间:成员”键：
    /// 只保留最后所在房间的成员，提取成成员 id 键；其他房间和离线成员不再保留。
    /// </summary>
    static void MigrateLegacy(Settings cfg)
    {
        string oldRoom = "";
        JsonObject root = null;
        try
        {
            if (File.Exists(Paths.ConfigFile))
                root = JsonNode.Parse(File.ReadAllText(Paths.ConfigFile)) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Write("旧配置读取失败：" + ex.Message);
        }

        cfg.PetX = ReadInt(root?["PetX"], -1);
        cfg.PetY = ReadInt(root?["PetY"], -1);
        oldRoom = ReadString(root?["RoomNo"]);
        if (string.IsNullOrEmpty(oldRoom)) oldRoom = ReadString(root?["LastRoom"]);
        // 旧房间历史不再保留。
        cfg.RoomNo = "";
        cfg.LastRoom = "";
        cfg.RoomHistory = Array.Empty<string>();

        var positions = new Dictionary<string, int[]>();
        if (root?["MemberPositions"] is JsonObject oldPositions && !string.IsNullOrEmpty(oldRoom))
        {
            foreach (var pair in oldPositions)
            {
                int split = pair.Key.LastIndexOf(':');
                if (split <= 0) continue;
                string room = pair.Key[..split];
                string memberId = pair.Key[(split + 1)..];
                if (!string.Equals(room, oldRoom, StringComparison.Ordinal)) continue;
                var point = ReadPoint(pair.Value);
                if (point != null) positions[memberId] = point;
            }
        }
        cfg.MemberPositions = positions;

        var visibility = new Dictionary<string, bool>();
        if (root?["MemberVisibility"] is JsonObject oldVisibility)
        {
            foreach (var pair in oldVisibility)
            {
                string key = pair.Key;
                int split = key.LastIndexOf(':');
                string memberId = split > 0 ? key[(split + 1)..] : key;
                // 只保留能在当前房间位置数据中找到的人；离线/其他房间的人直接丢弃。
                if (!positions.ContainsKey(memberId)) continue;
                if (!ReadBool(pair.Value)) visibility[memberId] = false;
            }
        }
        cfg.MemberVisibility = visibility;

        try
        {
            if (File.Exists(Paths.ConfigFile))
                File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".bak", true);
        }
        catch (Exception ex)
        {
            Log.Write("旧配置备份失败：" + ex.Message);
        }

        Log.Write($"状态迁移完成：成员位置 {positions.Count} 条，隐藏成员 {visibility.Count} 条");
        Save(cfg);
    }

    static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (string value in values ?? Enumerable.Empty<string>()) arr.Add(value ?? "");
        return arr;
    }

    static JsonObject ToPositions(Dictionary<string, int[]> positions)
    {
        var obj = new JsonObject();
        if (positions == null) return obj;
        foreach (var pair in positions)
        {
            if (pair.Value == null || pair.Value.Length < 2) continue;
            obj[pair.Key] = new JsonArray(pair.Value[0], pair.Value[1]);
        }
        return obj;
    }

    static JsonObject ToVisibility(Dictionary<string, bool> visibility)
    {
        var obj = new JsonObject();
        if (visibility == null) return obj;
        foreach (var pair in visibility)
        {
            if (!pair.Value) obj[pair.Key] = false;
        }
        return obj;
    }

    static Dictionary<string, int[]> ReadPositions(JsonNode node)
    {
        var result = new Dictionary<string, int[]>();
        if (node is not JsonObject obj) return result;
        foreach (var pair in obj)
        {
            var point = ReadPoint(pair.Value);
            if (point != null) result[pair.Key] = point;
        }
        return result;
    }

    static Dictionary<string, bool> ReadVisibility(JsonNode node)
    {
        var result = new Dictionary<string, bool>();
        if (node is not JsonObject obj) return result;
        foreach (var pair in obj)
        {
            if (!ReadBool(pair.Value)) result[pair.Key] = false;
        }
        return result;
    }

    static int[] ReadPoint(JsonNode node)
    {
        if (node is not JsonArray arr || arr.Count < 2) return null;
        int x = ReadInt(arr[0], 0);
        int y = ReadInt(arr[1], 0);
        return new[] { x, y };
    }

    static string[] ReadStringArray(JsonNode node)
    {
        if (node is not JsonArray arr) return Array.Empty<string>();
        return arr.Select(x => ReadString(x)).Where(x => !string.IsNullOrEmpty(x)).ToArray();
    }

    static int ReadInt(JsonNode node, int fallback)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<int>(out int i)) return i;
                if (value.TryGetValue<long>(out long l)) return (int)l;
                if (value.TryGetValue<string>(out string s) && int.TryParse(s, out int parsed)) return parsed;
            }
        }
        catch { }
        return fallback;
    }

    static bool ReadBool(JsonNode node)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool b)) return b;
                if (value.TryGetValue<long>(out long l)) return l != 0;
                if (value.TryGetValue<string>(out string s)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    static string ReadString(JsonNode node)
    {
        try { return node?.GetValue<string>() ?? ""; } catch { return ""; }
    }
}
