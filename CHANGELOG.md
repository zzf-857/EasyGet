# Changelog

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
