using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace WuwatappoNative.Core;

/// <summary>一个命名主题预设：面板配色 + 底栏配色 + 背景图引用。</summary>
public sealed class ThemePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string AccentColor { get; set; } = "#E7B77C";
    public string FontColor { get; set; } = "#8E5E43";
    public string Advanced { get; set; } = "";
    public bool BarFollowStandard { get; set; } = true;
    public string BarColor { get; set; } = "";
    public bool BarTextFollowTheme { get; set; } = true;
    public string BarTextColor { get; set; } = "";
    /// <summary>预设独占的缓存图片文件名；保存预设时会复制当前背景图，避免被后续换图覆盖。</summary>
    public string BackgroundImage { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 0.25;
    public double BackgroundBlur { get; set; }
    public long CreatedAtMs { get; set; }
}

public sealed class ThemeRotationState
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    /// <summary>当前（最近应用）的预设 id；轮换从这里继续向后走。</summary>
    public string CurrentPresetId { get; set; } = "";
    public long LastAppliedUtcMs { get; set; }
}

public sealed class ThemePresetDocument
{
    public int Schema { get; set; } = 1;
    public List<ThemePreset> Presets { get; set; } = new();
    public ThemeRotationState Rotation { get; set; } = new();
}

/// <summary>
/// 主题预设与轮换。数据保存在独立的 theme_presets.json，不进入 config.json。
/// 轮换定时器由 App 持有的 WinForms Timer 驱动，设置页/面板重渲染不会重建或丢失它。
/// </summary>
public sealed class ThemePresetManager : IDisposable
{
    const int MaxPresets = 50;
    const int MaxNameChars = 40;
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Settings _cfg;
    readonly string _filePath;
    readonly System.Windows.Forms.Timer _timer;
    ThemePresetDocument _doc;

    /// <summary>轮换或手动应用预设后触发（在 UI 线程）。</summary>
    public event Action<ThemePreset> Applied;

    public ThemePresetManager(Settings cfg, string filePath = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _filePath = filePath ?? Paths.ThemePresetFile;
        _doc = LoadDocument(_filePath);
        _timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timer.Tick += (s, e) => SafeRotate();
    }

    public IReadOnlyList<ThemePreset> Presets => _doc.Presets;
    public ThemeRotationState Rotation => _doc.Rotation;
    public bool RotationTimerRunning => _timer.Enabled;

    /// <summary>启动轮换定时器；面板关闭/重渲染都不会影响它。</summary>
    public void Start() => UpdateTimer();

    public ThemePreset SaveCurrent(string name, string id = "")
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = "主题预设";
        if (name.Length > MaxNameChars) name = name.Substring(0, MaxNameChars);

        ThemePreset preset = string.IsNullOrWhiteSpace(id)
            ? null
            : _doc.Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (preset == null)
        {
            if (_doc.Presets.Count >= MaxPresets) throw new InvalidOperationException($"最多保存 {MaxPresets} 个主题预设");
            preset = new ThemePreset { Id = Guid.NewGuid().ToString("N"), CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            _doc.Presets.Add(preset);
        }

        string oldImage = preset.BackgroundImage;
        string copied = ThemeBackgroundStore.CopyForPreset(_cfg.ThemeBackgroundImage);
        preset.Name = name;
        preset.AccentColor = ConfigStore.NormalizeThemeColor(_cfg.ThemeAccentColor, "#E7B77C");
        preset.FontColor = ConfigStore.NormalizeThemeColor(_cfg.ThemeFontColor, "#8E5E43");
        preset.Advanced = _cfg.ThemeAdvanced ?? "";
        preset.BarFollowStandard = _cfg.ThemeBarFollowStandard;
        preset.BarColor = ConfigStore.NormalizeOptionalThemeColor(_cfg.ThemeBarColor);
        preset.BarTextFollowTheme = _cfg.ThemeBarTextFollowTheme;
        preset.BarTextColor = ConfigStore.NormalizeOptionalThemeColor(_cfg.ThemeBarTextColor);
        preset.BackgroundImage = copied;
        preset.BackgroundOpacity = Math.Clamp(_cfg.ThemeBackgroundOpacity, 0, 1);
        preset.BackgroundBlur = Math.Clamp(_cfg.ThemeBackgroundBlur, 0, 24);
        if (preset.CreatedAtMs <= 0) preset.CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SaveDocument();
        UpdateTimer();
        if (!string.Equals(oldImage, copied, StringComparison.OrdinalIgnoreCase)) CleanupImage(oldImage);
        return preset;
    }

    public void Apply(string id) => ApplyInternal(Find(id), updateRotationState: true);

    public bool Delete(string id)
    {
        int index = _doc.Presets.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        var preset = _doc.Presets[index];
        string previousCurrent = _doc.Rotation.CurrentPresetId;
        bool previousEnabled = _doc.Rotation.Enabled;
        long previousLastApplied = _doc.Rotation.LastAppliedUtcMs;
        _doc.Presets.RemoveAt(index);
        if (string.Equals(_doc.Rotation.CurrentPresetId, preset.Id, StringComparison.Ordinal))
            _doc.Rotation.CurrentPresetId = "";
        // 删除最后一个预设后必须同时关闭轮换并持久化，否则下次新增预设会“自动复活”轮换。
        if (_doc.Presets.Count == 0)
        {
            _doc.Rotation.Enabled = false;
            _doc.Rotation.CurrentPresetId = "";
            _doc.Rotation.LastAppliedUtcMs = 0;
        }
        if (!SaveDocument())
        {
            // 写盘失败时回滚内存，避免 UI 显示已删除、重启后又复活。
            _doc.Presets.Insert(index, preset);
            _doc.Rotation.CurrentPresetId = previousCurrent;
            _doc.Rotation.Enabled = previousEnabled;
            _doc.Rotation.LastAppliedUtcMs = previousLastApplied;
            return false;
        }
        CleanupImage(preset.BackgroundImage);
        UpdateTimer();
        return true;
    }

    /// <summary>上移/下移预设；direction 为 -1 或 1。排序变化会按当前顺序持久化。</summary>
    public bool Move(string id, int direction)
    {
        int index = _doc.Presets.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (index < 0 || (direction != -1 && direction != 1)) return false;
        int target = index + direction;
        if (target < 0 || target >= _doc.Presets.Count) return false;
        var preset = _doc.Presets[index];
        _doc.Presets.RemoveAt(index);
        _doc.Presets.Insert(target, preset);
        // 排序失败时恢复原位置，避免内存顺序与磁盘不一致。
        if (!SaveDocument())
        {
            _doc.Presets.RemoveAt(target);
            _doc.Presets.Insert(index, preset);
            return false;
        }
        return true;
    }

    /// <summary>用户手动修改主题后清除“当前预设”标记；轮换仍按列表继续。</summary>
    public bool ClearCurrentPreset()
    {
        if (string.IsNullOrEmpty(_doc.Rotation.CurrentPresetId)) return false;
        _doc.Rotation.CurrentPresetId = "";
        return SaveDocument();
    }

    public ThemePreset Rename(string id, string name)
    {
        var preset = Find(id);
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("预设名称不能为空");
        if (name.Length > MaxNameChars) name = name.Substring(0, MaxNameChars);
        preset.Name = name;
        SaveDocument();
        return preset;
    }

    public ThemeRotationState SetRotation(bool enabled, int intervalMinutes)
    {
        // 没有任何预设时轮换没有意义，直接保持关闭，避免“空轮换”状态被持久化。
        if (enabled && _doc.Presets.Count == 0) enabled = false;
        bool wasEnabled = _doc.Rotation.Enabled;
        _doc.Rotation.Enabled = enabled;
        _doc.Rotation.IntervalMinutes = Math.Clamp(intervalMinutes <= 0 ? 30 : intervalMinutes, 1, 1440);
        if (enabled && !wasEnabled)
        {
            // 刚开启时从当前时刻开始计时，避免用户一按开关主题就立刻跳走。
            _doc.Rotation.LastAppliedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!_doc.Presets.Any(p => string.Equals(p.Id, _doc.Rotation.CurrentPresetId, StringComparison.Ordinal)))
                _doc.Rotation.CurrentPresetId = "";
        }
        SaveDocument();
        UpdateTimer();
        return _doc.Rotation;
    }

    /// <summary>供定时器和测试调用：到点则按顺序应用下一个预设。</summary>
    public ThemePreset CheckRotation(DateTime utcNow)
    {
        if (!_doc.Rotation.Enabled || _doc.Presets.Count == 0) return null;
        long now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long interval = Math.Clamp(_doc.Rotation.IntervalMinutes <= 0 ? 30 : _doc.Rotation.IntervalMinutes, 1, 1440) * 60_000L;
        long last = _doc.Rotation.LastAppliedUtcMs;
        if (last > 0 && now - last < interval) return null;
        if (last == 0)
        {
            // 兼容旧/手工编辑的 JSON：先记下起点，不立刻换主题。
            _doc.Rotation.LastAppliedUtcMs = now;
            SaveDocument();
            return null;
        }
        int index = _doc.Presets.FindIndex(p => string.Equals(p.Id, _doc.Rotation.CurrentPresetId, StringComparison.Ordinal));
        int next = index < 0 ? 0 : (index + 1) % _doc.Presets.Count;
        var preset = _doc.Presets[next];
        ApplyInternal(preset, updateRotationState: true, utcNow: utcNow);
        return preset;
    }

    /// <summary>清理不再被 config 或任何预设引用的图片。</summary>
    public void CleanupImage(string image)
    {
        image = (image ?? "").Trim();
        if (image.Length == 0) return;
        string name = Path.GetFileName(image);
        if (name.Length == 0) return;
        if (string.Equals(name, Path.GetFileName(_cfg.ThemeBackgroundImage ?? ""), StringComparison.OrdinalIgnoreCase)) return;
        if (_doc.Presets.Any(p => string.Equals(Path.GetFileName(p.BackgroundImage ?? ""), name, StringComparison.OrdinalIgnoreCase))) return;
        ThemeBackgroundStore.Delete(name);
    }

    public void CleanupOrphans()
    {
        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_cfg.ThemeBackgroundImage)) refs.Add(_cfg.ThemeBackgroundImage);
        refs.AddRange(_doc.Presets.Select(p => p.BackgroundImage));
        ThemeBackgroundStore.CleanupOrphans(refs);
    }

    /// <summary>当前所有引用的图片文件名（测试/诊断用）。</summary>
    public IEnumerable<string> ReferencedImages()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ThemeBackgroundImage)) yield return _cfg.ThemeBackgroundImage;
        foreach (var preset in _doc.Presets)
            if (!string.IsNullOrWhiteSpace(preset.BackgroundImage)) yield return preset.BackgroundImage;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    void SafeRotate()
    {
        try { CheckRotation(DateTime.UtcNow); }
        catch (Exception ex) { Log.Write("主题轮换失败 " + ex.Message); }
    }

    ThemePreset Find(string id)
    {
        var preset = _doc.Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (preset == null) throw new InvalidOperationException("主题预设不存在");
        return preset;
    }

    void ApplyInternal(ThemePreset preset, bool updateRotationState, DateTime? utcNow = null)
    {
        string oldImage = _cfg.ThemeBackgroundImage;
        _cfg.ThemeAccentColor = ConfigStore.NormalizeThemeColor(preset.AccentColor, "#E7B77C");
        _cfg.ThemeFontColor = ConfigStore.NormalizeThemeColor(preset.FontColor, "#8E5E43");
        _cfg.ThemeAdvanced = preset.Advanced ?? "";
        _cfg.ThemeBarFollowStandard = preset.BarFollowStandard;
        _cfg.ThemeBarColor = ConfigStore.NormalizeOptionalThemeColor(preset.BarColor);
        _cfg.ThemeBarTextFollowTheme = preset.BarTextFollowTheme;
        _cfg.ThemeBarTextColor = ConfigStore.NormalizeOptionalThemeColor(preset.BarTextColor);
        _cfg.ThemeBackgroundImage = ThemeBackgroundStore.Materialize(preset.BackgroundImage);
        _cfg.ThemeBackgroundOpacity = Math.Clamp(preset.BackgroundOpacity, 0, 1);
        _cfg.ThemeBackgroundBlur = Math.Clamp(preset.BackgroundBlur, 0, 24);
        ConfigStore.Save(_cfg);
        if (updateRotationState)
        {
            _doc.Rotation.CurrentPresetId = preset.Id;
            _doc.Rotation.LastAppliedUtcMs = utcNow.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(utcNow.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveDocument();
        }
        if (!string.Equals(oldImage, _cfg.ThemeBackgroundImage, StringComparison.OrdinalIgnoreCase))
            CleanupImage(oldImage);
        try { Applied?.Invoke(preset); } catch (Exception ex) { Log.Write("应用主题预设后刷新失败 " + ex.Message); }
    }

    void UpdateTimer()
    {
        if (_doc.Rotation.Enabled && _doc.Presets.Count > 0)
        {
            if (!_timer.Enabled) _timer.Start();
        }
        else _timer.Stop();
    }

    bool SaveDocument()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? Paths.Root);
            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_doc, Options));
            File.Move(tmp, _filePath, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("主题预设写入失败 " + ex.Message);
            return false;
        }
    }

    static ThemePresetDocument LoadDocument(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var doc = JsonSerializer.Deserialize<ThemePresetDocument>(File.ReadAllText(filePath), Options);
                if (doc != null) return Normalize(doc);
            }
        }
        catch (Exception ex) { Log.Write("主题预设读取失败 " + ex.Message); }
        return new ThemePresetDocument();
    }

    static ThemePresetDocument Normalize(ThemePresetDocument doc)
    {
        doc.Presets ??= new List<ThemePreset>();
        doc.Rotation ??= new ThemeRotationState();
        doc.Presets.RemoveAll(p => p == null);
        foreach (var preset in doc.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "主题预设";
            if (preset.Name.Length > MaxNameChars) preset.Name = preset.Name.Substring(0, MaxNameChars);
            preset.AccentColor = ConfigStore.NormalizeThemeColor(preset.AccentColor, "#E7B77C");
            preset.FontColor = ConfigStore.NormalizeThemeColor(preset.FontColor, "#8E5E43");
            preset.Advanced ??= "";
            preset.BarColor = ConfigStore.NormalizeOptionalThemeColor(preset.BarColor);
            preset.BarTextColor = ConfigStore.NormalizeOptionalThemeColor(preset.BarTextColor);
            preset.BackgroundImage = Path.GetFileName((preset.BackgroundImage ?? "").Trim());
            preset.BackgroundOpacity = Math.Clamp(preset.BackgroundOpacity, 0, 1);
            preset.BackgroundBlur = Math.Clamp(preset.BackgroundBlur, 0, 24);
        }
        if (doc.Presets.Count > MaxPresets)
            doc.Presets.RemoveRange(MaxPresets, doc.Presets.Count - MaxPresets);
        doc.Rotation.IntervalMinutes = Math.Clamp(doc.Rotation.IntervalMinutes <= 0 ? 30 : doc.Rotation.IntervalMinutes, 1, 1440);
        if (!doc.Presets.Any(p => string.Equals(p.Id, doc.Rotation.CurrentPresetId, StringComparison.Ordinal)))
            doc.Rotation.CurrentPresetId = "";
        return doc;
    }
}
