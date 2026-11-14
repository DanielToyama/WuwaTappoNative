# 项目主页

`docs/` 通过 GitHub Pages 发布，地址：

- 项目主页（含版本状态）：<https://code.danieluu.top/WuwaTappoNative/>
- 状态 JSON：<https://code.danieluu.top/WuwaTappoNative/compatibility.json>

## 更新状态

直接编辑 `compatibility.json` 并提交到 `main`，GitHub Pages 会自动更新；不需要发布新的 Native 版本。

官方 `official.cn.latest` / `official.global.latest` 由 `.github/workflows/update-compatibility.yml` 每 6 小时从官方更新清单同步一次，用于状态页展示；客户端还会直接读取官方 `latest_host.json`，因此不依赖 Actions 的同步延迟。`native.latest`、`native.minSafe` 和兼容性状态仍由发布流程手动维护。

常用字段：

| 字段 | 说明 |
|---|---|
| `status` | `safe` / `warning` / `unsafe` / `maintenance` |
| `message` | 状态页和客户端显示的说明 |
| `native.latest` | 当前已发布的最新 Native 版本，每次发版更新 |
| `native.minSafe` | 仍兼容当前官方协议的最低版本；只在兼容基线变化时更新，不随普通修复版提升 |
| `official.cn.latest` | 当前官方国服最新版本 |
| `official.global.latest` | 当前官方国际服最新版本，没有清单时可为 `null` |
| `official.global.inheritCn` | 为 `true` 时页面沿用国服的版本判断，并标注“沿用国服判断（未单独校验）” |
| `protocol.testedOfficial` | 当前协议已验证的官方版本；客户端会将其作为“不低于本地编译基线”的验证基线，维护者可在官方更新后直接提升，无需等待 Native 再发版 |
| `protocol.breaking` | 是否检测到破坏性协议变更 |
| `killSwitch.forceOffline` | 是否要求客户端进入安全模式 |
| `killSwitch.reason` | 安全模式原因 |

客户端会请求同一份 JSON，并直接读取官方版本清单；拉取失败时保留上次结果，不阻塞本地功能。

发布新版本时更新 `native.latest`；只有协议/服务器兼容基线发生变化时，才提升 `native.minSafe`。普通 bug 修复、界面调整和安装器修改不需要推进最低兼容版本。
