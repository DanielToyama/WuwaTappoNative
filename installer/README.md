# 打包说明（v0.0.2）

## 产物

- `dist/WuwatappoNative-0.0.2-x64.msi`：Per-User 安装包（无需管理员），安装到
  `%LOCALAPPDATA%\Programs\Wuwatappo Native`，创建开始菜单与桌面快捷方式，版本 0.0.2。
- `dist/WuwatappoNative-0.0.2-portable.zip`：解压即用，运行 `WuwatappoNative.exe`。

## 依赖

- Windows 10/11 x64
- WebView2 Runtime（Win10/11 一般已内置；缺失时面板无法显示）
- 自包含发布，不要求安装 .NET 8 Runtime

## 重新打包

```powershell
cd tappoNativeGit
# 首次需要 WiX（已放在 .wix/）
dotnet tool install --tool-path .wix wix --version 5.0.2
pwsh -File installer/build.ps1 -Version 0.0.2
```

脚本会：publish 自包含 → 生成 WiX 源（280 个组件）→ 编译 MSI → 打便携 ZIP。
MSI 为 Per-User 安装，升级由 `MajorUpgrade` 处理。

## 已做的验证

- MSI 静默安装退出码 0，安装目录包含 `assets/default`、`assets/ui`、`data`、`wwwroot` 完整子目录。
- 安装后的 exe `FileVersion=0.0.2.0`、`ProductVersion=0.0.2`。
- 开始菜单与桌面快捷方式均生成。
- 安装版可直接启动并加载本地资源。

## 注意

- `assets/`、`data/appearance_asset.json` 来自本机官方客户端，仅限本机使用，禁止再分发；仓库不包含这些文件，打包前需自行准备。
- 安装/卸载只影响 `%LOCALAPPDATA%`，不会删除 `%LOCALAPPDATA%\WuwatappoNative` 下的配置、日志与缓存。
