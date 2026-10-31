# 第三方组件说明

本项目自身源码按 [NOTICE.md](NOTICE.md) 的条款提供。以下第三方组件的版权与许可归各自权利人所有。

## 运行时依赖

| 组件 | 版本 | 许可 | 说明 |
|---|---|---|---|
| [.NET 8](https://dotnet.microsoft.com/)（含 Windows Desktop Runtime / WinForms） | 8.0 | MIT | 目标框架。自包含发布时随包分发，完整许可文本见 <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/) | 1.0.3351.48 | Microsoft 软件许可条款 | 主面板的浏览器控件。NuGet 包内 `LICENSE.txt` 为准 |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | 系统提供 | Microsoft 许可 | 不随本项目分发，由目标机器安装 |

自包含发布（`-r win-x64 --self-contained`）会把 .NET 运行时与 WinForms 原生库一起复制到发布目录，
这些文件仍受各自许可约束，不代表本项目对其主张版权。

## 开发期依赖

无。除上述 NuGet 包外，构建只依赖 .NET 8 SDK；前端为手写 HTML/CSS/JS，无 npm 依赖。

## 游戏内容

「鸣潮」、WuwaTappo 及全部游戏素材的版权归库洛游戏所有。本仓库随附的少量素材（默认角色帧、
宝箱 UI 底图、使用默认角色头帧合成的应用图标、外观目录 JSON）仅用于保证构建结果一致，
不构成对这些素材的任何授权；版权仍归库洛游戏所有。
详见 [NOTICE.md](NOTICE.md) 与 [assets/README.md](assets/README.md)。
