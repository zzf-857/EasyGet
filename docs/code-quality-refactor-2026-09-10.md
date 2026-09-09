# 代码质量排查与批量下载卡住修复

基线：`main` 的 `1fb412a`。先扫描 119 个生产 C# 文件的规模、重复实现、集合遍历、同步等待和跨线程调用，再重点复核下载队列、批量页面、设置保存、历史数据库、yt-dlp 与抖音解析。生成代码、测试夹具和 `handoff` 参考副本不作为生产冗余直接删除。

本轮保留公开调用与界面绑定；把有独立行为的职责移到独立组件，同时修复重复工作和并发问题。没有通过拆成多个 partial 文件掩盖同一个大类。

## 批量任务停在 0%／等待中的重点修复

用户提供的旧版截图显示 5 个抖音任务失败，错误为 `Unsupported URL: https://www.iesdouyi...`。截图未提供完整 URL 或日志；不能据此认定所有失败都出自同一个卡住原因。结合“任务一直停在 0% 或等待中”的反馈，本轮确认并修复了以下路径：

### 1. 进度更新与 UI 线程发生锁反转

原路径：后台进度回调进入 `TryUpdateCurrentAttempt`、持有 `attempt.UpdateSync`，随后 `ApplyProgress` 同步等待 UI；如果 UI 同时在完成／取消同一任务、等待 `UpdateSync`，双方均不能继续，队列名额也无法释放。

修复：整个受保护更新先切到 UI，再获取 `UpdateSync`。执行时仍检查当前 attempt、取消状态和 `Cts` 身份，防止排队的旧进度覆盖取消或重试后的任务。`ApplyProgress` 只负责赋值。MainViewModel 和 DownloadViewModel 的展示通知使用非阻塞调度，高频汇总与日志合并刷新。

验证：独立 STA Dispatcher 回归证明等待 UI 时不占更新锁，且取消／完成后的排队更新被拒绝。

### 2. 日志／进度订阅异常使子进程输出管道停止读取

原路径：`ReadProcessLinesAsync` 把读取循环和订阅回调放在同一个异常边界内。回调抛错后读取提前结束，yt-dlp 继续输出并填满管道，随后阻塞，直到长时间无输出监控终止任务。

修复：逐条隔离回调异常并记录诊断，继续读取 stdout/stderr。显示订阅失败不会阻止子进程排空输出，任务错误和持久化错误仍由各自业务流程处理。

验证：真实子进程持续输出，分别让 stdout/stderr 首条回调抛出不同异常；旧实现 4 个用例均因 2 秒无输出超时失败，修复后均读完 2002 行并正常退出。结合上面的两个 Dispatcher 用例，共 6 个故障回归完成了旧实现失败／修复后通过的对照。

### 3. 抖音分享地址未转换为下载器支持的作品地址

已知 `iesdouyin.com/share/video/<id>`、`www.iesdouyin.com/share/video/<id>` 和 `modal_id` 视频链接转换为 `https://www.douyin.com/video/<id>`，保留任务原始 URL 作为历史和队列身份。`v.douyin.com` 短链先做有边界的跳转解析，再交给元数据和下载流程。

解析最多发出 5 次 GET，总超时 20 秒；只读取响应头，支持取消、相对跳转和循环检测，拒绝外部域名与登录／验证跳转。已完成的转换缓存最多 128 条、有效期 10 分钟，代理配置变化后失效，减少元数据与下载阶段的重复跳转。没有把图文／主页／直播猜成视频。未支持地址显示具体说明并保留脱敏原错误，便于继续定位。

依据：[yt-dlp 官方 Douyin 提取器](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/tiktok.py) 的视频入口匹配 `douyin.com/video/<id>`。真实站点的 Cookie、风控或内容下架问题仍需原始链接及当时日志确认。

## 冗余和执行效率修复

| 问题 | 原有重复工作／混杂职责 | 本轮修复 |
| --- | --- | --- |
| 下载目录选择 | 单个页和批量页各自维护选择、刷新恢复、默认目录同步、持久化与文件夹对话框 | 共用 `DownloadDestinationViewModel`，两个页面保留绑定代理 |
| 队列汇总 | 多个计数、按钮和进度属性分别扫描队列 | `DownloadQueueSummary` 一次遍历生成快照；无变化时不重建可见列表 |
| 大合集导入／全选 | 每添加／勾选一个条目都重新分组、汇总并核对订阅 | 批量操作结束统一更新；1000 条导入／清空选择各重建一次分组汇总 |
| 订阅集合检查 | 对每个已跟踪对象再用集合 `Contains` 扫描 | 先创建成员 HashSet，线性核对订阅 |
| 批量重复检测 | 每个 URL 遍历并重新规范化全部历史，O(B×H) | 历史先建 URL 索引，O(B+H)；不为只判断重复而探测每条记录的磁盘文件 |
| Telegram 链接身份 | 解析入口已支持 `tg://`，重复检测仍只接受 HTTP(S) | Telegram 消息链接统一身份，深链接、别名域名和话题链接可正确参与重复检测 |
| 设置自动保存 | VM 混入防抖、串行保存、版本追踪和关闭前 flush | 独立 `SettingsSaveCoordinator`，保持保存中新增修改和失败重试语义 |
| Cookie 可用状态 | 每个平台反复过滤并排序整份健康记录 | `CookiePlatformStatusPresenter` 一次索引并统一投影，保留操作中行及来源优先级 |
| yt-dlp 元数据 | 服务同时处理进程、JSON、格式和资源；成功合集 JSON 解析两次 | `YtDlpMetadataParser` 单次解析文档并复用根节点；去除重复格式去重；非法浮点归零 |
| 抖音 sidecar | 协议消息、普通日志、任务流程和文件校验混在一个服务 | 独立协议解析器和输出文件解析器；普通日志不靠 JSON 异常分流 |
| 附件路径去重 | 对每个附件重复规范化并遍历已有路径，O(A²) | 首见顺序保持，路径一次规范化后 HashSet 去重，O(A)，保留目录边界校验 |
| 下载队列职责 | 管理器同时承担引擎构造、路由回退、输出预留和历史映射 | `DownloadExecutionService` 与 `DownloadHistoryRecorder` 分别承担执行和结果保存 |
| 历史页本地解析 | VM 处理文件系统、manifest 校验及内容摘要 | 提取 `HistoryItemEnrichmentService`，无 manifest 时跳过相关磁盘探测 |
| 历史页增量合并 | 新旧记录反复 `Any/FirstOrDefault`，O(N×M) | 字典合并 O(N+M)，目录计数由 O(F²) 改为 O(F) |
| 历史目的地查询 | 为获得目录加载 15 个字段的全部历史并解析附件 JSON | 仅查询 2 个路径列；普通全量加载继续复用已有结果 |
| 数据库启动迁移 | 每次用失败的 ALTER 判断列；健康订阅库也 UPDATE 全表、重建唯一索引 | PRAGMA 检查缺列；索引及键数据健康时跳过 legacy 写入和重建，错误索引／脏键仍走完整修复路径 |

## 大类规模变化

下表是原大类文件的行数变化，包含职责迁移，不代表这些减少的行数都是被删除的冗余代码。

| 文件 | 修改前 | 修改后 |
| --- | ---: | ---: |
| SettingsViewModel | 2365 | 2110 |
| DownloadManager | 2125 | 1949 |
| HistoryViewModel | 2094 | 1891 |
| BatchDownloadViewModel | 2072 | 1929 |
| YtDlpService | 1893 | 1551 |
| DouyinSpecialDownloadService | 1730 | 1142 |
| DownloadViewModel | 1329 | 1226 |

公开页面属性、队列状态机和数据表查询仍有较大体量。它们需要按真实边界继续演进，不能据此声称已经消除所有复杂性。本轮优先处理了有证据的重复工作、独立职责与卡住路径。

## 验证

使用 Release 全量回归、真实子进程输出压力、独立 STA Dispatcher、旧数据库升级、无 UPDATE 触发器与 `schema_version` 不变检查，以及大合集／路径集合的操作次数断言验证改动。

最终结果：Release 全量 **1709 通过、1 项原有在线测试跳过**；生产构建 **0 警告、0 错误**；IDE0051／IDE0052 私有成员检查及 `git diff --check` 通过。

全量并发测试还暴露了 Telegram 限流用例对线程池排队顺序的不必要假设：失败块重试和下一预取块可以先后不同。已保留并强化对完整冷却时间、单请求并发、仅失败块重复以及最终完整文件的验证，不再把某个合法调度顺序误报成下载缺陷。

```powershell
dotnet test EasyGet.Tests/EasyGet.Tests.csproj -c Release --no-restore
dotnet build EasyGet.csproj -c Release --no-restore /warnaserror
dotnet format analyzers EasyGet.csproj --no-restore --verify-no-changes --diagnostics IDE0051 IDE0052 --severity info
git diff --check
```

未使用旧 issue 的完整原始批量链接做联网复测，不能从截图独自判断具体账号和当时服务端状态。本轮没有修改版本号、发布配置或第三方依赖，也没有提交、推送或发布。
