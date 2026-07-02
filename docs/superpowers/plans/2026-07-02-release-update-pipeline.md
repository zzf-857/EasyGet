# EasyGet Release and Update Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a complete `v1.1.0` release/update pipeline for EasyGet.

**Architecture:** GitHub Actions builds release assets from tags. The WPF app checks GitHub Releases directly, downloads the setup exe, and launches it on user command. Config writes create a backup before overwriting user data.

**Tech Stack:** .NET 8 WPF, xUnit, PowerShell, Inno Setup, GitHub Actions, GitHub Releases API.

---

### Task 1: Add Updater Domain Tests

**Files:**
- Create: `EasyGet.Tests/AppUpdateServiceTests.cs`
- Modify: `EasyGet.Tests/ReleaseScriptTests.cs`

- [ ] Write tests for semantic version comparison, release JSON parsing, installer asset selection, and workflow asset names.
- [ ] Run `dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter FullyQualifiedName~AppUpdateServiceTests` and confirm the tests fail because the service does not exist.

### Task 2: Implement App Update Service

**Files:**
- Create: `Models/AppUpdateInfo.cs`
- Create: `Services/IAppUpdateService.cs`
- Create: `Services/AppUpdateService.cs`
- Modify: `App.xaml.cs`

- [ ] Implement `IAppUpdateService.CheckLatestAsync`, `DownloadInstallerAsync`, and `LaunchInstaller`.
- [ ] Use `https://api.github.com/repos/zzf-857/EasyGet/releases/latest` with a User-Agent.
- [ ] Store installers in `%LocalAppData%\EasyGet\updates`.
- [ ] Run the updater tests and confirm they pass.

### Task 3: Add Settings UI and ViewModel Commands

**Files:**
- Modify: `ViewModels/SettingsViewModel.cs`
- Modify: `Views/SettingsView.xaml`
- Modify: `EasyGet.Tests/SettingsViewModelTests.cs`
- Modify: `EasyGet.Tests/XamlBindingTests.cs`

- [ ] Add app version/update properties and commands.
- [ ] Inject `IAppUpdateService`.
- [ ] Add `版本与更新` settings section with check/download/install controls and progress.
- [ ] Run settings and XAML tests.

### Task 4: Protect Local Config Writes

**Files:**
- Modify: `Services/ConfigService.cs`
- Modify: `EasyGet.Tests/ConfigServiceTests.cs`

- [ ] Add an internal testable config directory constructor.
- [ ] Create `config.backup.json` before overwriting existing config.
- [ ] Run config tests.

### Task 5: Release Scripts and GitHub Actions

**Files:**
- Create: `.github/workflows/release.yml`
- Create: `scripts/build-installer.ps1`
- Modify: `scripts/publish-win-x64.ps1`
- Modify: `scripts/EasyGet.iss`
- Modify: `EasyGet.Tests/ReleaseScriptTests.cs`

- [ ] Make publish script accept a version override.
- [ ] Make Inno output `EasyGet-Setup-vX.Y.Z.exe`.
- [ ] Add workflow that runs tests, publishes zip/setup/manifest, and uploads release assets.
- [ ] Run release script tests.

### Task 6: Version, Changelog, and Docs

**Files:**
- Modify: `EasyGet.csproj`
- Create: `CHANGELOG.md`
- Create: `docs/release_and_updater_manual.md`
- Modify: `README.md`
- Modify: `app.manifest`

- [ ] Bump version to `1.1.0`.
- [ ] Document the release flow and updater behavior.
- [ ] Update README completion status.

### Task 7: Full Verification and Release

**Files:** repository-wide

- [ ] Run `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`.
- [ ] Run `dotnet build EasyGet.csproj -c Release`.
- [ ] Run local publish and installer build if Inno Setup is available.
- [ ] Commit changes, push `main`, create annotated `v1.1.0`, push tag, and watch GitHub Actions produce Release assets.
