using System;
using System.IO;

namespace WuwatappoNative.Core;

/// <summary>应用与用户数据路径。运行目录只读，所有可写数据放 %LOCALAPPDATA%。</summary>
public static class Paths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuwatappoNative");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ImageCacheDir => Path.Combine(CacheDir, "images");
    public static string ConfigFile => Path.Combine(Root, "config.json");
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
    }
}
