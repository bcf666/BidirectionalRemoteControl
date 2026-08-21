# Windows ↔ Android 双向远程控制 - 构建与使用说明

## 项目结构
```
Remote control/
├── Shared/                 # 跨端协议文档 (Protocol.md) 与示例 JSON、枚举
├── WinClient/              # Windows 端 (.NET 8 + WPF)，已编译通过
└── AndroidClient/          # Android 端 (Kotlin + Jetpack Compose)
```

## 一、Windows 端构建（已验证可编译）
前置：安装 .NET 8 SDK（或 .NET 10）

```
cd WinClient
dotnet build -c Release
dotnet run   # 直接运行（也可以发布为单文件绿色版）
```
- 启动后自动在 UDP 23000 广播，搜索局域网设备
- 左侧：切换主控/被控、监听端口、设备列表
- 右侧：远程画面区；我主控时，鼠标/键盘/拖拽/滚轮都会发送到对端；文件拖入即可发送

## 二、Android 端构建
前置：Android Studio 新版（含 Gradle 8.7 / AGP 8.7 兼容），JDK 17
1. 用 Android Studio 打开 `AndroidClient/` 目录，等待 Gradle sync
2. 生成 launcher 图标：右键 `res` -> Image Asset -> Launcher Icons
3. 菜单 Build -> Build APK，产物在 `app/build/outputs/apk/`
4. 安装后首次开启：
   - 打开 App -> "请求权限" -> 授予**屏幕录制**（用于 PC 控制手机时共享屏幕）
   - 跳转到系统 **无障碍** 里启用 `双向远程控制` 服务（PC 控制手机时模拟触摸必需）

## 三、使用流程（局域网）
1. 两端在同一 Wi-Fi；启动 App 自动出现在对方设备列表
2. 一端点击"连接"，或作为"服务器等待连接"均可
3. 切换"会话方向"：
   - PC 控制手机：PC 端显示手机画面、键鼠发 TOUCH/KEY 到手机注入
   - 手机控制 PC：手机端显示 PC 画面、触屏滑动控制鼠标、拖拽点击、辅助按钮滚轮/右键
4. 文件互传：Windows 端把文件拖进画面区即可发送给手机；手机端后续可在 UI 里扩展文件选择器（已实现底层 FileTransferService）

## 四、里程碑当前完成度
- ✅ M1 联通版：UDP 发现 + TCP 连接 + 握手/心跳 两端代码已串好
- ✅ M2 单向镜像：两端的屏幕捕获 (GDI/MediaProjection) -> MJPEG -> 对方解码显示 均已实现
- ✅ M3 单向控制：PC 端 SendInput / 安卓端 AccessibilityService dispatchGesture 输入注入
- ✅ M4 双向通：通过"切换方向"在两种模式切换；消息在 RemoteSession 中按方向分发
- 🔲 M5 文件传输 + 稳定版：底层 FileTransferService 已在两端实现；UI 进度条、选择器在 Android 端可继续完善
