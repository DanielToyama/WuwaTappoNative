using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WuwatappoNative.Net;

namespace WuwatappoNative.Core;

/// <summary>
/// 面板关闭期间领取到的宝箱奖励队列。
/// 「最近获得」原先只靠 WebView 的 chest 事件记录；面板未打开时事件会被丢弃，
/// 而背包扫描只能发现“从未拥有过”的物品，重复获得的奖励因此永远补不回来。
/// 这里由原生层先把奖励 id 落盘，面板启动后再合并并 ack。
/// </summary>
public static class RecentChestStore
{
    const int MaxPending = 80;
    static readonly object Gate = new();
    static bool _loaded;
    static string _scope = "";
    static readonly List<long> Ids = new();

    sealed class FileModel
    {
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "";
        [JsonPropertyName("ids")]
        public List<long> Ids { get; set; } = new();
    }

    static void EnsureLoaded(string scope)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(Paths.RecentChestFile)) return;
            var root = JsonNode.Parse(File.ReadAllText(Paths.RecentChestFile));
            if (root is JsonObject obj)
                _scope = ApiClient.ReadString(obj["scope"]);
            JsonArray items = root as JsonArray ?? (root as JsonObject)?["ids"] as JsonArray;
            if (items == null) return;
            foreach (var node in items)
            {
                long id = ApiClient.ReadLong(node);
                if (id > 0) Ids.Add(id);
            }
            Trim();
            // 旧版没有 scope：第一次登录后把这些奖励归到当前账号，避免继续跨账号可见。
            if (string.IsNullOrEmpty(_scope) && Ids.Count > 0 && IsAccountScope(scope))
            {
                _scope = scope;
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Write("宝箱奖励队列读取失败：" + ex.Message);
        }
    }

    static bool IsAccountScope(string scope)
        => !string.IsNullOrEmpty(scope) && !scope.EndsWith(":", StringComparison.Ordinal);

    static bool ScopeMatches(string scope)
    {
        if (!IsAccountScope(scope)) return true;
        if (string.IsNullOrEmpty(_scope))
        {
            _scope = scope;
            Save();
            return true;
        }
        return string.Equals(_scope, scope, StringComparison.Ordinal);
    }

    static void Trim()
    {
        if (Ids.Count > MaxPending)
            Ids.RemoveRange(0, Ids.Count - MaxPending);
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            string tmp = Paths.RecentChestFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Scope = _scope, Ids = Ids }));
            File.Move(tmp, Paths.RecentChestFile, true);
        }
        catch (Exception ex)
        {
            Log.Write("宝箱奖励队列写入失败：" + ex.Message);
        }
    }

    /// <summary>记录一次宝箱奖励。面板若正在运行，会通过 chest 事件立即 ack。</summary>
    public static void Add(long id, string scope = "")
    {
        if (id <= 0) return;
        lock (Gate)
        {
            EnsureLoaded(scope);
            if (!string.IsNullOrEmpty(scope) && !ScopeMatches(scope))
            {
                // 切换账号/区服后，旧账号未确认的奖励不能混进新账号。
                Ids.Clear();
                _scope = scope;
            }
            Ids.Add(id);
            Trim();
            Save();
        }
    }

    /// <summary>返回尚未合并到「最近获得」的奖励 id（按获得顺序，旧的在前）。</summary>
    public static long[] Pending(string scope = "")
    {
        lock (Gate)
        {
            EnsureLoaded(scope);
            if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(_scope)
                && !string.Equals(_scope, scope, StringComparison.Ordinal))
                return Array.Empty<long>();
            return Ids.ToArray();
        }
    }

    /// <summary>面板确认已合并后移除；同一个 id 多次获得时只移除对应次数。</summary>
    public static void Ack(IEnumerable<long> ids, string scope = "")
    {
        var ack = (ids ?? Enumerable.Empty<long>()).Where(id => id > 0).ToArray();
        if (ack.Length == 0) return;
        lock (Gate)
        {
            EnsureLoaded(scope);
            if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(_scope)
                && !string.Equals(_scope, scope, StringComparison.Ordinal))
                return;
            foreach (long id in ack)
            {
                int index = Ids.IndexOf(id);
                if (index >= 0) Ids.RemoveAt(index);
            }
            Save();
        }
    }
}
