# Changelog

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
