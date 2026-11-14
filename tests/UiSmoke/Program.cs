using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WuwatappoNative.Core;
using WuwatappoNative.Ui;

internal static class Program
{
    static readonly List<string> OffsetCommands = new();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: UiSmoke <wwwroot> <output.png>");
            return 2;
        }
        string wwwroot = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        string userData = Path.Combine(Path.GetTempPath(), "wuwa-webview-smoke-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(Path.GetTempPath(), "wuwa-webview-data-" + Guid.NewGuid().ToString("N"));
        string presetFile = Path.Combine(dataRoot, "theme_presets.json");

        // 隔离数据根目录：绝不读写用户真实的 %LOCALAPPDATA%\WuwatappoNative。
        Paths.Root = dataRoot;
        Paths.EnsureDirs();
        var cfg = new Settings { ThemeAccentColor = "#454C73", ThemeFontColor = "#3A3D51" };
        ConfigStore.Save(cfg);
        using var manager = new ThemePresetManager(cfg, presetFile);
        var first = manager.SaveCurrent("说的是");
        cfg.ThemeAccentColor = "#123456";
        ConfigStore.Save(cfg);
        var second = manager.SaveCurrent("是的是的是的");

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new Form
            {
                Text = "Wuwatappo UI smoke",
                Width = 1280,
                Height = 800,
                StartPosition = FormStartPosition.CenterScreen,
            };
            var web = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(web);
            form.Shown += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, userData);
                    await web.EnsureCoreWebView2Async(env);
                    var core = web.CoreWebView2;
                    core.SetVirtualHostNameToFolderMapping("wuwatappo.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

                    // 用真实 C# 映射产生的 camelCase JSON 作为页面初始主题预设。
                    string presetsJson = JsonSerializer.Serialize(PanelWindow.MapThemePresets(manager.Presets));
                    string rotationJson = JsonSerializer.Serialize(PanelWindow.MapThemeRotation(manager.Rotation));
                    string seed = "(() => { try { localStorage.setItem('wuwa.preview.themePresets.v1', " +
                                  JsonSerializer.Serialize(presetsJson) +
                                  "); localStorage.setItem('wuwa.preview.themeRotation.v1', " +
                                  JsonSerializer.Serialize(rotationJson) +
                                  "); } catch (e) { window.__uiSmokeSeedError = String(e); } })();";
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(seed);

                    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => loaded.TrySetResult(ev.IsSuccess);

                    JsonObject boot = null;
                    string lastDeleteId = null;
                    core.WebMessageReceived += (_, ev) =>
                    {
                        try
                        {
                            var msg = JsonNode.Parse(ev.WebMessageAsJson) as JsonObject;
                            if (msg == null) return;
                            int id = (int)(msg["id"]?.GetValue<long>() ?? 0);
                            string cmd = msg["cmd"]?.GetValue<string>() ?? "";
                            var data = msg["data"] as JsonObject ?? new JsonObject();
                            JsonNode result = HandleCommand(cmd, data, ref boot, manager, ref lastDeleteId);
                            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, data = result, error = (string)null }));
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("bridge: " + ex.Message);
                        }
                    };

                    core.Navigate("https://wuwatappo.local/index.html?preview=settings#page=settings");
                    if (!await loaded.Task) throw new InvalidOperationException("WebView2 navigation failed.");
                    await Task.Delay(900);

                    string bootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    boot = JsonNode.Parse(bootJson) as JsonObject ?? throw new InvalidOperationException("preview boot missing");
                    RefreshThemePresetBoot(boot, manager);
                    // 以真实 C# 序列化出的 boot 再推送一次，让设置页用这份数据重绘。
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(800);

                    // 底栏开关 + 快捷键单/双模式：只隐藏输入行，切回时原绑定仍在。
                    boot["settings"]!["hideMainBar"] = false;
                    boot["settings"]!["hideRemoteBars"] = false;
                    boot["settings"]!["separateBarHotkeys"] = false;
                    boot["settings"]!["hotkeys"] = new JsonObject
                    {
                        ["barToggle"] = "Ctrl+Alt+B",
                        ["barToggleMain"] = "Ctrl+Alt+M",
                        ["barToggleRemote"] = "Ctrl+Alt+R",
                    };
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string singleBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const ids = [...document.querySelectorAll('[data-hotkey]')].map(el => el.dataset.hotkey);
                        const values = Object.fromEntries([...document.querySelectorAll('[data-hotkey]')].map(el => [el.dataset.hotkey, el.value]));
                        return JSON.stringify({
                          hideMainBar: !!document.querySelector(""[data-switch='hideMainBar']""),
                          hideRemoteBars: !!document.querySelector(""[data-switch='hideRemoteBars']""),
                          separateSwitch: !!document.querySelector(""[data-switch='separateBarHotkeys']""),
                          barIds: ids.filter(id => id.startsWith('barToggle')),
                          value: values.barToggle || ''
                        });
                      })()");
                    var singleBarState = JsonNode.Parse(JsonSerializer.Deserialize<string>(singleBarUi))!.AsObject();
                    if (!singleBarState["hideMainBar"]!.GetValue<bool>() ||
                        !singleBarState["hideRemoteBars"]!.GetValue<bool>() ||
                        !singleBarState["separateSwitch"]!.GetValue<bool>())
                        throw new InvalidOperationException("Bar visibility / separate-hotkey switches were not rendered.");
                    var singleIds = singleBarState["barIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (singleIds.Length != 1 || singleIds[0] != "barToggle")
                        throw new InvalidOperationException("Single mode must show exactly the barToggle row: " + string.Join(",", singleIds));
                    if (singleBarState["value"]!.GetValue<string>() != "Ctrl+Alt+B")
                        throw new InvalidOperationException("Single-mode bar hotkey input lost its binding.");

                    boot["settings"]!["separateBarHotkeys"] = true;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string separateBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('[data-hotkey]')]
                          .filter(el => el.dataset.hotkey.startsWith('barToggle'))
                          .map(el => ({ id: el.dataset.hotkey, value: el.value }));
                        return JSON.stringify(rows);
                      })()");
                    var separateRows = JsonNode.Parse(JsonSerializer.Deserialize<string>(separateBarUi))!.AsArray();
                    if (separateRows.Count != 2 ||
                        separateRows[0]!["id"]!.GetValue<string>() != "barToggleMain" ||
                        separateRows[1]!["id"]!.GetValue<string>() != "barToggleRemote")
                        throw new InvalidOperationException("Separate mode must show exactly barToggleMain/barToggleRemote.");
                    if (separateRows[0]!["value"]!.GetValue<string>() != "Ctrl+Alt+M" ||
                        separateRows[1]!["value"]!.GetValue<string>() != "Ctrl+Alt+R")
                        throw new InvalidOperationException("Separate-mode bar hotkey inputs lost their bindings.");

                    boot["settings"]!["separateBarHotkeys"] = false;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string restoredBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('[data-hotkey]')]
                          .filter(el => el.dataset.hotkey.startsWith('barToggle'))
                          .map(el => ({ id: el.dataset.hotkey, value: el.value }));
                        return JSON.stringify(rows);
                      })()");
                    var restoredRows = JsonNode.Parse(JsonSerializer.Deserialize<string>(restoredBarUi))!.AsArray();
                    if (restoredRows.Count != 1 || restoredRows[0]!["id"]!.GetValue<string>() != "barToggle" ||
                        restoredRows[0]!["value"]!.GetValue<string>() != "Ctrl+Alt+B")
                        throw new InvalidOperationException("Switching back to single mode must keep the original barToggle binding.");

                    string initial = await core.ExecuteScriptAsync(@"
                      (() => {
                        const group = document.getElementById('settings-group-theme');
                        if (group) group.open = true;
                        const section = document.getElementById('themeSection');
                        if (section) section.scrollIntoView({ block: 'start' });
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({
                          seedError: window.__uiSmokeSeedError || '',
                          lsPresets: (localStorage.getItem('wuwa.preview.themePresets.v1') || '').length,
                          lsRotation: (localStorage.getItem('wuwa.preview.themeRotation.v1') || '').length,
                          rows: rows.length,
                          names: rows.map(r => r.querySelector('.theme-preset-name')?.textContent || ''),
                          deleteIds: rows.map(r => r.querySelector('[data-theme-preset-delete]')?.dataset.themePresetDelete || ''),
                          applyIds: rows.map(r => r.querySelector('[data-theme-preset-apply]')?.dataset.themePresetApply || '')
                        });
                      })()");
                    Console.WriteLine("initial=" + initial);
                    var initialState = JsonNode.Parse(JsonSerializer.Deserialize<string>(initial))!.AsObject();
                    if (initialState["seedError"]!.GetValue<string>().Length > 0)
                        throw new InvalidOperationException("Preview seed failed: " + initialState["seedError"]);
                    if (initialState["rows"]!.GetValue<int>() != 2)
                        throw new InvalidOperationException("Expected 2 seeded theme presets, got " + initialState["rows"]);
                    var names = initialState["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                    if (names[0] != "说的是" || names[1] != "是的是的是的")
                        throw new InvalidOperationException("Preset names rendered incorrectly: " + string.Join(" | ", names));
                    var deleteIds = initialState["deleteIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                    if (deleteIds.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidOperationException("Delete buttons must carry a non-empty preset id.");
                    if (!deleteIds.Contains(first.Id) || !deleteIds.Contains(second.Id))
                        throw new InvalidOperationException("Delete button ids do not match the real preset ids.");

                    // 重命名：覆盖 window.prompt，验证 rename 命令返回后 DOM 立即更新。
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        window.prompt = () => '重命名后的预设';
                        document.querySelector('[data-theme-preset-rename]').click();
                      })()");
                    await Task.Delay(900);
                    string renamed = await core.ExecuteScriptAsync("document.querySelector('.theme-preset-name')?.textContent || ''");
                    string renamedText = JsonSerializer.Deserialize<string>(renamed);
                    if (renamedText != "重命名后的预设")
                        throw new InvalidOperationException("Rename did not update the preset list: " + renamedText);
                    if (manager.Presets.First(p => p.Id == first.Id).Name != "重命名后的预设")
                        throw new InvalidOperationException("Rename did not reach the real ThemePresetManager.");

                    // 应用第二个预设：验证 apply 命令 id 与轮换当前项。
                    await core.ExecuteScriptAsync("document.querySelectorAll('[data-theme-preset-apply]')[1].click()");
                    await Task.Delay(900);
                    string applied = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({ active: rows.map(r => r.classList.contains('active')) });
                      })()");
                    var appliedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(applied))!.AsObject();
                    var active = appliedState["active"]!.AsArray().Select(x => x!.GetValue<bool>()).ToArray();
                    if (!active[1])
                        throw new InvalidOperationException("Applying the second preset did not mark it active.");
                    if (manager.Rotation.CurrentPresetId != second.Id)
                        throw new InvalidOperationException("Apply did not update the real rotation currentPresetId.");

                    // 轮换开关：验证 rotation.set 字段。
                    await core.ExecuteScriptAsync("document.getElementById('themeRotationToggle').click()");
                    await Task.Delay(700);
                    if (!manager.Rotation.Enabled)
                        throw new InvalidOperationException("Rotation toggle did not reach the real ThemePresetManager.");

                    // 删除第一项：点击后应立即从 DOM 消失，后端文件同步删除。
                    await core.ExecuteScriptAsync("document.querySelectorAll('.theme-preset-row')[0].querySelector('[data-theme-preset-delete]').click()");
                    await Task.Delay(1400);
                    string afterDelete = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({
                          rows: rows.length,
                          names: rows.map(r => r.querySelector('.theme-preset-name')?.textContent || '')
                        });
                      })()");
                    Console.WriteLine("afterDelete=" + afterDelete);
                    var deleteState = JsonNode.Parse(JsonSerializer.Deserialize<string>(afterDelete))!.AsObject();
                    if (deleteState["rows"]!.GetValue<int>() != 1)
                        throw new InvalidOperationException("Deleted preset still visible after click.");
                    string remainingName = deleteState["names"]![0]!.GetValue<string>();
                    if (remainingName != "是的是的是的")
                        throw new InvalidOperationException("Wrong preset remained after delete: " + remainingName);
                    if (lastDeleteId != first.Id)
                        throw new InvalidOperationException("Delete command carried the wrong id: " + (lastDeleteId ?? "<null>"));
                    string savedJson = File.ReadAllText(presetFile);
                    if (savedJson.Contains(first.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("Deleted preset id still present in theme_presets.json.");
                    if (!savedJson.Contains(second.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("Unrelated preset disappeared from theme_presets.json.");

                    using (var restarted = new ThemePresetManager(cfg, presetFile))
                    {
                        if (restarted.Presets.Count != 1 || restarted.Presets[0].Id != second.Id)
                            throw new InvalidOperationException("Deleted preset reappeared after manager restart.");
                    }

                    // 删除最后一项：列表应显示空状态，文件中的 Presets 应为空数组。
                    await core.ExecuteScriptAsync("document.querySelector('.theme-preset-row [data-theme-preset-delete]').click()");
                    await Task.Delay(1300);
                    string emptyState = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        rows: document.querySelectorAll('.theme-preset-row').length,
                        text: document.getElementById('themePresetList')?.textContent || ''
                      }))()");
                    var empty = JsonNode.Parse(JsonSerializer.Deserialize<string>(emptyState))!.AsObject();
                    if (empty["rows"]!.GetValue<int>() != 0)
                        throw new InvalidOperationException("Theme preset list is not empty after deleting the last preset.");
                    if (!empty["text"]!.GetValue<string>().Contains("还没有保存的主题预设", StringComparison.Ordinal))
                        throw new InvalidOperationException("Empty theme preset state was not rendered.");
                    if (lastDeleteId != second.Id)
                        throw new InvalidOperationException("Second delete command carried the wrong id: " + (lastDeleteId ?? "<null>"));
                    if (manager.Presets.Count != 0 || manager.Rotation.CurrentPresetId != "")
                        throw new InvalidOperationException("Deleting the last preset did not clear the real manager state.");
                    using (var fileDoc = JsonDocument.Parse(File.ReadAllText(presetFile)))
                    {
                        if (fileDoc.RootElement.GetProperty("Presets").GetArrayLength() != 0)
                            throw new InvalidOperationException("theme_presets.json still contains presets after deleting all.");
                    }
                    using (var restartedEmpty = new ThemePresetManager(cfg, presetFile))
                    {
                        if (restartedEmpty.Presets.Count != 0)
                            throw new InvalidOperationException("Deleted presets reappeared after restarting with an empty file.");
                    }

                    // 头饰微调：缩放/旋转保存、方向按钮保留 transform、归零同时复位四项并清掉脏值恢复。
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        const group = document.getElementById('settings-group-window');
                        if (group) group.open = true;
                        const set = (id, value) => {
                          const el = document.getElementById(id);
                          if (!el) return;
                          el.value = String(value);
                          el.dispatchEvent(new Event('input', { bubbles: true }));
                        };
                        set('accOffsetX', 0);
                        set('accOffsetY', 0);
                        set('accScale', 150);
                        set('accRotation', 30);
                        document.getElementById('settings-group-window')?.scrollIntoView({ block: 'start' });
                      })()");
                    await Task.Delay(500);
                    string accInitial = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        const text = id => document.getElementById(id)?.textContent ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation'),
                          scaleText: text('accScaleValue'), rotationText: text('accRotationValue'),
                          chip: text('accessoryCoords'),
                          resetDisabled: !!document.getElementById('resetAccessoryOffset')?.disabled
                        });
                      })()");
                    var accInitialState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accInitial))!.AsObject();
                    if (accInitialState["scale"]!.GetValue<string>() != "150" ||
                        accInitialState["rotation"]!.GetValue<string>() != "30")
                        throw new InvalidOperationException("Accessory scale/rotation sliders did not keep the saved values: " + accInitial);
                    if (accInitialState["scaleText"]!.GetValue<string>() != "150%" ||
                        accInitialState["rotationText"]!.GetValue<string>() != "30°")
                        throw new InvalidOperationException("Accessory scale/rotation outputs are out of sync: " + accInitial);
                    if (accInitialState["resetDisabled"]!.GetValue<bool>())
                        throw new InvalidOperationException("Reset must be enabled when only scale/rotation differ from defaults.");

                    // 方向按钮只改 X/Y，缩放与旋转必须原样保留。
                    await core.ExecuteScriptAsync("document.querySelector('[data-nudge=\"0,-1\"]')?.click()");
                    await Task.Delay(500);
                    string accNudged = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation')
                        });
                      })()");
                    var accNudgedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accNudged))!.AsObject();
                    if (accNudgedState["x"]!.GetValue<string>() != "0" ||
                        accNudgedState["y"]!.GetValue<string>() != "-1" ||
                        accNudgedState["scale"]!.GetValue<string>() != "150" ||
                        accNudgedState["rotation"]!.GetValue<string>() != "30")
                        throw new InvalidOperationException("Direction nudge must only change X/Y: " + accNudged);

                    // 制造一个未提交的脏滑条值并立即点归零；节流定时器必须被取消，旧值不能再写回。
                    OffsetCommands.Clear();
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        const el = document.getElementById('accOffsetX');
                        el.value = '77';
                        el.dispatchEvent(new Event('input', { bubbles: true }));
                        document.getElementById('resetAccessoryOffset')?.click();
                      })()");
                    await Task.Delay(700);
                    string accReset = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        const text = id => document.getElementById(id)?.textContent ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation'),
                          xText: text('accOffsetXValue'), yText: text('accOffsetYValue'),
                          scaleText: text('accScaleValue'), rotationText: text('accRotationValue'),
                          chip: text('accessoryCoords'),
                          scaleMin: document.getElementById('accScale')?.min ?? '',
                          scaleMax: document.getElementById('accScale')?.max ?? '',
                          resetDisabled: !!document.getElementById('resetAccessoryOffset')?.disabled
                        });
                      })()");
                    var accResetState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accReset))!.AsObject();
                    if (accResetState["x"]!.GetValue<string>() != "0" || accResetState["y"]!.GetValue<string>() != "0" ||
                        accResetState["scale"]!.GetValue<string>() != "100" || accResetState["rotation"]!.GetValue<string>() != "0")
                        throw new InvalidOperationException("Reset did not restore all four accessory sliders: " + accReset);
                    if (accResetState["xText"]!.GetValue<string>() != "0" || accResetState["yText"]!.GetValue<string>() != "0" ||
                        accResetState["scaleText"]!.GetValue<string>() != "100%" || accResetState["rotationText"]!.GetValue<string>() != "0°")
                        throw new InvalidOperationException("Reset did not synchronize accessory numeric outputs: " + accReset);
                    if (accResetState["scaleMin"]!.GetValue<string>() != "25" || accResetState["scaleMax"]!.GetValue<string>() != "175")
                        throw new InvalidOperationException("Scale slider must use the symmetric 25–175 range: " + accReset);
                    if ((int.Parse(accResetState["scale"]!.GetValue<string>()) - 25) * 2 != 175 - 25)
                        throw new InvalidOperationException("Reset scale thumb must sit exactly at the slider center: " + accReset);
                    if (!accResetState["chip"]!.GetValue<string>().Contains("100%") ||
                        !accResetState["chip"]!.GetValue<string>().Contains("0°"))
                        throw new InvalidOperationException("Reset did not synchronize the accessory summary chip: " + accReset);
                    if (!accResetState["resetDisabled"]!.GetValue<bool>())
                        throw new InvalidOperationException("Reset button must be disabled after all values return to defaults.");
                    if (OffsetCommands.Any(c => c.Contains("77", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Reset left a queued old slider write that reached the native bridge: " + string.Join(",", OffsetCommands));

                    // 强制更新风险选项：forced 预览应提供“自担风险继续联网”入口，
                    // 且只有在 boot.settings 记录当前官方版本后才显示为已允许。
                    var forcedLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => forcedLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=forced#page=settings");
                    if (!await forcedLoaded.Task) throw new InvalidOperationException("forced preview navigation failed.");
                    await Task.Delay(800);
                    string forcedBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var forcedBoot = JsonNode.Parse(forcedBootJson) as JsonObject
                        ?? throw new InvalidOperationException("forced preview boot missing");
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = forcedBoot }));
                    await Task.Delay(700);
                    string forcedUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.official-notice-title')?.textContent || '',
                        allow: document.querySelector('[data-official-allow]')?.textContent || '',
                        setting: document.getElementById('forcedOnlineSettingBtn')?.textContent || ''
                      }))()");
                    var forcedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(forcedUi))!.AsObject();
                    if (!forcedState["notice"]!.GetValue<string>().Contains("强制", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update notice was not rendered.");
                    if (forcedState["allow"]!.GetValue<string>() != "仍要联网（自担风险）")
                        throw new InvalidOperationException("Forced update notice is missing the risk opt-in button.");
                    if (!forcedState["setting"]!.GetValue<string>().Contains("仍要联网", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update setting entry was not rendered.");

                    await core.ExecuteScriptAsync("document.querySelector('[data-official-allow]').click()");
                    await Task.Delay(300);
                    string riskModal = await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''");
                    string riskModalText = JsonSerializer.Deserialize<string>(riskModal) ?? "";
                    if (!riskModalText.Contains("强制更新后继续联网", StringComparison.Ordinal)
                        || !riskModalText.Contains("我已了解", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update risk confirmation modal was not rendered.");
                    await core.ExecuteScriptAsync("document.getElementById('forcedOnlineCancel')?.click()");
                    await Task.Delay(200);

                    forcedBoot["settings"]!["forcedOnlineAcceptedVersion"] = "1.0.5";
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = forcedBoot }));
                    await Task.Delay(600);
                    string acceptedUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.official-notice-title')?.textContent || '',
                        setting: document.getElementById('forcedOnlineSettingBtn')?.textContent || '',
                        blocked: (document.getElementById('pageHost')?.textContent || '').includes('已暂停联网操作')
                      }))()");
                    var acceptedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(acceptedUi))!.AsObject();
                    if (!acceptedState["notice"]!.GetValue<string>().Contains("已允许", StringComparison.Ordinal))
                        throw new InvalidOperationException("Accepted forced-update state was not rendered.");
                    if (!acceptedState["setting"]!.GetValue<string>().Contains("撤销风险授权", StringComparison.Ordinal))
                        throw new InvalidOperationException("Accepted forced-update setting entry did not switch to revoke mode.");
                    if (acceptedState["blocked"]!.GetValue<bool>())
                        throw new InvalidOperationException("Accepted forced-update state still shows the network-blocked banner.");

                    // 高级合成：每次输入隐藏指令都应重新显示首次警告；确认后可打开高级面板。
                    var synthLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => synthLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=synth#page=synth");
                    if (!await synthLoaded.Task) throw new InvalidOperationException("synth preview navigation failed.");
                    await Task.Delay(800);
                    string synthBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var synthBoot = JsonNode.Parse(synthBootJson) as JsonObject
                        ?? throw new InvalidOperationException("synth preview boot missing");
                    synthBoot["settings"]!["anyItemSynthesisUnlocked"] = false;
                    synthBoot["settings"]!["anyItemSynthesisEnabled"] = false;
                    boot = synthBoot;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = synthBoot }));
                    await Task.Delay(700);

                    const string konamiScript = @"
                      (() => {
                        const keys = ['ArrowUp','ArrowUp','ArrowDown','ArrowDown','ArrowLeft','ArrowRight','ArrowLeft','ArrowRight','b','a','b','a'];
                        for (const key of keys) document.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
                        return true;
                      })()";
                    await core.ExecuteScriptAsync(konamiScript);
                    await Task.Delay(500);
                    string firstWarning = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''")) ?? "";
                    if (!firstWarning.Contains("高级合成警告", StringComparison.Ordinal)
                        || !firstWarning.Contains("我已知晓并解锁", StringComparison.Ordinal))
                        throw new InvalidOperationException("Advanced synth command did not show the first warning.");
                    await core.ExecuteScriptAsync("document.getElementById('synthUnlockOk')?.click()");
                    await Task.Delay(800);
                    string advancedPanel = await core.ExecuteScriptAsync("String(!!document.querySelector('.synth-advanced-panel'))");
                    if (JsonSerializer.Deserialize<string>(advancedPanel) != "true")
                        throw new InvalidOperationException("Advanced synth panel did not open after confirmation.");

                    await core.ExecuteScriptAsync(konamiScript);
                    await Task.Delay(500);
                    string secondWarning = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''")) ?? "";
                    if (!secondWarning.Contains("高级合成警告", StringComparison.Ordinal))
                        throw new InvalidOperationException("Advanced synth command did not warn again on the second activation.");
                    await core.ExecuteScriptAsync("document.getElementById('synthUnlockCancel')?.click()");
                    await Task.Delay(200);

                    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                    using (var stream = File.Create(output))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                    Console.WriteLine("PASS: WebView2 bar switches render and single/separate hotkey rows filter without losing bindings.");
                    Console.WriteLine("PASS: WebView2 theme preset list/apply/rename/delete uses real C# data and persists deletes.");
                    Console.WriteLine("PASS: WebView2 accessory scale/rotation, nudge and reset keep native boot and sliders in sync.");
                    Console.WriteLine("PASS: WebView2 forced-update risk opt-in notice, modal and accepted state render correctly.");
                    Console.WriteLine("PASS: WebView2 advanced-synth command warns on every activation.");
                    Console.WriteLine("screenshot=" + output);
                    form.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    form.Close();
                }
            };
            Application.Run(form);
            return File.Exists(output) ? 0 : 1;
        }
        finally
        {
            try { Paths.Root = null; } catch { }
            try { Directory.Delete(userData, true); } catch { }
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    static JsonNode HandleCommand(string cmd, JsonObject data, ref JsonObject boot, ThemePresetManager manager, ref string lastDeleteId)
    {
        if (boot == null) return null;
        if (cmd == "boot") return boot;
        var settings = boot["settings"] as JsonObject ?? new JsonObject();
        switch (cmd)
        {
            case "settings.set":
                foreach (var kv in data)
                    settings[kv.Key] = kv.Value?.DeepClone();
                return boot;
            case "theme.presets.save":
            {
                var preset = manager.SaveCurrent(
                    data["name"]?.GetValue<string>() ?? "",
                    data["id"]?.GetValue<string>() ?? "");
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["preset"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePreset(preset)) };
            }
            case "theme.presets.apply":
            {
                string id = data["id"]?.GetValue<string>() ?? "";
                manager.Apply(id);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = true };
            }
            case "theme.presets.delete":
            {
                lastDeleteId = data["id"]?.GetValue<string>() ?? "";
                bool removed = manager.Delete(lastDeleteId);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = removed };
            }
            case "theme.presets.rename":
            {
                var preset = manager.Rename(
                    data["id"]?.GetValue<string>() ?? "",
                    data["name"]?.GetValue<string>() ?? "");
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["preset"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePreset(preset)) };
            }
            case "theme.presets.move":
            {
                string id = data["id"]?.GetValue<string>() ?? "";
                int direction = data["direction"]?.GetValue<int>() ?? 0;
                bool moved = manager.Move(id, direction);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = moved, ["id"] = id, ["direction"] = direction };
            }
            case "theme.rotation.set":
            {
                var rotation = manager.SetRotation(
                    data["enabled"]?.GetValue<bool>() ?? false,
                    data["intervalMinutes"]?.GetValue<int>() ?? 30);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["rotation"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemeRotation(rotation)) };
            }
            case "appearance.offset":
            {
                // 模拟原生 PetWindow 的完整 transform 协议，供设置页归零/方向按钮回归测试。
                var appearance = boot["appearance"] as JsonObject ?? new JsonObject();
                int x = appearance["accessoryOffsetX"]?.GetValue<int>() ?? 0;
                int y = appearance["accessoryOffsetY"]?.GetValue<int>() ?? 0;
                int scale = appearance["accessoryScalePercent"]?.GetValue<int>() ?? 100;
                int rotation = appearance["accessoryRotationDegrees"]?.GetValue<int>() ?? 0;
                bool reset = data["reset"]?.GetValue<bool>() ?? false;
                if (reset)
                {
                    x = 0; y = 0; scale = 100; rotation = 0;
                    OffsetCommands.Add("reset");
                }
                else if (data.ContainsKey("dx") || data.ContainsKey("dy"))
                {
                    x += data["dx"]?.GetValue<int>() ?? 0;
                    y += data["dy"]?.GetValue<int>() ?? 0;
                    OffsetCommands.Add($"nudge:{x},{y},{scale},{rotation}");
                }
                else
                {
                    if (data.ContainsKey("x")) x = data["x"]?.GetValue<int>() ?? x;
                    if (data.ContainsKey("y")) y = data["y"]?.GetValue<int>() ?? y;
                    if (data.ContainsKey("scale")) scale = data["scale"]?.GetValue<int>() ?? scale;
                    if (data.ContainsKey("rotation")) rotation = data["rotation"]?.GetValue<int>() ?? rotation;
                    OffsetCommands.Add($"absolute:{x},{y},{scale},{rotation}");
                }
                x = Math.Clamp(x, -160, 160);
                y = Math.Clamp(y, -160, 160);
                scale = Math.Clamp(scale <= 0 ? 100 : scale, 25, 175);
                rotation = Math.Clamp(rotation, -180, 180);
                appearance["accessoryOffsetX"] = x;
                appearance["accessoryOffsetY"] = y;
                appearance["accessoryScalePercent"] = scale;
                appearance["accessoryRotationDegrees"] = rotation;
                long accessoryId = appearance["accessoryAssetId"]?.GetValue<long>() ?? 0;
                if (accessoryId > 0)
                {
                    var offsets = appearance["accessoryOffsets"] as JsonObject ?? new JsonObject();
                    offsets[accessoryId.ToString()] = new JsonArray(x, y, scale, rotation);
                    appearance["accessoryOffsets"] = offsets;
                }
                return new JsonObject
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["scale"] = scale,
                    ["rotation"] = rotation,
                    ["accessoryId"] = accessoryId,
                    ["boot"] = boot.DeepClone(),
                };
            }
            default:
                return null;
        }
    }

    static void RefreshThemePresetBoot(JsonObject boot, ThemePresetManager manager)
    {
        if (boot == null) return;
        boot["themePresets"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePresets(manager.Presets));
        boot["themeRotation"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemeRotation(manager.Rotation));
    }
}
