# EasyGet 代码全面审查记录（2026-08-01）

## 1. 审查范围

本次审查基于 `main` 的 `2640e1f`（`v1.4.3`）以及其后的当前未提交工作区，重点检查：

- 代码冗余与重复实现；
- 代码规范、错误处理和测试方式；
- 单个下载、批量下载、输出路径、配置持久化之间的职责边界；
- 下载队列、异步输入、托盘、更新和备份恢复中的状态竞态；
- 新目的地模型和批量确认流程引入后的行为一致性。

本轮只记录问题，不修改生产代码。

已执行的验证：

- `dotnet build EasyGet.csproj -c Release --no-restore /warnaserror`：通过，0 警告、0 错误；
- `dotnet test EasyGet.Tests/EasyGet.Tests.csproj -c Release --no-restore`：1214 通过、1 跳过；
- `git diff --check`：通过；
- `dotnet format EasyGet.csproj --verify-no-changes --no-restore`：失败，存在格式不一致；
- 对下载、配置、备份、托盘、更新和两个下载 ViewModel 做了交叉代码审查。

测试全绿不能证明当前实现没有问题。下列多项缺陷恰好位于现有测试没有覆盖的真实应用拓扑、并发时序或外部进程边界中。

## 2. 严重度定义

- **P1**：可能造成数据丢失、文件损坏、任务永久卡住或程序无法恢复，正式发布前应优先处理。
- **P2**：会产生错误状态、输入丢失、行为不一致或明显的用户功能故障，应在下一轮集中修复。
- **P3**：主要是职责混乱、重复实现、死代码、规范和测试质量问题，会持续放大后续改动风险。

## 3. P1 问题

### P1-01 应用内恢复备份时，历史数据库仍被长期连接占用

证据：

- `Services/HistoryService.cs:19-44` 在应用整个生命周期持有 SQLite 连接；
- `Services/UserDataBackupService.cs:606-645` 直接使用 `File.Replace` / `File.Move` 替换 `history.db`；
- `ViewModels/SettingsViewModel.cs:1783-1823` 允许应用运行期间直接恢复。

Windows 下保持当前项目同版本 `Microsoft.Data.Sqlite` 连接打开时替换数据库，可复现 `IOException`（文件正被使用）。现有恢复测试在替换前已经释放 `HistoryService`，没有模拟真实应用状态。下载仍在写历史时执行恢复，风险更高。

建议边界：恢复必须成为应用级事务。优先考虑退出后由辅助进程替换并重启；否则需要统一协调器暂停下载、取得历史写锁、关闭并重建 SQLite 连接，再执行替换。

### P1-02 恢复后的设置会在正常退出时被旧内存状态覆盖

证据：

- `ViewModels/SettingsViewModel.cs:1813-1814` 恢复后只提示重启，没有重载 `ConfigService` 和各 ViewModel；
- `MainWindow.xaml.cs:112-113` 退出前必定刷新 Settings 自动保存并再次保存当前内存配置；
- `ViewModels/SettingsViewModel.cs:1169-1329` 会把恢复前的 UI 状态重新写入 `config.json`。

结果是恢复文件即使替换成功，下载路径、合集目录及其他非敏感设置也可能在用户正常退出时被旧值覆盖。恢复前已经排队的自动保存任务也存在相同竞态。

建议边界：恢复成功后应进入“不再写旧配置”的退出重启流程，或在同一事务中取消自动保存、重载配置并刷新所有配置消费者。

### P1-03 普通 yt-dlp 下载没有输出路径预留

证据：

- `Services/DownloadManager.cs:1603` 的普通 yt-dlp 分支直接调用下载服务；
- `Services/YtDlpService.cs:737-740` 固定生成 `标题.%(ext)s`；
- `Services/DownloadOutputPathReservation.cs` 当前只用于原生 M3U8 和 M3U8 回退。

影响：

- 用户确认“仍然重新下载”时，yt-dlp 可能只报告 `has already been downloaded` 并成功退出，实际没有产生新文件；
- 批量任务中不同 URL 但相同标题会共享最终路径和临时分片路径，可能互相覆盖、互相失败，或两个任务都指向同一文件；
- 单个和批量虽然进入了同一个 `DownloadManager`，但普通 yt-dlp 与 M3U8 的输出冲突策略仍不一致。

建议边界：所有下载引擎在启动外部写入前必须通过统一的输出身份/预留服务获得最终 basename，并把实际产物校验纳入同一协议。

### P1-04 M3U8 分片在收到响应头后可以永久卡住

证据：

- `Services/M3u8DownloadService.cs:627` 使用 `ResponseHeadersRead`；
- `Services/M3u8DownloadService.cs:630-636` 读取正文时只有用户取消令牌，没有单分片空闲超时。

`HttpClient.Timeout` 在 `ResponseHeadersRead` 场景下只覆盖收到响应头之前。服务端返回 200 和响应头后停止发送正文时，worker 会永久停在 `ReadAsync`，重试逻辑不会触发，`Task.WhenAll` 也不会结束。

建议边界：为每次正文读取建立可重置的空闲超时，并把超时纳入分片重试与用户可见错误。

### P1-05 原生 M3U8 会错误处理合法的 fMP4 和字节范围 HLS

证据：

- `Services/M3u8DownloadService.cs:353-381` 只拒绝主播放列表和加密标签，其余标签被忽略；
- 没有处理 `#EXT-X-MAP` 和 `#EXT-X-BYTERANGE`；
- `Services/M3u8DownloadService.cs:296-315` 在 ffmpeg 失败后仍可能把拼接结果改名并标记完成。

缺少 `EXT-X-MAP` 会漏掉 fMP4 初始化分片；忽略 `BYTERANGE` 会重复下载并拼接完整资源。这两类合法播放列表都可能生成损坏文件，最终状态仍可能是 `Completed`。

建议边界：不要继续扩展手写 HLS 解析器。优先把成熟的 ffmpeg/yt-dlp 作为 HLS 协议引擎；若保留原生实现，必须显式拒绝所有未支持标签，而不是静默忽略。

### P1-06 Explorer 重启后托盘图标不会重新注册

证据：

- `Services/TrayIconService.cs:43-49` 只要 `_isAdded` 为真就直接返回；
- `Services/TrayIconService.cs:129` 忽略 `NIM_MODIFY` 的失败返回值；
- `Services/TrayIconService.cs:183-207` 没有处理任务栏重建消息；
- `MainWindow.xaml.cs:99-105` 仍会依据陈旧的 `_isAdded` 隐藏窗口。

Explorer/任务栏重启后 Shell 会丢失图标，但服务仍认为图标存在。此时关闭窗口会把程序隐藏到不存在的托盘入口，用户无法恢复窗口或正常退出。

建议边界：注册并处理 `TaskbarCreated`，在 Shell 重建后重新 `NIM_ADD`；任何 Shell 调用失败都必须使 `_isAdded` 失效。

### P1-07 应用允许多实例，但共享状态和输出保护只按单进程设计

证据：

- `App.xaml.cs:21-136` 没有单实例互斥或实例转发；
- 本次审查期间可以同时运行两个不同路径的 `EasyGet.exe`；
- `Services/TaskQueuePersistenceService.cs:338-344` 所有实例共享同一个 `queue-state.json`；
- `Services/TaskQueuePersistenceService.cs:188-224` 只有进程内写锁，没有跨进程锁；
- `DownloadOutputPathReservation` 的保留集合也是进程内静态集合。

两个实例会互相覆盖恢复队列；配置文件虽然有跨进程文件锁，但每个实例持有独立的旧内存快照，最后保存者仍会覆盖另一实例的新设置。不同实例下载同名文件时，输出预留也完全失效。

建议边界：桌面产品优先改成单实例，并把第二实例的 URL/激活请求转发给首实例。若必须支持多实例，则所有队列、配置和输出协议都要升级为跨进程事务。

## 4. P2 问题

### P2-01 yt-dlp 退出码为 0 时没有强制校验实际产物

证据：

- `Services/YtDlpService.cs:651-665` 在退出码为 0 时直接标记完成；
- `Services/YtDlpService.cs:1303-1333` 没有覆盖全部后处理输出消息；
- `Services/YtDlpService.cs:1336-1378` 捕获失败后扫描整个目录并取最近写入的同扩展文件。

音频提取、重封装或并发任务中，当前任务可能记录到另一任务的文件；即使最终没有找到任何文件，也仍会生成“完成”状态和空路径历史。

### P2-02 原生 M3U8 完全忽略用户选择的格式

`Services/M3u8DownloadService.cs:64-75` 始终输出 `.mp4`，不读取 `DownloadTask.Format`。用户选择 `mkv/webm/mp3/m4a` 时仍得到 MP4，ffmpeg 不可用时甚至只是扩展名为 MP4 的 TS 流；历史记录却继续保存用户原先选择的格式。

### P2-03 “全部取消”可能把已完成任务改成已取消

`Services/DownloadManager.cs:835-855` 中下载服务会先把任务设为完成，再异步写历史；attempt 此时仍处于活动集合。`Services/DownloadManager.cs:1356-1369` 的 `CancelAll` 可在这个窗口请求取消，finally 清理随后把状态覆盖成 `Cancelled`。数据库已有完成记录，但队列和通知显示取消。

### P2-04 Telegram 多个网络调用不能被取消

`Services/TelegramDownloadService.cs:548`、`:567`、`:729`、`:734`、`:857` 的会话、用户名、消息 RPC 和部分传输没有贯穿取消令牌。RPC 卡住且没有新的进度回调时，点击取消不能立即中断。

### P2-05 慢播放列表导入会覆盖用户后输入的新内容

`ViewModels/BatchDownloadViewModel.cs:1152-1164` 使用 `CancellationToken.None`，没有输入版本快照；`ApplyPlaylistImport` 在旧请求返回后直接覆盖 `UrlsText` 并清空 `PlaylistUrl`。请求期间两个输入框仍可编辑，旧请求会覆盖更新内容。

### P2-06 批量创建期间仍接受拖放，新输入会被旧流程清空

`Views/BatchDownloadView.xaml.cs:63-113` 始终接受拖放，`BatchDownloadViewModel.ImportText` 没有忙碌保护。点击“确认并下载”后，在历史读取或入队期间拖入新链接，旧批次成功后 `ViewModels/BatchDownloadViewModel.cs:1073-1081` 无条件清空 `UrlsText`，新输入丢失。

### P2-07 单个任务被批量页移除后，重试会进入永久假运行状态

`ViewModels/BatchDownloadViewModel.cs:1235-1254` 和 `:1389-1400` 可以从共享集合移除终态任务；单个页仍保留同一个 `CurrentTask`。`ViewModels/DownloadViewModel.cs:907-915` 会先设为下载中，再调用已经找不到任务的 `RetryAsync`，后续没有状态回调，界面一直显示下载中。

### P2-08 下载更新包时仍可并发执行“检查更新”

`ViewModels/SettingsViewModel.cs:446` 已有 `CanCheckAppUpdate`，但 `Views/SettingsView.xaml:535` 没有用它约束按钮/命令。下载期间再次检查会清空或替换 `_availableAppUpdate`；旧下载和新元数据可能交叉，版本文案、安装路径和实际文件不再对应。

### P2-09 批量确认没有快照格式和画质

标题和目标目录在异步步骤前已快照，但 `ViewModels/BatchDownloadViewModel.cs:1007-1020` 在历史读取之后才读取 `SelectedFormat` / `SelectedQuality`。`Views/BatchDownloadView.xaml:151-156` 的控件也没有在提交期间禁用。用户点击确认后快速修改选项，会改变已经确认的批次。

### P2-10 启动更新安装程序的异常会冒泡到全局 UI 异常处理

`Services/AppUpdateService.cs:223-235` 的 `Process.Start` 没有转换 `Win32Exception` 等错误；`ViewModels/SettingsViewModel.cs:1938-1945` 只处理 false。AppLocker、UAC 取消或文件被安全软件隔离时，更新状态会停在“正在准备安装”，异常交给全局处理。

### P2-11 暂时离线的合集目录会被永久清空

`Services/ExistingCollectionFolderStore.cs:326-381` 会过滤 `Directory.Exists == false` 的目录；随后两个页面在 `DownloadViewModel.cs:460-486`、`BatchDownloadViewModel.cs:493-519` 把“当前找不到”解释成“用户选择临时下载”，清空并保存 `SelectedCollectionDirectory`。移动盘拔出或网络共享暂时不可达后启动一次，最后选择永久丢失。同步 `Directory.Exists` 还可能在 UNC 不可达时阻塞 UI 初始化。

### P2-12 旧版本恢复可能被 `config.backup.json` 静默覆盖

`Services/UserDataBackupService.cs:37-41` 只恢复 `config.json`，不处理 `config.backup.json`；`Services/ConfigService.cs:602-617` 会优先选择配置版本更高的备份副本。恢复 v3 用户备份到已有 v4 本地备份的环境时，重启会忽略刚恢复的设置。

### P2-13 合集目录上限的两套规则互相冲突

`Services/ConfigService.cs:771-785` 先 `Take(100)`，随后可能把当前选择追加为第 101 项；`Services/UserDataBackupService.cs:499` 又拒绝超过 100 项。第 101 个合集会造成备份失败，之后归一化还可能丢掉最新目录。

### P2-14 显式标题批量输入会跳过元数据并留下空平台

`ViewModels/BatchDownloadViewModel.cs:857-864` 对 `标题---URL` 直接构造只有 URL 和标题的 `VideoInfo`；`DownloadManager.cs:608` 将它视为已解析，`DownloadManager.cs:1712-1727` 因而把空 `Platform` 写入任务，最终历史也保存空平台。相关测试还明确要求元数据请求数为 0，因此这是被测试固化的数据质量问题。

### P2-15 批量链接去重把所有终态任务也视为“仍在队列”

`ViewModels/BatchDownloadViewModel.cs:778-785` 和 `:924-940` 用 `_downloadManager.Tasks` 的全部 URL 去重，包含完成、失败、取消任务。用户无法像单个下载一样再次确认重下，必须先手动清理共享队列；单个和批量行为不一致。

### P2-16 全局 UI 异常被一律标记为已处理并继续运行

`App.xaml.cs:27-40` 对所有 `DispatcherUnhandledException` 设置 `Handled = true`。未知异常可能已经破坏 ViewModel、集合或命令状态，继续运行会把局部故障变成静默错误或后续数据覆盖。应区分可恢复异常和致命异常，致命异常记录后安全退出。

### P2-17 批量入队后默认筛选会隐藏等待任务

`ViewModels/BatchDownloadViewModel.cs:657-669` 的“进行中”不包含 `Waiting`；`ViewModels/BatchDownloadViewModel.cs:1084` 入队后却自动切换到“进行中”。并发上限之外的大量等待任务会立刻从列表消失，用户容易误以为没有加入队列。

## 5. P3 结构、冗余与规范问题

### P3-01 单个页和批量页仍复制整套目的地状态机

`ViewModels/DownloadViewModel.cs:330-501` 与 `ViewModels/BatchDownloadViewModel.cs:361-527` 近乎逐行重复：浏览、刷新保护、配置事件、选择同步、失效目录处理、保存任务和命令刷新。两个 XAML 也分别复制同一目的地工具栏。

当前只是通过事件同步数据，并没有共享控制逻辑；以后改 A 漏 B 的风险依然存在。应抽出共享的 `DownloadDestinationCoordinator` 或独立子 ViewModel，并复用同一 View。

### P3-02 单个/批量任务准备仍未真正统一

两个页面仍各自完成格式映射、重复检测、预检、合集上下文、`DownloadTask` 构造和元数据复用：

- 单个：`ViewModels/DownloadViewModel.cs:577-821`；
- 批量：`ViewModels/BatchDownloadViewModel.cs:919-1096`。

`DownloadManager` 只统一了入队之后的部分。格式能力、重复规则和快照时机已经出现差异。应建立共享的不可变 `DownloadRequest`、任务工厂和提交服务，让 UI 只负责收集输入。

### P3-03 多个核心类过大并混合过多职责

当前主要大类行数：

- `ViewModels/SettingsViewModel.cs`：约 2150 行，混合设置映射、Cookie 登录、环境安装、更新、备份、Telegram 和自动保存；
- `ViewModels/HistoryViewModel.cs`：约 2100 行，混合查询、分组、缩略图、目录、拖放、删除、清单解析和 Shell 启动；
- `Services/DownloadManager.cs`：约 1870 行，混合队列、调度、解析 worker、attempt 状态机、引擎路由、历史和持久化；
- `Services/DouyinSpecialDownloadService.cs`：约 1730 行，混合协议模型、解析、校验、脱敏和进程运行；
- `Services/YtDlpService.cs`：约 1619 行，混合元数据、播放列表、参数、进程、输出解析和站点回退；
- `ViewModels/BatchDownloadViewModel.cs`：约 1434 行；
- `ViewModels/DownloadViewModel.cs`：约 1304 行。

这些类难以独立验证，任何局部改动都需要理解多个子系统。下一轮重构应按职责拆分，而不是继续在原类中增加布尔状态和分支。

### P3-04 `DownloadManager.Tasks` 暴露为可变集合

`Services/DownloadManager.cs:38-40` 公开 `ObservableCollection<DownloadTask>`；批量 ViewModel 在 `:1253`、`:1398-1399` 直接删除任务，绕过管理器状态机。P2-07 正是这种所有权不清造成的跨页面故障。

应对外只暴露只读集合，所有添加、移除、清理和重试都通过管理器命令完成，并由管理器发出一致事件。

### P3-05 单个下载页维护三套任务状态

`DownloadTask.Status`、`DownloadViewModel.PageState` 和 `DownloadViewModel.IsDownloading` 都在表达同一任务生命周期，`DownloadViewModel.cs:771-803` 依靠手写映射同步。再加上可被其他页面删除的 `CurrentTask`，状态很容易分叉。应让页面状态从单一任务状态和独立的解析草稿状态派生。

### P3-06 目的地存在四份“真相”

两个下载 ViewModel 同时维护：

- `ConfigService.Config.SelectedCollectionDirectory`；
- `_downloadRootDirectory`；
- `SelectedCollectionFolder`；
- 可写的 `DownloadDirectory`。

再加刷新期间的临时字段和两个布尔保护，形成复杂的双向同步。应保留一个不可变目的地值对象（临时根目录或合集目录），显示属性全部派生。

### P3-07 路径归一化和安全判断存在多份实现

重复点包括：

- `ConfigService.PathsEqual/NormalizeOptionalDirectory`；
- `ExistingCollectionFolderStore.PathsEqual`；
- `DownloadManager.AreEquivalentPaths/IsSafeOutputFilePath`；
- `DouyinSpecialDownloadService.AreEquivalentPaths/IsSafeOutputFilePath`；
- `HistoryViewModel.AreEquivalentPaths`；
- `DownloadFileDeletionService` 与 `HistoryDirectoryDiscoveryService` 各自的 root containment。

这些实现的大小写、根目录自身是否允许、异常回退策略并不完全相同。应收敛为一个经过专项测试的 `PathPolicy`。

### P3-08 业务状态大量依赖中文展示字符串

格式、画质、字幕、队列筛选等逻辑通过中文字符串 switch：

- `DownloadViewModel.cs:115-117`、`:1015-1028`、`:1239-1255`；
- `BatchDownloadViewModel.cs:135-137`、`:657-669`、`:1007-1020`。

修改文案或本地化会改变业务逻辑。单个页支持 `2160p/m4a`，批量页不支持，也已经发生能力漂移。应使用枚举/值对象保存 ID，展示文本只由 converter 或 option record 提供。

### P3-09 Settings 的加载和保存是两份巨大的手工映射

`SettingsViewModel.cs:544-620` 从 `AppConfig` 复制到 UI，`:1169-1329` 再反向复制，后面还有几十个 `OnXChanged => AutoSave()`。新增字段时很容易只改一个方向。应拆成分区设置模型，并对映射做结构化测试或显式 mapper。

### P3-10 新目的地流程存在重复磁盘写入和事件级联

浏览新目录时，`ExistingCollectionFolderStore.RegisterCollectionAsync` 先注册并保存一次；随后设置 `SelectedCollectionFolder` 又触发 `UpdateSelectedCollectionDirectory + SaveAsync`。`CollectionDirectoriesChanged` 还会安排一次额外刷新。一次点击至少产生两次配置写和多轮刷新。

### P3-11 存在已经不再被 UI 消费的属性和通知

- 两个 ViewModel 的 `CanSelectExistingCollectionFolder` 仍有属性、通知和测试，但 XAML 已改绑 `CanEdit...`；
- `ExistingCollectionFolderStore.Placeholder` 仍计算“正在读取/暂无合集”，两个 ViewModel 的 placeholder 已自行返回“临时下载 · 路径”，却仍监听并转发 store placeholder 变化；
- `AutoCategorizeByPlatform` 只作为兼容字段保留，实际下载逻辑已移除。

应删除死属性，或明确把兼容字段隔离到序列化 DTO，避免继续污染运行时模型。

### P3-12 抖音专项子系统处于“维护但未接线”的中间状态

`DouyinSpecialDownloadService`、sidecar、几十个 `AppConfig`/Settings 属性和大量测试仍在维护，但 `DownloadManagerTests.cs:458-489` 明确固化“即使启用专项开关也始终走 yt-dlp”。当前 XAML 只显示 sidecar 健康检查，没有对应配置入口。

应做明确决策：完成接线并提供受支持的 UI/路由，或把实验性子系统移到独立模块并停止让主配置、主 ViewModel 和历史模型承担它的复杂度。

### P3-13 缺少统一格式规范，仓库当前不能通过 formatter

仓库没有 `.editorconfig`、`Directory.Build.props` 或统一规则文件。`dotnet format --verify-no-changes` 当前在 `AssemblyInfo.cs`、`M3u8DownloadService.cs`、`TelegramDownloadService.cs` 等处报告空白格式错误。CI 只要求编译和测试，无法阻止格式继续漂移。

### P3-14 大量测试在断言源码文本，而不是行为

至少 19 个测试文件读取生产源码，约 273 处匹配/断言源码字符串。例如 `EasyGet.Tests/YtDlpProcessTests.cs:23-30` 只保证使用 `Directory.EnumerateFiles`，并不能证明并发时找到了当前任务的文件。

这种测试容易把某种实现写法固化，同时放过真实行为缺陷。源码规则只应保留少量架构约束；下载、恢复、取消和异步输入必须使用可控 fake、临时目录或进程集成测试验证结果。

### P3-15 宽泛或静默异常处理掩盖故障

生产代码中存在大量 `catch (Exception)`，还有完全空的 catch，例如：

- `Views/BatchDownloadView.xaml.cs:104`；
- `ViewModels/HistoryViewModel.cs:1736`；
- `ViewModels/DownloadViewModel.cs:813`、`:872`、`:900`；
- `ViewModels/BatchDownloadViewModel.cs:550`、`:602`、`:1282`。

Shell 启动、剪贴板、队列刷新和恢复失败经常只被静默忽略，用户无法判断命令未生效，日志也缺少上下文。应只捕获可恢复异常，并统一进入日志与用户通知策略。

### P3-16 README 与当前行为不一致

`README.md:60` 和 `:188` 仍声明“按平台自动归类保存”，但当前 `DownloadManager` 已移除平台子目录逻辑，`AppConfig.AutoCategorizeByPlatform` 的注释也说明只为兼容保留。抖音设计文档中同样存在旧路由描述。

下一轮修复时应把行为文档、README、设置模型和实现一起更新，避免安装用户按不存在的规则理解下载目录。

## 6. 必须补充的测试

下一轮修改至少应新增以下行为测试，而不是源码字符串断言：

1. 保持真实 `HistoryService` 连接打开时执行恢复；
2. 恢复完成后走正常退出，再启动验证配置和历史；
3. 存在更高 `ConfigVersion` 的 `config.backup.json` 时恢复旧备份；
4. 同目录并发两个同标题 yt-dlp 任务，验证两个独立产物及历史路径；
5. 对已有文件确认重新下载，验证产生新文件而不是复用旧文件；
6. yt-dlp 退出 0 但无产物，以及后处理输出路径场景；
7. M3U8 响应头后停止发送正文，验证空闲超时和重试；
8. 含 `EXT-X-MAP`、`EXT-X-BYTERANGE` 的播放列表；
9. M3U8 的 mp3/m4a/mkv/webm 格式契约；
10. `CancelAll` 与历史写入交叉的完成状态竞态；
11. Telegram RPC/传输卡住时取消；
12. 阻塞播放列表导入后修改输入，旧结果不得覆盖新输入；
13. 阻塞批量提交时拖入新链接，新输入不得被清空；
14. 单个失败任务被批量页移除后再重试；
15. 下载更新包期间并发检查更新；
16. 批量确认后修改格式/画质，验证使用确认时快照；
17. Explorer 重启或 Shell 图标丢失后的托盘重注册；
18. 两个实例同时启动时的单实例转发或明确拒绝；
19. 移动盘/UNC 暂时不可用后仍保留最后选择；
20. 第 100/101 个合集目录的保存、恢复和备份边界。

## 7. 建议的后续迭代顺序

### 第一阶段：数据和文件正确性

1. 重做备份恢复事务；
2. 增加单实例机制；
3. 统一所有引擎的输出预留、实际产物验证和历史写入；
4. 处理或移除手写 HLS 协议分支。

### 第二阶段：异步状态正确性

1. 修复批量输入/播放列表/格式快照竞态；
2. 修复队列所有权、单页重试和 CancelAll 状态竞态；
3. 修复托盘重建和更新命令互斥；
4. 为 Telegram 和所有外部调用贯穿取消令牌。

### 第三阶段：结构收敛

1. 抽取共享目的地协调器和复用 View；
2. 引入 `DownloadRequest`、任务工厂和统一提交服务；
3. 把 `DownloadManager`、Settings、History、yt-dlp 和抖音 sidecar 按职责拆分；
4. 收敛路径策略、状态类型和配置映射。

### 第四阶段：工程规范

1. 增加 `.editorconfig` 和 CI formatter gate；
2. 将源码文本测试替换为行为测试；
3. 清理死属性、兼容字段和宽泛 catch；
4. 同步 README、设计文档和实际下载行为。

## 8. 当前结论

当前工作区的 Release 构建和 1214 个测试均通过，但仍存在多项 P1/P2 级真实缺陷。普通代码推送可以作为后续修复基线，但不建议在解决 P1-01 至 P1-07 前创建新的正式版本 Release。
