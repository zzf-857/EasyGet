# Changelog

## 2.0.0 - 2026-09-10

EasyGet 2.0 重点升级桌面界面、批量下载稳定性与 Telegram 下载体验，并完成核心代码重构。继续兼容现有设置、下载历史和合集订阅。

### 界面与交互升级
- 重新设计单个下载、批量队列、媒体库和设置页面，统一字体、间距、控件与卡片样式，支持宽窗口和紧凑侧栏布局。
- 侧栏图标与文字统一左对齐，保持选中状态、数量徽标和紧凑模式布局一致。
- 合并高频进度、任务汇总和日志刷新，降低批量下载时的界面开销；修复清空日志后旧日志重新出现，以及复制日志遗漏待显示内容的问题。

### 批量下载卡住问题修复
- 修复进度更新持有任务锁并同步等待界面线程造成的锁反转，避免任务长期停在 0% 或等待中。
- 修复日志或进度订阅异常后停止读取子进程输出的问题，防止 yt-dlp 输出管道写满后阻塞、占用下载名额。
- 保留取消、暂停、恢复和重试时的任务身份检查，避免迟到进度覆盖已取消或重新开始的任务。
- 批量导入、全选和取消选择后统一更新分组及汇总，避免每个条目反复重建整个列表。

### Telegram 下载优化
- 修复私有频道和群组只查第一页会话的问题，完整处理分页与归档，并支持话题消息、消息范围、别名域名及 `tg://privatepost` 深链接。
- 下载前恢复已保存的登录会话，登录状态查询不再重复提交手机号；修复代理拆包、认证、超时和 IPv6 连接处理。
- 视频与文档使用 512 KiB 分块、最多四块并发并复用媒体连接；收到限流后按服务器要求等待并降低并发，只重试受影响的分块。
- 临时文件校验大小后再保存为正式文件，失败或取消时保留已有完整文件；范围任务存在真实下载失败时不再误报全部完成。
- 完善无数据超时、取消和后台传输回收，支持更清晰的错误说明、总耗时及平均速度日志。免费账号实际速度仍受 Telegram 服务端与网络限制。

### 抖音分享链接修复
- 将 `iesdouyin.com/share/video/视频编号` 和 `modal_id` 视频地址转换为下载器支持的作品地址，同时保留任务原始链接。
- 为短链接增加最多五次跳转、20 秒总超时、取消与成功结果缓存，减少重复解析；链接循环、失效或需要浏览器验证时给出明确提示。
- 改善 `Unsupported URL` 的中文说明并保留脱敏错误原因，便于区分地址格式、登录状态和平台限制。

### 性能优化与代码重构
- 拆分下载执行、历史记录保存、元数据解析、设置保存调度和本地文件解析职责，合并单个页与批量页的目录选择逻辑。
- 批量重复检测改为一次历史索引，队列指标改为单次汇总，附件与资源去重改为线性处理，减少重复遍历、JSON 解析和磁盘访问。
- 历史目录查询只读取必要字段，增量记录通过字典合并；健康数据库启动时不再重复写入订阅键或重建唯一索引，旧库仍按需升级。
- Cookie 状态一次索引后统一计算，保留认证来源优先级与保存失败重试；过滤异常浮点数据，避免污染进度和媒体信息。

### 验证与交付
- Release 自动化测试 1709 项通过，1 项原有在线测试跳过；构建零警告、零错误。
- 增加真实子进程、独立界面线程、旧数据库升级、大合集和分块传输回归；六个卡住场景完成旧实现失败、修复后通过的对照验证。
- 补充中文排查记录、下载优化说明和离线吞吐模拟报告；离线模拟结果不作为真实账号倍速承诺。
- 通过 GitHub Actions 生成 Windows 安装包、便携 ZIP、自动更新清单和 SPDX 依赖清单，并校验文件大小、SHA-256 及不可变发布状态。

## 1.4.6 - 2026-08-30

### Collection Subscriptions
- Persist collection source links and their original download directories after batch import, then support manual checks and configurable scheduled refreshes for newly published videos.
- List newly discovered video titles in a dedicated update view, allow selecting individual entries or downloading all updates, and keep new downloads in the collection's original directory.
- Track queued, downloaded, skipped, cancelled, and retried collection entries across restarts while avoiding duplicate downloads and preserving subscription state through backup and restore.

### Streaming Downloads
- Improve multi-entry collection parsing and naming, preserve collection metadata throughout the download queue, and route direct HTTP media resources through a dedicated downloader.
- Harden M3U8 downloads, retry and cancellation behavior, output reservation, progress reporting, and task persistence for long-running streaming downloads.

### Tests
- Add focused coverage for collection refresh coordination, subscription persistence, update selection, download-state synchronization, HTTP resources, M3U8 downloads, configuration, history, backup, and queue recovery.
- Verified 1404 automated tests pass; 1 live network test remains explicitly skipped.

## 1.4.5 - 2026-08-14

### Release Recovery
- Recover from the unpublished `v1.4.4` tag after the release workflow failed on a timing-sensitive scheduled-download restore test.
- Discover the tag-triggered `release.yml` run without crashing when `gh run list` still returns an empty JSON array under PowerShell StrictMode.

### Tests
- Keep a restored future schedule in `Scheduled` across process restart, then activate it once, instead of racing a 700ms due time on CI.

## 1.4.4 - 2026-08-14

### Reliability
- Keep the parse button usable, preserve the active download when the URL changes, restore waiting queue items as paused, and score taskbar progress against the full queue.
- Escape `%` in output directories, reserve distinct yt-dlp file names, fail when exit code 0 produces no file, and keep Yangshipin, Telegram, M3U8, and Xiaohongshu downloads from mixing identities or overwriting the wrong files.
- Isolate corrupt Cookie vaults and history databases, stop unknown-platform Cookie errors from blocking settings saves, and write crash logs under LocalAppData.

### Desktop Shell
- Enforce a single EasyGet instance that forwards URLs to the running window, rebuild the tray icon after Explorer restarts, and refresh page titles, navigation, toasts, and progress bars without adding list shadows or blur.

### Tool Updates
- Check official yt-dlp and ffmpeg releases from Settings, show current versus latest versions, and apply updates by downloading and replacing the tools. Downloads honor the app proxy and do not auto-install.

### Tests
- Add coverage for single-instance handoff, idle HTTP reads, reserved outputs, tool version checks, and the download, Cookie, and engine fixes.
- Verified 1305 automated tests pass; 1 live network test remains explicitly skipped.

## 1.4.3 - 2026-08-01

### Download Organization
- Standardize platform download folders, reuse existing collection directories, and improve batch organization across download and history workflows.
- Add local history thumbnails, media-aware directory discovery, and clearer grouping for existing downloads.

### Performance Guidance
- Add runtime performance recommendations for concurrency, fragments, connection counts, and related download settings.
- Present the recommendations in a dedicated settings dialog with the current value, suggested value, and expected impact.

### Desktop Shell
- Refine the tray context menu with a compact dark appearance, Fluent glyphs, and restrained interaction states.
- Make the title-bar close button, taskbar Close command, and Alt+F4 hide EasyGet while keeping it available from the tray; only the tray Exit command now shuts down the application.

### Diagnostics and Release Reliability
- Repair the Douyin sidecar import self-test arguments and avoid misclassifying command-line usage errors as expired Cookies.
- Add a guarded release entry point with immutable-release verification, tag ancestry checks, and end-to-end update-manifest validation.

### Tests
- Add regression coverage for platform directories, existing collection reuse, local thumbnails, performance recommendations, tray styling, close-to-tray behavior, sidecar health checks, and release automation.
- Verified 1184 automated tests pass; 1 live network test remains explicitly skipped.

## 1.4.1 - 2026-07-29

### Startup Reliability
- Correct the Windows tray integration to call `Shell_NotifyIconW`, preventing the v1.4.0 startup error and allowing yt-dlp and ffmpeg readiness initialization to complete.
- Make tray initialization, notifications, and cleanup fail safely, while retaining normal taskbar minimization when tray integration is unavailable.

### Release Reliability
- Preserve version-specific changelog notes when GitHub Actions publishes tagged releases.

### Tests
- Add regression coverage for the native tray export, startup ordering, and isolation of tray failures from core initialization.
- Verified 1106 automated tests pass; 1 live network test remains explicitly skipped.

## 1.4.0 - 2026-07-29

### Download Safety and Recovery
- Persist unfinished queues across restarts while restoring interrupted work in a paused state, and add download-directory, write-access, disk-space, and duplicate-download checks before work starts.
- Add guided first-run dependency readiness, categorized failure recovery actions, privacy-safe diagnostic bundles, and verified user-data backup and restore.

### Download Controls
- Add a configurable global yt-dlp rate limit, persistent scheduled downloads with restart recovery, and queue filtering and cancellation for planned work.
- Expose parsed source streams with real resolution, frame rate, container, codecs, estimated size, and yt-dlp format identifiers, while preserving exact selections in the queue.

### Desktop Operations
- Add tray controls, completion and failure notifications, optional sleep prevention during active downloads, periodic update checks, stale-package cleanup, and safe history-file lifecycle actions.

### Release Security
- Add optional Authenticode signing for first-party executables and installers, SPDX 2.2 SBOM generation with a pinned Microsoft tool checksum, and GitHub provenance and SBOM attestations.

### Tests
- Add focused coverage for recovery, persistence, scheduling, format selection, rate limiting, desktop operations, and release contracts.
- Verified 1101 automated tests pass; 1 live or environment-dependent test remains explicitly skipped.

## 1.3.10 - 2026-07-28

### Desktop Interface
- Redesigned the four primary workspaces with larger standardized typography, denser spacing, aligned controls, responsive navigation, and overflow-safe layouts.
- Added resizable batch-input and history side panes, queue thumbnails with aligned progress columns, richer folder details, context-menu deletion, and recent-download indicators.

### Responsiveness and Reliability
- Cached page views to remove navigation stalls and refreshed completed downloads and folders immediately in history.
- Prevented browser Cookie login checks from leaving all platform actions disabled when the external login flow stalls or exits unexpectedly.

### Tests
- Added layout contracts, cached-page coverage, and focused regressions for history refresh, notifications, settings, Cookie login, and download workflows.
- Verified 1015 automated tests pass; 1 live or environment-dependent test remains explicitly skipped.

## 1.3.9 - 2026-07-26

### Update Reliability
- Replaced the anonymous GitHub Releases API check with the static `easyget-update.json` Release asset, removing the shared hourly API rate-limit dependency.
- Strictly validated the manifest version, tag, setup filename, size, and SHA-256 before exposing an update.
- Verified installer length and SHA-256 while streaming to a temporary file, preserving any existing installer until the replacement passes every check.
- Bounded manifest and installer sizes and stopped unknown-length responses as soon as they exceed the declared package size.

### Installation Diagnostics
- Read both 32-bit and 64-bit Windows uninstall registry views so Inno installations are recognized correctly on 64-bit systems.

### Tests
- Added regression coverage for static manifest requests, malformed metadata, canonical Release URLs, response-size limits, package hashes, temporary-file cleanup, and dual registry views.
- Verified 985 automated tests pass; 1 live or environment-dependent test remains explicitly skipped.

## 1.3.8 - 2026-07-26

### Window Stability
- Restored saved window geometry before the first frame, eliminating the initial primary-monitor flash and delayed size jump.
- Persisted native normal-window placement and validated it against real monitor work areas, including negative coordinates, offset layouts, taskbars, and gaps between displays.
- Recovered windows after display, DPI, or work-area changes with debounced checks, bounded retries, and two-stage mixed-DPI resizing.
- Kept a usable draggable title-bar area visible and fitted oversized windows when moving from a larger display to a smaller one.

### Startup Reliability
- Loaded configuration and the selected theme before constructing the main window, so startup services use saved settings instead of temporary defaults.
- Unified configured and XAML window defaults and removed the obsolete MainViewModel configuration dependency.

### Tests
- Added regression coverage for disconnected monitors, negative-coordinate displays, offset-layout gaps, top taskbars, draggable-area boundaries, 150% DPI, oversized windows, startup ordering, native placement persistence, and hook cleanup.
- Verified 970 automated tests pass; 1 live or environment-dependent test remains explicitly skipped.

## 1.3.7 - 2026-07-26

### Platform Download Reliability
- Let Douyin and TikTok downloads continue past blocked metadata lookups with deterministic fallback names and targeted guidance for fresh-cookie, login, HTTP 403, and IP restrictions.
- Isolated EasyGet from user-level yt-dlp configuration, reused already resolved single-video metadata, and preserved the most actionable failure across Cookie retry attempts.
- Made Telegram cancellation effective throughout login, message lookup, and media transfer; strictly validated Telegram and shared URLs, and confined remote filenames to the selected download directory.
- Propagated Xiaohongshu image-download cancellation correctly instead of reporting it as an ordinary failure.

### Download Task Stability
- Reworked each download attempt to own its cancellation and cleanup resources, preventing pause, resume, retry, cancel, shutdown, and rapid repeated actions from interfering with one another.
- Replaced live concurrency resizing with a cancellation-aware gate so rapid limit changes neither over-admit work nor leave queued downloads stuck.
- Gave concurrent M3U8 jobs unique temporary paths, rejected master playlists and incomplete segment sets, and only promoted non-empty ffmpeg output after a successful merge.
- Stopped M3U8 progress reporters and external merge processes cleanly on completion or cancellation, avoiding stale progress and background-task leaks.

### Application Reliability
- Restored windows to a visible monitor when a saved display position is no longer available and corrected Ctrl+1-4 navigation after removal of the obsolete Douyin workspace.
- Handled temporarily busy clipboard access without disrupting single or batch downloads, bound compatibility-login dialogs to the main window, and released download log subscriptions when pages unload.
- Kept unsaved manual Cookie drafts out of automatic settings saves, fixed overlapping save-version tracking, and ensured history database connections release their files cleanly.

### Security and Updates
- Validated update-package names and byte counts before installation, removed incomplete temporary downloads, and preserved an existing installer when a replacement download is truncated or invalid.

### Tests
- Added focused regression coverage for download-attempt races, dynamic concurrency, M3U8 completeness and cleanup, Telegram URL and path safety, platform fallbacks, settings persistence, updater integrity, shortcuts, and off-screen window recovery.
- Verified 958 automated tests pass; 1 live or environment-dependent test remains explicitly skipped.

## 1.3.6 - 2026-07-18

### Download History Selection
- Made “select all current” reversible in both the workspace header and floating toolbar, with clear “cancel select all” and “clear selection” states.
- Added Ctrl+A to select the current directory and Escape to leave selection mode without intercepting text-entry controls.
- Moved batch-folder selection to a persistent leading checkbox and added unchecked, partial, and fully selected states.

### Bulk Organization Interaction
- Made the bottom target-folder picker open upward with a matching chevron, bounded height, stronger contrast, and popup elevation.
- Added an explicit “create an organization folder first” empty state instead of presenting an apparently unresponsive empty picker.
- Exposed folder actions during keyboard focus as well as pointer hover and removed visual lift that displaced folder hit areas.

### Tests
- Added regression coverage for reversible selection, tri-state batch selection, upward popup placement, empty-folder guidance, keyboard shortcuts, and stable folder-card geometry.
- Verified 918 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.5 - 2026-07-17

### Download History Performance
- Replaced the eager history-card wrap panel with a recycling, pixel-scrolling virtualized row layout so large playlists only create cards near the viewport.
- Added responsive row regrouping for window-size changes while preserving smooth continuous scrolling.
- Skipped full-content transition animation for large folders to keep directory entry responsive.

### Selection and Layout Fixes
- Replaced the layout-changing selected border with a non-interactive overlay ring that stays aligned with the card frame.
- Removed hover translation that displaced the visual card from its interaction area and added a safe top inset so first-row outlines and shadows are not clipped.
- Tightened the transition between the folder workspace and media grid, removed raw file-path tooltips, and avoided unwanted focus outlines after whole-card selection.

### Tests
- Added regression coverage for 75-item virtualized folders, responsive row grouping, recycling configuration, large-folder animation guards, and stable selection geometry.
- Verified 917 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.4 - 2026-07-17

### Download History Selection
- Made the entire non-action area of every history card toggle selection, so users no longer need to target the small circular selector.
- Kept the circular selector fully functional while preventing its click from bubbling into a second card-level toggle.
- Excluded preview, folder, source-link, and delete controls from card selection and protected drag-to-folder gestures from being misread as clicks.
- Ignored the second half of a double-click so a rapid click does not immediately undo the intended selection.

### Tests
- Added regression coverage for whole-card selection, nested action isolation, and drag-state protection.
- Verified 915 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.3 - 2026-07-17

### Apple-inspired Visual System
- Rebuilt the dark semantic palette around neutral layered materials, Apple system blue, softer dividers, restrained elevation, and clearer text contrast.
- Preserved the legacy `Indigo` theme key while transparently migrating its appearance to the new system-blue default, so existing user settings do not reset.
- Refined shared buttons, text fields, segmented controls, scrollbars, window controls, notifications, and the source-list sidebar with consistent radii and short spatial motion.

### Download History
- Reorganized the page into a compact title area, centered search toolbar, one coherent folder workspace, and a four-column responsive media library.
- Replaced the permanent folder-name input with a focused creation popover and kept “select all current” directly beside it.
- Added Finder-style custom and automatic folder cards, circular selectors, lighter media cards, hover quick actions, and a bottom floating contextual toolbar that no longer reflows the grid.

### Interaction and Reliability
- Added subtle lift, fade, and directory-transition animations without blurring card text or thumbnails.
- Added a delayed scroll-position safeguard so opening history through navigation or keyboard shortcuts consistently starts at the top.
- Kept drag-and-drop organization, rename, folder navigation, batch actions, selection scope, and download-history persistence unchanged beneath the redesigned surface.

### Tests
- Added regression coverage for the new semantic styles, creation popover, floating selection toolbar, card motion, and scroll-reset behavior.
- Verified 914 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.2 - 2026-07-17

### History Workspace
- Removed the redundant all-records and unfiled folder cards and combined folder creation and batch organization into one workspace panel.
- Moved “select all current” beside the create-folder action and scoped it to records actually displayed in the current directory.
- Promoted imported playlists and download batches into large, responsive folder cards with two-line titles, counts, size/date summaries, group selection, local-folder access, and deletion actions.
- Added direct folder navigation with a clear return-to-history-home action while keeping organized records out of the loose home grid.

### Interaction Polish
- Reset the history list to the top after entering or leaving folders, changing media filters, or starting a new search.
- Added a bounded scrolling folder area for large libraries, an automatic-folder empty hint, selected-folder styling, and safe title truncation in narrow layouts.
- Kept the contextual bulk toolbar hidden until records are selected and renamed the remove action to the clearer “move back home”.

### Tests
- Added regression coverage for automatic batch-folder discovery, folder navigation, root visibility, current-directory selection scope, return-home behavior, scroll reset, and the unified workspace layout.
- Verified 904 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.1 - 2026-07-17

### Download History
- Changed the history library from item-based logical scrolling to continuous pixel scrolling, so a mouse-wheel notch no longer jumps over an entire playlist or batch group.
- Rebuilt the organization area as a responsive wrapping layout with unified cards for all records, unfiled records, and custom folders.
- Added clearer selected states, count badges, concise folder descriptions, hover-only management actions, and an integrated folder-creation area.

### Bulk Organization
- Replaced the always-visible disabled controls with a calm selection prompt that expands into a contextual bulk toolbar only after records are selected.
- Kept selection counts, move/remove/delete actions, folder targets, and select-all controls readable without horizontal overflow.
- Kept folder counters synchronized after clearing the history library.

### Dialogs
- Replaced native Windows confirmation boxes in history and batch-download workflows with an EasyGet-themed modal dialog.
- Added action-specific Chinese confirmation labels, clear destructive-action styling, and Escape/Enter keyboard handling.

### Tests
- Added regression coverage for pixel scrolling, responsive folder cards, built-in folder behavior, themed confirmations, and folder-count refreshes.
- Verified 904 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.3.0 - 2026-07-17

### History Library
- Added persistent custom folders for organizing download history without moving or rewriting local video files.
- Added folder counts, all/unfiled views, create/rename/delete actions, drag-and-drop organization, per-item checkboxes, whole-collection selection, and bulk move/remove/delete controls.
- Kept imported playlists collapsed as named collections while restoring a compact multi-column grid for ordinary downloads.
- Added automatic SQLite migration for existing history databases and preserved every existing file path.

### Batch Download Experience
- Added total, completed, active, remaining, failed, and cancelled task summaries with aggregate progress, live transfer speed, per-task ETA, and direct folder access.
- Added active, failed, finished, and all-task filters plus pause all, resume all, retry failed, stop unfinished, and clear finished controls.
- Defaulted the queue to unfinished work so large completed playlists no longer overwhelm the active download view.
- Improved playlist import deduplication, preserved original episode indexes when some entries already exist, kept real playlist titles, and cleared successfully queued input automatically.

### Interaction Fixes
- Fixed merge-stage cancellation so stopping a task cancels the underlying work instead of only removing its card.
- Excluded completed-task speed from the live aggregate and cleared stale speed/ETA when a task leaves the downloading state.
- Added stable loading and empty states, keyboard-first folder renaming with automatic focus, truthful operation notifications, and a directly accessible “select all current” action.
- Prevented stale concurrent history searches and repeated bulk queue refreshes from causing misleading results or unnecessary UI churn.
- Disabled single-video parsing until the input contains a valid URL and added clear input guidance.

### Tests
- Added folder migration, persistence, drag/move, bulk selection, playlist indexing, 85-item admission, queue filtering, progress, cancellation, command-state, and XAML interaction regression coverage.
- Verified 894 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.5 - 2026-07-17

### Playlist Organization
- Read the platform's real playlist title during import and use it as the dedicated download folder and history-group name.
- Keep playlist files directly inside that folder even when automatic platform categorization is enabled.
- Remove the repeated collection title and `p01`/`p02` markers from each filename while preserving the platform-provided episode number and title.
- Reuse the same folder and stable history group when the same playlist is downloaded again.
- Recover real collection names from older Bilibili multipart history entries where possible.

### Compatibility
- Fetch playlist root metadata and entry URLs in one flat JSON pass while retaining EasyGet's automatic Cookie strategy fallback.

### Tests
- Added real-title parsing, filename cleanup, direct-folder output, stable grouping, playlist metadata, and legacy-history regression coverage.
- Verified 882 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.4 - 2026-07-17

### Batch Organization
- Create one dedicated root folder for every multi-link batch or imported playlist before downloads start.
- Name Bilibili multipart folders with the shared BV identifier, while mixed-platform batches use a timestamped batch folder.
- Persist batch identity, display name, and root directory in SQLite without discarding or rewriting existing history.
- Collapse each new batch into one history group with actions to expand items, open the batch folder, or remove the whole group of history records without deleting downloaded files.
- Automatically group legacy Bilibili `?p=` history entries by BV identifier so existing multipart downloads are less cluttered after upgrading.

### Performance
- Raised the supported simultaneous-download range from 1–8 to 1–12.
- Added a shared per-task connection budget for yt-dlp, native M3U8, and the Douyin sidecar; eight or more tasks automatically use at most four fragment connections each.
- Migrate only the untouched legacy `3 tasks / 8 fragments` profile to a hardware-aware recommendation; user-customized concurrency settings remain unchanged.
- On the reported i5-14600KF / 32 GB / 1 Gbps system, verified 12 simultaneous Bilibili downloads complete without HTTP 412/429 errors; 10 tasks is the recommended everyday setting.

### Tests
- Added batch-folder naming, collision handling, history migration/grouping, configuration migration, and shared concurrency-policy coverage.
- Verified 874 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.3 - 2026-07-17

### Fixes
- Fixed the batch queue crash caused by WPF `Run.Text` attempting a default two-way binding to the read-only `DownloadTask.StatusText` property.
- Made every display-only inline `Run` binding explicitly one-way, including batch status/progress and single-download clipboard/progress output.
- Deduplicated identical handled UI exception dialogs for one minute, preventing one malformed item template from opening a modal error window for every queued task.
- Changed handled dispatcher failures from the misleading “Application Crashed” message to a truthful recoverable UI error notice, while continuing to write diagnostic logs safely.

### Batch Downloads
- Added a regression scenario with all 85 `?p=` parts of the reported Bilibili video to verify every distinct part enters the queue before metadata resolution completes.

### Tests
- Added binding-mode and concurrent exception-throttling coverage.
- Verified 852 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.2 - 2026-07-16

### Fixes
- Completed the system-browser login loop: “Login and Detect” now opens the Windows default browser, polls local browser Cookie metadata for up to three minutes, and updates the platform row as soon as the login is detected.
- Distinguished a detected browser login from a Cookie source that has already passed a real download or metadata request, avoiding both false success and the previous ambiguous “browser profiles found” message.
- Use Chromium WAL activity when detecting profile changes so newly completed logins are noticed before the browser checkpoints its main Cookie database.

### Performance
- Prioritize the browser profile that contains the matching platform login before other profiles during downloads.
- Cache readable and briefly unavailable profile probes; repeated status checks are instant while Cookie activity still invalidates stale results automatically.

### Privacy
- Login detection queries only Cookie domain, name, and expiry metadata. Cookie values, account details, and full browser profile paths are never displayed or logged.

### User Experience
- Renamed the primary action to “Login and Detect” and added separate visual states for detected login, download-verified access, login required, and temporarily unreadable browser data.

### Tests
- Added Chromium, Firefox, expiry, domain-isolation, malformed-database, cache-invalidation, WAL-activity, detected-profile ordering, login-loop, and truthful-error regression coverage.
- Verified 845 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.1 - 2026-07-16

### Fixes
- Changed platform login to open the Windows default browser, while keeping an explicit EasyGet compatibility login that stores platform-scoped cookies in the encrypted vault.
- Removed automatic managed-login popups from download attempts and prioritized the last successful browser profile, then the system-default browser and recent profiles.
- Made settings persistence latest-wins and flush pending changes before app shutdown or update installation, so custom download paths survive restarts and upgrades.
- Added serialized, cross-process, atomic config writes with on-disk flushing, validation, safe backups, and recovery from corrupt or legacy-overwritten primary configs.
- Clear EasyGet login data without affecting the user's system-browser session, and isolate tests from the real `%LocalAppData%\EasyGet` configuration.

### User Experience
- Added separate “Browser Login” and “Compatibility Login” actions with truthful status messages and visible settings-save feedback.

### Tests
- Added regression coverage for default-browser launching, explicit compatibility login, cookie strategy ordering, encrypted vault cleanup, rapid settings changes, update-time flushing, concurrent config saves, and backup recovery.
- Verified 835 automated tests pass; 14 live or environment-dependent tests remain explicitly skipped.

## 1.2.0 - 2026-07-16

### Features
- Added native recognition and download support for public `yangshipin.cn/video/home?vid=...` pages.
- Resolve each short-lived Yangshipin MP4 address immediately before download through a hidden Edge/Chrome session instead of persisting expiring signatures.
- Added resumable HTTP downloads with progress, proxy, referer, strict host validation, unique output names, and platform auto-categorization.

### Optimizations
- Coalesced concurrent metadata requests for the same canonical video so batch imports share one hidden-browser capture and size probe.

### Security
- Disabled automatic media redirects and now revalidate every redirect target against the HTTPS Yangshipin/CCTV MP4 boundary.

### Tests
- Added URL spoofing guards, dynamic DOM parsing, stable metadata caching, concurrent request coalescing, redirect validation, expired-link refresh, request-header/range-resume, and yt-dlp gateway routing coverage.
- Verified the live sample metadata against the downloaded media: title, 7,284-second duration, and 2,455,784,251-byte size all match.

## 1.1.8 - 2026-07-02

### Optimizations
- Increased the yt-dlp download buffer to `1M` to improve throughput on larger media streams without changing downloader selection or format behavior.
- Kept the m3u8 segmented downloader at its existing 16-way baseline while allowing higher existing fragment settings to raise parallel segment downloads up to the configured maximum.

### Tests
- Added regression coverage for m3u8 segment concurrency so the downloader does not accidentally fall below the previous default parallelism.
- Updated yt-dlp argument coverage to lock in the larger download buffer.

## 1.1.7 - 2026-07-02

### Optimizations
- Added a SQLite index for descending history reads so the history page can load newest-first records more efficiently as the local database grows.

### Tests
- Added regression coverage to ensure the history database creates the read-order index during initialization and legacy database upgrades.

## 1.1.6 - 2026-07-02

### Optimizations
- Replaced the remaining direct WinForms folder pickers with WPF `OpenFolderDialog`, keeping folder selection behavior while simplifying project dependencies.
- Switched the Inno Setup payload compression to `lzma2/ultra64`, reducing the setup executable size by about 0.95 MB in local release builds.

### Tests
- Added release-script coverage to keep direct WinForms folder dialog usage and weaker installer compression from returning.

## 1.1.5 - 2026-07-02

### Optimizations
- Reduced Release package size by limiting satellite resources to `zh-Hans` and removing debug symbols/PDB files from published artifacts.
- Tidied duplicate download-manager completion paths while preserving existing task and history behavior.
- Increased the initial yt-dlp download buffer to improve throughput without changing downloader selection or format behavior.

### Release
- Hardened version extraction in local and GitHub release scripts so conditional project property groups do not break strict-mode packaging.
- Removed duplicate installer file entries and excluded PDB files from the Inno Setup payload.

## 1.1.4 - 2026-07-02

### Fixes
- Added update download diagnostics around `.download` creation, stream disposal, final `File.Move`, installer launch, runtime path, and version detection.
- Added exclusive file-open checks and retry logging before replacing an existing downloaded installer package.
- Surfaced the current runtime mode in Settings so project-directory, published, development, and installed runs are easier to tell apart.

### Release
- Added GitHub Actions release version validation so `vX.Y.Z` tags must match `EasyGet.csproj` before packaging.
- Switched release asset selection to the exact setup package name for the tag instead of a broad setup wildcard.

### Tests
- Expanded updater regression tests to prove the final installer can be opened exclusively after download and `.download` is removed.
- Added coverage for runtime-mode detection, Inno close-application settings, and release workflow version guards.

## 1.1.3 - 2026-07-02

### Fixes
- Stabilized the release CI history-search debounce test so tagged GitHub Actions builds can finish reliably.

### Release
- Kept the fixed updater download flow from 1.1.2 and republished on a fresh version after the 1.1.2 workflow failed before assets were created.

## 1.1.2 - 2026-07-02

### Fixes
- Fixed app update downloads failing at the final rename step because the `.download` file was still open.

### Tests
- Added regression coverage for updater installer downloads moving from `.download` to the final setup executable.

## 1.1.1 - 2026-07-02

### Fixes
- Let Douyin links continue past metadata parsing when `yt-dlp` asks for fresh cookies, so the existing browser fallback can handle download.
- Ignore the local `EXE/` install directory in Git.

### Tests
- Added coverage for full Douyin share text URL extraction and Douyin fallback metadata.

## 1.1.0 - 2026-07-02

### Features
- Added GitHub Actions release packaging for tagged Windows releases.
- Added Inno Setup installer generation with stable `EasyGet-Setup-vX.Y.Z.exe` asset names.
- Added in-app GitHub Release update checking, update package download, and installer launch from Settings.
- Added local `config.backup.json` creation before config overwrites to protect cookies and Telegram credentials from future bad writes.

### Documentation
- Added the release and updater manual for local and GitHub-hosted releases.

### Tests
- Added regression coverage for updater version comparison, release parsing, config backups, settings bindings, and release scripts.
