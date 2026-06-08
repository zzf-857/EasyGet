# EasyGet 迭代 Todo

本文档用于记录每个小迭代的完成状态、验证命令和提交说明。

## 进行中

- [ ] 继续评估下载成功率、下载速度、UI 现代化和测试覆盖，按低风险小步迭代。

## 已完成

- [x] 2026-06-09 为主要按钮样式补充禁用态视觉
  - 内容：为 `AccentButton` 和 `SurfaceButton` 增加 `IsEnabled=False` 视觉状态，禁用时降低透明度、恢复普通光标并关闭 hover overlay，让检测、安装、更新等忙碌态按钮更明确地不可操作。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ThemeStyleTests` 观察到两个按钮样式缺少禁用态触发器的测试失败；实现后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，56 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补充按钮禁用态样式`

- [x] 2026-06-09 增强设置页安装进度阶段展示
  - 内容：为设置页环境安装反馈增加 `InstallStatusStage`，将安装日志归类为“检测中 / 准备安装 / 下载中 / 解压中 / 完成 / 失败”等阶段，并在安装状态区域显示阶段标签和原始说明，方便用户快速判断当前卡在哪一步或失败原因。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~XamlBindingTests"` 观察到缺少 `DescribeInstallStatusStage` 的编译失败；实现后同命令 17 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，54 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`增强设置页安装进度展示`

- [x] 2026-06-09 补齐 Windows 发布脚本与 smoke 检查
  - 内容：新增 `scripts\publish-win-x64.ps1`，串联 restore、test、self-contained publish、`EasyGet.exe` 存在性 smoke 检查和可选 zip 打包；更新手动打包说明、README 与项目进度分析；将发布产物目录加入 `.gitignore`；固定主项目 NuGet 版本，避免 `8.*` 浮动导致测试/发布依赖冲突。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ReleaseScriptTests` 观察到脚本缺失和文档未引用的 2 个测试失败；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ProjectDependencyTests` 观察到 3 个 `8.*` 包版本未固定的测试失败；实现后两个目标测试均通过；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1 -Configuration Release -Runtime win-x64 -SkipZip` 成功，脚本内 44 个 Release 测试通过并生成 `EasyGet.exe`；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，44 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐 Windows 发布脚本`

- [x] 2026-06-09 在设置页暴露 aria2c 外部下载器开关
  - 内容：在性能设置区增加 `UseAria2c` Toggle 开关，沿用现有 `ToggleSwitch` 样式和自动保存流程，并补充说明“未安装 aria2c 时自动回退到 yt-dlp 内置下载器”，让已有加速配置真正可被用户操作。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests` 观察到缺少 `UseAria2c` Toggle 的测试失败；实现后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，41 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`在设置页增加 aria2c 开关`

- [x] 2026-06-09 为 yt-dlp 长下载进程增加无输出卡住保护
  - 内容：将长下载路径切到可复用的 `RunDownloadProcessAsync`，并发读取 stdout/stderr，进程超过 10 分钟没有任何输出时自动终止并写入失败诊断；保留取消时杀进程清理，成功退出时完整读取剩余输出，避免队列被沉默的 yt-dlp 子进程无限卡住。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProcessTests` 观察到缺少 `RunDownloadProcessAsync` 的编译失败；实现后同命令 4 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，40 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`为 yt-dlp 长下载增加卡住保护`

- [x] 2026-06-09 清理高 DPI manifest 构建警告
  - 内容：将高 DPI 配置迁移到 `EasyGet.csproj` 的 `ApplicationHighDpiMode=PerMonitorV2`，删除 manifest 中重复的 DPI 节点，让 Release build 输出保持干净。
  - 验证：`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，39 个测试全部通过。
  - 提交说明：`清理高 DPI 构建警告`

- [x] 2026-06-09 统一抖音兜底网络中断异常并清理临时文件
  - 内容：为抖音浏览器兜底直链下载增加响应中断测试；底层 `HttpRequestException` 会包装为更贴近下载语义的 `IOException`，同时删除 `.part` 和避免生成最终文件。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DouyinBrowserDownloadServiceTests` 观察到响应中断场景抛出 `HttpRequestException` 的测试失败；修复后同命令 8 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，39 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`统一抖音兜底网络中断处理`

- [x] 2026-06-09 补齐下载管理器并发上限归一化测试
  - 内容：为 `DownloadManager` 增加并发上限归一化 helper 和测试，构造函数与动态更新共用同一套 clamp 规则，避免 0、负数或超大并发值绕过配置归一化后进入队列管理器。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DownloadManagerTests` 观察到缺少目标 API 的编译失败；实现后同命令 4 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，38 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`补齐下载并发上限测试`

- [x] 2026-06-09 清理抖音兜底下载取消后的临时文件
  - 内容：为抖音浏览器兜底直链下载增加慢速响应取消测试；下载循环、字节校验或取消发生异常时删除 `.part` 临时文件，避免取消后下载目录残留半截文件。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DouyinBrowserDownloadServiceTests` 观察到取消后 `.part` 仍存在的测试失败；修复后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，34 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`清理抖音兜底取消残留文件`

- [x] 2026-06-09 修复抖音兜底下载 Range 被忽略时的文件追加错误
  - 内容：为抖音浏览器兜底直链下载增加 Range 忽略场景测试；当续传请求收到 `200 OK` 时重置 `.part` 并从头写入，避免把完整响应追加到半截文件后面；下载结束前校验已下载字节数与已知总长度一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DouyinBrowserDownloadServiceTests` 观察到最终文件被追加为 `abcdeabcdefghij` 的测试失败；修复后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，33 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`修复抖音兜底续传追加错误`

- [x] 2026-06-09 修复 yt-dlp 短命令取消后子进程残留
  - 内容：`YtDlpService.RunProcessAsync` 在用户取消时也会结束子进程并清理输出任务，避免短命令取消后后台进程继续执行。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProcessTests` 观察到取消后 marker 文件仍被写入的测试失败；修复后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，32 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`修复 yt-dlp 短命令取消清理`

- [x] 2026-06-09 为 yt-dlp 信息解析进程增加 stderr 与超时保护
  - 内容：新增 `YtDlpService.RunProcessAsync`，用于信息解析和播放列表导入等短命令；并发读取 stdout/stderr，避免 stderr 堵塞；默认 60 秒超时，超时后结束进程并抛出 `TimeoutException`。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProcessTests` 观察到缺少目标 API 的编译失败；实现后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，31 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`增强 yt-dlp 短命令执行可靠性`

- [x] 2026-06-09 为环境命令增加超时与 stderr 保护
  - 内容：`EnvironmentService.RunCommandAsync` 改为并发读取 stdout/stderr，避免 stderr 输出过多造成进程等待死锁；增加默认 30 秒超时，超时后结束进程并抛出 `TimeoutException`；保留 stderr 作为失败诊断输出。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~EnvironmentServiceTests` 观察到缺少目标 API 的编译失败；实现后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，29 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`增强环境命令执行可靠性`

- [x] 2026-06-09 校准 README 开发进度
  - 内容：同步 README 已完成/待完善列表，移除窗口持久化和日志自动滚动等过期待办，补充 aria2c 可用性检测、抖音兜底和后续端到端验证事项。
  - 验证：`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，27 个测试全部通过。
  - 提交说明：`校准 README 开发进度`

- [x] 2026-06-09 优化设置页环境按钮忙碌态与更新反馈显示
  - 内容：为设置页环境操作增加 `CanCheckEnvironment`、`CanInstallMissingTools`、`CanUpdateYtDlp` 三个可用状态，避免检测、安装、更新期间重复点击；将 yt-dlp 更新状态文本改为按 `UpdateStatusMessage` 内容显示，更新完成后仍能看到结果。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "FullyQualifiedName~XamlBindingTests"` 观察到 4 个测试失败；修复后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，27 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`优化设置页环境操作状态`

- [x] 2026-06-09 增加 aria2c 可用性检测与性能配置上下限保护
  - 内容：为并发分片数和批量同时下载数增加运行时 clamp；启用 aria2c 时优先查找内置 tools 目录和 PATH 中的 `aria2c.exe`，未找到则回退 yt-dlp 内置下载器并输出日志提示；新增配置归一化、PATH 查找和 aria2c 参数拼装测试。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "FullyQualifiedName~ConfigServiceTests|FullyQualifiedName~EnvironmentServiceTests|FullyQualifiedName~YtDlpArgsTests"` 观察到缺少目标 API 的编译失败；实现后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，23 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个错误，保留现有高 DPI manifest 警告。
  - 提交说明：`增加 aria2c 检测与并发配置保护`

- [x] 2026-06-09 修复批量与历史列表的平台标签可见性
  - 内容：将批量下载列表和历史列表的平台标签 `Visibility` 绑定从 `BoolToVisibility` 改为 `StringToVisibility`，修复非空平台名被错误折叠的问题；新增 XAML 回归测试防止后续误绑。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter PlatformLabelsUseStringVisibilityConverter` 观察到 2 个测试失败；修复后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，19 个测试全部通过。
  - 提交说明：`修复平台标签可见性绑定`

- [x] 2026-06-09 项目进度体检与当前可靠性改进收束
  - 内容：核对 README、进度追踪文档、未提交 diff 和测试入口；确认当前代码已补齐运行环境自动安装、设置页手动安装、yt-dlp 更新入口、aria2c 参数集成、并发上限动态调整、批量任务操作、Toast、日志自动滚动、窗口状态持久化、抖音浏览器兜底下载与相关测试。
  - 验证：`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，17 个测试全部通过。
  - 提交说明：`更新项目进度并收束环境安装与抖音兜底`

## 候选优化池

- [ ] 下载可靠性：为抖音浏览器兜底下载增加真实站点回归验证，降低大文件失败概率。
- [ ] UI 现代化：统一按钮图标和控件圆角/密度，降低 emoji 按钮带来的视觉不一致。
- [ ] UI 现代化：检查 ComboBox、列表项 hover、导航选中态与队列操作按钮的暗色主题一致性。
