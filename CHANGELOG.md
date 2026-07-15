# Changelog

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
