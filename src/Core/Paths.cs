using System;
using System.IO;

namespace WuwatappoNative.Core;

/// <summary>应用与用户数据路径。运行目录只读，所有可写数据放 %LOCALAPPDATA%。</summary>
public static class Paths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    static string _root;
    /// <summary>用户数据根目录。测试可临时改写；生产环境固定为 %LOCALAPPDATA%\WuwatappoNative。</summary>
    public static string Root
    {
        get => _root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuwatappoNative");
        internal set => _root = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ImageCacheDir => Path.Combine(CacheDir, "images");
    /// <summary>主题背景图缓存；要求放在 cache 下，便于统一清理且避免 config 携带大图。</summary>
    public static string ThemeDir => Path.Combine(CacheDir, "theme");
    /// <summary>旧版主题图片目录，仅用于启动时迁移到 cache\theme。</summary>
    public static string LegacyThemeDir => Path.Combine(Root, "theme");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string StateFile => Path.Combine(Root, "state.json");
    /// <summary>主题预设与轮换状态；独立 JSON，避免塞进 config 大字段。</summary>
    public static string ThemePresetFile => Path.Combine(Root, "theme_presets.json");
    /// <summary>高级合成的锁定物资，单独存 JSON，避免状态/配置迁移时丢失。</summary>
    public static string LocksFile => Path.Combine(Root, "locks.json");
    /// <summary>面板关闭期间领取到的宝箱奖励；下次打开面板时补进「最近获得」。</summary>
    public static string RecentChestFile => Path.Combine(Root, "recent_chests.json");
    public static string UpdatesDir => Path.Combine(Root, "updates");
    public static string LogFile => Path.Combine(Root, "log.txt");
    public static string CatalogCache => Path.Combine(CacheDir, "appearance_asset.json");
    public static string CatalogMeta => Path.Combine(CacheDir, "appearance_meta.json");
    public static string DefaultAssets => Path.Combine(AppDir, "assets", "default");
    public static string BundledCatalog => Path.Combine(AppDir, "data", "appearance_asset.json");
    public static string WwwRoot => Path.Combine(AppDir, "wwwroot");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ImageCacheDir);
        Directory.CreateDirectory(ThemeDir);
    }
}
