using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Assets;

public sealed class AssetFrame
{
    public string Url;
    public double OffsetX, OffsetY;
}

public sealed class AssetEntry
{
    public long Id;
    public string Type = "";
    public string Url = "";
    public double OffsetX, OffsetY;
    public readonly Dictionary<string, AssetFrame> Frames = new(StringComparer.OrdinalIgnoreCase);
    public string Name = "";
    public string SeriesName = "";
    public string Rarity = "common";
    public long GameRoleId;
    public long HeightLightId;
    public bool IsAtlas;

    public AssetFrame Frame(string key) => Frames.TryGetValue(key, out var f) ? f : null;
}

/// <summary>
/// 外观目录。真实链路：activity-config → assets_host → /app/json-host/appearance_asset_host.json → appearance_asset.json。
/// 六个区：character_skin / accessory / emote 及对应 *_atlas。名称来自 api/common/atlas。
/// </summary>
public sealed class Catalog
{
    public string AssetsHost { get; private set; } = "";
    public long AssetsJsonTs { get; private set; }
    public readonly Dictionary<string, Dictionary<long, AssetEntry>> ByType = new();
    public readonly Dictionary<long, string> Names = new();
    public readonly Dictionary<long, string> Series = new();
    public readonly Dictionary<long, string> Rarities = new();
    public readonly Dictionary<string, long> AtlasIdByName = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> IdByAssetName = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictCharacter = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictSeries = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictAccessory = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictEmote = new(StringComparer.OrdinalIgnoreCase);
    public bool DictReady { get; private set; }
    public event Action Loaded;

    public int Count(string type) => ByType.TryGetValue(type, out var m) ? m.Count : 0;
    public IEnumerable<AssetEntry> All(string type) => ByType.TryGetValue(type, out var m) ? m.Values : Enumerable.Empty<AssetEntry>();
    public AssetEntry Find(string type, long id) => ByType.TryGetValue(type, out var m) && m.TryGetValue(id, out var e) ? e : null;

    public string CdnUrl(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return "";
        var host = (AssetsHost ?? "").TrimEnd('/');
        return host + "/" + relative.TrimStart('/');
    }

    public string AtlasPath(string assetType, string assetName)
    {
        string folder = assetType switch
        {
            "character_skin" => "character_atlas",
            "accessory" => "accessory_atlas",
            "emote" => "emote_atlas",
            "character_skin_atlas" => "character_atlas",
            "accessory_atlas" => "accessory_atlas",
            "emote_atlas" => "emote_atlas",
            _ => assetType
        };
        if (string.IsNullOrEmpty(assetName)) return "";
        return folder + "/" + assetName;
    }

    public async Task<bool> LoadAsync(ApiClient api, Action<string> progress = null)
    {
        bool fromCdn = false;
        try
        {
            progress?.Invoke("读取活动配置…");
            var cfg = await api.GetAsync("api/common/activity-config");
            if (cfg is JsonObject o)
            {
                AssetsHost = ApiClient.ReadString(o["assets_host"]).TrimEnd('/');
                AssetsJsonTs = ApiClient.ReadLong(o["assets_json_ts"]);
            }
            if (string.IsNullOrEmpty(AssetsHost)) throw new InvalidOperationException("activity-config 未返回 assets_host");

            progress?.Invoke("读取素材目录地址…");
            var hostFile = await GetJsonAsync($"{AssetsHost}/app/json-host/appearance_asset_host.json?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            string catalogUrl = ApiClient.ReadString(hostFile?["url"]);
            if (string.IsNullOrEmpty(catalogUrl)) throw new InvalidOperationException("appearance_asset_host.json 缺少 url");

            progress?.Invoke("下载外观目录…");
            var root = await GetJsonAsync(catalogUrl);
            Parse(root);
            try
            {
                Directory.CreateDirectory(Paths.CacheDir);
                File.WriteAllText(Paths.CatalogCache, root.ToJsonString());
                File.WriteAllText(Paths.CatalogMeta, new JsonObject
                {
                    ["host"] = AssetsHost,
                    ["ts"] = AssetsJsonTs
                }.ToJsonString());
            }
            catch (Exception ex) { Log.Write("目录缓存写入失败 " + ex.Message); }
            fromCdn = true;
            Log.Write($"目录加载完成 host={AssetsHost} skins={Count("character_skin")} accessories={Count("accessory")} emotes={Count("emote")}");
        }
        catch (Exception ex)
        {
            Log.Write("CDN 目录失败，使用离线兜底：" + ex.Message);
            progress?.Invoke("CDN 不可用，使用离线目录");
            LoadOffline();
        }

        try
        {
            progress?.Invoke("读取中文名称字典…");
            await LoadDictionariesAsync();
        }
        catch (Exception ex) { Log.Write("名称字典加载失败 " + ex.Message); }

        try
        {
            progress?.Invoke("读取图鉴名称…");
            await LoadNamesAsync(api);
        }
        catch (Exception ex) { Log.Write("图鉴名称加载失败 " + ex.Message); }

        Loaded?.Invoke();
        return fromCdn;
    }

    public void LoadOffline()
    {
        if (!DictReady) LoadCachedDictionary();
        try
        {
            string path = File.Exists(Paths.CatalogCache) ? Paths.CatalogCache : Paths.BundledCatalog;
            if (File.Exists(Paths.CatalogMeta))
            {
                var meta = JsonNode.Parse(File.ReadAllText(Paths.CatalogMeta));
                AssetsHost = ApiClient.ReadString(meta?["host"]);
                AssetsJsonTs = ApiClient.ReadLong(meta?["ts"]);
            }
            if (File.Exists(path))
            {
                Parse(JsonNode.Parse(File.ReadAllText(path)));
                Log.Write("离线目录已载入：" + path);
            }
        }
        catch (Exception ex) { Log.Write("离线目录载入失败 " + ex.Message); }
    }

    void Parse(JsonNode root)
    {
        ByType.Clear();
        if (root is not JsonObject obj) return;
        foreach (var key in new[] { "character_skin", "accessory", "emote", "character_skin_atlas", "accessory_atlas", "emote_atlas" })
        {
            var map = new Dictionary<long, AssetEntry>();
            if (obj[key] is JsonObject section)
            {
                foreach (var pair in section)
                {
                    if (!long.TryParse(pair.Key, out var id) || pair.Value is not JsonObject e) continue;
                    var entry = new AssetEntry
                    {
                        Id = id,
                        Type = key,
                        Url = ApiClient.ReadString(e["url"]),
                        OffsetX = ReadDouble(e["offset_x"]),
                        OffsetY = ReadDouble(e["offset_y"]),
                        GameRoleId = ApiClient.ReadLong(e["game_role_id"]),
                        HeightLightId = ApiClient.ReadLong(e["height_light_id"]),
                        IsAtlas = key.EndsWith("_atlas", StringComparison.Ordinal),
                    };
                    foreach (var frameKey in new[] { "body1", "body2", "body3", "head1", "head2" })
                    {
                        if (e[frameKey] is JsonObject f)
                            entry.Frames[frameKey] = new AssetFrame
                            {
                                Url = ApiClient.ReadString(f["url"]),
                                OffsetX = ReadDouble(f["offset_x"]),
                                OffsetY = ReadDouble(f["offset_y"]),
                            };
                    }
                    map[id] = entry;
                }
            }
            ByType[key] = map;
        }
    }

    async Task LoadDictionariesAsync()
    {
        if (string.IsNullOrEmpty(AssetsHost)) return;
        var host = await GetJsonAsync($"{AssetsHost}/app/json-host/dict_host.json");
        string url = ApiClient.ReadString(host?["url"]?["zh"]).Trim();
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("dict_host.json 缺少 zh");
        var dict = await GetJsonAsync(url);
        try { File.WriteAllText(System.IO.Path.Combine(Paths.CacheDir, "dict_zh.json"), dict.ToJsonString()); } catch { }
        ApplyDictionary(dict);
        Log.Write($"名称字典 character={DictCharacter.Count} series={DictSeries.Count} accessory={DictAccessory.Count} emote={DictEmote.Count}");
    }

    public bool LoadCachedDictionary()
    {
        try
        {
            var path = System.IO.Path.Combine(Paths.CacheDir, "dict_zh.json");
            if (!File.Exists(path)) return false;
            ApplyDictionary(JsonNode.Parse(File.ReadAllText(path)));
            return DictReady;
        }
        catch (Exception ex) { Log.Write("字典缓存读取失败 " + ex.Message); return false; }
    }

    void ApplyDictionary(JsonNode dict)
    {
        DictCharacter.Clear(); DictSeries.Clear(); DictAccessory.Clear(); DictEmote.Clear();
        FillDict(dict?["character"], DictCharacter);
        FillDict(dict?["series"], DictSeries);
        FillDict(dict?["accessory"], DictAccessory);
        FillDict(dict?["emote"], DictEmote);
        DictReady = DictCharacter.Count > 0;
    }

    static void FillDict(JsonNode node, Dictionary<string, string> into)
    {
        if (node is not JsonObject o) return;
        foreach (var pair in o)
        {
            string value = ApiClient.ReadString(pair.Value);
            if (!string.IsNullOrEmpty(value)) into[pair.Key] = value;
        }
    }

    static string StripPng(string name) => (name ?? "").Trim().Replace(".png", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>按原版规则拼显示名：角色 = 角色名 ・ 系列名；配件 = 配件名；表情 = 角色名 ・ 表情名。</summary>
    public string DisplayName(string assetType, string assetName)
    {
        string n = (assetName ?? "").Trim();
        if (string.IsNullOrEmpty(n)) return "";
        string type = (assetType ?? "").ToLowerInvariant();
        if (type.StartsWith("character_skin"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(StripPng(n), @"^role_(\d+)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                DictCharacter.TryGetValue(m.Groups[1].Value, out var ch);
                DictSeries.TryGetValue(m.Groups[2].Value, out var se);
                return JoinParts(ch, se);
            }
        }
        else if (type.StartsWith("accessory"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(StripPng(n), @"^accessory_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string key = m.Success ? m.Groups[1].Value : StripPng(n);
            if (DictAccessory.TryGetValue(key, out var acc)) return acc;
        }
        else if (type.StartsWith("emote"))
        {
            string baseName = StripPng(n);
            if (DictEmote.TryGetValue(baseName + ".png", out var full)) return full;
            var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^emote_(\d+)_([a-z0-9]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                DictCharacter.TryGetValue(m.Groups[1].Value, out var ch);
                DictEmote.TryGetValue(m.Groups[2].Value, out var em);
                if (string.IsNullOrEmpty(em)) DictEmote.TryGetValue(m.Groups[2].Value + ".png", out em);
                return JoinParts(ch, em);
            }
        }
        // 兜底：字典里按完整文件名查
        foreach (var map in new[] { DictCharacter, DictAccessory, DictEmote })
            if (map.TryGetValue(n, out var v) || map.TryGetValue(StripPng(n), out v)) return v;
        return "";
    }

    /// <summary>素材缩略图：先用 asset_name 找到图鉴 asset_id，再取 CDN 目录里的 atlas 图片。</summary>
    public string ThumbUrl(string assetType, string assetName)
    {
        string atlasType = assetType switch
        {
            "character_skin" => "character_skin_atlas",
            "accessory" => "accessory_atlas",
            "emote" => "emote_atlas",
            _ => assetType
        };
        if (!string.IsNullOrEmpty(assetName) && AtlasIdByName.TryGetValue(assetName, out var atlasId))
        {
            var entry = Find(atlasType, atlasId);
            if (entry != null && !string.IsNullOrEmpty(entry.Url)) return CdnUrl(entry.Url);
        }
        return CdnUrl(AtlasPath(assetType, assetName));
    }

    public string AtlasThumb(string atlasType, long atlasId)
    {
        var entry = Find(atlasType, atlasId);
        return entry != null && !string.IsNullOrEmpty(entry.Url) ? CdnUrl(entry.Url) : "";
    }

    /// <summary>联机 b/c 字段可能是数字 id，也可能是 role_xxx_yyy.png 这样的素材名。</summary>
    public long ResolveId(string assetType, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim();
        if (long.TryParse(key, out var id)) return id;
        if (IdByAssetName.TryGetValue(key, out var resolved)) return resolved;
        if (IdByAssetName.TryGetValue(key + ".png", out resolved)) return resolved;
        if (IdByAssetName.TryGetValue(StripPng(key), out resolved)) return resolved;
        return 0;
    }

    public string SeriesName(string assetType, string assetName)
    {
        string type = (assetType ?? "").ToLowerInvariant();
        string n = StripPng(assetName);
        if (type.StartsWith("character_skin"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(n, @"^role_(\d+)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && DictSeries.TryGetValue(m.Groups[2].Value, out var se)) return se;
        }
        return "";
    }

    static string JoinParts(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return a + "・" + b;
    }

    async Task LoadNamesAsync(ApiClient api)
    {
        var data = await api.GetAsync("api/common/atlas");
        if (data is not JsonArray arr) return;
        Names.Clear();
        Series.Clear();
        Rarities.Clear();
        AtlasIdByName.Clear();
        IdByAssetName.Clear();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            long id = ApiClient.ReadLong(o["asset_id"]);
            if (id <= 0) continue;
            string type = ApiClient.ReadString(o["asset_type"]);
            string assetName = ApiClient.ReadString(o["asset_name"]);
            string rarity = ApiClient.ReadString(o["rarity"]);
            string display = DisplayName(type, assetName);
            if (!string.IsNullOrEmpty(display)) Names[id] = display;
            if (!string.IsNullOrEmpty(rarity)) Rarities[id] = rarity.ToLowerInvariant();
            if (!string.IsNullOrEmpty(assetName))
            {
                AtlasIdByName[assetName] = id;
                // 图鉴 atlas 条目的 height_light_id 指向正片 id，用于把 asset_name 反查成可渲染的皮肤/配件 id
                if (ByType.TryGetValue(type, out var atlasMap) && atlasMap.TryGetValue(id, out var atlasEntry) && atlasEntry.HeightLightId > 0)
                    IdByAssetName[assetName] = atlasEntry.HeightLightId;
            }
        }
        Log.Write("图鉴名称 " + Names.Count);
    }

    /// <summary>为条目补全显示名与稀有度（来自图鉴接口按 asset_id 索引）。</summary>
    public void ApplyNames(IEnumerable<(long id, string name, string rarity, string series)> rows)
    {
        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.name)) Names[r.id] = r.name;
            if (!string.IsNullOrEmpty(r.series)) Series[r.id] = r.series;
        }
    }

    static double ReadDouble(JsonNode n)
    {
        if (n == null) return 0;
        try { return n.GetValue<double>(); } catch { return 0; }
    }

    static async Task<JsonNode> GetJsonAsync(string url)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var text = await http.GetStringAsync(url).ConfigureAwait(false);
        return JsonNode.Parse(text);
    }
}
