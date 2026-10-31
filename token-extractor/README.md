# Wuwatappo Token 提取器

从**本机官方 WuwaTappo 客户端**提取登录 token（通常是 `kuro_infoToken`），用于桌宠（Wuwatappo Native）的「设置 → 导入 token」。

- 不再需要 Python、pip 或 pycryptodome。
- 单文件 exe，Windows 10/11 x64 双击即可运行。
- 只读取本机文件，不联网、不上传，不包含任何账号数据。
- 临时 cookie 副本在读取完成后自动删除。
- 主程序（设置 → 账号 → 继承官方程序登录状态）已经内置相同功能；本独立工具适合单独使用或排查问题，核心解密逻辑与主程序共用 `src/Net/OfficialTokenReader.cs`。

> token 等同于账号访问权。请勿截图、发送给他人或上传到任何地方。

---

## 使用前准备

1. 官方 WuwaTappo 客户端已安装，并且**至少登录过一次**。
2. 使用**与官方客户端相同的 Windows 用户**运行本工具。
3. 建议先关闭官方客户端；如果它还在运行，工具会询问是否代你关闭。

## 一键使用

1. 解压后双击 `WuwatappoTokenExtractor.exe`。
2. 点击「开始提取」。
3. 如果提示官方客户端正在运行，选择「是」让工具关闭它（或先手动完全退出，包括托盘图标）。
4. 成功后 token 会显示在窗口中，点击「复制 Token」。
5. 打开桌宠面板：**设置 → 导入 token → 粘贴 → 导入并登录**。

如果 Windows SmartScreen 提示未知发布者，可点击「更多信息 → 仍要运行」。本工具没有购买代码签名证书。

## 常见问题

| 现象 | 处理 |
|---|---|
| 没有找到官方客户端数据 | 先启动并登录官方客户端一次；确认与工具使用同一个 Windows 用户 |
| 无法关闭官方客户端 | 客户端可能以管理员身份运行。请手动退出客户端（包括托盘图标）后重试 |
| 找到 token 但解密失败 | 官方客户端数据与工具不在同一 Windows 用户下，或登录态已损坏；在登录客户端的同一用户下重新提取 |
| v20 / App-Bound Encryption | 新版客户端使用 v20 cookie，本工具无法解密；请改用桌宠的 **UID + 验证码登录** |
| 没有找到 token | 官方客户端还没有完成登录；登录后重试 |
| 导入后提示 token 无效 | token 已过期或被客户端登出；重新登录官方客户端后再提取，或使用 UID 验证码登录 |

## 安全说明

- 工具只复制必要文件到 `%TEMP%\WuwatappoTokenExtractor-<随机>`，读取完立即删除。
- 不会关闭或修改桌宠程序，也不会写入官方客户端目录。
- 不要保留带有 token 的截图或日志；本工具的日志不会打印完整 token。

## 从源码构建

```powershell
dotnet publish token-extractor/WuwatappoTokenExtractor.csproj -c Release `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -o publish/token-extractor
```

也可以运行：

```powershell
pwsh -File token-extractor/build.ps1
```

产物为 `dist/WuwatappoTokenExtractor-1.0.0-x64.zip`，其中包含单文件 exe 和本说明。

## 实现摘要

1. 官方客户端是 WebView2 应用，数据在 `%LOCALAPPDATA%\com.kuro.wuwatappo.cn\EBWebView`。
2. `Local State` 中的 `os_crypt.encrypted_key` 由 Windows DPAPI 保护：Base64 → 去掉 `DPAPI` 前缀 → `CryptUnprotectData`。
3. `Default\Network\Cookies` 是 SQLite 数据库；`v10/v11` 为 AES-256-GCM：`nonce=[3:15]`、`ciphertext=[15:-16]`、`tag=[-16:]`。
4. 解密明文前 32 字节是域名哈希，去掉后取 cookie 值。
5. 只筛选名字含 `token` 的 cookie；优先展示 `kuro_infoToken`。

版本：1.0.0
