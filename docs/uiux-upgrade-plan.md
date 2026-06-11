# EasyGet UI/UX 升级计划 v2.0

> **文档角色**：本文档是 UI/UX 升级的唯一权威计划，由审核 Agent（Claude）基于 2026-06-11 对当前代码（commit `1a7bd42`）的逐文件调研制定。**本文档取代旧版 v1.0 计划**——旧计划基于过时的 v1.0 截图制定，与当前代码状态不符（例如其声称测试基线 57 个，实际为 170 个）。
> **执行方**：实施改动的 Agent（下称"执行 Agent"）必须按本计划执行，并按第 8 节要求向 `docs/uiux-upgrade-progress.md` 汇报进度。
> **审核方式**：审核 Agent 将读取进度文档、抽查代码 diff、查看截图、复跑测试，逐项验收。

---

## 1. 现状基线（执行 Agent 必读，已验证）

当前代码**已经完成**了一轮基于 `stitch_easyget/` 设计稿（4 张 PNG，是本次升级的视觉目标参照）的重构，不要重做这些：

- ✅ 240px 品牌侧栏 + 自绘窗口 chrome + 顶栏（`MainWindow.xaml`）
- ✅ 暗色主题 token 体系与 14+ 控件样式（`Themes/Generic.xaml`，含 `MotionDurationFast/Medium`、`MotionEaseOut` 动效资源）
- ✅ 页面进入动画附加行为（`Behaviors/Motion.cs`）、导航/按钮/开关/Toast 动效（规格见 `docs/superpowers/specs/2026-06-10-motion-interaction-design.md`，新增动效必须遵循该规格：克制、150-300ms、只动 transform/opacity）
- ✅ 历史页媒体库卡片网格 + hover 覆盖层动画（`Views/HistoryView.xaml`）
- ✅ 批量队列卡片 + 空状态（`Views/BatchDownloadView.xaml`）
- ✅ 下载中进度卡（缩略图/标题/速度/ETA/进度条，`Views/DownloadView.xaml:169-233`）

**测试基线：`dotnet test EasyGet.Tests\EasyGet.Tests.csproj` = 170 个全部通过（2026-06-11 验证），`dotnet build` 0 警告 0 错误。** 交付时不允许低于此基线。

`docs/v1.0版本 screenshots/` 是旧版界面存档，仅作历史对比，不代表当前状态。

### 关键文件索引

| 文件 | 说明 |
|---|---|
| `Themes/Generic.xaml` | 颜色 token（L5-19）、动效资源、全部控件 Style |
| `MainWindow.xaml` | 侧栏、顶栏、Toast（L197-271） |
| `ViewModels/MainViewModel.cs` | 导航、通知状态、`CurrentPageTitle` |
| `ViewModels/DownloadViewModel.cs` | 单下载逻辑（无解析预览，错误走日志） |
| `ViewModels/HistoryViewModel.cs` | 搜索/筛选/清空（无防抖、无确认） |
| `Services/YtDlpService.cs` | `GetVideoInfoAsync()` 返回 `VideoInfo{Title,Platform,Duration,Thumbnail,FileSize,Url}`（L10-18, L67） |
| `Services/DownloadManager.cs` | 队列管理；下载前内部已调用 `GetVideoInfoAsync`（L89） |
| `Models/DownloadTask.cs` | 已有 `SpeedText/EtaText/DurationText/FileSizeText` 计算属性 |
| `Converters/CommonConverters.cs` | `StatusToText`、`PlatformToColor`、`FileExistsToOpacity`、`HttpUrlToBool` 等 |

## 2. 问题清单（全部经代码核实，附出处）

### A. 假数据与占位文案（从 Stitch 设计稿照搬，误导用户）
1. 侧栏 "PRO ACCOUNT"（`MainWindow.xaml:78`）——本应用无账户体系。
2. 批量页底部状态栏 "V1.0.8 STABLE"（假版本）、"ACTIVE THREADS" 实际绑定的是队列总数而非线程数、"SERVER STATUS: ONLINE"（桌面应用无服务器）（`Views/BatchDownloadView.xaml:369-376`）。
3. 历史页底部 "磁盘空间充足 · v1.2.4 Stable"——未做任何磁盘检查，版本号又是第三个值（`Views/HistoryView.xaml:403-405`）。
4. 下载页底部信息卡 "网速限制：无限制"、"代理状态：系统默认" 为写死文本，与 `ConfigService` 实际配置完全无关（`Views/DownloadView.xaml:305-353`）。
5. 顶栏标题第 1 页中文、第 2 页却是英文 "Batch Operations"（`ViewModels/MainViewModel.cs:31`）。
6. 版本号三处不一致：App 实际 1.0.0 / 批量页 V1.0.8 / 历史页 v1.2.4。

### B. 视觉系统缺陷
7. `Success` token 是蓝色 `#74D1FF`，与 `Accent #60CDFF` 几乎相同，语义错误（`Themes/Generic.xaml:17`）。
8. Views 中 11 处硬编码颜色绕过 token：`#1A4250`×4（Batch L51/L271、Settings L62/L126）、`#4A1E1E`×2（Settings L65/L129）、`#0E0E0E`（Download L284）、`#66000000`/`#99FFFFFF`（Batch L258/260）、`#99000000`/`#AA101416`（History L208/215）；`MainWindow.xaml` Toast 硬编码 `#2D4A2D`/`#4A2D2D`（L206/243）。
9. 页面标题排版不统一：下载页 24px SemiBold（卡内）/ 批量页 34px ExtraBold / 历史页 26px SemiBold / 设置页 36px ExtraBold。

### C. 核心流程缺陷
10. 无"解析→预览→确认"流程：点下载直接入队，用户在出结果前不知道链接对不对。
11. 进度卡随 `IsDownloading=false` 整体消失（`Views/DownloadView.xaml:172`）：完成后无成功态、无"打开文件夹"入口；失败后卡片消失，错误只能去日志里翻。
12. 输入校验错误写进日志（`DownloadViewModel.cs:130/139`），而不是输入框旁的内联提示。
13. 300px 原始日志框常驻页面主区域，是页面最大的视觉元素。
14. 无任务栏进度（`TaskbarItemInfo` 未使用）。

### D. 交互细节缺陷
15. 批量任务卡 4 个操作按钮（暂停/恢复/重试/取消）不分任务状态全部常驻（`BatchDownloadView.xaml:310-353`）；缩略图上的播放图标覆盖层任何状态都显示（L258-264）。
16. 历史页媒体筛选（全部/视频/音频）无选中态——三个普通按钮，用户看不出当前筛选（`HistoryView.xaml:95-133`；VM 中 `SelectedMediaFilter` 状态已存在）。
17. 历史搜索需点"搜索"按钮（`HistoryViewModel.cs:75`），输入框绑定了 `PropertyChanged` 却不触发查询。
18. "清空记录"一键即清空全部历史，无确认（`HistoryViewModel.cs:94`）——破坏性操作。
19. Toast 单条互斥，无堆叠、无倒计时可视化（`MainWindow.xaml:197`、`MainViewModel.cs:79` 4 秒定时器）。
20. 批量页播放列表输入框无任何标签/占位提示，是页面上一个"裸"文本框（`BatchDownloadView.xaml:119-122`）。
21. 无键盘快捷键；无拖拽导入。
22. README "当前界面"截图引用 `docs/screenshots/*.png`，这些文件已在工作区删除，链接全部失效。

## 3. 全局约束（违反任意一条该任务即验收不通过）

1. **真实性原则（本轮新增，最重要）**：UI 上任何文案/数字/状态必须可由真实应用状态推导。不允许为了"还原设计稿"保留装饰性假元素。设计稿（`stitch_easyget/`）只约束布局与风格，不约束文案内容。
2. **禁止硬编码颜色**：所有颜色引用 `Generic.xaml` token；需要新色先定义 `Color` + `SolidColorBrush` token（含半透明遮罩色）。
3. **动效**必须使用 `MotionDuration*`/`MotionEase*` 资源，遵循已有动效规格（克制、只动 transform/opacity、可中断不影响可用性）。
4. **MVVM 边界**：code-behind 只允许纯视觉逻辑；业务状态一律进 ViewModel；新增命令用 CommunityToolkit 源生成器。
5. **不修改 `Services/` 的下载与解析行为**。允许新增对现有方法的调用（如 `GetVideoInfoAsync`）、允许新增事件订阅；不允许改动其内部实现与参数语义。
6. **每个任务完成后**：`dotnet build EasyGet.csproj`（0 警告）+ `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`（≥170 通过，删除过时断言须在进度文档说明）。
7. **每个涉及可见变化的任务必须截图**：运行应用，截图存 `docs/screenshots/uiux-v2/`，命名 `<任务ID>-<描述>.png`。
8. 中文文案风格与现有一致；可交互控件保留/补充 `AutomationProperties.Name` 与 `ToolTip`。

---

## 4. 阶段 P0：可信度修复（先做，工作量小、价值高）

### UX-001 清除全部假数据与占位文案
对应问题 1-6。逐项处理：

- 侧栏 "PRO ACCOUNT" → 删除，改为真实版本号小字（版本号从 `Assembly` 版本单一来源读取，建议 `MainViewModel` 暴露 `AppVersion` 属性）。
- 批量页底部状态栏：要么删除整条，要么全部换成真实数据——版本号（同上）、"进行中任务"绑定真正处于 Downloading 状态的任务数、用 yt-dlp/ffmpeg 就绪状态替代 "SERVER STATUS"。禁止保留任何无数据来源的字段。
- 历史页底部胶囊：换成真实磁盘可用空间（`DriveInfo` 取默认下载目录所在盘，后台线程刷新）或删除。
- 下载页底部三卡片：「保存目录」保留（已真实）；「代理状态」绑定 `ConfigService.Config`（启用时显示代理地址、未启用显示"未启用"，且设置页改动后能刷新）；「网速限制」无对应功能，删除，可换成「并发分片」等真实配置的只读展示。
- `CurrentPageTitle` 的 "Batch Operations" → "批量下载"。

**验收**：`grep -rn "PRO ACCOUNT\|SERVER STATUS\|V1.0.8\|v1.2.4\|磁盘空间充足\|Batch Operations\|无限制\|系统默认" Views/ MainWindow.xaml ViewModels/` 零命中；版本号全 UI 唯一来源；代理卡片随设置变化。

## 5. 阶段 P1：视觉系统治理

### UX-101 语义色修正 + 硬编码颜色清零
对应问题 7、8。

- `Success` 改绿色系（建议 `#6CCB77` 一类，深色底对比度 ≥3:1）；`Accent` 不动。
- 新增容器级 token（参考现有命名风格）：`AccentContainer`（替代 4 处 `#1A4250`）、`ErrorContainer`（替代 `#4A1E1E`、Toast `#4A2D2D`）、`SuccessContainer`（替代 Toast `#2D4A2D`）、遮罩类 `ScrimBrush`/`ScrimHeavyBrush`（替代 `#66000000`/`#99000000`/`#AA101416`）、`BgConsole`（替代日志区 `#0E0E0E`）等。数量可合并裁量，但**每个被替换的字面量必须落到某个 token**。
- `MainWindow.xaml` Toast 背景 token 化；成功态用 `SuccessContainer`+绿色图标，失败态 `ErrorContainer`+红色图标。
- `Themes/Generic.xaml` 内部残留的魔法色（L428/432 关闭按钮红、L567-568 渐变、L785/789 滚动条）一并提升为 token 或改用现有 token。
- 同步更新 `EasyGet.Tests/ThemeStyleTests.cs` 中的颜色断言，并为新 token 增加存在性断言。

**验收**：`grep -rn '#[0-9A-Fa-f]\{3,8\}' Views/ MainWindow.xaml` 零命中；`Themes/Generic.xaml` 中 `#` 字面量只出现在 token 定义区；成功 Toast 绿、失败 Toast 红；测试全过。

### UX-102 页面标题排版统一
对应问题 9。定一套标题规格并应用到四页：页面主标题统一字号/字重（建议 28-30px SemiBold，归位到各页内容区顶部），副标题统一 13-14px `TextSecondary`。设置页/批量页的 ExtraBold 34-36px 降级对齐。顶栏标题与页内主标题避免同义重复（顶栏保留短名即可）。

**验收**：四页截图对比，标题字号字重一致；XamlBindingTests 如有相关断言同步更新。

## 6. 阶段 P2：单视频下载流程升级（核心体验）

### UX-201 解析预览卡（贴链接 → 预览确认 → 下载）
对应问题 10。

- `DownloadViewModel` 新增状态机（enum `DownloadPageState { Idle, Parsing, Ready, Downloading, Completed, Failed }`）+ `VideoInfo` 可观察属性 + `ParseCommand`（调 `YtDlpService.GetVideoInfoAsync`，带 `CancellationToken`，URL 变更/清空时取消上一次）。
- URL 输入后点击"解析"（主按钮文案在 Idle 态为"解析视频"）→ Parsing 态显示骨架/loading（token 色）→ Ready 态渲染预览卡：封面（加载失败显示占位图标）、标题（2 行省略）、平台徽章（复用 `PlatformToColor`）、时长、预估大小；格式/画质/字幕选择保留在卡上；按钮变"开始下载"。
- 解析失败 → Failed 态错误卡（错误摘要 + 重试按钮），不崩溃。
- 兼容性要求：`DownloadManager` 下载前会再次解析（其内部行为不改），预览卡数据仅作展示用途。

**验收**：贴合法 URL → loading → 预览卡出现（含封面标题）→ 点下载进入下载态；贴非法 URL → 错误卡；解析期间改 URL 不产生竞态（旧结果不覆盖新输入）。

### UX-202 进度卡全生命周期（完成/失败态不消失）
对应问题 11。

- 进度卡可见性改由状态机驱动（Downloading/Completed/Failed 均显示），不再绑 `IsDownloading`。
- Completed 态：进度条变 `Success` 色、显示"已保存到 …"+「打开文件夹」「播放」按钮（文件路径从 `DownloadTask` 取；打开文件夹逻辑参考 `HistoryViewModel.OpenFolder` 在 VM 内实现，勿在 code-behind）。
- Failed 态：`ErrorContainer` 底色 + 错误信息（`DownloadTask.ErrorMessage`）+「重试」按钮。
- 新任务开始/URL 清空时回 Idle 隐藏卡片。

**验收**：完整下载一次，完成后卡片保留为绿色成功态且能打开文件夹；人为输入会失败的链接，失败态显示错误并可重试。

### UX-203 内联校验与日志降级
对应问题 12、13。

- 空 URL / 无法识别 URL：输入框边框变 `Error` 色 + 下方内联错误文案（VM 暴露 `UrlError` 属性），不再写日志。
- 日志区改为 Expander（默认折叠，标题"详细日志"，保留复制/清空按钮），收纳到页面底部；折叠状态记忆到当前会话即可。

**验收**：空 URL 点解析 → 输入框红边+提示，日志无新增行；日志默认折叠、展开可查、自动滚动保持。

### UX-204 任务栏下载进度
对应问题 14。`MainWindow` 加 `TaskbarItemInfo`，`MainViewModel` 聚合进度（订阅 `DownloadManager` 既有事件；单任务取其进度，多任务取平均）；下载中 `Normal` 状态显示进度，出错 `Error`，全部结束后清除。

**验收**：下载时任务栏图标有进度填充；完成后消失。

## 7. 阶段 P3：批量与历史页交互

### UX-301 批量任务卡状态化
对应问题 15。操作按钮按 `DownloadTask.Status` 显隐：运行中→暂停+取消；已暂停→恢复+取消；失败→重试+取消（移除）；排队→取消。缩略图播放图标覆盖层仅在有缩略图且非下载中时显示（或干脆移除）。卡片左侧加 4px 状态色条（运行 Accent / 完成 Success / 失败 Error / 暂停 Warning），参考 Stitch 设计稿 2。可用 `DataTrigger` + 现有 `StatusTo*` converter 体系扩展。

**验收**：构造各状态任务（可借助暂停/失败链接），按钮组合与色条符合上述映射。

### UX-302 历史筛选选中态 + 搜索即筛
对应问题 16、17。媒体筛选改为带选中态的分段控件（`RadioButton` 复用/仿 `NavRadioButton` 思路，绑定 `SelectedMediaFilter`）；搜索输入 300ms 防抖自动查询（VM 内 `DispatcherTimer` 或 CTK 异步节流），移除"搜索"按钮。

**验收**：切换筛选有明确选中视觉；连续输入只触发一次查询、停止输入后列表即时更新。

### UX-303 破坏性操作确认
对应问题 18。"清空记录"增加确认（应用内样式一致的确认层或 `MessageBox`，前者优先且须用 token）；批量页"取消全部"同样加确认。确认逻辑放 VM 可测试处（注入式确认回调，便于单测）。

**验收**：误点一次不丢数据；确认后才清空；新增对应单测。

### UX-304 历史空状态区分
对应问题（搜索无结果时仍显示"无下载历史/快去粘贴链接"，误导）。VM 暴露区分依据（有无关键词/筛选），空状态模板双形态："还没有下载记录"（带引导文案）vs "未找到匹配的记录"（带"清除筛选"按钮）。

**验收**：无记录与搜索无结果显示不同文案；"清除筛选"一键还原。

### UX-305 批量页拖拽导入 + 队列动效 + 播放列表输入修缮
对应问题 20、21。

- 链接文本或 `.txt` 文件拖入批量页 → 逐行提取 URL 追加到输入框（复用 `DownloadViewModel.ExtractUrl` 思路，非 URL 行忽略并 Toast 提示忽略数）。拖拽 hover 时显示 token 色高亮边框提示区。
- 队列卡片新增/移除 fade+slide 动效（扩展 `Behaviors/Motion.cs`，遵守动效规格）。
- 播放列表输入框补标签"播放列表链接"与占位提示（现有 `DarkTextBox` 若无 watermark 能力，加标签行即可）。

**验收**：拖入含 3 链接的 txt → 输入框追加 3 行且计数徽章更新；增删任务有平滑动画；播放列表输入框有清晰标识。

## 8. 阶段 P4：通知与全局细节

### UX-401 Toast 堆叠队列
对应问题 19。`MainViewModel` 改为 `ObservableCollection<NotificationItem>`（消息/类型/时间戳），`ItemsControl` 垂直堆叠渲染（最多 3 条，新的在下），每条独立 4s 倒计时 + 底部细进度条显示剩余时间，hover 暂停，点击关闭。替换现单条 Border 方案；沿用现有出入场动效模式。

**验收**：快速完成 2+ 任务出现堆叠 Toast；hover 不消失；逐条独立超时。

### UX-402 剪贴板智能检测
窗口 `Activated` 时检测剪贴板：是 http(s) 链接、与输入框当前值不同、且未提示过同一值 → 下载页顶部出现轻量提示条"检测到链接 xxx，点击解析"，8s 自动消失。判断逻辑抽成静态方法供 VM 调用与单测。

**验收**：复制链接切回窗口出现提示，点击后填入并触发解析；同一链接不重复提示；剪贴板异常（被占用）不崩溃。

### UX-403 键盘快捷键
`Ctrl+1~4` 切换导航页；`Esc` 关闭 Toast/取消解析中状态；下载页 URL 框未聚焦时 `Ctrl+V` 直接粘贴并解析（焦点在任何 TextBox 时保持原生行为）。设置页底部加快捷键说明一行。

**验收**：逐条手测生效且无输入框冲突。

### UX-404 README 截图与文档收尾
对应问题 22。全部任务完成后：重新截四页新图存 `docs/screenshots/`（沿用原文件名，README 链接即恢复）；README"当前界面"措辞核对；`docs/screenshots/uiux-v2/` 过程截图保留。

**验收**：README 中四个图片链接在仓库内有效。

---

## 9. 执行顺序与依赖

```
UX-001（独立，最先）
→ UX-101（语义色，后续任务的颜色基础）→ UX-102
→ UX-201 → UX-202 → UX-203 → UX-204（同一状态机，必须顺序做）
→ UX-301 / UX-302 / UX-303 / UX-304 / UX-305（彼此独立，可任意顺序）
→ UX-401 → UX-402 → UX-403
→ UX-404（最后收尾）
```

**提交粒度**：每个任务 ID 一个独立 git commit，message 以任务 ID 开头（如 `UX-101 语义色修正与硬编码颜色清零`），便于审核按 commit 对照。

---

## 10. 进度汇报机制（执行 Agent 必须遵守，硬性要求）

审核 Agent 完全依赖 `docs/uiux-upgrade-progress.md` 验收。**没有按格式汇报的任务一律视为未完成。**

1. **进度文件**：`docs/uiux-upgrade-progress.md`（模板已按本计划任务列表重置，往里追加）。
2. **更新时机**：每完成一个任务 ID 立即追加记录并更新顶部总览表，不要攒到最后。
3. **每条记录必须包含**（缺一项即退回）：
   - 任务 ID 与状态（✅ 完成 / ⚠️ 部分完成 / ❌ 阻塞；后两者必须写原因）
   - 对应 commit hash
   - 修改/新增文件清单（精确路径）
   - 实现说明（3-8 行：做了什么、关键决策、**与计划的偏离点**——无偏离写"无偏离"）
   - 自测结果（build 是否 0 警告；test 通过数/总数，基线 170；新增测试列名称）
   - 截图路径（`docs/screenshots/uiux-v2/<任务ID>-*.png`）
   - 遗留问题/对后续任务的影响（没有写"无"）
4. **偏离必须声明**：换方案、跳过子项、改了计划外文件，都必须写进"偏离点"并给理由。未声明的偏离按缺陷处理。
5. **阻塞处理**：某任务被卡住时记 ❌ 并继续做不依赖它的任务，不要停摆等待。

## 11. 审核流程（供执行 Agent 了解标准）

审核 Agent 将：读总览表 → 逐任务核对"验收标准"（含本计划中给出的 grep 命令逐条执行）→ 按 commit 抽查 diff（重点：真实性原则、硬编码颜色、MVVM 边界、动效规格）→ 查看截图 → 复跑 `dotnet build` + `dotnet test`。不达标项以"返工清单"写入进度文档 `## 审核记录` 区，执行 Agent 按清单返工后再次汇报。

---

## 12. 第三轮（P5）：字体排版与文字渲染规范（TYP 系列）

> 立项依据：用户在真机人工审查中确认两类问题——**文字大小不一、无梯度规范**；**部分文字明显发糊**。审核 Agent 已完成代码取证（2026-06-11），本节规范全部建立在实测数据上。第 3 节全局约束与第 10 节汇报机制对本轮继续生效。

### 12.1 取证结论（执行 Agent 必读）

1. **模糊根因 = 容器级 DropShadowEffect**。WPF 对设置了 `Effect` 的元素会把**整个子树离屏栅格化为位图**，ClearType/Display 模式失效，文字变成缩放位图。当前违规位置（全部在 `Themes/Generic.xaml`）：
   - L82：`ToolPanelBorder` 样式自带 `Effect=SoftShadow` —— 全应用所有内容卡片的文字因此模糊，**这是主因**；
   - L161 / L270：`AccentButton` / `SurfaceButton` 模板根 Border 带 SoftShadow —— 所有按钮文字模糊，且与按压 ScaleTransform 叠加加剧；
   - L784 / L868：`NavRadioButton` / 筛选 RadioButton 选中态触发器追加 SoftShadow —— 选中导航项文字模糊（用户截图可见）；
   - `MainWindow.xaml`：Toast 卡片 `PopupShadow`（含文字，违规）；侧栏 Logo 的 SoftShadow（仅含 Image，可保留）。
2. **字号失控**：Views+MainWindow+Generic 中 FontSize 字面量共 16 种（9/10/11/12/13/14/15/16/17/18/20/24/28/44/48/56），其中文本与图标字形混用同一属性无任何区分。
3. **字重滥用**：15 处 `FontWeight="Bold"`，多压在 10-11px 中文小字上（中文小字加粗 = 发糊второй来源）；46 处 SemiBold 分布尚可。
4. **字体族失控**：74 处内联 `FontFamily` 字面量（每处自写 fallback 列表），无单一来源。

### 12.2 排版规范（Typography Tokens）

**A. 文本字号梯度 —— 只允许 6 级**，在 `Generic.xaml` 定义为 `sys:Double` 资源：

| Token | 值 | 用途 |
|---|---|---|
| `FontSizeCaption` | 11 | 徽章、底栏状态、辅助小标签（现有 9/10/11 全部归并到此） |
| `FontSizeBody` | 12 | 正文、列表次要信息、说明文字（现有 12/13 按语义归并到 12 或 14） |
| `FontSizeBodyStrong` | 14 | 按钮文字、副标题、输入框、强调正文（现有 13/14/15 归并） |
| `FontSizeSection` | 16 | 区块标题（如设置页"环境检测"，现有 16/17/18 文本归并） |
| `FontSizeCardTitle` | 20 | 卡片级标题、批量页"下载队列"（现有 20/24 归并） |
| `FontSizePageTitle` | 28 | 页面主标题（维持 UX-102 的 28 不变） |

**B. 图标字形与文本分离**：图标（Segoe Fluent Icons 的 TextBlock）不占用上述梯度，定义独立 token：`IconSizeSmall=12`、`IconSizeBody=16`、`IconSizeLarge=18`、`IconSizeEmptyState=48`（空状态 44/48/56 归并为 48）。图标 TextBlock 必须套用基础样式 `IconGlyph`（内含 FontFamilyIcon + 默认尺寸），不得散写。

**C. 字重规则**：
- 只允许 `Normal` 与 `SemiBold` 两档；**`Bold` 全面禁用**（现有 15 处全部降为 SemiBold），`ExtraBold` 维持禁用；
- **11px 及以下不允许任何加粗**（小号中文加粗必糊），现有 10-11px Bold 徽章一律改为 11px Normal 或 SemiBold 提级到 12px。

**D. 字体族 token**（三个，禁止其他字面量）：
- `FontFamilyUI`：`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`
- `FontFamilyMono`：`Cascadia Code, Consolas, Microsoft YaHei UI`（日志、速度/百分比等高频刷新数字）
- `FontFamilyIcon`：`Segoe Fluent Icons, Segoe MDL2 Assets`
在窗口级隐式样式设默认 `FontFamilyUI`，74 处内联字面量全部清除。

**E. 文本样式套件**：基于上述 token 在 `Generic.xaml` 提供 7 个命名样式：`TextPageTitle / TextCardTitle / TextSection / TextBodyStrong / TextBody / TextCaption / TextMono`（含 Foreground 默认值：标题 TextPrimary、正文 TextSecondary、Caption TextMuted）。**Views 与 MainWindow 中禁止出现任何 `FontSize=`、`FontFamily=`、`FontWeight=` 字面量**——文本一律 `Style="{StaticResource Text*}"`（个别需覆盖 Foreground 允许），图标一律 `IconGlyph`（覆盖 FontSize 时只能引用 `IconSize*` token）。

### 12.3 文字渲染规范（模糊治理）

- **R1（最高优先）：含文字子树的元素禁止设置 `DropShadowEffect`。** 整改方案：内容卡片与按钮走**扁平化**（删除容器级 SoftShadow，靠 1px 边框 + 背景分层表达层次，Stitch 设计稿本就接近扁平）；确需阴影的弹层（Toast、下拉）改用**垫层方案**——同尺寸同圆角、无子内容的兄弟 Border 单独承载 Effect，文字容器保持无 Effect。
- **R2：动画终值落整像素**。Translate 类动画终值必须为 0/整数；禁止常驻非整数 RenderTransform。按压 ScaleTransform 瞬态豁免。
- **R3：Popup 类视觉树显式补渲染设置**。`TextOptions.TextFormattingMode="Display"`、`UseLayoutRounding="True"` 不会穿透到独立 HwndSource——ComboBox 下拉、ToolTip、右键菜单的 Popup 根元素需显式设置。
- **R4：最小文本字号 11px**（R1 完成后若 11px 仍显糊，允许该处提级到 12，并在汇报中说明）。

### 12.4 任务拆分与验收

**TYP-01 排版 token 与样式套件**：在 `Generic.xaml` 新增 12.2 全部 token + 7 个文本样式 + `IconGlyph`；窗口级默认字体族。验收：token/样式存在性测试。
**TYP-02 全量字号字重替换**：按 12.2 梯度替换 Views/MainWindow/Generic 内部的全部字面量；**汇报中必须附"旧值→新 token"映射表**（逐字号说明归并去向）。验收：`rg 'FontSize="|FontFamily="|FontWeight="' Views MainWindow.xaml` 零命中（样式引用 `{StaticResource …}` 除外）；`rg 'FontWeight="Bold"'` 全仓库（含 Generic.xaml）零命中。
**TYP-03 阴影治理**：按 12.3-R1 执行；ToolPanelBorder/按钮/导航选中态去容器 Effect，Toast 改垫层；Popup 补 R3 设置。验收：`Generic.xaml` 中 `Effect=` 仅出现在无文字子树的垫层或图形元素上（每处保留须在汇报中列明并证明无文字子树）；新增静态测试断言 `ToolPanelBorder`、两个按钮样式、Nav 样式不含 Effect setter。
**TYP-04 防回归测试与收尾**：把 TYP-02/03 的 grep 规则固化为单元测试（沿用 ThemeStyleTests 模式）；全量跑测试；用户做最终人工目检（截图由用户提供，执行 Agent 不得伪造）。

执行顺序：TYP-01 → TYP-02 → TYP-03 → TYP-04，每任务一个 commit（`TYP-xx` 开头），进度追加到 `docs/uiux-upgrade-progress.md` 的"## 第三轮排版返工记录"区，规则同第 10 节。当前测试基线 **218**，只增不减。

---

*计划制定：2026-06-11 · 审核 Agent（Claude）· 基于 commit `1a7bd42` 的代码调研，取代 v1.0 计划*
*第 12 节（P5 排版与渲染规范）追加：2026-06-11 · 基于第二轮验收后真机截图与代码取证*
