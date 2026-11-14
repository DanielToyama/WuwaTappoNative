# Wuwatappo Token 提取器

从**本机**读取登录 token，用于桌宠（Wuwatappo Native）的「设置 → 导入 token」。提供三个独立的一键入口：

| 入口 | 数据来源 | 说明 |
|---|---|---|
| **获取国服 Token** | 官方国服 WuwaTappo 客户端 | 读取 `%LOCALAPPDATA%\com.kuro.wuwatappo.cn\EBWebView` 下的 WebView2 登录数据，解密 `kuro_infoToken`。只认国服目录，不会静默回退到国际服。 |
| **获取国际服 Token** | 官方国际服 WuwaTappo 客户端 | 读取 `%LOCALAPPDATA%\com.kuro.wuwatappo.global\EBWebView` 下的 WebView2 登录数据，解密 `kuro_infoToken`。只认国际服目录，不会静默回退到国服。 |
| **提取 Native Token** | Wuwatappo Native 已保存配置 | 只读 `%LOCALAPPDATA%\WuwatappoNative\config.json` 的 `Token` 字段。不会启动、修改或关闭桌宠。 |

- 不再需要 Python、pip 或 pycryptodome。
- 单文件 exe，Windows 10/11 x64 双击即可运行。
- 只读取本机文件，**不联网、不上传**，不包含任何账号数据。
- 官方客户端的临时 cookie 副本在读取完成后自动删除。
- 主程序（设置 → 账号 → 继承官方程序登录状态）已经内置官方客户端提取功能；本独立工具适合单独使用或排查问题，核心解密逻辑与主程序共用 `src/Net/OfficialTokenReader.cs`。

> 这是你的登录凭据，请确保输入到任何地方的时候，都代表你信任此渠道。请不要在任何情况下公开展示此内容。如果已泄露，可以通过重新通过在官方/改版中使用手机号/游戏uid重新登陆来更新token。

---

## 使用前准备

### 获取国服 Token

1. 官方**国服** WuwaTappo 客户端已安装，并且**至少登录过一次**。
2. 使用**与官方客户端相同的 Windows 用户**运行本工具。
3. 建议先关闭国服客户端；如果它还在运行，工具会询问是否代你关闭。

### 获取国际服 Token

1. 官方**国际服** WuwaTappo 客户端已安装，并且**至少登录过一次**。
2. 使用**与官方客户端相同的 Windows 用户**运行本工具。
3. 建议先关闭国际服客户端；如果它还在运行，工具会询问是否代你关闭。

### 提取 Native Token

1. 先启动 Wuwatappo Native 并完成登录，让配置文件中保存 Token。
2. 本工具只读取配置；桌宠可以保持运行，无需退出。

## 一键使用

1. 解压后双击 `WuwatappoTokenExtractor.exe`。
2. 根据需要点击「获取国服 Token」「获取国际服 Token」或「提取 Native Token」。
3. 官方客户端入口如果提示客户端正在运行，选择「是」让工具关闭它（或先手动完全退出，包括托盘图标）。
4. 成功后窗口会显示 token，并弹出安全警告。**请先阅读并关闭警告，再点击「复制 Token」**。
5. 打开桌宠面板：**设置 → 导入 token → 粘贴 → 导入并登录**。

如果 Windows SmartScreen 提示未知发布者，可点击「更多信息 → 仍要运行」。本工具没有购买代码签名证书。

## 常见问题

| 现象 | 处理 |
|---|---|
| 未检测到国服客户端数据 | 先安装并登录国服客户端一次；确认存在 `%LOCALAPPDATA%\com.kuro.wuwatappo.cn\EBWebView`，且与工具使用同一个 Windows 用户 |
| 未检测到国际服客户端数据 | 先安装并登录国际服客户端一次；确认存在 `%LOCALAPPDATA%\com.kuro.wuwatappo.global\EBWebView`，且与工具使用同一个 Windows 用户 |
| 无法关闭国际服客户端 | 客户端可能以管理员身份运行。请手动退出客户端（包括托盘图标）后重试 |
| 找到 token 但解密失败 | 官方客户端数据与工具不在同一 Windows 用户下，或登录态已损坏；在登录客户端的同一用户下重新提取 |
| v20 / App-Bound Encryption | 新版客户端使用 v20 cookie，本工具无法解密；请改用桌宠的 **UID + 验证码登录** |
| 没有找到 token | 国际服客户端还没有完成登录；登录后重试 |
| Native 尚未登录或没有保存 Token | 先启动 Wuwatappo Native 并完成登录，再重新点击「提取 Native Token」 |
| 读取 Native 配置失败 | 文件可能正被占用。工具会自动重试；仍失败时请稍后再试，或确认 `%LOCALAPPDATA%\WuwatappoNative\config.json` 未被手动改坏 |
| 导入后提示 token 无效 | token 已过期或被客户端登出；重新登录后再提取，或使用 UID 验证码登录 |

## 安全说明

- **不要在任何情况下公开展示 token 或包含 token 的截图。**
- 本工具只把必要的官方客户端文件复制到 `%TEMP%\WuwatappoTokenExtractor-<随机>`，读取完立即删除；启动时会清理超过 1 小时的遗留副本。
- 日志只记录文件名、数量、区服和结果，**不会打印完整 token**；token 只显示在窗口的 Token 框中供手动复制。
- 不联网、不上传，不包含遥测。
- 不修改官方客户端目录；不启动、不修改 Wuwatappo Native 及其 `config.json`。
- 如果 token 已泄露，请尽快通过手机号 / 游戏 UID 等方式重新获取并更新 token。

## 从源码构建

仅编译：

```powershell
dotnet build token-extractor/WuwatappoTokenExtractor.csproj -c Release
```

发布单文件 exe 和 zip（等价于 `build.ps1` 的核心命令）：

```powershell
dotnet publish token-extractor/WuwatappoTokenExtractor.csproj -c Release `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -o publish/token-extractor
```

或直接运行：

```powershell
pwsh -File token-extractor/build.ps1
```

产物为 `dist/WuwatappoTokenExtractor-1.1.2-x64.zip`，其中包含单文件 exe 和本说明。

## 隔离自检

自检只会创建临时目录和假 token，不读取真实用户 token，也不启动官方客户端或桌宠：

```powershell
dotnet run --project tests/TokenExtractorChecks/TokenExtractorChecks.csproj -c Release
```

覆盖内容：临时 `config.json`（大小写不敏感、文件占用重试、空 token 提示）、临时 official data root（DPAPI + AES-GCM 假 cookie、区服判定、严格国际服定位、临时副本清理、日志不包含 token）。

## 实现摘要

1. 官方国际服客户端是 WebView2 应用，数据在 `%LOCALAPPDATA%\com.kuro.wuwatappo.global\EBWebView`。
2. `Local State` 中的 `os_crypt.encrypted_key` 由 Windows DPAPI 保护：Base64 → 去掉 `DPAPI` 前缀 → `CryptUnprotectData`。
3. `Default\Network\Cookies` 是 SQLite 数据库；`v10/v11` 为 AES-256-GCM：`nonce=[3:15]`、`ciphertext=[15:-16]`、`tag=[-16:]`。
4. 解密明文前 32 字节是域名哈希，去掉后取 cookie 值；只筛选名字含 `token` 的 cookie，优先 `kuro_infoToken`。
5. Native 入口直接读取 `%LOCALAPPDATA%\WuwatappoNative\config.json` 的 `Token`，使用 `FileShare.ReadWrite | FileShare.Delete` 和短重试兼容桌宠同时写入。

版本：1.1.2
