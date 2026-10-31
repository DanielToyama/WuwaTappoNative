# data 目录

| 文件 | 内容 | 用途 |
|---|---|---|
| `appearance_asset.json` | 角色 / 配件 / 表情的 id、CDN 路径、帧偏移量表 | 外观目录的离线副本 |

这份目录在运行时也会从官方接口自动刷新并缓存到
`%LOCALAPPDATA%\WuwatappoNative\cache\appearance_asset.json`（缓存优先）。随仓库提供是为了：

1. 断网首次启动也能正常显示图鉴与桌宠；
2. 保证不同机器克隆后编译、运行的结果与作者本机一致。

数据来自官方 WuwaTappo 1.0.2 客户端，版权归库洛游戏所有，仅用于构建一致性，请勿单独再分发。
