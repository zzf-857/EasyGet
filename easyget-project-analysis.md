# EasyGet 项目分析与开发进度追踪

基于 yt-dlp 的全平台视频下载 WPF 桌面应用，采用 .NET 8 + MVVM 架构，当前处于 **v1.0.0 功能硬化阶段**。核心下载、批量队列、历史记录、设置页、环境检测、Cookie 处理和基础通知能力已基本完成，后续重点应放在下载失败率、真实站点端到端验证、UI 现代化和工程化发布。

> 2026-06-09 硬化进度：`dotnet test EasyGet.Tests\EasyGet.Tests.csproj` 通过，当前测试覆盖已扩展到 57 个；Release 构建和发布脚本 smoke 检查均已跑通。

---

## 一、项目概况

| 项目 | 详情 |
|---|---|
| **技术栈** | .NET 8 / WPF / C# 12 |
| **架构模式** | MVVM (CommunityToolkit.Mvvm) |
| **依赖注入** | Microsoft.Extensions.DependencyInjection |
| **数据存储** | SQLite (Microsoft.Data.Sqlite) — 下载历史 |
| **配置存储** | JSON 文件 (`%LocalAppData%/EasyGet/config.json`) |
| **外部依赖** | yt-dlp (下载核心), ffmpeg (音视频合并) |
| **UI 主题** | Catppuccin Mocha 暗色系 |

---

## 二、模块结构与完成状态

### 2.1 Models 层 — ✅ 基本完成

| 文件 | 职责 | 状态 |
|---|---|---|
| `AppConfig.cs` | 应用配置模型（下载路径/格式/画质/代理/aria2c/窗口状态） | ✅ 完成 |
| `DownloadTask.cs` | 下载任务模型（ObservableObject，含进度/速度/ETA 等可观察属性） | ✅ 完成 |
| `DownloadHistory.cs` | 下载历史记录模型 | ✅ 完成 |

### 2.2 Services 层 — ⚠️ 核心已完成，部分功能缺失

| 文件 | 职责 | 状态 | 备注 |
|---|---|---|---|
| `ConfigService.cs` | JSON 配置读写 | ✅ 完成 | 简洁可靠 |
| `EnvironmentService.cs` | yt-dlp/ffmpeg 环境检测、自动安装、手动安装与 yt-dlp 更新 | ✅ 完成 | 已有分阶段日志和下载百分比，仍需真实网络失败场景验证 |
| `HistoryService.cs` | SQLite 历史记录 CRUD | ✅ 完成 | |
| `YtDlpService.cs` | yt-dlp 命令封装（获取信息/下载/进度解析、Cookie 策略、aria2c 参数、抖音兜底） | ✅ 完成 | 已补短命令超时、长下载无输出卡住保护，建议继续补真实站点回归 |
| `DownloadManager.cs` | 下载队列管理器（并发控制、暂停/恢复、重试、取消、完成通知） | ✅ 基本完成 | 动态并发已有实现与基础测试，建议继续做真实下载验证 |

### 2.3 ViewModels 层 — ⚠️ 基本完成，部分逻辑待补

| 文件 | 职责 | 状态 | 备注 |
|---|---|---|---|
| `MainViewModel.cs` | 导航与全局状态 / 启动初始化 | ✅ 完成 | |
| `DownloadViewModel.cs` | 单视频下载页逻辑 | ✅ 完成 | |
| `BatchDownloadViewModel.cs` | 批量下载页逻辑 | ✅ 基本完成 | 已支持单任务暂停、恢复、重试、取消/移除 |
| `HistoryViewModel.cs` | 历史记录页逻辑 | ✅ 完成 | |
| `SettingsViewModel.cs` | 设置页逻辑（自动保存、环境安装阶段反馈） | ✅ 完成 | |

### 2.4 Views 层 — ⚠️ 布局完成，交互细节待完善

| 文件 | 职责 | 状态 | 备注 |
|---|---|---|---|
| `MainWindow.xaml` | 主窗口 + 侧边栏导航 + ContentControl 页面路由 | ✅ 完成 | |
| `DownloadView.xaml` | 单视频下载 UI | ✅ 完成 | |
| `BatchDownloadView.xaml` | 批量下载 UI（URL 输入 + 队列列表） | ✅ 基本完成 | 已有队列单任务操作按钮，后续可统一视觉风格 |
| `HistoryView.xaml` | 历史记录列表 UI | ✅ 完成 | |
| `SettingsView.xaml` | 设置面板 UI（环境检测/下载/代理/性能） | ✅ 完成 | |

### 2.5 主题与转换器 — ✅ 完成

| 文件 | 职责 | 状态 |
|---|---|---|
| `Themes/Generic.xaml` | Catppuccin Mocha 暗色主题 + 全局控件样式 | ✅ 完成 |
| `Converters/CommonConverters.cs` | 5 个值转换器（Bool、Visibility、Status、Platform 等） | ✅ 完成 |

### 2.6 基础设施 — ✅ 完成

| 文件 | 职责 | 状态 |
|---|---|---|
| `App.xaml.cs` | DI 容器 / 全局异常捕获 / 崩溃日志 | ✅ 完成 |
| `MainWindow.xaml.cs` | 窗口启动 + 异步初始化 | ✅ 完成 |

---

## 三、已实现功能清单

- [x] **MVVM 架构 + 依赖注入**完整搭建
- [x] **侧边导航栏** — 4 个页面切换（下载/批量/历史/设置）
- [x] **单视频下载** — URL 输入、格式/画质/字幕选择、目录浏览、进度展示、取消
- [x] **批量下载** — 多 URL 文本输入、链接计数、播放列表导入、队列展示、并发控制、单任务操作
- [x] **历史记录** — SQLite 持久化、搜索、单条删除、清空、打开文件夹
- [x] **设置页** — 环境检测状态、默认下载路径/格式/画质、代理开关与地址、Cookie、aria2c、并发分片数/同时下载数
- [x] **环境自动检测** — 启动时自动检测 yt-dlp/ffmpeg，缺失则自动下载安装，设置页可手动重试
- [x] **yt-dlp 集成** — `--dump-json` 获取视频信息、完整下载参数构建、实时进度解析、Cookie 自动转换与浏览器 Cookie 策略
- [x] **代理支持** — HTTP / SOCKS5 代理传递给 yt-dlp
- [x] **暗色主题** — Catppuccin Mocha 色板，自定义控件模板
- [x] **全局异常捕获** — `AppDomain.UnhandledException` + `DispatcherUnhandledException`，日志写入 `logs/` 目录
- [x] **配置持久化** — JSON 格式，设置变更自动保存
- [x] **剪贴板粘贴** — 单视频和批量页都支持
- [x] **下载完成/失败/取消 Toast** — 应用内通知，可点击关闭
- [x] **窗口位置/大小持久化** — 退出时保存普通窗口状态，下次启动恢复
- [x] **抖音浏览器兜底下载** — yt-dlp 抽取失败后可用本机 Chrome/Edge headless 捕获 mp4 响应并下载
- [x] **Windows 发布脚本** — `scripts\publish-win-x64.ps1` 可执行测试、发布、`EasyGet.exe` smoke 检查和可选 zip 打包
- [x] **依赖版本固定** — 主项目 NuGet 包版本已固定，避免 `8.*` 浮动造成测试/发布版本冲突
- [x] **设置页安装阶段展示** — 环境安装反馈可区分检测、准备安装、下载、解压、完成和失败阶段
- [x] **按钮禁用态视觉** — 主要按钮样式在不可用时会降低透明度并恢复普通光标
- [x] **yt-dlp 网络重试参数** — 长下载默认追加重试、分片重试、socket 超时和重试间隔参数

---

## 四、未实现 / 待完善功能

### 🔴 高优先级

1. **真实站点端到端验证不足** — YouTube、抖音、Bilibili、X 等站点的 Cookie、风控、代理、失败重试仍需要真实 URL 回归验证
2. **抖音浏览器兜底仍需真实站点验证** — 单元测试已覆盖 Range 被忽略、取消残留、网络中断等场景，但还需要用真实抖音 URL 验证大文件与风控路径
3. **暂停/恢复需要真实下载验证** — 当前通过取消进程并重新入队依赖 yt-dlp/HTTP 续传，需验证不同站点和格式的恢复行为

### 🟡 中优先级

4. **UI 视觉风格仍偏早期原型** — 暗色主题已完成，但 emoji 按钮、列表操作区、设置页密度和控件状态还可统一成更现代的 Fluent/Material 风格
5. **ComboBox 下拉样式需继续检查** — `DarkComboBox` 已有基础样式，但 Popup/Item 状态仍需视觉验收
6. **日志和运行时诊断仍弱** — 有崩溃日志和下载日志列表，但缺少结构化运行日志与可导出诊断包
7. **系统托盘或系统级通知缺失** — 目前是应用内 Toast，后台下载时仍需要系统级提示

### 🟢 低优先级

8. **安装包配置不完整** — 已有 Windows publish 脚本，但还缺少 MSIX / Inno Setup 安装包
9. **国际化 (i18n) 未实现** — UI 文字硬编码中文
10. **应用自身自动更新缺失** — 仅支持 yt-dlp 更新
11. **UI 自动化测试缺失** — 已有基础单元测试，但没有 WPF 交互测试或截图回归

---

## 五、代码质量评估

### 优点
- MVVM 职责分离清晰，代码组织规范
- CommunityToolkit.Mvvm 源生成器用法正确
- 注释完整（XML 文档注释覆盖所有公共 API）
- 异步编程模式使用得当
- 全局异常处理机制到位

### 待改进
- **线程安全** — 下载进度已通过 Dispatcher 回到 UI 线程，但状态、历史写入、队列移除等路径仍建议继续审查
- **资源释放** — `YtDlpService` 中的 `HttpClient` 未托管（在 `EnvironmentService` 中），`Process` 对象的 `Kill` 异常处理过于宽泛
- **硬编码** — 格式/画质的中文显示文本和内部值转换分散在多个 ViewModel 中，存在重复
- **异常处理** — 多处 `catch { }` 空捕获，可能隐藏问题

---

## 六、建议开发路线图

### Phase 1 — Bug 修复与功能补全（当前阶段）
- [x] 实现 aria2c 外部下载器参数集成
- [x] 实现窗口位置/大小持久化
- [x] 批量下载视图添加单任务操作按钮
- [x] 添加应用内 Toast 通知
- [x] 添加日志自动滚动
- [x] 添加 yt-dlp/ffmpeg 自动安装和设置页手动安装
- [x] 添加抖音浏览器兜底下载
- [x] 为 aria2c 增加可用性检测和回退提示
- [x] 为性能配置增加上下限保护
- [x] 补并发动态调整测试
- [x] 补抖音兜底异常场景测试
- [x] 为 yt-dlp 长下载进程增加无输出卡住保护

### Phase 2 — 用户体验提升
- [ ] 完善 ComboBox 下拉样式（暗色下拉面板）
- [ ] 统一按钮图标和操作区视觉风格
- [x] 增强设置页安装进度和错误提示
- [ ] 系统级通知或托盘入口

### Phase 3 — 高级功能
- [ ] 下载暂停/恢复
- [ ] 播放列表识别与批量导入
- [ ] 应用自动更新检查

### Phase 4 — 工程化
- [x] 添加单元测试项目
- [x] 添加 Windows 发布脚本与 EasyGet.exe smoke 检查
- [x] 固定主项目 NuGet 依赖版本
- [ ] 集成日志框架 (Serilog)
- [ ] 安装包配置（MSIX / Inno Setup）
- [ ] 国际化支持
