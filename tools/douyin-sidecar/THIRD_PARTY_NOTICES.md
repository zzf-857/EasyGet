# EasyGet Douyin Sidecar Third-Party Notices

This directory is the release asset location for the EasyGet Douyin sidecar.

## douyin-downloader-promax

- Project: `douyin-downloader-promax`
- License: MIT License
- Bundled license file: `licenses\douyin-downloader-promax-LICENSE.txt`
- Build provenance: `sidecar-version.json` records the third-party repo path
  supplied by `DOUYIN_DOWNLOADER_PROMAX_ROOT` or `-ThirdPartyRepoRoot`, plus
  the commit and dirty status when the path is a Git checkout.

The current PyInstaller asset is a smoke-only release skeleton. It packages
`tools\douyin-sidecar\sidecar.py` so `EasyGet.DouyinSidecar.exe --emit-sample`
can be validated from the published Windows package.

Real Douyin downloads are not yet self-contained in this onefile asset.
`sidecar.py` has an in-process runner, but this onefile asset does not yet
bundle the third-party source tree or dependency graph needed for real
downloads. Closing that runtime loop is explicitly deferred to the follow-up
sidecar-runtime task.

## Apache-2.0 File-Level Notices

The third-party downloader contains file-level Apache-2.0 notices for signing
helpers that may be imported or packaged by a future self-contained runtime:

- `utils/xbogus.py`: derived from the Douyin_TikTok_Download_API project,
  copyright 2021 Evil0ctal, licensed under Apache License 2.0.
- `utils/abogus.py`: declares Apache License 2.0, author JohnserfSeed, with
  upstream project metadata pointing to `https://github.com/johnserf-seed`.

The stage-1 sidecar does not claim these files are bundled into a closed real
download runtime. If a later runtime task vendors or embeds them, keep this
notice and ship the Apache-2.0 license text in `licenses\APACHE-2.0.txt`.

## Python Package Licenses

Baseline Python dependency inventory: `licenses/python-dependency-license-inventory.md`.

The smoke-only skeleton only proves packaging, `--emit-sample`, and release
asset placement. The inventory documents baseline runtime dependencies that a
future self-contained sidecar is expected to audit. Optional extras such as
Playwright/browser automation, server mode, Whisper/transcribe, dev/test tools,
docs, images, virtual environments, `.git`, `Downloaded`, `config.yml`, and
`.cookies.json` are not included in the stage-1 packaging boundary.
