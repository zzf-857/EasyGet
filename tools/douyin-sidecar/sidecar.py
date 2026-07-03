#!/usr/bin/env python3
"""EasyGet Douyin sidecar prototype.

This wrapper keeps EasyGet isolated from the third-party downloader. It builds
a temporary config, invokes douyin-downloader-promax as a subprocess, and emits
machine-readable JSON events on stdout.
"""

from __future__ import annotations

import argparse
import contextlib
import importlib
import io
import json
import os
import re
import runpy
import subprocess
import sys
import tempfile
import time
import traceback
from datetime import date
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple


KNOWN_NUMBER_MODES = ("post", "like", "mix", "music", "collect", "collectmix", "allmix")
PRIMARY_MEDIA_SUFFIXES = (".mp4", ".mov", ".m4v", ".flv", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".mp3", ".m4a")
SECONDARY_NAME_MARKERS = ("_cover", "_avatar", "_music", "_data", "_comments", ".transcript")
SECONDARY_OUTPUT_SUFFIXES = {".json", ".txt"}
COUNT_RE = re.compile(
    r"Total:\s*(?P<total>\d+),\s*Success:\s*(?P<success>\d+),\s*"
    r"Failed:\s*(?P<failed>\d+),\s*Skipped:\s*(?P<skipped>\d+)",
    re.IGNORECASE,
)
COOKIE_NAME_RE = re.compile(r"^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$")
DATE_FILTER_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
PHASE_ONE_IMPORT_MODULES = (
    "config",
    "auth.cookie_manager",
    "core.api_client",
    "core.url_parser",
    "storage.file_manager",
    "storage.database",
)
SIDECAR_STATE_DIR_NAME = ".easyget-douyin-sidecar"


class CookieSourceError(ValueError):
    """Raised for CLI cookie source errors that should be returned as JSONL."""


def parse_cookie(cookie: str) -> Dict[str, str]:
    """Parse either a JSON cookie object or a Cookie header string."""
    if not cookie:
        return {}

    raw = cookie.strip()
    if not raw:
        return {}

    if raw.startswith("{"):
        try:
            loaded = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise ValueError(f"cookie JSON is invalid: {exc}") from exc
        if not isinstance(loaded, dict):
            raise ValueError("cookie JSON must be an object")
        return _sanitize_cookie_map(loaded)

    parsed: Dict[str, str] = {}
    for chunk in raw.split(";"):
        item = chunk.strip()
        if not item or "=" not in item:
            continue
        key, value = item.split("=", 1)
        key = key.strip()
        if not COOKIE_NAME_RE.match(key):
            continue
        parsed[key] = value.strip()
    return parsed


def _sanitize_cookie_map(cookies: Dict[Any, Any]) -> Dict[str, str]:
    sanitized: Dict[str, str] = {}
    for raw_key, raw_value in cookies.items():
        if not isinstance(raw_key, str):
            continue
        key = raw_key.strip()
        if not COOKIE_NAME_RE.match(key):
            continue
        sanitized[key] = "" if raw_value is None else str(raw_value).strip()
    return sanitized


def normalize_modes(mode: str) -> List[str]:
    modes = [part.strip().lower() for part in (mode or "post").split(",") if part.strip()]
    if not modes or modes == ["auto"]:
        return ["post"]
    return modes


def normalize_video_quality(quality: str) -> str:
    normalized = (quality or "").strip().lower()
    if normalized in ("", "best", "highest"):
        return "highest"
    if normalized == "lowest":
        return "lowest"
    if normalized in ("1080", "1080p"):
        return "1080p"
    if normalized in ("720", "720p"):
        return "720p"
    if normalized in ("480", "480p"):
        return "480p"
    if normalized in ("1440", "1440p"):
        return "1440p"
    if normalized in ("540", "540p"):
        return "540p"
    if normalized in ("360", "360p"):
        return "360p"
    return "highest"


def normalize_date_filter(value: str, option_name: str) -> str:
    normalized = (value or "").strip()
    if not normalized:
        return ""
    if not DATE_FILTER_RE.match(normalized):
        raise ValueError(f"{option_name} must use YYYY-MM-DD format.")
    try:
        date.fromisoformat(normalized)
    except ValueError as exc:
        raise ValueError(f"{option_name} must be a real date in YYYY-MM-DD format.") from exc
    return normalized


def build_config(
    *,
    url: str,
    output_dir: Path,
    cookie: str = "",
    proxy: str = "",
    mode: str = "post",
    limit: int = 1,
    quality: str = "best",
    include_cover: bool = False,
    include_avatar: bool = False,
    include_music: bool = False,
    include_json: bool = False,
    include_comments: bool = False,
    enable_database: bool = False,
    incremental: bool = False,
    start_time: str = "",
    end_time: str = "",
    download_pinned: bool = False,
) -> Dict[str, Any]:
    modes = normalize_modes(mode)
    numbers = {name: 0 for name in KNOWN_NUMBER_MODES}
    for selected_mode in modes:
        if selected_mode in numbers:
            numbers[selected_mode] = max(0, int(limit))
    numbers["allmix"] = numbers.get("mix", 0)
    increase = {
        "post": False,
        "like": False,
        "mix": False,
        "music": False,
        "allmix": False,
    }
    if incremental:
        for selected_mode in modes:
            if selected_mode in ("post", "like", "mix", "music"):
                increase[selected_mode] = True
        increase["allmix"] = increase["mix"]

    database_path = output_dir.expanduser().resolve() / SIDECAR_STATE_DIR_NAME / "dy_downloader.db"
    normalized_start_time = normalize_date_filter(start_time, "--start-time")
    normalized_end_time = normalize_date_filter(end_time, "--end-time")

    config = {
        "link": [url],
        "path": str(output_dir),
        "music": bool(include_music),
        "cover": bool(include_cover),
        "avatar": bool(include_avatar),
        "json": bool(include_json),
        "video_quality": normalize_video_quality(quality),
        "folderstyle": True,
        "filename_template": "{date}_{title}_{id}",
        "folder_template": "{date}_{title}_{id}",
        "author_dir": "nickname",
        "group_by_mode": True,
        "mode": modes,
        "number": numbers,
        "increase": increase,
        "thread": 3,
        "retry_times": 3,
        "rate_limit": 2,
        "proxy": proxy or "",
        "database": bool(enable_database or incremental),
        "database_path": str(database_path),
        "progress": {"quiet_logs": True},
        "transcript": {
            "enabled": False,
            "model": "gpt-4o-mini-transcribe",
            "output_dir": "",
            "response_formats": ["txt", "json"],
            "api_url": "https://api.openai.com/v1/audio/transcriptions",
            "api_key_env": "OPENAI_API_KEY",
            "api_key": "",
            "upload_audio_only": True,
        },
        "browser_fallback": {
            "enabled": False,
            "headless": True,
            "max_scrolls": 60,
            "idle_rounds": 6,
            "wait_timeout_seconds": 300,
        },
        "comments": {
            "enabled": bool(include_comments),
            "include_replies": False,
            "max_comments": 0,
            "page_size": 20,
        },
        "notifications": {
            "enabled": False,
            "on_success": False,
            "on_failure": False,
            "providers": [],
        },
        "cookies": parse_cookie(cookie),
    }
    if normalized_start_time:
        config["start_time"] = normalized_start_time
    if normalized_end_time:
        config["end_time"] = normalized_end_time
    if download_pinned:
        config["download_pinned"] = True
    return config


def build_config_from_args(args: argparse.Namespace, output_dir: Path) -> Tuple[Dict[str, Any], Dict[str, Any], List[str]]:
    cookie_text, cookie_source = resolve_cookie_source(args)
    config = build_config(
        url=args.url,
        output_dir=output_dir,
        cookie=cookie_text,
        proxy=args.proxy,
        mode=args.mode,
        limit=args.limit,
        quality=args.quality,
        include_cover=args.include_cover,
        include_avatar=args.include_avatar,
        include_music=args.include_music,
        include_json=args.include_json,
        include_comments=args.include_comments,
        enable_database=args.enable_database,
        incremental=args.incremental,
        start_time=args.start_time,
        end_time=args.end_time,
        download_pinned=args.download_pinned,
    )
    return config, cookie_source, cookie_redaction_secrets(cookie_text, config)


def resolve_cookie_source(args: argparse.Namespace) -> Tuple[str, Dict[str, Any]]:
    legacy_cookie = getattr(args, "cookie", "") or ""
    if legacy_cookie:
        raise CookieSourceError("Raw Cookie command-line input is not supported; use --cookie-env or --cookie-file.")

    cookie_env = (getattr(args, "cookie_env", "") or "").strip()
    cookie_file = (getattr(args, "cookie_file", "") or "").strip()
    if cookie_env and cookie_file:
        raise CookieSourceError("Only one cookie source may be selected: use --cookie-env or --cookie-file, not both.")

    if cookie_env:
        if cookie_env not in os.environ:
            raise CookieSourceError(f"Cookie environment variable is not set: {cookie_env}.")
        value = os.environ.get(cookie_env, "")
        if not value or not value.strip():
            raise CookieSourceError(f"Cookie environment variable is empty: {cookie_env}.")
        return value.strip(), {
            "source": "env",
            "env": cookie_env,
            "present": True,
            "redacted": True,
        }

    if cookie_file:
        path = Path(cookie_file).expanduser()
        try:
            resolved = path.resolve()
        except OSError:
            resolved = path
        if not path.is_file():
            raise CookieSourceError(f"Cookie file does not exist: {resolved}.")
        try:
            value = path.read_text(encoding="utf-8")
        except OSError as exc:
            raise CookieSourceError(f"Cookie file could not be read: {resolved}.") from exc
        if not value or not value.strip():
            raise CookieSourceError(f"Cookie file is empty: {resolved}.")
        return value.strip(), {
            "source": "file",
            "path": str(resolved),
            "present": True,
            "redacted": True,
        }

    return "", {
        "source": "none",
        "present": False,
        "redacted": False,
    }


def explicit_secure_cookie_source_requested(args: argparse.Namespace) -> bool:
    return bool((getattr(args, "cookie_env", "") or "").strip() or (getattr(args, "cookie_file", "") or "").strip())


def cookie_redaction_secrets(cookie_text: str, config: Dict[str, Any]) -> List[str]:
    cookies = config.get("cookies") if isinstance(config, dict) else {}
    secrets: List[str] = []
    if isinstance(cookies, dict):
        for value in cookies.values():
            if value:
                secrets.append(str(value))
    if cookie_text:
        secrets.append(cookie_text)
    return _dedupe([secret for secret in secrets if secret])


def redact_text(text: str, secrets: Sequence[str]) -> str:
    redacted = text or ""
    for secret in secrets:
        if secret:
            redacted = redacted.replace(secret, "***")
    return redacted


def make_progress_event(
    *,
    percent: float,
    downloaded_bytes: int = 0,
    total_bytes: int = 0,
    speed_bytes_per_sec: int = 0,
    eta_seconds: Optional[int] = None,
    message: str = "",
    details: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    event: Dict[str, Any] = {
        "event": "progress",
        "percent": max(0, min(100, percent)),
        "downloaded_bytes": int(downloaded_bytes),
        "total_bytes": int(total_bytes),
        "speed_bytes_per_sec": int(speed_bytes_per_sec),
        "eta_seconds": eta_seconds,
    }
    if message:
        event["message"] = message
    if details is not None:
        event["details"] = details
    return event


def make_log_event(message: str, details: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    event: Dict[str, Any] = {"event": "log", "message": message}
    if details is not None:
        event["details"] = details
    return event


def make_failed_event(
    error: str,
    *,
    details: Optional[Dict[str, Any]] = None,
    event_name: str = "failed",
) -> Dict[str, Any]:
    event: Dict[str, Any] = {
        "event": event_name,
        "error": error,
        "message": error,
        "reason": error,
    }
    if details is not None:
        event["details"] = details
    return event


def make_success_event(
    *,
    title: str,
    output_file_path: str,
    file_size_bytes: int,
    duration_seconds: float = 0,
    thumbnail_url: Optional[str] = None,
    details: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    event: Dict[str, Any] = {
        "event": "success",
        "title": title,
        "platform": "douyin",
        "duration_seconds": duration_seconds,
        "thumbnail_url": thumbnail_url,
        "file_size_bytes": int(file_size_bytes),
        "output_file_path": output_file_path,
    }
    if details is not None:
        event["details"] = details
    return event


def redact_config(config: Dict[str, Any]) -> Dict[str, Any]:
    safe = json.loads(json.dumps(config, ensure_ascii=False))
    cookies = safe.get("cookies")
    if isinstance(cookies, dict):
        safe["cookies"] = {key: ("***" if value else "") for key, value in cookies.items()}
    return safe


def find_downloader_root(explicit_root: str = "") -> Optional[Path]:
    candidates: List[Path] = []
    if explicit_root:
        candidates.append(Path(explicit_root))

    env_root = os.environ.get("DOUYIN_DOWNLOADER_PROMAX_ROOT")
    if env_root:
        candidates.append(Path(env_root))

    here = Path(__file__).resolve()
    starts = [Path.cwd().resolve(), here.parent]
    for start in starts:
        candidates.extend(_candidate_roots_from(start))

    seen = set()
    for candidate in candidates:
        resolved = candidate.expanduser().resolve()
        key = str(resolved).lower()
        if key in seen:
            continue
        seen.add(key)
        if (resolved / "run.py").exists():
            return resolved
    return None


def _candidate_roots_from(start: Path) -> Iterable[Path]:
    for base in (start, *start.parents):
        yield base / "douyin-downloader-promax"
        yield base / "GitRepositories" / "douyin-downloader-promax"
        yield base / "05_ThirdPartyRepos" / "GitRepositories" / "douyin-downloader-promax"
        if base.parent != base:
            yield base.parent / "05_ThirdPartyRepos" / "GitRepositories" / "douyin-downloader-promax"


def planned_command(python_exe: str, downloader_root: Optional[Path], config_path: Path, output_dir: Path) -> List[str]:
    run_py = (downloader_root / "run.py") if downloader_root else Path("<douyin-downloader-promax>") / "run.py"
    return [python_exe, str(run_py), "-c", str(config_path), "-p", str(output_dir)]


def resolve_downloader_python(python_exe: str, downloader_root: Optional[Path]) -> str:
    explicit = (python_exe or "").strip()
    if explicit:
        return explicit

    if downloader_root is not None:
        candidates = (
            downloader_root / ".venv" / "Scripts" / "python.exe",
            downloader_root / "venv" / "Scripts" / "python.exe",
            downloader_root / ".venv" / "bin" / "python",
            downloader_root / "venv" / "bin" / "python",
        )
        for candidate in candidates:
            if candidate.exists():
                return str(candidate.resolve())

    return sys.executable


def write_json_config(config: Dict[str, Any], config_path: Path) -> None:
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")
    if os.name != "nt":
        try:
            os.chmod(config_path, 0o600)
        except OSError:
            pass


def manifest_line_count(output_dir: Path) -> int:
    manifest = output_dir / "download_manifest.jsonl"
    if not manifest.exists():
        return 0
    try:
        return len(manifest.read_text(encoding="utf-8").splitlines())
    except OSError:
        return 0


def manifest_output_files(output_dir: Path, start_line: int) -> Tuple[List[str], List[Dict[str, Any]]]:
    manifest = output_dir / "download_manifest.jsonl"
    if not manifest.exists():
        return [], []

    try:
        lines = manifest.read_text(encoding="utf-8").splitlines()
    except OSError:
        return [], []

    output_files: List[str] = []
    entries: List[Dict[str, Any]] = []
    for line in lines[start_line:]:
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(entry, dict):
            continue
        entries.append(entry)
        for raw_path in entry.get("file_paths") or []:
            path = Path(str(raw_path))
            if not path.is_absolute():
                path = output_dir / path
            resolved_path = path.resolve()
            if not is_path_inside(resolved_path, output_dir):
                continue
            if is_sidecar_state_path(resolved_path, output_dir):
                continue
            if not resolved_path.is_file():
                continue
            if not is_supported_output_file(resolved_path):
                continue
            output_files.append(str(resolved_path))
    return _dedupe(output_files), entries


def is_path_inside(path: Path, directory: Path) -> bool:
    try:
        path.resolve().relative_to(directory.resolve())
        return True
    except ValueError:
        return False


def is_sidecar_state_path(path: Path, output_dir: Path) -> bool:
    try:
        relative_parts = path.resolve().relative_to(output_dir.resolve()).parts
    except ValueError:
        return False
    return any(part == SIDECAR_STATE_DIR_NAME or part.startswith(f"{SIDECAR_STATE_DIR_NAME}-") for part in relative_parts)


def scan_recent_output_files(output_dir: Path, since_timestamp: float) -> List[str]:
    if not output_dir.exists():
        return []

    files: List[str] = []
    for path in output_dir.rglob("*"):
        if not path.is_file():
            continue
        if is_sidecar_state_path(path, output_dir):
            continue
        if path.name in {"download_manifest.jsonl", "dy_downloader.db"}:
            continue
        if not is_supported_output_file(path):
            continue
        try:
            if path.stat().st_mtime + 1 < since_timestamp:
                continue
        except OSError:
            continue
        files.append(str(path.resolve()))
    return _dedupe(files)


def is_supported_output_file(path: Path) -> bool:
    suffix = path.suffix.lower()
    if suffix in PRIMARY_MEDIA_SUFFIXES:
        return True

    if suffix in SECONDARY_OUTPUT_SUFFIXES:
        name = path.name.lower()
        return any(marker in name for marker in SECONDARY_NAME_MARKERS)

    return False


def _dedupe(values: Sequence[str]) -> List[str]:
    seen = set()
    deduped: List[str] = []
    for value in values:
        if value in seen:
            continue
        seen.add(value)
        deduped.append(value)
    return deduped


def existing_output_files(output_files: Sequence[str]) -> List[str]:
    return _dedupe([raw_path for raw_path in output_files if Path(raw_path).is_file()])


def parse_counts(output: str) -> Optional[Dict[str, int]]:
    matches = list(COUNT_RE.finditer(output or ""))
    if not matches:
        return None
    match = matches[-1]
    return {key: int(match.group(key)) for key in ("total", "success", "failed", "skipped")}


def choose_primary_output_file(output_files: Sequence[str]) -> str:
    if not output_files:
        return ""

    def rank(raw_path: str) -> Tuple[int, int]:
        path = Path(raw_path)
        name = path.name.lower()
        suffix = path.suffix.lower()
        is_secondary = any(marker in name for marker in SECONDARY_NAME_MARKERS)
        try:
            suffix_rank = PRIMARY_MEDIA_SUFFIXES.index(suffix)
        except ValueError:
            suffix_rank = len(PRIMARY_MEDIA_SUFFIXES)
        return (1 if is_secondary else 0, suffix_rank)

    return sorted(output_files, key=rank)[0]


def file_size(path_text: str) -> int:
    if not path_text:
        return 0
    try:
        return Path(path_text).stat().st_size
    except OSError:
        return 0


def build_success_summary(
    *,
    url: str,
    output_files: Sequence[str],
    manifest_entries: Sequence[Dict[str, Any]],
    counts: Dict[str, int],
    return_code: int,
) -> Dict[str, Any]:
    primary_file = choose_primary_output_file(output_files)
    manifest_entry = manifest_entries[0] if manifest_entries else {}
    metadata = load_first_metadata_file(output_files)
    title = str(
        metadata.get("desc")
        or manifest_entry.get("desc")
        or (Path(primary_file).stem if primary_file else url)
    )
    duration_seconds = extract_duration_seconds(metadata)
    thumbnail_url = extract_thumbnail_url(metadata)

    return make_success_event(
        title=title,
        output_file_path=primary_file,
        file_size_bytes=file_size(primary_file),
        duration_seconds=duration_seconds,
        thumbnail_url=thumbnail_url,
        details={
            "return_code": return_code,
            "counts": counts,
            "output_files": list(output_files),
            "manifest_entries": len(manifest_entries),
        },
    )


def load_first_metadata_file(output_files: Sequence[str]) -> Dict[str, Any]:
    for raw_path in output_files:
        path = Path(raw_path)
        name = path.name.lower()
        if path.suffix.lower() != ".json" or not name.endswith("_data.json"):
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(data, dict):
            return data
    return {}


def extract_duration_seconds(metadata: Dict[str, Any]) -> float:
    video = metadata.get("video") if isinstance(metadata.get("video"), dict) else {}
    raw_duration = video.get("duration") or metadata.get("duration")
    try:
        duration = float(raw_duration)
    except (TypeError, ValueError):
        return 0
    if duration > 1000:
        return round(duration / 1000, 3)
    return duration


def extract_thumbnail_url(metadata: Dict[str, Any]) -> Optional[str]:
    video = metadata.get("video") if isinstance(metadata.get("video"), dict) else {}
    cover = video.get("cover") if isinstance(video.get("cover"), dict) else {}
    url_list = cover.get("url_list") or cover.get("urlList")
    if isinstance(url_list, list):
        for item in url_list:
            if isinstance(item, str) and item:
                return item
    return None


def tail_text(text: str, limit: int = 2400) -> str:
    normalized = (text or "").strip()
    if len(normalized) <= limit:
        return normalized
    return normalized[-limit:]


def classify_no_output_failure(output: str) -> Optional[str]:
    normalized = output or ""
    if "Cookies may be invalid or incomplete" in normalized:
        return "Douyin Cookie may be invalid or incomplete. Please update Cookie and retry."
    return None


def emit_sample(args: argparse.Namespace) -> List[Dict[str, Any]]:
    output_dir = Path(args.output_dir).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    sample_path = output_dir / "easyget_douyin_sidecar_sample.json"
    sample_payload = {
        "source": "easyget-douyin-sidecar",
        "kind": "sample",
        "url": args.url,
    }
    sample_path.write_text(json.dumps(sample_payload, ensure_ascii=False, indent=2), encoding="utf-8")

    return [
        make_progress_event(
            percent=10,
            message="emit_sample",
            details={"url": args.url},
        ),
        make_success_event(
            title="EasyGet Douyin sidecar sample",
            output_file_path=str(sample_path.resolve()),
            file_size_bytes=file_size(str(sample_path.resolve())),
            duration_seconds=0,
            thumbnail_url=None,
            details={"kind": "sample"},
        ),
    ]


def dry_run(args: argparse.Namespace) -> List[Dict[str, Any]]:
    output_dir = Path(args.output_dir).expanduser().resolve()
    config, cookie_source, _ = build_config_from_args(args, output_dir)
    downloader_root = find_downloader_root(args.downloader_root)
    config_path = output_dir / SIDECAR_STATE_DIR_NAME / "config.json"
    downloader_python = resolve_downloader_python(args.python, downloader_root)
    command = planned_command(downloader_python, downloader_root, config_path, output_dir)
    selected_runner_mode = select_runner_mode(args)

    return [
        make_log_event(
            "dry_run",
            details={
                "downloader_root": str(downloader_root) if downloader_root else None,
                "planned_command": command,
                "config": redact_config(config),
                "cookie_source": cookie_source,
                "runner_options": compatibility_options(args),
                "runner_mode": selected_runner_mode,
            },
        )
    ]


def self_test_imports(args: argparse.Namespace) -> Tuple[List[Dict[str, Any]], int]:
    downloader_root = find_downloader_root(args.downloader_root)
    if downloader_root is None:
        return [
            make_failed_event(
                (
                    "douyin-downloader-promax root not found. Pass --downloader-root "
                    "or set DOUYIN_DOWNLOADER_PROMAX_ROOT."
                ),
                details={
                    "step": "self_test_locate_downloader",
                    "imports_ok": False,
                    "checked_modules": list(PHASE_ONE_IMPORT_MODULES),
                    "failed_modules": list(PHASE_ONE_IMPORT_MODULES),
                },
            )
        ], 2

    results, captured_output = import_phase_one_modules(downloader_root)
    failed = {name: error for name, error in results.items() if error}
    details = {
        "step": "self_test_imports",
        "downloader_root": str(downloader_root),
        "imports_ok": not failed,
        "checked_modules": list(results.keys()),
        "failed_modules": list(failed.keys()),
        "errors": failed,
    }
    if captured_output:
        details["captured_output"] = tail_text(captured_output, limit=1200)

    if failed:
        return [
            make_failed_event(
                "douyin-downloader-promax import self-test failed.",
                details=details,
            )
        ], 2

    return [
        make_success_event(
            title="EasyGet Douyin sidecar import self-test",
            output_file_path="",
            file_size_bytes=0,
            details=details,
        )
    ], 0


def import_phase_one_modules(downloader_root: Path) -> Tuple[Dict[str, str], str]:
    root = downloader_root.resolve()
    original_cwd = Path.cwd()
    original_path = sys.path[:]
    original_modules = set(sys.modules)
    stdout_buffer = io.StringIO()
    stderr_buffer = io.StringIO()
    results: Dict[str, str] = {}

    try:
        os.chdir(root)
        root_text = str(root)
        sys.path[:] = [root_text, *[entry for entry in sys.path if entry != root_text]]
        with contextlib.redirect_stdout(stdout_buffer), contextlib.redirect_stderr(stderr_buffer):
            for module_name in PHASE_ONE_IMPORT_MODULES:
                try:
                    importlib.import_module(module_name)
                    results[module_name] = ""
                except Exception as exc:
                    results[module_name] = f"{type(exc).__name__}: {exc}"
    finally:
        os.chdir(original_cwd)
        sys.path[:] = original_path
        for module_name in list(sys.modules):
            if module_name not in original_modules and _is_third_party_downloader_module(module_name):
                sys.modules.pop(module_name, None)

    captured_output = "\n".join(
        part for part in (stdout_buffer.getvalue(), stderr_buffer.getvalue()) if part
    ).strip()
    return results, captured_output


def _is_third_party_downloader_module(module_name: str) -> bool:
    roots = ("auth", "cli", "config", "control", "core", "storage", "tools", "utils")
    return any(module_name == root or module_name.startswith(f"{root}.") for root in roots)


def run_real(args: argparse.Namespace) -> Tuple[List[Dict[str, Any]], int]:
    output_dir = Path(args.output_dir).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    try:
        config, _, redaction_secrets = build_config_from_args(args, output_dir)
    except ValueError as exc:
        return [
            make_failed_event(str(exc), details={"step": "parse_arguments"})
        ], 2

    downloader_root = find_downloader_root(args.downloader_root)
    if downloader_root is None:
        return [
            make_failed_event(
                (
                    "douyin-downloader-promax root not found. Pass --downloader-root "
                    "or set DOUYIN_DOWNLOADER_PROMAX_ROOT."
                ),
                details={"step": "locate_downloader"},
            )
        ], 2

    start_manifest_line = manifest_line_count(output_dir)
    start_timestamp = time.time()

    events = [
        make_progress_event(
            percent=5,
            message="starting_downloader",
            details={"downloader_root": str(downloader_root)},
        )
    ]

    if args.keep_temp_config:
        state_dir = output_dir / ".easyget-douyin-sidecar"
        state_dir.mkdir(parents=True, exist_ok=True)
        config_path = state_dir / "last-config.json"
        write_json_config(config, config_path)
        completed, exit_code = _invoke_downloader(
            args, downloader_root, config_path, output_dir, start_manifest_line, start_timestamp, redaction_secrets
        )
    else:
        with tempfile.TemporaryDirectory(prefix=".easyget-douyin-sidecar-") as temp_dir:
            config_path = Path(temp_dir) / "config.json"
            write_json_config(config, config_path)
            completed, exit_code = _invoke_downloader(
                args, downloader_root, config_path, output_dir, start_manifest_line, start_timestamp, redaction_secrets
            )

    events.append(completed)
    return events, exit_code


def _invoke_downloader(
    args: argparse.Namespace,
    downloader_root: Path,
    config_path: Path,
    output_dir: Path,
    start_manifest_line: int,
    start_timestamp: float,
    redaction_secrets: Sequence[str],
) -> Tuple[Dict[str, Any], int]:
    runner_mode = select_runner_mode(args)
    downloader_python = resolve_downloader_python(args.python, downloader_root)
    command = planned_command(downloader_python, downloader_root, config_path, output_dir)
    timeout = args.timeout if args.timeout and args.timeout > 0 else None

    if runner_mode == "in-process":
        completed = run_downloader_in_process(downloader_root, config_path, output_dir)
        command = list(completed.args)
    else:
        try:
            completed = subprocess.run(
                command,
                cwd=str(downloader_root),
                text=True,
                capture_output=True,
                encoding="utf-8",
                errors="replace",
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            error = redact_text(tail_text(str(exc)), redaction_secrets)
            return (
                make_failed_event(
                    f"Downloader timed out after {timeout} seconds. {error}",
                    details={
                        "step": "download_timeout",
                        "planned_command": command,
                        "runner_mode": runner_mode,
                    },
                ),
                1,
            )

    combined_output = redact_text(
        "\n".join(part for part in (completed.stdout, completed.stderr) if part),
        redaction_secrets,
    )
    counts = parse_counts(combined_output)
    output_files, manifest_entries = manifest_output_files(output_dir, start_manifest_line)
    if not output_files:
        output_files = scan_recent_output_files(output_dir, start_timestamp)
    output_files = existing_output_files(output_files)

    if counts is None:
        inferred_success = len(manifest_entries) if manifest_entries else (1 if output_files else 0)
        counts = {
            "total": inferred_success,
            "success": inferred_success,
            "failed": 0,
            "skipped": 0,
        }

    if completed.returncode != 0:
        error = tail_text(combined_output) or f"Downloader exited with code {completed.returncode}"
        return (
            make_failed_event(
                error,
                details={
                    "step": "download_failed",
                    "return_code": completed.returncode,
                    "planned_command": command,
                    "runner_mode": runner_mode,
                    "counts": {
                        **counts,
                        "failed": max(1, counts["failed"]),
                    },
                    "output_files": output_files,
                },
            ),
            completed.returncode or 1,
        )

    if not output_files or counts["success"] == 0:
        output_tail = tail_text(combined_output)
        classified_error = classify_no_output_failure(output_tail)
        error = classified_error or "Downloader completed with no output files or successful downloads."
        if output_tail and classified_error is None:
            error = f"{error}\n{output_tail}"
        details = {
            "step": "download_no_outputs",
            "return_code": completed.returncode,
            "planned_command": command,
            "runner_mode": runner_mode,
            "counts": {
                **counts,
                "failed": max(1, counts["failed"]),
            },
            "output_files": output_files,
        }
        if output_tail:
            details["captured_output_tail"] = output_tail
        return (
            make_failed_event(
                error,
                details=details,
            ),
            1,
        )

    return (
        build_success_summary(
            url=args.url,
            output_files=output_files,
            manifest_entries=manifest_entries,
            counts=counts,
            return_code=completed.returncode,
        ),
        0,
    )


def select_runner_mode(args: argparse.Namespace) -> str:
    requested = getattr(args, "runner_mode", "auto")
    if requested != "auto":
        return requested
    if (getattr(args, "python", "") or "").strip():
        return "subprocess"
    if getattr(args, "timeout", 0) and args.timeout > 0:
        return "subprocess"
    return "in-process"


def run_downloader_in_process(
    downloader_root: Path,
    config_path: Path,
    output_dir: Path,
) -> subprocess.CompletedProcess:
    run_py = downloader_root / "run.py"
    command = [sys.executable, str(run_py), "-c", str(config_path), "-p", str(output_dir)]
    stdout_buffer = io.StringIO()
    stderr_buffer = io.StringIO()
    original_cwd = Path.cwd()
    original_argv = sys.argv[:]
    original_path = sys.path[:]
    return_code = 0

    try:
        os.chdir(downloader_root)
        sys.argv = [str(run_py), "-c", str(config_path), "-p", str(output_dir)]
        root_text = str(downloader_root)
        sys.path[:] = [root_text, *[entry for entry in sys.path if entry != root_text]]
        with contextlib.redirect_stdout(stdout_buffer), contextlib.redirect_stderr(stderr_buffer):
            try:
                runpy.run_path(str(run_py), run_name="__main__")
            except SystemExit as exc:
                return_code = system_exit_code(exc)
                if return_code == 1 and exc.code not in (None, 0, 1):
                    print(str(exc.code), file=sys.stderr)
            except Exception:
                return_code = 1
                traceback.print_exc(file=sys.stderr)
    finally:
        os.chdir(original_cwd)
        sys.argv = original_argv
        sys.path[:] = original_path

    return subprocess.CompletedProcess(
        args=command,
        returncode=return_code,
        stdout=stdout_buffer.getvalue(),
        stderr=stderr_buffer.getvalue(),
    )


def system_exit_code(exc: SystemExit) -> int:
    code = exc.code
    if code is None:
        return 0
    if isinstance(code, int):
        return code
    return 1


def compatibility_options(args: argparse.Namespace) -> Dict[str, str]:
    options = {
        "format": args.format,
        "quality": args.quality,
        "title": args.title,
        "runner_mode": args.runner_mode,
    }
    return {key: value for key, value in options.items() if value}


def write_events(events: List[Dict[str, Any]], output_format: str) -> None:
    if output_format == "json":
        print(json.dumps(events[-1], ensure_ascii=True), flush=True)
        return
    for event in events:
        print(json.dumps(event, ensure_ascii=True), flush=True)


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="EasyGet Douyin sidecar prototype")
    parser.add_argument("--url", required=True, help="Douyin URL to download")
    parser.add_argument("--output-dir", required=True, help="Directory for downloaded files")
    parser.add_argument("--cookie", default="", help=argparse.SUPPRESS)
    parser.add_argument("--cookie-env", default="", help="Read Cookie header string or JSON object from this environment variable")
    parser.add_argument("--cookie-file", default="", help="Read Cookie header string or JSON object from this file")
    parser.add_argument("--proxy", default="", help="HTTP/HTTPS proxy, for example http://127.0.0.1:7890")
    parser.add_argument("--mode", default="post", help="User modes for profile downloads, for example post, like,mix,music, collect, or collectmix")
    parser.add_argument("--limit", type=int, default=1, help="Per-mode item limit; 0 means unlimited")
    parser.add_argument("--include-cover", action="store_true", help="Download cover images when supported")
    parser.add_argument("--include-avatar", action="store_true", help="Download author avatar images when supported")
    parser.add_argument("--include-music", action="store_true", help="Download music/audio side outputs when supported")
    parser.add_argument("--include-json", action="store_true", help="Save raw JSON metadata when supported")
    parser.add_argument("--include-comments", action="store_true", help="Collect comments when supported")
    parser.add_argument("--enable-database", action="store_true", help="Enable local SQLite deduplication/history database")
    parser.add_argument("--incremental", action="store_true", help="Enable incremental download for supported batch modes")
    parser.add_argument("--start-time", default="", help="Pass through third-party start_time filter value")
    parser.add_argument("--end-time", default="", help="Pass through third-party end_time filter value")
    parser.add_argument("--download-pinned", action="store_true", help="Include pinned posts in third-party downloads")
    parser.add_argument("--format", default="", help="Accepted for EasyGet C# runner compatibility; currently ignored")
    parser.add_argument("--quality", default="", help="Map EasyGet quality to third-party video_quality")
    parser.add_argument("--title", default="", help="Accepted for EasyGet C# runner compatibility; currently ignored")
    parser.add_argument("--downloader-root", default="", help="Path to douyin-downloader-promax")
    parser.add_argument("--python", default="", help="Python executable used for the third-party runner; defaults to the downloader venv when present")
    parser.add_argument("--timeout", type=int, default=0, help="Downloader timeout in seconds; 0 disables timeout")
    parser.add_argument("--dry-run", action="store_true", help="Emit planned config/command without downloading")
    parser.add_argument("--emit-sample", action="store_true", help="Emit deterministic sample JSONL and create a sample file")
    parser.add_argument("--self-test-imports", action="store_true", help="Verify phase-one downloader imports in the current runtime")
    parser.add_argument("--keep-temp-config", action="store_true", help="Keep last config under output-dir state folder for debugging")
    parser.add_argument(
        "--runner-mode",
        choices=("auto", "subprocess", "in-process"),
        default="auto",
        help="Downloader invocation mode; auto prefers in-process unless subprocess-only options are used",
    )
    parser.add_argument("--output-format", choices=("jsonl", "json"), default="jsonl")
    args = parser.parse_args(argv)
    if args.limit < 0:
        parser.error("--limit must be >= 0")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    try:
        args = parse_args(argv)
        if getattr(args, "cookie", ""):
            resolve_cookie_source(args)
        if explicit_secure_cookie_source_requested(args) and (args.emit_sample or args.self_test_imports):
            resolve_cookie_source(args)
        if args.emit_sample:
            write_events(emit_sample(args), args.output_format)
            return 0
        if args.self_test_imports:
            events, exit_code = self_test_imports(args)
            write_events(events, args.output_format)
            return exit_code
        if args.dry_run:
            write_events(dry_run(args), args.output_format)
            return 0

        events, exit_code = run_real(args)
        write_events(events, args.output_format)
        return exit_code
    except CookieSourceError as exc:
        output_format = "jsonl"
        if "args" in locals():
            output_format = args.output_format
        write_events([make_failed_event(str(exc), details={"step": "resolve_cookie_source"})], output_format)
        return 2
    except Exception as exc:
        fallback = make_failed_event(str(exc), details={"step": "sidecar_exception"})
        print(json.dumps(fallback, ensure_ascii=True), flush=True)
        return 1
    except KeyboardInterrupt:
        fallback = make_failed_event("cancelled by user", event_name="cancelled")
        print(json.dumps(fallback, ensure_ascii=True), flush=True)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
