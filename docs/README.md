# 版本状态页

`docs/` 通过 GitHub Pages 发布，地址：

- 状态页：<https://code.danieluu.top/WuwaTappoNative/>
- 状态 JSON：<https://code.danieluu.top/WuwaTappoNative/compatibility.json>

## 更新状态

直接编辑 `compatibility.json` 并提交到 `main`，GitHub Pages 会自动更新；不需要发布新的 Native 版本。

常用字段：

| 字段 | 说明 |
|---|---|
| `status` | `safe` / `warning` / `unsafe` / `maintenance` |
| `message` | 状态页和客户端显示的说明 |
| `native.latest` | 当前已发布的最新 Native 版本 |
| `native.minSafe` | 低于此版本时客户端提示更新 |
| `official.cn.latest` | 当前官方国服最新版本 |
| `official.global.latest` | 当前官方国际服最新版本，没有清单时可为 `null` |
| `protocol.testedOfficial` | 当前协议已验证的官方版本 |
| `protocol.breaking` | 是否检测到破坏性协议变更 |
| `killSwitch.forceOffline` | 是否要求客户端进入安全模式 |
| `killSwitch.reason` | 安全模式原因 |

客户端会请求同一份 JSON，并在设置页显示状态；拉取失败时保留上次结果，不阻塞本地功能。

发布新版本时，记得同步更新 `native.latest` 和 `native.minSafe`。
