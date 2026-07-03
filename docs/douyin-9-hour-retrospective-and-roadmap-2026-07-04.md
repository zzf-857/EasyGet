# EasyGet 抖音模块 9 小时运行复盘与后续路线

日期：2026-07-04

范围：围绕 EasyGet 抖音专项模块，参考 `douyin-downloader-promax` 做功能集成、工程加固、测试与产品路线梳理。
当前状态：主线 HEAD 为 `1904152 feat: 接入抖音发现服务层`，工作区另有未提交的发现 UI/ViewModel 测试切片。

## 说明

用户描述的目标运行时长约 9 小时。Git 可验证的抖音专项集中提交发生在 `2026-07-03T17:57:31+08:00` 到 `2026-07-04T00:23:36+08:00`，从 `ac8e836` 到 `1904152` 共 31 个抖音相关提交。其余时间主要用于项目阅读、第三方仓库调研、子代理调度、测试验证、运行检查和收尾判断。

本复盘只记录仓库中能确认的结果，不把尚未完成的 UI 接入、真实联网下载或真实联网发现写成已完成。

## 这 9 个小时做了什么

### 1. 确立抖音专项引擎路线

最早的核心提交是 `ac8e836 feat: 集成抖音专项下载 sidecar 能力`。这一阶段确立了 EasyGet 不重写抖音核心协议，而是采用混合架构：

- C# 侧继续负责桌面 UI、任务队列、历史、配置、通知和下载路由。
- Python sidecar 负责抖音高波动能力，包括 API 调用、短链解析、图文/视频资源选择、作者作品分页和 manifest 输出。
- EasyGet 与 sidecar 之间使用 stdout JSONL 契约，避免把第三方 Python 依赖直接污染到主进程。

相关落地包括：

- 新增 `Services/DouyinUrlParser.cs`，只做 URL 分类，不展开短链，不下载，不启动 sidecar。
- 新增 `Services/DouyinSpecialDownloadService.cs`，负责启动 sidecar、读取 JSONL、映射进度和终态。
- 在 `Services/DownloadManager.cs` 中接入抖音专项路由。
- 建立 `tools/douyin-sidecar/sidecar.py` 原型。
- 补充 `docs/douyin-special-engine-design.md`、sidecar 手册、发布风险文档和第三方 notices/license。

### 2. 加固 Cookie、路径和发布边界

抖音下载高度依赖 Cookie 与登录态，所以安全边界被提前处理：

- `5310c1f` 避免临时配置写入下载目录，降低 Cookie 或运行配置落到用户输出目录的风险。
- `176365e` 禁用抖音 sidecar 原文 Cookie 参数，避免 Cookie 出现在命令行参数中。
- `ca50eb4` 阻断抖音 sidecar Cookie 解析前泄露。
- `c00bffb` 对齐抖音命名模板控制字符校验，避免路径和文件名异常。
- 发布脚本侧补充 sidecar 打包骨架、`--emit-sample` smoke、可选 import self-test 和 Cookie-backed real download smoke gate。

这一阶段的目标不是一次性解决所有发布风险，而是先把最危险的 Cookie 泄露和路径污染问题挡住。

### 3. 把 promax 下载配置产品化到 EasyGet

随后连续把 `douyin-downloader-promax` 中适合桌面用户控制的能力接入到 EasyGet 配置、设置页、抖音页和 sidecar 参数链路。

已完成的配置包括：

- 画质映射：把 EasyGet 的画质语义映射到抖音专项下载。
- 下载模式：支持 `post`、`like`、`mix`、`music`、`collect`、`collectmix`。
- 数量、时间范围和置顶筛选。
- 封面、头像、音乐、JSON、数据库、增量下载等附加资源开关。
- 命名模板和文件夹模板。
- 作者目录策略：`nickname`、`sec_uid`、`nickname_uid`、`user_sec_uid`。
- 是否按模式分层。
- 并发线程：复用 `AppConfig.ConcurrentFragments`，传给 sidecar 的 `--thread`。
- 评论采集细项：是否包含二级回复、最大评论数、评论分页大小。

关键提交包括：

- `651127f feat: 支持抖音收藏模式配置`
- `b1e87a5 feat: 映射抖音专项下载画质`
- `0c52e8d feat: 支持抖音时间范围和置顶筛选配置`
- `77e75eb feat: 支持抖音命名模板配置`
- `647ef3f feat: 支持抖音多模式下载配置`
- `127143c feat: 支持抖音作者目录策略`
- `faca61e feat: 贯通抖音并发线程配置`
- `7e76f68 feat: 支持抖音评论采集细项`

### 4. 建立抖音工作台和 manifest 视图

为了让抖音能力不是散落在设置页和通用下载队列里，本轮新增并逐步增强了专门的抖音工作台。

已经形成的用户侧能力包括：

- 抖音模块入口。
- 任务中心队列和状态摘要。
- 下载成果摘要。
- manifest 历史摘要。
- manifest 明细结构化读取。
- 文件角色标注，例如主视频、封面、头像、音乐、评论、JSON、转写等附件。
- 任务搜索、状态筛选、作品档案筛选和清除筛选。
- 最近作者入口。
- 抖音专项设置区。

关键提交包括：

- `6323b10 feat: 新增抖音工作台模块`
- `34f0b48 feat: 增强抖音任务中心队列`
- `2cc85d9 feat: 新增抖音下载成果摘要`
- `0840cba feat: 结构化抖音 manifest 明细`
- `6143a35 feat: 标注抖音 manifest 文件角色`
- `b5c1de1 feat: 补齐抖音工作台专项设置`
- `a0136c3 feat: 增强抖音任务中心筛选`
- `fbe3201 feat: 增强抖音作品档案筛选`
- `91f9fd1 feat: 补齐抖音档案清除筛选`
- `0d04aab feat: 新增抖音最近作者入口`

### 5. 强化任务事件、结果摘要和历史写入

抖音任务经常是多文件、多作品、部分成功的形态，不能只用单个 mp4 文件的思路展示结果。因此本轮增强了任务事件摘要和输出附件处理：

- 解析 sidecar 的 `progress`、`success`、`failed`、`cancelled`、`log` 事件。
- 映射成功、失败、跳过计数。
- 从 manifest 和 sidecar summary 中提取输出路径。
- 保护输出路径必须在任务输出目录下。
- 在历史和 UI 中区分主文件与附件。
- 让图文、评论、封面、音乐、JSON 等附件能被归档视图识别。

代表提交是 `7a57fcd feat: 增强抖音任务事件摘要`。

### 6. 接入抖音发现能力基础

`douyin-downloader-promax` 已提供热榜和关键词搜索能力。本轮先把发现能力打到 sidecar 和 C# 服务层，还没有完整接进 UI 工作流。

已提交内容：

- `c764fb2 feat: 支持抖音发现能力基础`
  - sidecar 增加 `--hot-board [N]`。
  - sidecar 增加 `--search KEYWORD` 与 `--search-max N`。
  - dry-run 输出发现计划。
  - 真实运行后读取 `hot_board/` 或 `search/` 下的 JSONL 结果。
  - 以 `success` 终态返回，并用 `details.kind=discovery` 区分普通下载任务。

- `1904152 feat: 接入抖音发现服务层`
  - 新增 `DouyinDiscoveryType`、`DouyinDiscoveryRequest`、`DouyinDiscoveryResult`、`DouyinDiscoveryItem`。
  - `IDouyinSpecialDownloadService` 增加 `DiscoverAsync`。
  - `DouyinSpecialDownloadService` 解析发现任务 success details。
  - `DouyinSidecarProcessRunner` 支持不带 `--url` 的发现参数。
  - 测试覆盖发现请求映射、成功结果、失败、取消和异常路径。

一个重要决策是：EasyGet 不把 `--search-max 0` 解释为“不限”。因为当前 promax CLI 中 `int(args.search_max or 50)` 会把 `0` 当成默认 `50`，这和用户语义不一致。

### 7. 质量门和测试覆盖

本轮持续补了两侧测试。

C# 侧覆盖：

- `DouyinUrlParserTests`
- `DouyinSpecialDownloadServiceTests`
- `DouyinManifestReaderTests`
- `DownloadManagerTests`
- `HistoryViewModelTests`
- `SettingsViewModelTests`
- `UiTruthfulnessViewModelTests`
- `XamlBindingTests`
- `ReleaseScriptTests`

Python sidecar 侧覆盖：

- CLI 参数解析。
- dry-run 配置输出。
- Cookie 安全。
- sidecar stdout JSONL 契约。
- manifest 读取和输出摘要。
- 作者目录、线程数、评论参数。
- 热榜和搜索发现能力。
- 失败、缺输出、异常 JSONL 等边界。

最近已记录的验证包括：

- `DouyinSpecialDownloadServiceTests`：39 passed。
- 全量 `.NET` 测试：591 passed, 1 skipped。
- `python -m unittest tools.douyin-sidecar.tests.test_sidecar`：76 passed。
- `git diff --check`：无实际空白错误，只有 LF/CRLF 提示。

注意：当前工作区已有一个未提交测试文件，因此本文档不声明当前脏工作树已经重新全量通过。

## 当前状态

已经提交到主线的能力：

- 抖音专项 sidecar 基础链路。
- URL 分类与 DownloadManager 路由。
- JSONL 事件解析和任务状态映射。
- Cookie 脱敏和命令行安全加固。
- 多模式、时间范围、置顶、画质、命名模板、作者目录、线程数、评论细项等配置。
- 抖音工作台、任务中心、manifest 摘要、作品档案筛选、最近作者入口。
- sidecar 和 C# 服务层的热榜/搜索发现基础。

当前未完成的切片：

- `EasyGet.Tests/UiTruthfulnessViewModelTests.cs` 已新增抖音发现 UI/ViewModel 行为测试。
- `ViewModels/DouyinViewModel.cs` 尚未实现对应的发现属性与命令。
- `Views/DouyinView.xaml` 尚未出现完整的热榜/搜索结果 UI。
- 因此发现能力目前应描述为“sidecar 与服务层基础已接入，UI 工作流仍在进行中”。

当前仍不能夸大的部分：

- 没有声明真实抖音网络下载已经在当前环境完成验收。
- 没有声明真实热榜/搜索网络请求已经稳定通过。
- 没有声明发布包已经在无 Python 干净机器上完成闭环验证。
- 没有声明直播、转写、通知、REST 常驻服务已经集成。

## 子代理调度情况

本轮使用了两个子代理整理文档素材：

- Herschel：整理过去 9 小时已经完成的工作、当前边界与风险。
- Dalton：整理后续提升路线，分近期、中期、长期。

两个子代理都只做只读整理，没有修改文件；素材返回后均已关闭。当前工具没有提供“列出并批量关闭历史所有子对象”的接口，因此本轮只能保证明确创建的子代理已经关闭。

## 后续提升路线

### 近期可落地

#### 1. 完成抖音发现页 UI

价值：

- 把已经接好的 `--hot-board`、`--search`、`DiscoverAsync` 变成用户可见能力。
- 热榜和关键词搜索是感知很强的功能，适合作为抖音模块下一步展示面。

依赖和风险：

- 发现结果 schema 需要稳定。
- 搜索可能依赖有效 Cookie 或登录态。
- 需要避免从发现结果重复创建下载任务。

验收方式：

- 在抖音工作台能加载热榜。
- 输入关键词能搜索作品。
- 空关键词、空结果、登录失效、sidecar 失败都有明确提示。
- 发现结果能展示标题、作者、热度、链接等关键字段。

#### 2. 发现结果创建下载任务

价值：

- 让发现从“看结果”升级到“可执行下载”。
- 支持从热榜、搜索结果快速添加单条或多条下载任务。

依赖和风险：

- 需要去重策略，避免同一 aweme 重复入队。
- 需要明确结果中缺 URL 时如何回退构造链接。

验收方式：

- 选择发现结果后能创建 EasyGet 下载任务。
- 重复 aweme 不重复下载。
- 批量添加时能显示成功、跳过、失败数量。

#### 3. 真实网络验收矩阵

价值：

- 把当前单测通过推进到“可发布信心”。
- 找出 Cookie、代理、风控和真实输出路径上的问题。

依赖和风险：

- 抖音反爬、Cookie 有效期、账号状态、网络波动都会影响结果。
- 需要专门的测试账号和样例 URL 集合。

验收方式：

- 用固定样例覆盖 `short`、`video`、`gallery`、`user`、`mix`、`music`、`hot-board`、`search`。
- 记录 JSONL 事件、输出文件、manifest、失败分类和取消行为。
- 不把 Cookie、原始敏感 URL 或账号信息写入日志。

#### 4. Cookie 和登录诊断

价值：

- 降低用户遇到“下载失败但不知道为什么”的概率。
- 特别适用于用户主页、收藏夹、评论、搜索等依赖登录态的功能。

依赖和风险：

- 需要从 promax 异常和返回体中抽取稳定错误类型。
- 仍要保持 Cookie 全链路脱敏。

验收方式：

- 缺 Cookie、过期 Cookie、权限不足、限流、网络失败、代理失败分别显示可执行提示。
- 日志中不出现 Cookie 原文。

#### 5. 评论采集端到端 MVP

价值：

- EasyGet 配置已经有评论相关选项，promax 也有评论采集器，适合打通完整链路。

依赖和风险：

- 评论接口可能限流。
- 二级回复分页和大评论量会拉长任务时间。

验收方式：

- 开启评论后生成 `{作品}_comments.json`。
- 尊重最大评论数、分页大小、是否包含二级回复。
- manifest 和历史视图能把评论文件标为附件。

#### 6. sidecar 发布闸门强化

价值：

- 降低“开发机可用，用户机器不可用”的风险。

依赖和风险：

- PyInstaller 打包、依赖导入、无 Python 环境、杀软误报、版本不匹配都是主要风险。

验收方式：

- 干净 Windows 机器无需 Python 即可启动 sidecar。
- 通过 import self-test、sample smoke、Cookie-backed 真实下载 smoke、发现请求和版本/能力声明校验。

### 中期增强

#### 1. 发现结果批量队列化

价值：

- 让热榜、搜索和作者结果可以批量处理。

依赖和风险：

- 需要筛选、全选、去重、批量取消和失败重试策略。

验收方式：

- 支持按作者、类型、关键词筛选。
- 支持全选、反选、去重建任务。
- 重复 aweme 不重复入队。

#### 2. item 级进度和事件模型

价值：

- 用户主页、合集、音乐等批量模式需要看到每个作品的状态，而不是只有总进度。

依赖和风险：

- 需要稳定扩展 sidecar JSONL 字段，避免 UI 随字段变化频繁破坏。

验收方式：

- 批量任务显示发现数、下载中、成功、失败、跳过。
- 每个失败条目带原因并写入 manifest。

#### 3. 数据库和增量下载策略定型

价值：

- promax 有 SQLite 和增量能力，EasyGet 有历史和 manifest，需要明确事实来源。

依赖和风险：

- 双存储容易出现状态不一致。
- schema 迁移和并发写入要谨慎。

验收方式：

- 同一作者重复下载时，增量模式只处理新增作品。
- EasyGet 历史、manifest、sidecar DB 的状态一致。

#### 4. 浏览器 fallback 产品化

价值：

- promax 对用户作品分页有浏览器 fallback 经验，可缓解接口受限场景。

依赖和风险：

- 浏览器依赖体积、登录态复用、无头检测和隐私提示都需要产品化。

验收方式：

- 在受限作者页场景能自动或手动启用 fallback。
- 未安装或未登录时给出清晰引导。

#### 5. 代理能力实测与边界声明

价值：

- EasyGet 已有代理配置，抖音 sidecar 也需要明确 HTTP、HTTPS、SOCKS5 支持范围。

依赖和风险：

- promax 底层 `aiohttp`、`httpx` 对 SOCKS 可能需要额外依赖或适配。

验收方式：

- HTTP 与 SOCKS5 分别有验收样例。
- 不支持的代理类型在 UI 中提前提示。

#### 6. 抖音归档分析视图

价值：

- 让抖音模块从下载工具走向内容管理工具。

依赖和风险：

- 需要统一 manifest、EasyGet 历史和 sidecar DB 字段。

验收方式：

- 支持按作者、日期、媒体类型、任务状态筛选。
- 显示最近作者、失败重试入口、下载量统计。

### 长期演进

#### 1. 直播录制作为独立任务类型

价值：

- promax 有直播相关能力，但直播生命周期与普通下载不同，需要独立模型。

依赖和风险：

- 直播断流、最长录制时长、空闲超时、取消后保留部分文件都需要单独设计。

验收方式：

- 直播任务显示录制中状态。
- 取消或断流后保留可用部分文件。
- 支持最大时长和空闲超时配置。

#### 2. 常驻 REST sidecar 服务

价值：

- 适合未来多任务调度、能力探测和减少进程启动成本。

依赖和风险：

- promax 当前 REST job 能力偏基础，缺少取消、事件流、健康检查和生命周期管理。

验收方式：

- REST 服务提供 health、capabilities、events、cancel。
- 与当前 stdout JSONL 进程模式结果一致。
- EasyGet 关闭时可安全退出。

#### 3. 视频转写与 AI 后处理

价值：

- 下载后生成字幕、摘要和检索文本，扩展内容管理价值。

依赖和风险：

- ffmpeg 体积、OpenAI API Key 管理、费用、隐私和失败重试都需要明确。

验收方式：

- 功能默认关闭，由用户明确启用。
- API Key 本地安全保存。
- 转写结果作为附件写入 manifest。
- UI 展示费用提示和失败原因。

#### 4. 通知体系整合

价值：

- 长任务完成、失败、直播结束等场景适合系统通知或外部通知。

依赖和风险：

- 需要在 promax 通知器和 EasyGet 原生通知之间选主路径，避免配置分裂。

验收方式：

- 成功、失败、取消、批量完成均可配置通知开关。
- 可发送测试通知。

#### 5. sidecar 版本锁定、自更新与回滚

价值：

- 抖音接口变化频繁，需要可控升级和回滚。

依赖和风险：

- 依赖许可证、二进制签名、兼容性矩阵和反爬变化跟踪。

验收方式：

- EasyGet 显示 sidecar 版本和能力。
- 升级前校验兼容版本。
- 失败可回滚。
- 发布说明记录接口变更。

#### 6. 跨平台复用

价值：

- JSONL/REST 契约稳定后，sidecar 可以复用到 macOS、Linux 或其他桌面壳。

依赖和风险：

- 路径、浏览器、打包、Cookie 存储和文件名规则都有跨平台差异。

验收方式：

- 先冻结 JSONL/REST 契约。
- 用最小 CLI 验证跨平台下载、发现和 manifest 输出一致。

## 后续优先级建议

建议下一阶段按以下顺序推进：

1. 完成当前未提交的发现 UI/ViewModel 切片，让热榜和搜索能在抖音工作台展示。
2. 增加从发现结果创建下载任务的能力。
3. 建立真实网络验收矩阵，明确哪些能力已经真实可用。
4. 做 Cookie/登录诊断和错误分类，降低用户排错成本。
5. 把评论采集跑通端到端。
6. 强化 sidecar 发布包验证，再考虑更大的 REST 常驻服务、直播和 AI 后处理。

## 参考依据

EasyGet 侧：

- `docs/douyin-special-engine-design.md`
- `docs/douyin-sidecar-manual.md`
- `docs/douyin-sidecar-release-risk.md`
- `docs/douyin-integration-progress-2026-07-03.md`
- `Models/AppConfig.cs`
- `Models/DownloadTask.cs`
- `Services/DouyinUrlParser.cs`
- `Services/DouyinSpecialDownloadService.cs`
- `Services/DouyinManifestReader.cs`
- `Services/DownloadManager.cs`
- `Services/ConfigService.cs`
- `ViewModels/DouyinViewModel.cs`
- `Views/DouyinView.xaml`
- `tools/douyin-sidecar/sidecar.py`
- `tools/douyin-sidecar/tests/test_sidecar.py`
- `EasyGet.Tests/DouyinSpecialDownloadServiceTests.cs`
- `EasyGet.Tests/DouyinManifestReaderTests.cs`
- `EasyGet.Tests/UiTruthfulnessViewModelTests.cs`
- `EasyGet.Tests/XamlBindingTests.cs`

第三方参考：

- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\README.zh-CN.md`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\PROJECT_SUMMARY.md`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\config.example.yml`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\core\discovery.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\core\comments_collector.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\core\live_downloader.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\core\user_downloader.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\core\video_downloader.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\cli\main.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\server\app.py`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\server\jobs.py`
