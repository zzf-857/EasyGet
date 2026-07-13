# 智能 Cookie 与批量下载验收记录

## 记录规则

- 验收日期：2026-07-14（Asia/Shanghai）
- 只记录平台、场景、结果和脱敏错误类别；不记录账号、Cookie、完整浏览器配置路径、私人链接或网页表单内容。
- 自动化结果必须能对应到仓库测试；真实站点只使用公开、无需账号的测试资源。
- 需要个人账号或会改变外部登录状态的场景不擅自执行，保留为用户侧验收项。

## 自动化验收

| 完成 | 场景 | 结果 | 证据 / 备注 |
|---|---|---|---|
| [x] | Cookie 源码不记录参数密钥且不使用永久全局 `cookies.txt` | 通过 | `YtDlpCookieTests.CookieImplementation_DoesNotLogSecretsOrUsePermanentGlobalCookieFile` |
| [x] | 20 条链接在首条元数据阻塞时立即全部入队 | 通过 | `BatchDownloadViewModelTests.StartBatchDownload_AddsAllTasksBeforeMetadataResolutionCompletes` |
| [x] | 元数据有界并发且与下载并发解耦 | 通过 | `DownloadManagerTests.EnqueueAsync_MetadataWorkersContinueWhileDownloadsWaitForConcurrency` |
| [x] | 20 条同平台任务共享一次托管认证 | 通过 | `DownloadManagerTests.EnqueueAsync_TwentySamePlatformTasksReuseOneManagedAuthentication` |
| [x] | 排队中的元数据任务可立即取消 | 通过 | `DownloadManagerTests.Cancel_QueuedMetadataTaskUpdatesImmediatelyAndSkipsResolution` |
| [x] | 异常信息不显示 Cookie 或浏览器完整路径 | 通过 | Cookie 设置与 DownloadManager 脱敏回归测试 |
| [x] | 两个平台认证互不阻塞 | 通过 | `CookieAcquisitionCoordinatorTests` 的跨平台并发用例与设置页并发登录用例 |
| [x] | WebView2 后台进入 WPF 可视树再初始化 | 通过 | `ManagedLoginSessionServiceTests.ManagedLoginWindow_PreparesWithoutActivatingBeforeInitializingWebView2`；初始化与已保存会话检查完成前禁用按钮并拦截关闭，环境和 WebView2 初始化均响应最后等待者取消 |
| [x] | 普通站点 Cookie 不冒充已登录会话 | 通过 | YouTube `NID` / `CONSENT` 回归测试；只有平台认证 Cookie 才可复用或标记成功 |
| [x] | 手动 Cookie 不进入明文配置或备份 | 通过 | 设置页与 `ConfigService` 双层回归；保存后进入按平台加密仓库并清空 VM / AppConfig 明文 |
| [x] | 未归属平台的旧版 Cookie 无损迁移 | 通过 | 退出前先写入 DPAPI 加密隔离项，失败则保留原持久副本；重启只回填内存，选择平台后完成迁移 |
| [x] | 关闭智能模式仍使用主动导入的手动 Cookie | 通过 | 只关闭浏览器探测和托管登录；匿名后仍尝试加密手动 Cookie |
| [x] | 最后一个批量等待者取消会关闭共享登录 | 通过 | 单个消费者取消不影响其他任务；最后消费者取消底层请求，新任务可重新发起 |
| [x] | 任务卡错误文本脱敏 | 通过 | 浏览器配置路径、临时 Cookie 路径、完整 Cookie / Authorization 请求头和常见凭据赋值在进入 `ErrorMessage` 前统一隐藏 |
| [x] | 手动 Cookie 保存状态与实际状态一致 | 通过 | 保存后显示对应平台或“已按域名拆分”，成功提示使用成功色；不再因输入框清空而显示“未配置” |

## 真实站点与浏览器验收

| 完成 | 场景 | 结果 | 脱敏备注 |
|---|---|---|---|
| [x] | 公共 YouTube 视频：匿名解析并下载 | 通过 | 使用公开短视频 `jNQXAC9IVRw`；`--ignore-config` 且未提供 Cookie，生成 629,172 字节 MP4 |
| [ ] | 公共 Bilibili 视频：匿名解析并下载 | 平台阻断 | 两个公开 BV 链接均在匿名元数据阶段返回 HTTP 412；未借用个人浏览器会话，不把平台风控误记为通过 |
| [ ] | 抖音短链接解析与下载 | 未执行 | 缺少稳定、可公开记录且无需账号的固定测试链接 |
| [ ] | 已登录 YouTube 内容 | 用户侧验收 | 需要个人账号状态，不在自动验收中读取或记录 |
| [ ] | 已登录 X / Twitter 内容 | 用户侧验收 | 需要个人账号状态，不在自动验收中读取或记录 |
| [ ] | 已登录 Instagram 内容 | 用户侧验收 | 需要个人账号状态，不在自动验收中读取或记录 |
| [x] | Chrome 多配置发现 | 通过 | 发现 21 个配置，只记录数量，不记录路径 |
| [x] | Edge 多配置发现 | 通过 | 发现 1 个配置，只记录数量，不记录路径 |
| [x] | Firefox 多配置发现 | 通过 | 发现 2 个配置，只记录数量，不记录路径；合计与 UI 的 24 个一致 |
| [ ] | 浏览器打开且 Cookie 数据库被占用 | 自动化通过 / 手工待执行 | 应自动尝试下一个配置，不要求常规关闭浏览器 |
| [ ] | 会话过期 | 自动化通过 / 手工待执行 | 应降级并最多弹出一次平台登录窗口 |
| [x] | 用户取消托管登录 | 通过 | 100% 缩放实机点击 YouTube 登录，434ms 内加载真实平台页；点击取消后仅登录窗关闭，主窗口回落为黄色“未完成登录” |
| [ ] | 两个平台同时认证 | 自动化通过 / 手工待执行 | 不应互相串行阻塞 |
| [ ] | 20 条同平台批量链接 | 自动化通过 / 手工待执行 | 只出现一个平台级认证请求 |

## 发布与 UI 验收

| 完成 | 场景 | 结果 | 证据 / 备注 |
|---|---|---|---|
| [x] | Release 全量测试与发布 | 通过 | 最新全量测试：795 通过、14 跳过、0 失败；任一 dotnet 原生命令非零退出时发布脚本立即失败；最终独立构建结果在合并前复验 |
| [x] | win-x64 发布目录和 EasyGet.exe 启动 | 通过 | 发布 smoke 与唯一命名预览实例均成功启动，不记录用户目录 |
| [x] | WebView2 Loader / 运行依赖打包 | 通过 | 发布脚本强制检查 Core、WPF 与发布根目录 Loader；`runtimes/win-x64/native` Loader 已在发布实物中核对存在 |
| [ ] | 设置页 100% / 125% / 150% 缩放 | 100% 通过 | 当前单显示器为 100%；未改动系统缩放。125% / 150% 由滚动与 XAML 布局回归补充，仍保留手工项 |
| [ ] | 托管登录窗 100% / 125% / 150% 缩放 | 100% 通过 | 100% 下标题、允许域名、真实网页、取消和继续按钮均可达；125% / 150% 保留手工项 |
| [x] | 截图不包含 Cookie、账号或完整浏览器路径 | 通过 | 保留脱敏的平台状态截图；含默认路径与已过时高级按钮布局的截图均已删除 |
