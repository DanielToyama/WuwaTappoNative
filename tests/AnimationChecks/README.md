# 动画回归检查与绘制基准

要求 Windows 10/11、.NET 8 SDK；没有额外测试框架依赖。

```powershell
cd WuwaTappoNative
dotnet run --project tests/AnimationChecks/AnimationChecks.csproj -c Release
```

成功时输出 `PASS: ... assertions.`，失败时返回非零退出码。

## 隔离边界

程序直接测试绘制与时钟组件，不启动主应用，不登录、不读写账号/config/state、不安装输入钩子，不发起网络请求或显示窗口。联机绘制检查仅创建隐藏的测试窗口，不加入房间，不影响已安装实例。

## 检查内容

- 主桌宠缓存与未缓存绘制在 25%、100%、125%、200% 尺寸下的像素对照；头像/身体/头饰到货、手部/眨眼、计数、键盘开关、头饰偏移、宝箱、加载指示器和图层顺序。
- 联机桌宠与上游 0.1.7 独立绘制快照的像素对照；缓存命中/失效、昵称/字号、尺寸、动作、图片替换、气泡图层顺序，以及真实隐藏句柄入队后关闭最后一个窗口时清空队列并停钟。
- 气泡缩略图与原始高质量缩放算法在不同大小、透明度、像素偏移模式下的像素对照。分层预乘 alpha 合成允许最大 2/255 的通道舍入差。
- 气泡数量上限、生命周期、到期/淘汰时释放缩略图。
- 动画 Tick 在 UI 线程执行、阻塞 UI 时最多一个待处理通知、停止后不再 Tick、可重新启动与释放。

## 基准口径

- 分别运行 WinForms 50ms、WinForms 16ms 和新动画时钟各约 3 秒，报告回调频率、帧间隔中位数/P95。不对忙碌机器强制 FPS 门槛。
- 使用内置默认角色、0/10 个合成半透明表情、272x331 和 544x662 位图，各预热 5 帧、测 120 帧。表情进度按 60 帧一个生命周期推进，包含缩放和渐隐，不是只测静止的缓存命中。
- `original-reference` 保留角色的未缓存绘制，并按原始气泡算法实时缩放；测试只在计时路径补画气泡。默认位置的气泡不会覆盖底栏，因此该基准没有因重排 HUD 而减少工作；原始气泡参数通过反射读取，额外开销也计入结果。
- `cached` 使用生产缓存路径。图像、窗口和缩略图持续复用；这些数值是离屏合成耗时，不是进程 CPU 百分比。
- 不包含 UpdateLayeredWindow、DWM、游戏负载、输入网络延迟或屏幕显示帧率；不能据此推断鸣潮掉帧改善幅度。

## 手动试用构建产物

可用 `dotnet publish WuwatappoNative.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o publish/animation-test` 生成本地单文件测试版，运行 `publish/animation-test/WuwatappoNative.exe`。它与已安装版共用 `%LOCALAPPDATA%\WuwatappoNative`，不是独立账号沙盒：

1. 先从托盘正常退出原版，并备份用户数据目录中的 `config.json` 和 `state.json`。不要把含凭证的备份上传。
2. 启动测试版，连续发表情/敲键、切换大小和外观；有联机条件时再观察远端表情。
3. 暂不启用测试版的开机自启，不点击重新配置管理员启动；它们可能改变原版的启动路径。
4. 试用后从托盘退出测试版，再用原快捷方式启动已安装版。无需覆盖或卸载原安装。

测试版不修复透明置顶窗口对游戏呈现路径的影响，也不保证所有负载下稳定 60 FPS。
