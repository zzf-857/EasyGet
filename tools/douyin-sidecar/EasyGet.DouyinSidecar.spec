# -*- mode: python ; coding: utf-8 -*-
#
# EasyGet Douyin sidecar PyInstaller onefile smoke-only skeleton.
#
# This first spec intentionally packages tools/douyin-sidecar/sidecar.py only.
# It validates the release asset shape and --emit-sample smoke path. The real
# download path is documented as smoke-only: real download self-contained runtime is not closed here.
# sidecar.py has an in-process runner, but this spec still does not bundle the
# third-party source/dependency graph. True self-contained real downloads belong
# to the follow-up sidecar-runtime task.

from pathlib import Path
import os
import sys


sidecar_dir = Path(SPECPATH).resolve()
sidecar_entry = sidecar_dir / "sidecar.py"
third_party_root_value = os.environ.get("DOUYIN_DOWNLOADER_PROMAX_ROOT", "").strip()
third_party_root = Path(third_party_root_value).resolve() if third_party_root_value else None

pathex = [str(sidecar_dir)]
if third_party_root and third_party_root.exists():
    pathex.append(str(third_party_root))
    if str(third_party_root) not in sys.path:
        sys.path.insert(0, str(third_party_root))

bundled_source_roots = [
    "auth",
    "cli",
    "config",
    "control",
    "core",
    "storage",
    "tools",
    "utils",
]

datas = []
third_party_hiddenimports = []
if third_party_root and third_party_root.exists():
    datas.append((str(third_party_root / "run.py"), "douyin-downloader-promax"))
    init_file = third_party_root / "__init__.py"
    if init_file.exists():
        datas.append((str(init_file), "douyin-downloader-promax"))
    for root_name in bundled_source_roots:
        source_root = third_party_root / root_name
        if source_root.exists():
            datas.append((str(source_root), f"douyin-downloader-promax/{root_name}"))

    third_party_hiddenimports = [
        "auth",
        "auth.cookie_manager",
        "auth.ms_token_manager",
        "cli",
        "cli.login_flow",
        "cli.main",
        "cli.progress_display",
        "config",
        "config.config_loader",
        "config.default_config",
        "control",
        "control.queue_manager",
        "control.rate_limiter",
        "control.retry_handler",
        "core",
        "core.api_client",
        "core.comments_collector",
        "core.discovery",
        "core.downloader_base",
        "core.downloader_factory",
        "core.live_downloader",
        "core.metadata",
        "core.mix_downloader",
        "core.music_downloader",
        "core.retry_executor",
        "core.url_parser",
        "core.user_downloader",
        "core.user_mode_registry",
        "core.video_downloader",
        "storage",
        "storage.database",
        "storage.file_manager",
        "storage.metadata_handler",
        "tools",
        "tools.cookie_fetcher",
        "utils",
        "utils.abogus",
        "utils.cookie_utils",
        "utils.helpers",
        "utils.logger",
        "utils.naming",
        "utils.notifier",
        "utils.validators",
        "utils.xbogus",
        "aiofiles",
        "aiohttp",
        "aiosqlite",
        "certifi",
        "dateutil",
        "gmssl",
        "httpx",
        "rich",
        "yaml",
    ]

hiddenimports = third_party_hiddenimports

excluded_optional_modules = [
    "playwright",
    "fastapi",
    "uvicorn",
    "pydantic",
    "transcribe",
    "whisper",
    "openai_whisper",
    "pytest",
    "pytest_asyncio",
    "hypothesis",
    "ruff",
]

# Documented packaging boundary for future datas collection. These source/data
# areas must stay out of the stage-1 sidecar asset unless a later runtime task
# explicitly proves they are required and safe to ship.
excluded_source_roots = [
    "server",
    "tests",
    "docs",
    "img",
    "venv",
    ".git",
    ".hypothesis",
    ".pytest_cache",
    "Downloaded",
    "config.yml",
    ".cookies.json",
]

a = Analysis(
    [str(sidecar_entry)],
    pathex=pathex,
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excluded_optional_modules,
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="EasyGet.DouyinSidecar",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
