<p align="center">
  <img src="Assets/app.png" alt="EasyGet Logo" width="120"/>
</p>

<h1 align="center">EasyGet</h1>

<p align="center">
  <strong>基于 yt-dlp 的全平台视频下载桌面工具</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/WPF-Desktop-0078D4?logo=windows" alt="WPF"/>
  <img src="https://img.shields.io/badge/Architecture-MVVM-green" alt="MVVM"/>
  <img src="https://img.shields.io/badge/Version-1.1.2-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/License-MIT-yellow" alt="License"/>
</p>

---

## 📖 简介

EasyGet 是一款基于 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 的 Windows 桌面视频下载工具，支持 YouTube、Bilibili、X(Twitter)、Instagram、抖音等 [1000+ 网站](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md) 的视频下载。项目采用 WPF + MVVM 架构，提供暗色主题、多任务队列、下载历史、Cookie 辅助下载、播放列表导入和环境自动安装等能力。

## 🖼️ 当前界面

以下截图基于当前版本截取，示例路径与历史内容已做脱敏处理。

| 视频下载 | 批量下载 |
|---|---|
| ![视频下载页](docs/screenshots/download-view.png) | ![批量下载页](docs/screenshots/batch-download-view.png) |

| 历史记录 | 设置 |
|---|---|
| ![历史记录页](docs/screenshots/history-view.png) | ![设置页](docs/screenshots/settings-view.png) |

## ✨ 功能特性

### 🎯 核心功能
- **单视频下载** — 输入 URL 后自动解析标题、平台、时长、缩略图，支持格式、画质、字幕与目录选择
- **批量下载** — 多 URL 批量输入，支持播放列表导入、队列管理、并发下载控制和单任务操作
- **任务控制** — 支持取消、暂停、恢复、失败重试、全部取消与已结束任务清理
- **下载历史** — SQLite 持久化存储，支持搜索、删除、清空、打开文件夹 and 缩略图展示
- **m3u8 视频流下载** — 自动识别 `.m3u8` 或 `.m3n8` 播放列表并调用 16 线程高速分片下载，支持进度速度计算，并由 ffmpeg 自动 remux 合并为 MP4（对加密流有防护校验）
- **Telegram 媒体直链流式下载** — 纯 C# 原生对接 Telegram 核心网络套接字（基于 MTProto WTelegramClient），自动匹配公开（如 `t.me/durov/123`）或私有（如 `t.me/c/xxxx/xxxx`）频道直链及批量范围消息，流式分块下载，支持代理外挂与断点续传
- **智能环境检测** — 启动时自动检测 yt-dlp / ffmpeg，缺失则从官方发布源自动下载安装，并支持在设置页手动重试与更新 yt-dlp
- **应用版本更新** — 设置页可检查 GitHub Release 最新版本，下载 `EasyGet-Setup-vX.Y.Z.exe` 并启动安装器覆盖升级

### ⚙️ 配置管理
- 默认下载路径、格式、画质设置
- HTTP / SOCKS5 代理支持
- 并发分片数与同时下载数调节，批量下载并发上限可动态更新
- aria2c 外部下载器开关
- Cookie 内容保存与转换，支持常见浏览器 Cookie 字符串、JSON 和 Netscape 格式
- 下载完成后按平台自动归类保存
- JSON 配置自动持久化

### 🎨 用户体验
- **多主题实时换肤系统** — 基于精致暗色架构，提供星空靛蓝、极光青、玫瑰粉、琥珀金以及经典浅蓝等多种强调色方案，支持在设置页一键实时、零延迟切换，全局图标、选中指示器和主按钮均同步完美响应
- **极致动效系统** — 遵循轻量与克制的微动效规范，支持页面平滑入场（Slide + Fade）、队列卡片增删平移动画，以及删除时的左滑消隐动效
- **智能剪贴板监测** — 窗口激活时自动安全捕捉视频 URL，提供顶端轻量级浮条提醒，支持一键解析，8秒后自动收起
- **全局键盘快捷键** — 支持 `Ctrl+1~4` 一键跨页导航；任意状态按 `Esc` 优先中止解析或一键关闭全部 Toast；非输入焦点下 `Ctrl+V` 直达下载页并自动解析
- **任务栏进度同步** — 利用 `TaskbarItemInfo` 将下载队列进度（多任务时为加权平均进度）及状态（进行中、失败）实时同步至 Windows 任务栏，支持进度跑马灯
- **Toast 堆叠队列** — 重构为可垂直堆叠的通知中心（最多保留3条最新通知），卡片底部配备跑马灯倒计时，支持鼠标 hover 悬停与手动提前关闭
- **极简日志降级** — 将原先 300px 常驻的大控制台降级为可折叠 `Expander`，折叠状态自记忆，标题栏支持复制日志、清空日志的一键平滑操作
- **交互防御性设计** — 清空历史记录、一键取消所有批量任务等破坏性操作前增加警告确认提示，防止用户因误触导致数据丢失
- **全局异常捕获与崩溃日志**

## 🏗️ 技术架构

```
EasyGet/
├── Models/                  # 数据模型层
│   ├── AppConfig.cs         #   应用配置模型
│   ├── DownloadTask.cs      #   下载任务模型（ObservableObject）
│   └── DownloadHistory.cs   #   下载历史记录模型
├── Services/                # 服务层（业务逻辑）
│   ├── ConfigService.cs     #   JSON 配置读写
│   ├── YtDlpService.cs      #   yt-dlp 命令封装（核心服务）
│   ├── M3u8DownloadService.cs # m3u8 多线程下载服务
│   ├── TelegramDownloadService.cs # Telegram 原生下载服务
│   ├── DownloadManager.cs   #   下载队列与并发管理
│   ├── HistoryService.cs    #   SQLite 历史 CRUD
│   ├── EnvironmentService.cs#   环境检测、自动安装与 yt-dlp 更新
│   ├── AppUpdateService.cs  #   GitHub Release 应用更新检查与安装包下载
│   └── DownloadFileNameBuilder.cs # 下载文件名与输出模板构建
├── ViewModels/              # 视图模型层
│   ├── MainViewModel.cs     #   导航与全局状态
│   ├── DownloadViewModel.cs #   单视频下载逻辑
│   ├── BatchDownloadViewModel.cs # 批量下载逻辑
│   ├── HistoryViewModel.cs  #   历史记录逻辑
│   └── SettingsViewModel.cs #   设置页逻辑（自动保存）
├── Views/                   # 视图层（XAML）
│   ├── DownloadView.xaml    #   单视频下载页
│   ├── BatchDownloadView.xaml #  批量下载页
│   ├── HistoryView.xaml     #   历史记录页
│   └── SettingsView.xaml    #   设置页
├── Themes/
│   └── Generic.xaml         # VibeTracker Calm Apple Dark 主题 + 控件样式
├── Converters/
│   └── CommonConverters.cs  # 值转换器（Bool/Visibility/Status 等）
├── EasyGet.Tests/           # 单元测试项目
├── App.xaml(.cs)            # DI 容器 / 全局异常捕获
└── MainWindow.xaml(.cs)     # 主窗口 + 侧边栏导航
```

### 技术栈

| 技术 | 用途 |
|---|---|
| **.NET 8 / WPF / C# 12** | 应用框架 |
| **CommunityToolkit.Mvvm** | MVVM 框架（源生成器） |
| **Microsoft.Extensions.DependencyInjection** | 依赖注入 |
| **Microsoft.Data.Sqlite** | 下载历史持久化 |
| **yt-dlp** | 视频下载核心引擎 |
| **ffmpeg** | 音视频合并转码 |
| **xUnit** | 单元测试 |

## 🚀 快速开始

### 环境要求

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 构建与运行

```bash
# 克隆仓库
git clone https://github.com/zzf-857/EasyGet.git
cd EasyGet/EasyGet

# 还原依赖
dotnet restore

# 运行
dotnet run

# 运行测试
dotnet test EasyGet.Tests/EasyGet.Tests.csproj

# 发布并做 smoke 检查
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1 -SkipZip

# 构建安装包（需要 Inno Setup 6）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.1.2
```

> **提示：** 首次运行时，EasyGet 会自动检测并下载 yt-dlp 和 ffmpeg，无需手动安装。yt-dlp 来自官方 GitHub Release，ffmpeg Windows 构建来自 FFmpeg 官网链接的 gyan.dev。

## 📊 开发进度

### ✅ 已完成
- [x] MVVM 架构 + 依赖注入容器
- [x] 侧边栏四页导航
- [x] 单视频下载（URL 解析 / 格式选择 / 字幕选择 / 进度展示 / 取消）
- [x] 批量下载（多 URL / 播放列表导入 / 队列管理 / 单任务操作）
- [x] SQLite 历史记录（搜索 / 删除 / 清空 / 打开文件夹 / 缩略图）
- [x] 设置页（环境检测 / 路径 / 格式 / 代理 / Cookie / 性能）
- [x] yt-dlp / ffmpeg 自动检测与安装
- [x] yt-dlp 手动更新
- [x] aria2c 外部下载器参数集成
- [x] aria2c 可用性检测与不可用回退提示
- [x] 并发数动态调整
- [x] 下载暂停 / 恢复 / 取消 / 失败重试
- [x] 下载完成、失败、取消 Toast 通知
- [x] 缩略图解析与展示
- [x] Cookie 粘贴、转换与下载参数集成
- [x] 按平台自动归类保存
- [x] 抖音浏览器兜底下载
- [x] 多主题动态换肤（支持星空靛蓝、极光青、玫瑰粉、琥珀金、经典浅蓝等强调色在设置页一键实时无刷切换）
- [x] 全局异常捕获 + 崩溃日志
- [x] JSON 配置持久化
- [x] 窗口位置 / 大小持久化
- [x] 日志自动滚动到底部
- [x] 剪贴板粘贴支持与智能 activated 自动监测提示（8秒超时）
- [x] 全局键盘快捷键导航、中止与粘贴解析（Ctrl+1~4, Esc, Ctrl+V）
- [x] 任务栏进度同步聚合展示（支持 Normal 与 Error 状态指示）
- [x] Toast 堆叠队列与通知倒计时（最多3条垂直堆叠，支持悬停）
- [x] 批量拖拽文件或文本链接自动导入与 Drop 虚线指示层
- [x] 批量卡片 4 状态彩色条展示与动态操作按钮过滤
- [x] SQLite 下载历史的按类型多选分段式筛选（RadioButton）
- [x] 历史空态细分引导与无匹配结果一键清筛
- [x] 破坏性敏感操作的二次防误触确认层
- [x] 日志控制台 Expander 降级折叠与内联 URL 校验（不污染日志）
- [x] m3u8/m3n8 格式多线程下载支持与 ffmpeg 自动转码 remux
- [x] Telegram 媒体直链原生下载方案（WTelegramClient tcp 握手），支持公有/私有链接拦截与范围消息下载
- [x] Telegram 账号凭证分离存储（隔离于系统 AppData，安全不推送远端）与新手小白超链接绑定界面（支持发送验证码、提交 2FA 密码、注销等完整交互，提供 my.telegram.org 一键点击唤起浏览器访问）
- [x] 全面覆盖并扩充至 283 个稳定单元测试（覆盖 ViewModel、命令、XAML 结构断言、快捷键检测、m3u8 相对路径、Telegram 直链解析与发布更新链路；另保留 1 个真实站点手动验收用例）
- [x] Windows 发布脚本与 EasyGet.exe smoke 检查
- [x] GitHub Actions 自动打包发布与 Inno Setup 安装包
- [x] 设置页应用更新检查、更新包下载与安装器启动

### 🔧 待完善
- [ ] 系统托盘或系统级 Toast 通知
- [ ] 真实站点下载速度与异常场景端到端验证
- [ ] 抖音浏览器兜底的网络中断、Range 续传和取消清理测试
- [ ] 动态并发调整的服务级测试
- [ ] 继续补齐 VibeTracker Calm Apple Dark 的环境光背景、导航动效与页面内容密度
- [ ] 国际化支持
- [ ] 更完整的单元测试和 UI 自动化测试

## 📁 数据存储

| 数据 | 位置 |
|---|---|
| 配置文件 | `%LocalAppData%/EasyGet/config.json` (包含 Telegram 绑定凭证，独立于项目，防推送泄露) |
| 配置备份 | `%LocalAppData%/EasyGet/config.backup.json` |
| 下载历史 | `%LocalAppData%/EasyGet/history.db` (SQLite) |
| Telegram 登录会话 | `%LocalAppData%/EasyGet/tools/telegram.session` (本地登录凭据，独立于项目，防推送泄露) |
| Cookie 转换文件 | `%LocalAppData%/EasyGet/cookies.txt` |
| 应用更新包 | `%LocalAppData%/EasyGet/updates/` |
| 崩溃日志 | `<应用目录>/logs/crash_*.txt` |
| yt-dlp | `<应用目录>/tools/yt-dlp.exe` |
| ffmpeg | `<应用目录>/tools/ffmpeg.exe` |

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

本项目基于 [MIT License](LICENSE) 开源。
