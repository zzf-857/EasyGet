# EasyGet Agent Instructions

These instructions apply to the entire repository. Every agent must follow them for pushes, packaging, and releases.

## Normal Pushes And Releases Are Different Operations

- A normal code or documentation push updates `main`. It does not create or modify a version tag or GitHub Release.
- A formal release publishes a new immutable `vX.Y.Z` tag, runs the GitHub `Build and Release` workflow, and exposes update assets to installed EasyGet clients.
- Never infer that a request to "push" also authorizes a release. Ask the user when the intended operation is ambiguous.

## Normal Push Procedure

- Inspect `git status` and separate the current task from unrelated or concurrent worktree changes. Never discard changes owned by another task.
- Run tests appropriate to the changed surface. Stage only the intended paths or hunks; do not use `git add .` in a shared dirty worktree.
- Create a normal descriptive commit. Do not change `EasyGet.csproj`, create a version tag, or call a GitHub Release command as part of an ordinary push.
- Unless the user explicitly selected another branch, push only the intended commit to `origin/main`.
- Wait for the `Build and Package` run associated with the pushed commit SHA. A normal push is complete only after that run succeeds.
- If the normal push prepares a future release, include the new `CHANGELOG.md` section in this preparation commit, but leave the project version unchanged for `scripts/release.ps1`.

## The Only Release Entry Point

- Use `./scripts/release.ps1` for every formal release. Do not reproduce its steps manually or edit `EasyGet.csproj` solely to prepare a release.
- First run the non-publishing preflight (the explicit `-DryRun` switch is equivalent):

  ```powershell
  ./scripts/release.ps1 -Version X.Y.Z
  ```

- Show the user the selected version and preflight result. Obtain explicit user confirmation before running:

  ```powershell
  ./scripts/release.ps1 -Version X.Y.Z -Publish
  ```

- `-Publish` is never implied by an earlier generic request to commit or push.
- Do not use direct `git tag`, `git push origin vX.Y.Z`, `gh release create`, `gh release edit`, or `gh release delete` commands. The release script owns tag creation, pushing, workflow monitoring, and release verification.
- `-SkipTests` is exceptional. Use it only when the same release candidate has already passed the required Release tests and state that evidence to the user.

## Required Release State

Before the publishing run, all of the following must be true:

- The checkout is on `main`.
- The working tree is clean.
- Local `main` is synchronized with `origin/main` after fetching; it is neither ahead nor behind.
- `EasyGet.csproj` still contains the current version; the target `X.Y.Z` is a new, greater SemVer. The release script owns the version edit.
- `CHANGELOG.md` contains a non-empty `## X.Y.Z - YYYY-MM-DD` entry describing the release.
- The target `vX.Y.Z` tag and GitHub Release do not already exist.
- GitHub repository-level immutable releases remain enabled. Never disable this protection for a release.
- GitHub has registered `.github/workflows/release.yml`, and the workflow is active.
- Required tests pass.

Commit and push the target changelog entry and all release content as a normal `main` update first. Do not pre-edit the project version: after confirmation, `-Publish` updates `EasyGet.csproj`, creates the `chore: release vX.Y.Z` commit and annotated tag, and pushes them together. Run the non-publishing preflight from the clean, synchronized preparation commit.

## Published Tags Are Immutable

- GitHub repository-level immutable releases are enabled for future releases. The release script must verify the repository setting before pushing a tag; the workflow must verify that the published Release reports `immutable: true`.
- Never delete, force-move, overwrite, or reuse any published `v*` tag, even when its workflow or artifacts are broken.
- Never retag a newer commit with an existing version.
- Any fix after a tag has been pushed requires a new SemVer version and tag. For example, a fix after `v1.4.2` must be released as at least `v1.4.3`.
- If a release fails, diagnose and fix the cause on `main`, select a new version, add its changelog entry, and publish it through `scripts/release.ps1`.

## Completion Criteria

A push is complete when the intended `main` commit is present on `origin/main` and its required CI checks pass.

A release is complete only when all of the following have been verified:

- The tag-triggered `Build and Release` workflow completed successfully.
- The GitHub Release is public, non-draft, and marked as the latest release where applicable.
- The GitHub Release reports `immutable: true`.
- `EasyGet-Setup-vX.Y.Z.exe`, `EasyGet-win-x64-Release.zip`, `easyget-update.json`, and `EasyGet-vX.Y.Z.spdx.json` are present.
- `easyget-update.json` reports the expected version, tag, asset names, sizes, and SHA-256 values.
- The post-release smoke check confirms `https://github.com/zzf-857/EasyGet/releases/latest/download/easyget-update.json` resolves to the new version so the in-app updater can discover it.

Do not report a release as complete while the workflow is queued or running. On failure, report the failed job and preserve the published tag; the recovery path is always a new version.
