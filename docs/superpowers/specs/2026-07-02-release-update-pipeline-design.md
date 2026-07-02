# EasyGet Release and Update Pipeline Design

## Goal

Bring EasyGet to a clean `v1.1.0` release flow: versioned code, GitHub Actions packaging, GitHub Release assets, an Inno Setup installer, and an in-app updater that can download and launch the installer.

## Current State

The repository has `EasyGet.csproj` at `1.0.0`, a stale remote `v1.0.0` tag that points before the Telegram feature, a local publish script, and an Inno Setup script with hard-coded `1.0.0` metadata. There is no GitHub Actions workflow and no app-level update check. User data lives under `%LocalAppData%\EasyGet`; cookie data remains present, while Telegram API fields can be lost when an older build saves the shared config file.

## Release Architecture

GitHub Releases will be the release source of truth. A pushed `v*` tag triggers `.github/workflows/release.yml` on `windows-latest`. The workflow restores .NET, runs tests, publishes a self-contained `win-x64` build, installs Inno Setup, builds `EasyGet-Setup-vX.Y.Z.exe`, generates `easyget-update.json`, and uploads the setup exe, portable zip, and manifest to the GitHub Release.

The local release script remains useful for manual smoke checks. A new installer script wraps publishing and Inno Setup compilation so local and CI builds produce the same asset names.

## Update Architecture

The app will use a small WPF-friendly updater service instead of Electron's `latest.yml` flow. `AppUpdateService` calls the GitHub latest release API for `zzf-857/EasyGet`, parses the latest tag and release assets, compares semantic versions, and selects the setup exe asset. The settings page shows the current app version, check status, download progress, and buttons to check, download, and launch the downloaded installer.

The installer launch is explicit. The app downloads the setup exe into `%LocalAppData%\EasyGet\updates`, starts it with normal shell execution, and then exits so the installer can replace files safely. This keeps behavior predictable and avoids silent self-modification.

## Data Protection

`ConfigService.SaveAsync` will create a local `config.backup.json` before overwriting `config.json`. This does not undo damage already caused by older builds, but it gives future releases a recovery point if an old or broken build writes empty credentials.

## UI

Settings gets a new `版本与更新` section using the existing dark tool-panel style, icon/text buttons, compact status text, and the existing progress bar style. It will not add a marketing surface; it remains an operational control in the settings page.

## Testing

Tests will cover version comparison, GitHub release JSON parsing, installer asset selection, config backup behavior, workflow/script structure, and settings bindings. Full verification will run `dotnet test`, `dotnet build -c Release`, and local publish/installer commands where tools are available.
