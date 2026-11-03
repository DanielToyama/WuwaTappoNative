# Wuwatappo Native

Wuwatappo Native 是 WuwaTappo 1.0.2 的 Windows 原生实现，使用 C# / .NET 8、WinForms 和 WebView2 编写，连接库洛官方桌宠服务；本项目不是库洛官方项目。

## 功能说明省流

**已实现功能：**

- ✅ 支持原版全部在线内容与联机
- ✅ 支持国服与国际服（Global）切换与官方登录继承
- ✅ 支持国服手机号 + 短信验证码登录（库洛通行证）
- ✅ 支持自定义所有皮肤（他人房间中仅显示你实际拥有的皮肤，但本地可设置为任意皮肤）
- ✅ 支持发送任意表情（无视拥有状态、无视角色限制，他人可见）
- ✅ 支持本地微调头饰位置
- ✅ 支持刷 Key 数量（一键 +10000）
- ✅ 支持自动领取宝箱
- ✅ 支持自动表情、自动敲击和游戏模式规则
- ✅ 支持快捷键
- ✅ 支持显示当前键盘/手柄键位
- ✅ 音游模式支持 4K / 5K / 6K，每个模式分别保存键位；可设置空格和 ; ' , . / 等标点键
- ✅ 支持一键继承官方登录状态
- ✅ 支持主播模式 OBS 绿幕窗口采集
- ✅ 支持联机角色自动避让、横向/纵向排列与重置排序

**细节优化：**

- 内存优化，同样场景（12人同屏）运行内存官方版本占用650mb，本版本占用80m
- 多人联机保存每一个人的位置，对方掉线，重进，你重启软件，所有的人都保持原位
- 直播采集优化：使用独立可采集窗口，支持自定义窗口分辨率，避免低画质

演示视频：<https://www.bilibili.com/video/BV1nRxk6dERS/>

如果这个项目对你有帮助，希望收到你的 Star，也欢迎到哔哩哔哩一键三连。交流 QQ 群：1130030892。

## 安装

### 使用发布包

从 [GitHub Releases](https://github.com/DanielToyama/WuwaTappoNative/releases) 下载：

- `WuwatappoNative-<版本>-Setup.exe`：安装到 `%LOCALAPPDATA%\Programs\Wuwatappo Native`，按当前用户安装，不需要管理员权限。
- `WuwatappoNative-<版本>-portable.zip`：解压后直接运行 `WuwatappoNative.exe`。

系统要求：

- Windows 10/11 x64。
- WebView2 Runtime。Windows 10/11 通常已预装；缺失时面板无法显示，桌宠本身仍可运行。
- 使用发布包不需要安装 .NET Runtime。

安装或启动新版本前，请先从托盘退出正在运行的旧实例。

## 快速开始

1. 启动程序。首次启动会打开主面板并显示免责声明，确认后程序会记住该状态。
2. 在面板中登录：
   - **UID + 验证码**：输入 9–10 位游戏 UID，获取并填写 6 位验证码。
   - **手机号 + 短信验证码**：国服可在登录页切换到「手机号登录」，使用库洛通行证手机号登录。
   - **继承官方程序登录状态**：打开 `设置 → 账号 → 继承官方程序登录状态`，程序会读取本机官方 WuwaTappo 的登录数据并关闭正在运行的官方客户端。
   - **导入 token**：可用 `token-extractor/` 中的独立工具读取 `kuro_infoToken`，再粘贴到设置中导入。
3. 在 `设置 → 账号 → 服务器区服` 选择国服（CN）或国际服（Global）；切换区服会退出当前账号并要求重新登录。
4. 打开 `联机`，创建房间或输入房间号加入。加入成功后，房间成员会显示在右侧成员列表。
5. 首次运行会创建以下文件：

```text
%LOCALAPPDATA%\WuwatappoNative\config.json
%LOCALAPPDATA%\WuwatappoNative\log.txt
%LOCALAPPDATA%\WuwatappoNative\cache\
```

`config.json` 保存登录状态、外观、窗口设置、快捷键和房间记录；`log.txt` 保存运行日志；`cache\` 保存外观和图片缓存。

## 用法与配置

### 面板与桌宠

面板包含 `背包`、`图鉴`、`合成`、`我的预设`、`联机`、`设置` 六个页面。

桌宠操作：

- 拖动桌宠可以移动位置；拖到屏幕或任务栏边缘约 28 像素内会自动贴边。
- 右键桌宠可打开菜单，包含打开面板、表情轮盘、窗口置顶、鼠标悬停半透明、鼠标穿透、桌宠大小、桌宠透明度、重置位置和退出。
- `设置 → 窗口与桌宠` 可调整桌宠大小 25%–200%、不透明度、鼠标穿透、悬停淡出、悬停隐藏程度（10%–100%，默认 40%）和 OBS 绿幕窗口尺寸。
- 开启鼠标穿透后，桌宠不接收鼠标点击；可从右键菜单或 `F7` 恢复。
- OBS 绿幕窗口的窗口标题为 `Wuwatappo Capture`，可在 OBS 中使用窗口采集。

### 默认快捷键

| 功能 | 默认键 |
|---|---|
| 游戏模式（鼠标穿透）开关 | `F7` |
| 切换屏幕 | `F8` |
| 显示/隐藏桌宠 | `F9` |
| 刷新界面 | `F5` |
| 发送表情槽位 1–6 | `Ctrl+Alt+1` 至 `Ctrl+Alt+6` |

快捷键可在 `设置 → 桌面键盘 → 快捷键` 中录制、清除或恢复默认值；保存后立即生效。快捷键通过 Raw Input 被动监听，不注册系统级热键。

### 设置

| 设置位置 | 内容 |
|---|---|
| `设置 → 账号` | 区服、登录、继承官方登录、导入 token、退出登录、刷新账号信息、累计点击补充 |
| `设置 → 窗口与桌宠` | 桌宠大小、不透明度、置顶、鼠标穿透、悬停隐藏、重叠处理、OBS 绿幕窗口 |
| `设置 → 桌面键盘` | 26 键、鸣潮专属、音游模式、手柄模式、键位映射和快捷键 |
| `设置 → 联机与宝箱` | 联机桌宠横向/纵向排列、隐藏其他成员、宝箱显示、自动领取宝箱 |
| `设置 → 自动化` | 自动表情、自动敲击、自动敲击频率和虚拟敲击动画 |
| `设置 → 通知` | 宝箱领取通知、快捷键操作通知 |
| `设置 → 其他` | 开机自动启动、管理员权限启动、游戏模式规则、检查更新 |

自动表情的来源可选快捷栏随机、指定表情或表情库随机，发送频率上限为 1–20 次/秒。自动敲击频率为 1–10 次/秒，是否显示虚拟敲击动画可单独设置。

`设置 → 其他 → 游戏模式规则` 可填写进程名，例如 `WutheringWaves.exe`。规则命中后程序会自动开启鼠标穿透，进程退出后恢复；名称匹配不区分大小写，带不带 `.exe` 均可。

### 开机启动与管理员权限

- `设置 → 其他 → 开机自动启动` 控制当前用户的开机启动。
- 普通权限使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 管理员权限使用当前用户登录触发的最高权限计划任务，登录时不再弹出 UAC。
- `设置 → 其他 → 以管理员权限启动` 用于接收以管理员权限运行的前台程序的 Raw Input；手动启动时会请求 UAC。

### 更新

设置页底部的“检查更新”会读取 GitHub Releases。当前版本落后多个版本时，更新说明会按从新到旧列出这些版本的更新内容，并在设置页更新横幅内滚动显示。程序不会自动安装更新，需要从 Release 页面下载新的 Setup 或便携版。

版本兼容状态页：<https://code.danieluu.top/WuwaTappoNative/>。页面和客户端都会读取同一份 `docs/compatibility.json`，用于展示当前版本是否仍在安全兼容范围内。

## 性能

44 名成员、每秒 10 次全员按键的压测下约占单核 6.7%，动作无积压；拖动角色时键盘只移动不重绘。压测命令：

```powershell
WuwatappoNative.exe --stress 44 --stress-hz 10
```

## 限制

- 手机号登录仅支持国服；国际服请使用继承官方客户端登录或导入 token。
- 没有自动更新器；需要手动下载并安装新版本。
- 官方客户端使用 App-Bound Encryption（v20）时无法解密其 cookie，只能改用 UID 验证码或手机号登录。
- 普通权限进程无法接收管理员前台窗口的 Raw Input；需要该场景时请开启管理员权限启动。
- 联机、开宝箱和账号信息依赖库洛服务器；网络不可达或服务器不可用时相关功能不可用。
- 仅支持 Windows 10/11 x64。
- 本项目不是库洛官方项目；游戏素材版权归库洛游戏所有，详见 [NOTICE.md](NOTICE.md) 和 [assets/README.md](assets/README.md)。


### 从源码构建

要求：

- Windows 10/11 x64
- Git
- .NET 8 SDK

```powershell
git clone https://github.com/DanielToyama/WuwaTappoNative.git
cd WuwaTappoNative
dotnet build -c Release
.\bin\Release\net8.0-windows\WuwatappoNative.exe
```

仓库已包含构建和运行所需的默认素材与离线外观目录，不需要额外下载素材包。

### 生成安装包

需要 PowerShell 7 和 Inno Setup 6.7 或更高版本。

```powershell
pwsh -File .\installer\build.ps1 -Version 0.0.43
```

输出：

```text
dist/WuwatappoNative-0.0.43-Setup.exe
dist/WuwatappoNative-0.0.43-portable.zip
dist/WuwatappoTokenExtractor-1.0.0-x64.zip
```

## License

- 源代码版权归项目作者所有，仅供学习与研究使用，不提供任何形式的担保；详见 [LICENSE](LICENSE) 和 [NOTICE.md](NOTICE.md)。
- 「鸣潮」、WuwaTappo 及全部游戏素材版权归库洛游戏所有；本项目与库洛游戏无任何关联。仓库内素材仅用于构建和运行本程序，不应单独提取或再分发。
- 第三方组件及许可证详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
