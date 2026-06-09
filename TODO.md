# EasyGet 迭代 Todo

本文档用于记录每个小迭代的完成状态、验证命令和提交说明。

## 进行中

- [ ] 继续评估下载成功率、下载速度、UI 现代化和测试覆盖，按低风险小步迭代。

## 已完成

- [x] 2026-06-09 优化主窗口侧栏分隔与导航提示
  - 内容：为主窗口左侧导航栏增加 `BorderSubtleBrush` 右侧细分隔线，让侧栏和内容区边界更清晰；同时为 4 个导航 RadioButton 补齐 `ToolTip` 与 `AutomationProperties.Name`，提升悬浮提示和辅助技术识别效果，主框架第一屏更精致也更可用。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "FullyQualifiedName~MainWindowSidebarUsesSubtleContentDivider|FullyQualifiedName~MainWindowNavigationItemsExposeTooltipAndAutomationName"` 观察到侧栏缺少分隔线、4 个导航项缺少提示/无障碍名称的 5 个测试失败；实现后同命令 5 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests`，26 个测试全部通过；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，118 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`优化主窗口侧栏导航`

- [x] 2026-06-09 统一历史页现代卡片样式
  - 内容：将历史记录列表项外层卡片切换到 `ToolPanelBorder` 主题样式，保留原有紧凑内边距、文件存在状态透明度和文件路径提示，同时与下载页、批量页、设置页共用 8px 圆角、统一边框、柔和阴影和像素对齐的现代工具面板视觉；历史页与其他主页面的卡片语言更一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryViewUsesModernToolPanelStyleForHistoryCards` 观察到历史页历史卡片未使用 `ToolPanelBorder` 的测试失败；实现后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests`，21 个测试全部通过；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，113 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`统一历史页卡片样式`

- [x] 2026-06-09 清理粘贴链接尾随分享标点
  - 内容：修正 `DownloadViewModel.ExtractUrl` 的 URL 提取逻辑，干净 URL 和混合分享文案都会在识别后清理中文逗号、句号、右括号以及常见英文尾随标点；单视频下载和批量下载复用该入口，用户从 YouTube、抖音等分享文案中复制带标点的链接时，不再把标点传给 yt-dlp，降低粘贴后解析失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DownloadViewModelTests` 观察到 3 个带中文尾随标点的 clean URL 被原样返回的测试失败；实现后同命令 4 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，112 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`清理粘贴链接尾随标点`

- [x] 2026-06-09 元数据阶段复用浏览器 Cookie 策略
  - 内容：将 YouTube/抖音的 Cookie 策略构建抽成 `BuildCookieStrategies`，正式下载、视频信息解析和播放列表导入共用同一套“默认 Cookie → Chrome Cookie → Edge Cookie”策略；元数据或播放列表阶段遇到风控、年龄门槛、403、浏览器 Cookie 读取错误等 stderr 信号时，会继续尝试下一种可用 Cookie 策略，降低解析阶段失败导致任务缺标题、播放列表导入为空或还未下载就中断的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到缺少 `BuildCookieStrategies` 且 `CookieStrategy` 不可访问的编译失败；实现后同命令 14 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，108 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`元数据阶段复用浏览器 Cookie`

- [x] 2026-06-09 为 yt-dlp 短命令增加网络重试参数
  - 内容：将视频信息解析和播放列表导入的 yt-dlp 基础参数抽成可测试入口，并复用长下载已有的 `--retries 20`、`--fragment-retries 30`、`--socket-timeout 30` 和线性 `--retry-sleep`；网络抖动或临时连接失败时，元数据/播放列表阶段也能由 yt-dlp 自身重试，降低还未进入正式下载就失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpArgsTests` 观察到缺少短命令基础参数构建入口的编译失败；实现后同命令 5 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，106 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`增强 yt-dlp 短命令网络重试`

- [x] 2026-06-09 统一批量下载页现代工具面板样式
  - 内容：将批量下载页的链接输入区和下载队列区统一切换到 `ToolPanelBorder` 主题样式，保留原有紧凑内边距，同时与下载页、设置页共用 8px 圆角、统一边框、柔和阴影和像素对齐的现代工具面板视觉；批量流程页面的主面板层级更一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~BatchDownloadViewUsesModernToolPanelStyleForPrimarySections` 观察到批量下载页 2 个主要面板未使用 `ToolPanelBorder` 的测试失败；实现后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests`，20 个测试全部通过；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，104 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`统一批量页工具面板样式`

- [x] 2026-06-09 统一设置页现代工具面板样式
  - 内容：将设置页的环境检测、下载设置、代理设置、Cookie 设置和性能设置外层面板统一切换到 `ToolPanelBorder` 主题样式，保留原有布局间距，同时将这些主要设置区收敛到 8px 圆角、统一边框、柔和阴影和像素对齐的现代工具面板视觉；设置页与下载页的卡片语言更一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~SettingsViewUsesModernToolPanelStyleForPrimarySections` 观察到设置页 5 个主要设置面板未使用 `ToolPanelBorder` 的测试失败；实现后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests`，19 个测试全部通过；`dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，103 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`统一设置页工具面板样式`

- [x] 2026-06-09 补齐下载页按钮提示与无障碍名称
  - 内容：为下载页主要操作按钮补齐 `ToolTip` 和 `AutomationProperties.Name`，覆盖粘贴链接、开始下载、选择目录、取消下载、复制日志和清空日志；鼠标悬停和辅助技术都能得到明确命令名称，提升界面可用性和现代桌面应用细节体验。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~XamlBindingTests` 观察到 6 个下载页操作按钮缺少提示/无障碍名称的测试失败；实现后同命令 18 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，102 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐下载页按钮提示`

- [x] 2026-06-09 统一下载页现代工具面板样式
  - 内容：新增 `ToolPanelBorder` 主题样式，将下载页的选项区、下载进度区和日志区统一到 8px 圆角、细边框、柔和阴影和像素对齐的现代工具面板；减少页面内不同卡片圆角和边框写法不一致的问题，让下载页视觉更整齐。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "FullyQualifiedName~ThemeStyleTests|FullyQualifiedName~XamlBindingTests"` 观察到缺少 `ToolPanelBorder` 样式且下载页未使用该样式的 2 个测试失败；实现后同命令 18 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，96 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`统一下载页工具面板样式`

- [x] 2026-06-09 保留 HttpOnly Netscape Cookie 行
  - 内容：修复 `YtDlpService.BuildCookieFileLines` 的 Netscape Cookie 文件转换逻辑，字段合法的 `#HttpOnly_` Cookie 行不再被当作普通注释跳过，普通注释仍继续忽略；浏览器或插件导出的 `cookies.txt` 中的 HttpOnly 登录态 Cookie 可被保留下来，降低粘贴标准 Cookie 文件后仍缺认证信息的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 `#HttpOnly_` Netscape Cookie 行被过滤的测试失败；实现后同命令 12 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，94 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`保留 HttpOnly Cookie 行`

- [x] 2026-06-09 识别 YouTube 年龄门槛重试 Cookie
  - 内容：扩展 `YtDlpService.ShouldRetryWithNextCookieStrategy` 的 YouTube 错误识别范围，除 403 和 bot 校验外，也识别 `Sign in to confirm your age`、`This video may be inappropriate for some users` 和 `age-restricted`；遇到 YouTube 年龄/登录门槛时，会继续尝试已有的 Chrome/Edge Cookie 策略，降低有浏览器登录态但首次下载失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 YouTube 年龄确认提示未触发 Cookie 策略重试的测试失败；实现后同命令 11 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，93 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`识别 YouTube 年龄门槛重试 Cookie`

- [x] 2026-06-09 兼容 Cookie 域名字段别名
  - 内容：为 `YtDlpService.BuildCookieFileLines` 的浏览器 Cookie JSON 解析增加域名字段别名兼容，缺少 `domain` 时可从 `host` 读取域名，或从 `url` 解析精确主机名；从 DevTools、自动化脚本或非标准 Cookie 导出工具拿到的 JSON 不再因域名字段名不同被整条跳过，降低粘贴 Cookie 后仍无法带登录态下载的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 `host`/`url` 域名来源被跳过的测试失败；实现后同命令 10 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，92 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`兼容 Cookie 域名字段别名`

- [x] 2026-06-09 兼容 Cookie 过期字段别名
  - 内容：为 `YtDlpService.BuildCookieFileLines` 的浏览器 Cookie JSON 解析增加过期时间字段别名兼容，除 `expirationDate` 外也支持常见的 `expires` 和 `expiry`；从 Chrome DevTools、自动化工具或不同 Cookie 插件导出的 JSON 不再把长期 Cookie 误写成会话 Cookie，降低登录态校验失败概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 `expires`/`expiry` 被写成过期时间 `0` 的测试失败；实现后同命令 9 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，91 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`兼容 Cookie 过期字段别名`

- [x] 2026-06-09 支持 data 包装 Cookie JSON
  - 内容：扩展 `YtDlpService.BuildCookieFileLines` 的浏览器 Cookie JSON 根结构识别，在 `{ "cookies": [...] }` 之外兼容 `{ "data": [...] }` 对象包装格式；部分 Cookie 导出工具使用 `data` 作为数组字段时，不再被误判为普通文本 Cookie，降低用户粘贴 Cookie 后仍下载失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 `{ "data": [...] }` 无法生成 `VISITOR_INFO1_LIVE` Cookie 的测试失败；实现后同命令 8 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，90 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`支持 data 包装 Cookie JSON`

- [x] 2026-06-09 支持对象包装 Cookie JSON
  - 内容：扩展 `YtDlpService.BuildCookieFileLines` 的浏览器 Cookie JSON 根结构识别，除顶层数组外也支持 `{ "cookies": [...] }` 对象包装格式；从部分浏览器插件或导出工具复制 Cookie JSON 时，不再被当作普通文本 Cookie 误拆，降低用户粘贴 Cookie 后仍下载失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到 `{ "cookies": [...] }` 被误当纯文本而无法生成 `PREF` Cookie 的测试失败；实现后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，89 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`支持对象包装 Cookie JSON`

- [x] 2026-06-09 容错 Cookie JSON 字段类型
  - 内容：为 `YtDlpService.BuildCookieFileLines` 的浏览器 Cookie JSON 解析增加安全字段读取，支持 `secure`、`hostOnly` 和 `expirationDate` 以字符串或数字形式出现，并继续兼容 `sessionValue`；用户从浏览器插件粘贴的 Cookie JSON 字段类型不标准时，不再整段解析失败后退化为空 Cookie 文件，降低 YouTube/抖音 Cookie 下载失败概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpCookieTests` 观察到字符串布尔值导致 JSON Cookie 没有写入的测试失败；实现后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，88 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错 Cookie JSON 字段类型`

- [x] 2026-06-09 规范化 yt-dlp 播放列表裸 YouTube ID
  - 内容：为 `YtDlpService.ExtractPlaylistUrlFromJson` 增加 YouTube 扁平播放列表裸视频 ID 规范化；当 yt-dlp 只输出 `url` 为视频 ID 且 `ie_key/extractor_key` 表明来源是 YouTube 时，会转换为标准 `https://www.youtube.com/watch?v=...`，避免批量下载拿到裸 ID 后失败或误解析。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpMetadataTests` 观察到裸 ID 被原样返回的测试失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，87 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`规范化播放列表 YouTube ID`

- [x] 2026-06-09 容错 yt-dlp 播放列表 URL 字段
  - 内容：将 `YtDlpService.GetPlaylistUrlsAsync` 的单行 JSON URL 提取抽成 `ExtractPlaylistUrlFromJson`，并改为安全读取 `url`、不可用时回退 `webpage_url`；当 yt-dlp 扁平播放列表输出里 `url` 字段类型异常但 `webpage_url` 仍可用时，不再整行跳过，降低播放列表解析漏项概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpMetadataTests` 观察到缺少 `ExtractPlaylistUrlFromJson` 的编译失败；实现后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，86 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错 yt-dlp 播放列表 URL`

- [x] 2026-06-09 容错 yt-dlp 元数据异常字段
  - 内容：将 `YtDlpService.GetVideoInfoAsync` 的 JSON 元数据解析抽成可测试的 `ParseVideoInfoJson`，并为标题、平台、缩略图、时长和文件大小增加安全读取；yt-dlp 输出里字段类型异常时会按空值或 0 处理，缩略图列表仍会继续寻找后续可用 URL，避免单个坏字段让视频信息解析整体失败。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpMetadataTests` 观察到缺少 `ParseVideoInfoJson` 的编译失败；实现后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，85 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错 yt-dlp 元数据字段`

- [x] 2026-06-09 容错抖音兜底捕获异常字段
  - 内容：为 `DouyinBrowserDownloadService` 的浏览器 DevTools 消息解析增加非字符串字段容错，CDP 响应里的 `url`、`mimeType` 或 headers 形状异常时会按空值跳过，不再抛出 `InvalidOperationException` 中断抖音兜底视频/缩略图捕获，降低浏览器事件噪声导致兜底下载失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DouyinBrowserDownloadServiceTests` 观察到非字符串 `url` 字段触发 `InvalidOperationException` 的测试失败；实现后同命令 9 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，84 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错抖音兜底捕获字段`

- [x] 2026-06-09 归一化异常下载配置文本
  - 内容：为 `ConfigService.NormalizeRuntimeConfig` 增加下载配置文本清洗，空白下载目录回退到默认 `Downloads\EasyGet`，格式、画质、字幕按支持白名单归一到默认值，代理地址去除首尾空白，空 Cookie 文本归一为空字符串，避免手改或损坏配置导致启动建目录失败、下载参数异常或设置页出现未知选项。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ConfigServiceTests` 观察到空白下载目录未回退的测试失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，83 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`归一化异常下载配置文本`

- [x] 2026-06-09 归一化异常窗口状态配置
  - 内容：为 `ConfigService.NormalizeRuntimeConfig` 增加窗口位置与尺寸清洗，非有限 Left/Top 会重置为 `NaN` 以继续居中启动；非有限宽高回落到默认 `1280x800`，过小宽高按主窗口 `MinWidth=960`、`MinHeight=600` 拉回安全范围，避免坏配置导致窗口不可见或布局异常。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ConfigServiceTests` 观察到缺少窗口边界常量与归一化逻辑的编译失败；实现后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，82 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`归一化异常窗口状态配置`

- [x] 2026-06-09 按列名读取历史记录
  - 内容：将 `HistoryService.GetAllAsync` 的历史查询改为显式列清单，并按列名读取字段，避免旧库/迁移表列顺序变化时把 `id`、URL、标题等字段读错，提升历史数据库兼容性。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryServiceTests` 观察到乱序列表把 URL 读成 `id` 的测试失败；实现后同命令 9 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，81 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`按列名读取历史记录`

- [x] 2026-06-09 容错历史文本字段空值
  - 内容：为历史记录读写增加文本字段空值容错，旧库或异常数据中的 `url`、标题、平台、格式、画质、文件路径、缩略图等 NULL 文本会读取为空字符串；`AddAsync` 也会将运行期空引用归一为空字符串，避免写入历史时触发 SQLite 参数异常。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryServiceTests` 观察到旧库 NULL 文本字段读取触发 `InvalidOperationException`、空引用写入触发 SQLite 参数异常的 2 个测试失败；实现后同命令 8 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，80 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错历史文本空值`

- [x] 2026-06-09 容错历史文件大小坏数据
  - 内容：为历史记录读取增加 `file_size` 容错，旧库或异常数据中的文本/负数文件大小会归零，避免历史页读取异常或显示负文件大小；`DownloadHistory.FileSizeText` 也会将负值显示为 `0 B`。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryServiceTests` 观察到负数 `file_size` 被原样读取、负文件大小显示为 `-2048 B` 的 2 个测试失败；实现后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，78 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错历史文件大小数据`

- [x] 2026-06-09 容错损坏历史时间戳
  - 内容：为历史记录读取增加损坏 `download_time` 容错，无法解析的旧库/异常数据不再让历史页整体失败，而是保留记录并将时间标记为未知；`DownloadHistory.DownloadTimeText` 对未知时间显示 `--`，避免 UI 出现 `0001-01-01`。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryServiceTests` 观察到损坏时间戳触发 `FormatException`、未知时间显示为 `0001-01-01 00:00` 的 2 个测试失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，75 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`容错损坏历史时间戳`

- [x] 2026-06-09 稳定历史记录时间戳持久化
  - 内容：为 `HistoryService` 增加临时数据库测试入口，并将历史记录下载时间的写入与读取统一为 invariant culture 固定格式，避免在泰国佛历等区域设置下写入后切换语言/区域读取时年份漂移，提升历史列表数据稳定性。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~HistoryServiceTests` 观察到缺少临时数据库构造入口的编译失败；补入口后同命令观察到 `2026` 被跨区域读取为 `2569` 的测试失败；实现 invariant culture 持久化后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，73 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`稳定历史时间戳持久化`

- [x] 2026-06-09 修复恢复任务等待取消收尾
  - 内容：补齐 `DownloadManager.ResumeAsync` 在等待并发下载名额时被取消的处理分支，恢复中的任务如果尚未开始下载就被取消，会像首次入队任务一样进入 `Cancelled` 状态并触发 `TaskFinished`，避免队列卡在等待态。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DownloadManagerTests` 观察到恢复任务等待并发位时取消无法触发 `TaskFinished` 的测试失败；实现后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，72 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`修复恢复任务等待取消收尾`

- [x] 2026-06-09 清理非有限下载进度值
  - 内容：在 `DownloadManager.ApplyProgress` 中将 `NaN`、正无穷和负无穷等非有限进度数字归零，再进行百分比、速度和 ETA 的边界保护，避免异常进度源把不可显示数值写入任务模型和 UI。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DownloadManagerTests` 观察到 `NaN` 直接写入任务进度的测试失败；实现后同命令 6 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，71 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`清理非有限下载进度值`

- [x] 2026-06-09 归一化下载进度 UI 数值
  - 内容：在 `DownloadManager.ApplyProgress` 中对进度百分比、速度、ETA 和已下载字节做边界保护，百分比限制在 0-100，速度/ETA/已下载字节不允许为负，避免异常进度输出让 UI 显示超过 100% 或负数。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~DownloadManagerTests` 观察到异常进度值直接写入任务状态的测试失败；实现后同命令 5 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，70 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`归一化下载进度数值`

- [x] 2026-06-09 保留 yt-dlp ETA 占位符下的进度更新
  - 内容：放宽 yt-dlp 进度行 ETA 字段解析，支持 `--:--` 等非标准 ETA 占位符；当 ETA 无法解析时仍保留百分比和速度，ETA 回落为 0，避免整行进度被丢弃。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProgressTests` 观察到 `ETA --:--` 进度行解析结果为 null 的测试失败；实现后同命令 4 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，69 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`保留 ETA 占位符进度更新`

- [x] 2026-06-09 保留 yt-dlp 未知速度下的进度更新
  - 内容：放宽 yt-dlp 进度行解析，支持 `Unknown B/s` 速度占位符；当速度未知时仍解析百分比和 ETA，速度回落为 0，避免下载初期或网络抖动时整行进度被丢弃。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProgressTests` 观察到 `Unknown B/s` 进度行解析结果为 null 的测试失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，68 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`保留未知速度进度更新`

- [x] 2026-06-09 校准 yt-dlp 进度小数与速度单位解析
  - 内容：将 yt-dlp 进度百分比改为使用 invariant culture 解析，避免非英文区域设置下 `12.5%` 被解析成 `125`；同时修正 `MiB/s` 等速度单位换算，让下载速度显示和任务状态计算使用正确的字节/秒数值。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProgressTests` 观察到 `de-DE` 区域设置下百分比被解析为 `125`、速度未按 `MiB/s` 换算的测试失败；实现后同命令 2 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，67 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`校准 yt-dlp 进度解析`

- [x] 2026-06-09 增强 yt-dlp 进度解析容错
  - 内容：为 yt-dlp 进度文本解析增加异常 ETA/速度数字容错，遇到 `ETA abc` 这类非标准占位符或无法解析的速度数字时回落为 0，避免单行异常进度输出打断下载流程。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpProgressTests` 观察到 `int.Parse("abc")` 抛出 `FormatException` 的测试失败；实现后同命令 1 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，66 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`增强 yt-dlp 进度解析容错`

- [x] 2026-06-09 校验环境组件下载完整性
  - 内容：为自动安装 yt-dlp/ffmpeg 的工具下载增加 `Content-Length` 完整性校验；当服务端声明的长度与实际写入字节数不一致时抛出 `IOException`、进入已有重试流程，并在最终失败时删除半截目标文件，避免坏的工具文件被误认为安装成功。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~EnvironmentServiceTests` 观察到短文件未抛异常的测试失败；实现后同命令 8 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，65 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`校验环境组件下载完整性`

- [x] 2026-06-09 补齐 TextBox 禁用态视觉
  - 内容：为 `DarkTextBox` 增加 `IsEnabled=False` 禁用态触发器，禁用时降低透明度、恢复普通光标并关闭焦点高亮，使代理配置等不可编辑输入框与按钮、ComboBox、ToggleSwitch 的禁用态反馈一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ThemeStyleTests` 观察到 `DarkTextBox` 缺少禁用态触发器的测试失败；实现后同命令 5 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，64 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐 TextBox 禁用态样式`

- [x] 2026-06-09 补齐 ToggleSwitch 禁用态视觉
  - 内容：为 `ToggleSwitch` 增加 `IsEnabled=False` 禁用态触发器，禁用时降低透明度并恢复普通光标，使设置页开关在不可操作时与按钮、ComboBox 的禁用态反馈一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ThemeStyleTests` 观察到 `ToggleSwitch` 缺少禁用态触发器的测试失败；实现后同命令 4 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，63 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐 ToggleSwitch 禁用态样式`

- [x] 2026-06-09 补齐 ComboBox 禁用态视觉
  - 内容：为 `DarkComboBox` 增加手型光标和 `IsEnabled=False` 禁用态触发器，禁用时降低透明度并恢复普通光标，使格式、清晰度、字幕、设置页下拉框在不可操作时与按钮禁用态保持一致。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~ThemeStyleTests` 观察到 `DarkComboBox` 缺少禁用态触发器的测试失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，62 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐 ComboBox 禁用态样式`

- [x] 2026-06-09 为环境组件下载增加瞬时失败重试
  - 内容：为自动安装 yt-dlp/ffmpeg 的工具下载增加最多 3 次瞬时失败重试，覆盖网络异常、IO 中断、HTTP 408/429/5xx 等临时故障；重试前清理半截目标文件，并在安装进度中输出“准备重试”提示，降低首次环境安装因网络抖动失败的概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~EnvironmentServiceTests` 观察到缺少 `DownloadFileAsync` 可测试重试入口的编译失败；实现后同命令 7 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，61 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`增强环境组件下载重试`

- [x] 2026-06-09 补齐图标按钮提示与可访问名称
  - 内容：为批量任务队列、历史记录和设置页的 icon-only 按钮补齐 `ToolTip` 与 `AutomationProperties.Name`，让短符号按钮在悬浮提示、屏幕阅读器和自动化测试中都有明确语义，同时新增 XAML 结构测试防止后续遗漏。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~IconOnlyButtonsExposeTooltipAndAutomationName` 观察到 3 个视图中的图标按钮缺提示或可访问名称而失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，60 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`补齐图标按钮可访问提示`

- [x] 2026-06-09 为 yt-dlp 下载增加网络重试参数
  - 内容：为长下载参数追加 `--retries 20`、`--fragment-retries 30`、`--socket-timeout 30` 和线性 `--retry-sleep`，让普通网络抖动、分片失败和连接卡顿更容易由 yt-dlp 自身恢复，降低偶发失败概率。
  - 验证：先运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~YtDlpArgsTests` 观察到缺少 `AddNetworkReliabilityArgs` 的编译失败；实现后同命令 3 个测试通过；再运行 `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`，57 个测试全部通过；`dotnet build EasyGet.csproj -c Release` 成功，0 个警告、0 个错误；`git diff --check` 无空白错误。
  - 提交说明：`增强 yt-dlp 网络重试参数`

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
