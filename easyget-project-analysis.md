# EasyGet 项目分析与开发进度追踪

基于 yt-dlp 的全平台视频下载 WPF 桌面应用，采用 .NET 8 + MVVM 架构，当前处于 **v1.0.0 初始功能开发阶段**，核心下载流程已基本搭建完成，但存在多项功能缺失和质量短板。

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
| `EnvironmentService.cs` | yt-dlp/ffmpeg 环境检测与自动安装 | ✅ 完成 | 有下载进度但无百分比展示 |
| `HistoryService.cs` | SQLite 历史记录 CRUD | ✅ 完成 | |
| `YtDlpService.cs` | yt-dlp 命令封装（获取信息/下载/进度解析） | ✅ 完成 | 核心服务 |
| `DownloadManager.cs` | 下载队列管理器（并发控制） | ⚠️ 部分完成 | `UpdateConcurrencyLimit` 未实现 |

### 2.3 ViewModels 层 — ⚠️ 基本完成，部分逻辑待补

| 文件 | 职责 | 状态 | 备注 |
|---|---|---|---|
| `MainViewModel.cs` | 导航与全局状态 / 启动初始化 | ✅ 完成 | |
| `DownloadViewModel.cs` | 单视频下载页逻辑 | ✅ 完成 | |
| `BatchDownloadViewModel.cs` | 批量下载页逻辑 | ⚠️ 基本完成 | 缺少单任务取消按钮的绑定 |
| `HistoryViewModel.cs` | 历史记录页逻辑 | ✅ 完成 | |
| `SettingsViewModel.cs` | 设置页逻辑（自动保存） | ✅ 完成 | |

### 2.4 Views 层 — ⚠️ 布局完成，交互细节待完善

| 文件 | 职责 | 状态 | 备注 |
|---|---|---|---|
| `MainWindow.xaml` | 主窗口 + 侧边栏导航 + ContentControl 页面路由 | ✅ 完成 | |
| `DownloadView.xaml` | 单视频下载 UI | ✅ 完成 | |
| `BatchDownloadView.xaml` | 批量下载 UI（URL 输入 + 队列列表） | ⚠️ 基本完成 | 队列中缺少取消单任务按钮 |
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
- [x] **批量下载** — 多 URL 文本输入、链接计数、队列展示
- [x] **历史记录** — SQLite 持久化、搜索、单条删除、清空、打开文件夹
- [x] **设置页** — 环境检测状态、默认下载路径/格式/画质、代理开关与地址、并发分片数/同时下载数
- [x] **环境自动检测** — 启动时自动检测 yt-dlp/ffmpeg，缺失则自动下载安装
- [x] **yt-dlp 集成** — `--dump-json` 获取视频信息、完整下载参数构建、实时进度解析
- [x] **代理支持** — HTTP / SOCKS5 代理传递给 yt-dlp
- [x] **暗色主题** — Catppuccin Mocha 色板，自定义控件模板
- [x] **全局异常捕获** — `AppDomain.UnhandledException` + `DispatcherUnhandledException`，日志写入 `logs/` 目录
- [x] **配置持久化** — JSON 格式，设置变更自动保存
- [x] **剪贴板粘贴** — 单视频和批量页都支持

---

## 四、未实现 / 待完善功能

### 🔴 高优先级

1. **aria2c 加速未实现** — `AppConfig.UseAria2c` 已定义，设置页有开关，但 `YtDlpService` 中未使用 `--external-downloader aria2c` 参数
2. **并发数动态调整未实现** — `DownloadManager.UpdateConcurrencyLimit()` 方法体为空注释 `// SemaphoreSlim 不支持动态修改`
3. **下载完成后 IsDownloading 状态不准确** — `DownloadViewModel.StartDownload()` 中 `EnqueueAsync` 返回后即检查状态，但下载实际在 `Task.Run` 中异步执行，状态可能不正确
4. **窗口位置/大小持久化未实现** — `AppConfig.WindowState` 已定义但 `MainWindow` 未读写
5. **批量下载队列缺少单任务取消按钮** — `BatchDownloadViewModel.CancelTaskCommand` 已定义但 View 中未绑定

### 🟡 中优先级

6. **yt-dlp 更新功能缺失** — 无 `yt-dlp -U` 更新入口
7. **下载暂停/恢复未实现** — `DownloadStatus.Paused` 已定义但无暂停逻辑
8. **导航按钮无选中高亮** — `NavIndexToBackgroundConverter` 已定义但 `MainWindow.xaml` 导航按钮未使用
9. **日志自动滚动到底部未实现** — `DownloadView.xaml` 中 LogList 无自动滚动行为
10. **ComboBox 下拉样式不完整** — `DarkComboBox` 只设了基础属性，未自定义 Popup/Item 模板，下拉框可能白底
11. **无任务完成通知** — 缺少系统托盘通知或 Toast 提示
12. **缺少错误重试机制** — 下载失败后无重试入口

### 🟢 低优先级

13. **无单元测试** — 项目无测试项目
14. **无应用图标** — `Assets/` 目录为空，无 `.ico` 文件
15. **无安装程序/发布配置** — 缺少 MSIX / Inno Setup / 单文件发布配置
16. **无国际化 (i18n)** — UI 文字硬编码中文
17. **无日志框架** — 仅有崩溃日志，无运行时日志（如 Serilog）
18. **无自动更新机制** — 应用自身无检查更新功能
19. **缩略图未展示** — `DownloadTask.ThumbnailUrl` 已获取但 UI 未显示
20. **播放列表支持不完整** — yt-dlp 支持播放列表 URL，但 UI 无播放列表专属处理

---

## 五、代码质量评估

### 优点
- MVVM 职责分离清晰，代码组织规范
- CommunityToolkit.Mvvm 源生成器用法正确
- 注释完整（XML 文档注释覆盖所有公共 API）
- 异步编程模式使用得当
- 全局异常处理机制到位

### 待改进
- **线程安全** — `DownloadManager.EnqueueAsync` 中 `Task.Run` 内直接修改 UI 绑定属性，依赖 `ObservableObject` 的线程行为，但 `ObservableCollection<DownloadTask>` 的 `Add` 在 UI 线程调用尚可，后续操作在后台线程可能引发异常
- **资源释放** — `YtDlpService` 中的 `HttpClient` 未托管（在 `EnvironmentService` 中），`Process` 对象的 `Kill` 异常处理过于宽泛
- **硬编码** — 格式/画质的中文显示文本和内部值转换分散在多个 ViewModel 中，存在重复
- **异常处理** — 多处 `catch { }` 空捕获，可能隐藏问题

---

## 六、建议开发路线图

### Phase 1 — Bug 修复与功能补全（当前阶段）
- [ ] 修复 `IsDownloading` 状态追踪问题
- [ ] 实现 aria2c 外部下载器集成
- [ ] 实现窗口位置/大小持久化
- [ ] 批量下载视图添加单任务取消按钮
- [ ] 导航按钮添加选中高亮

### Phase 2 — 用户体验提升
- [ ] 日志自动滚动
- [ ] 完善 ComboBox 下拉样式（暗色下拉面板）
- [ ] 添加应用图标
- [ ] 缩略图展示
- [ ] 下载完成 Toast 通知
- [ ] 失败任务重试按钮

### Phase 3 — 高级功能
- [ ] 下载暂停/恢复
- [ ] yt-dlp 自动更新（`yt-dlp -U`）
- [ ] 播放列表识别与批量导入
- [ ] 应用自动更新检查

### Phase 4 — 工程化
- [ ] 添加单元测试项目
- [ ] 集成日志框架 (Serilog)
- [ ] 发布配置（单文件 / MSIX）
- [ ] 国际化支持
