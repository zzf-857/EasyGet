# Changelog

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
