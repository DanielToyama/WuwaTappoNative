# Wuwatappo Native

[![Build](https://github.com/DanielToyama/WuwaTappoNative/actions/workflows/build.yml/badge.svg)](https://github.com/DanielToyama/WuwaTappoNative/actions/workflows/build.yml)

WuwaTappo 1.0.2（库洛《鸣潮》官方桌宠）的 Windows 原生复刻实现，使用 C# / WinForms / WebView2 编写，不依赖官方客户端或任何补丁版运行时。

- 桌宠：GDI+ 分层窗口（`UpdateLayeredWindow`），逐像素 alpha，无闪烁。
- 主面板：WebView2 承载本地 HTML/CSS/JS，负责背包、图鉴、合成、预设、联机与设置。
- 网络：`HttpClient` + 手写 Socket.IO v4 / Engine.IO v4 客户端。
- 目标：**功能与原版一致，界面大致相同**，不追求逐像素复制。

> 本项目与库洛游戏无任何关联。游戏素材（角色、配件、表情、UI 图标）版权归库洛游戏所有。
> 仓库随附的少量素材仅用于保证构建结果一致，不构成本项目对素材的任何授权，详见 [NOTICE.md](NOTICE.md)。

本版本（0.0.2）亮点：

- **人多场景性能优化**：30 人房间全员每秒按键 10 次的压测下平均 CPU 约 0.3%，拖动角色不再吃满单核。
- **自动宝箱**：每 30 秒检查左右宝箱，解锁满 5 秒自动领取，到点前先同步云端点击数。
- **设置全会同步**：不透明度、悬停半透明、置顶、鼠标穿透对所有联机成员桌宠一起生效。
- **快捷键可改**：默认对齐官方 Helper（F5/F7/F8/F9、Ctrl+Alt+1~6），支持录制、清除、冲突检测，保存即生效。
- **快捷键不抢键**：用 Raw Input 被动监听，不注册系统热键、不装键盘钩子，其他程序照常收到 F5/F7 等按键，也不增加按键延迟。
- 完整功能与实测数据见 [本版本特色](#本版本特色002)。

---

## 构建

要求：Windows 10/11 x64、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet build -c Release
# 产物：bin\Release\net8.0-windows\WuwatappoNative.exe
```

运行还需要 **WebView2 Runtime**（Win10/11 通常已内置；缺失时面板无法显示，桌宠仍可用）。

发布自包含版本（不要求目标机器安装 .NET Runtime）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:DebugType=none -p:DebugSymbols=false -o publish/app-0.0.2
```

> 说明：执行过上面这条 `publish -r win-x64` 之后，`bin\Release\net8.0-windows\win-x64\`
> 里会出现一整套自包含运行时文件。它是 RID 专用构建的中间产物（MSBuild 在带 `-r` 时会多算一遍），
> 不是源码，也不是发布目录，可以直接删除；真正的发布产物在 `publish\app-0.0.2\`。

### 随仓库分发的素材

为保证任何人克隆后编译/运行的效果与作者本机完全一致，以下文件**已包含在仓库中**：

| 路径 | 用途 |
|---|---|
| `assets/default/*.png` | 未登录或加载失败时的默认桌宠帧（头 2 帧 + 身体 3 帧） |
| `assets/ui/chest-btn.png` | 可领取宝箱按钮底图 |
| `assets/ui/chest-countdown.png` | 宝箱倒计时气泡底图 |
| `assets/app.ico` | exe 与快捷方式图标 |
| `data/appearance_asset.json` | 外观目录离线副本（联网后会自动刷新到缓存） |

这些素材提取自官方 WuwaTappo 1.0.2 客户端，版权归库洛游戏所有，仅随本项目用于构建一致性，
请勿单独提取再分发（详见 [assets/README.md](assets/README.md) 与 [NOTICE.md](NOTICE.md)）。
在线运行时角色/配件/表情图片仍从官方 CDN 下载并缓存到
`%LOCALAPPDATA%\WuwatappoNative\cache\images`，不写入程序目录。

### 打包安装包（可选）

见 [installer/README.md](installer/README.md)。素材已随仓库提供，额外需要 WiX 5.0.2（脚本会从 NuGet 拉取工具）。

---

## 运行

首次运行会在 `%LOCALAPPDATA%\WuwatappoNative\` 创建：

- `config.json`：登录 token、外观、缩放/透明度、快捷键、房间历史等；
- `log.txt`：运行日志；
- `cache/`：外观目录与图片缓存。

登录方式（面板 → 设置）：

1. **UID + 验证码**（官方主路径）：输入游戏 UID（9~10 位）或账号绑定手机号（11 位），点「发送验证码」，填入 6 位验证码登录。
2. **导入 token**：若你已从官方客户端拿到 `kuro_infoToken`，可在设置里直接粘贴导入。

> 手机号登录依赖官方短信通道；实测该通道暂未开放，目前请使用游戏 UID 登录。

### 快捷键（默认值与官方 Helper 一致，可在设置里修改）

| 功能 | 默认键 |
|---|---|
| 游戏模式（鼠标穿透）开关 | `F7` |
| 切换屏幕 | `F8` |
| 显示 / 隐藏桌宠 | `F9` |
| 刷新界面 | `F5` |
| 发送表情槽位 1~6 | `Ctrl+Alt+1` ~ `Ctrl+Alt+6` |

`F5`/`F7`/`F8`/`F9` 与表情快捷键都支持录制修改、清除（禁用）与冲突检测，保存后立即生效。

### 开发参数

```powershell
WuwatappoNative.exe --panel           # 启动即打开主面板
WuwatappoNative.exe --demo            # 面板跳过登录门槛（只读页面预览）
WuwatappoNative.exe --leave-room      # 维护：退出当前房间后关闭
WuwatappoNative.exe --set-skin 1046   # 维护：登录后切换本地皮肤并热重载
WuwatappoNative.exe --verbose         # 额外调试日志
WuwatappoNative.exe --stress 30 --stress-hz 10
                                      # 本地伪造 30 个成员、每秒 10 次按键，用于人多场景压测（不联网）
```

---

## 目录结构

```
tappoNativeGit/
├── WuwatappoNative.csproj
├── app.manifest                  PerMonitorV2 DPI 声明
├── src/
│   ├── Program.cs / App.cs       入口与应用编排（托盘、快捷键、定时任务）
│   ├── Core/                     Paths、Log、Config、Hotkeys
│   ├── Net/                      ApiClient、AuthService、SocketClient、RoomManager
│   ├── Assets/                   Catalog（外观目录）、AssetStore（图片缓存）
│   ├── Pet/                      PetWindow、PetComposer、RemoteCatWindow、
│   │                             KeyboardWindow、GlobalInputHook、表情轮播/轮盘
│   └── Ui/                       PanelWindow（WebView2 + bridge）
├── wwwroot/                      面板前端：index.html、style.css、app.js
├── assets/                       默认角色帧、宝箱 UI 底图、app.ico（随仓库分发）
├── data/appearance_asset.json    外观目录离线副本（随仓库分发）
└── installer/
    ├── build.ps1                 发布 + 生成 WiX 源 + 打包 MSI/ZIP
    └── gen-wxs.py                按 publish 目录生成 product.wxs
```

主要实现细节（渲染几何、合成规则、联机事件语义）见源码注释。

---

## 本版本特色（0.0.2）

### 人多场景性能优化

几十人的联机房间里，远端桌宠和事件都按人数线性增长，这里做了四层收敛：

- **事件分流**：成员按键只驱动对应桌宠的挥手动画，不再触发房间状态全量推送与名册刷新。
- **推送合并**：面板的房间状态推送按帧合并，房间页再用内容签名去重，内容没变就不重建 DOM。
- **重绘调度**：所有远端桌宠共用一个 16ms 调度器，每帧最多画 6 个窗口；状态没变的成员不重绘。
- **零拷贝后缓冲**：每个分层窗口用 DIB Section 直接作画，不再每帧 `GetHbitmap` 拷贝整幅位图；窗口位图复用，避免高频分配。
- **拖拽不再烧 CPU**：拖动角色时键盘只移动不重绘，几何更新节流到 60Hz。

实测：30 名成员、每秒 10 次全员按键的压测下平均 CPU 约 0.3%；拖动角色 4 秒约占单核 2.7%（优化前同样场景会吃满一个核、动画掉帧）。
压测入口：`WuwatappoNative.exe --stress 30 --stress-hz 10`（只在本机伪造，不联网、不影响服务器）。

### 自动宝箱

- 每 30 秒检查左右宝箱时间戳，解锁满 5 秒自动领取，失败 60 秒后重试。
- 到点前先刷新一次云端点击数，避免用过期余额判断导致开箱被拒。
- 领取成功弹奖励弹窗 + 托盘通知；可手动点击宝箱按钮提前查看。
- 关闭「总是显示宝箱」时只露出可领取按钮，悬停桌宠时临时显示两侧倒计时。

### 设置对所有联机成员生效

不透明度、悬停半透明、窗口置顶、鼠标穿透不再只作用于主角：

- 悬停淡出按窗口各自生效，鼠标在谁身上谁变淡；
- 其余三项会同步应用到同屏所有成员桌宠，改了立刻生效。

### 自定义快捷键

- 默认键位与官方 Helper 一致：`F7` 游戏模式（鼠标穿透）、`F8` 切换屏幕、`F9` 显隐桌宠、`F5` 刷新界面、`Ctrl+Alt+1~6` 表情槽位。
- 设置页可点击录制新组合键，支持清除（禁用该功能）、恢复默认与冲突检测；
- 保存后立刻生效，不用重启；快捷键改动的开关状态会实时反映回设置页。
- **高亮跟手**：键盘按键高亮由 Raw Input 边沿直接驱动（不等轮询），每次按下至少亮 70ms，
  快速点按不会“一闪就没”；主角挥手/计数重绘合并到约 60fps，面板点击数推送 100ms 节流，连打不会卡住。
- **不占用按键**：通过 Raw Input（`RIDEV_INPUTSINK`）被动接收按键，不调用 `RegisterHotKey`、也不安装低级键盘钩子。
  其他程序仍能收到 F5/F7/F8/F9 等按键，输入路径上没有任何额外处理，不会增加按键延迟。
  代价是这些键在前台程序里也会照常生效（例如浏览器 F5 仍会刷新）；如果某个组合与常用软件冲突，可在设置里改键或清除。

### 界面与体验

- **最近获得**常驻独立页签，新物品自动插入最前，重启不丢，可一键清空记录。
- 联机页常驻**历史房间**，支持直接点选切换；切换会先退出当前房间再进入。
- 合成栏按稀有度分成三行，最高稀有度不再被挤到另一行。
- 图鉴/背包里设置表情时，槽位预览图标尺寸修正。
- 缩放档位扩到 25% / 50% / 75% / 100% / 125% / 150% / 200%。
- 首次启动弹出免责声明；设置里有「关于」，包含版本、作者、鸣谢与 GitHub 链接。

### 桌宠与键盘

- 桌宠：272×331 逻辑像素，眨眼、拖动方向反馈、左右手交替按键动画；表情轮盘与气泡参数对齐原版（1s 生命、上浮 102px、70% 后淡出）。
- 桌面键盘 / 手柄：26 键、鸣潮专属、XInput 手柄面板；手柄按键与键盘走同一条计数/广播链路（按任意键都会计数、广播、挥手），摇杆只做显示不计数，避免转摇杆被当成按键。
- 远端桌宠：皮肤下载完成前不显示昵称条，避免先出现空白窗口；加载失败会回落到默认帧。

## 已知限制

- 手机号登录依赖官方短信通道，当前实测不可用，请使用游戏 UID 登录。
- 未实现自动更新器；安装包需手动下载替换。
- 官方客户端的 App-Bound Encryption（v20）cookie 无法解密，请使用 UID + 验证码登录。

---

## 版权与许可

- 本仓库源码版权归作者所有，仅供学习与研究使用；不提供任何形式的担保，详见 [LICENSE](LICENSE) 与 [NOTICE.md](NOTICE.md)。
- 「鸣潮」、WuwaTappo 及全部游戏素材版权归库洛游戏所有；本项目与库洛游戏无任何关联。
- 第三方组件：.NET 8（MIT）、Microsoft.Web.WebView2（Microsoft 许可），详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
