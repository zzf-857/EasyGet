# EasyGet Douyin Sidecar Python Dependency License Inventory

This inventory is for the stage-1 Douyin sidecar packaging boundary. It records
the baseline runtime dependencies expected by `douyin-downloader-promax`, but
the current release asset remains smoke-only and does not claim a closed,
self-contained real-download runtime.

Optional extras are not included in this stage-1 boundary. In particular, the
browser extra (`playwright` and browsers), server extra (`fastapi`, `uvicorn`,
`pydantic`), transcribe extra (`openai-whisper`), dev/test tools, docs, images,
virtual environments, `.git`, `Downloaded`, `config.yml`, and `.cookies.json`
must stay out unless a later runtime task explicitly adds and audits them.

| Package | Current role | Declared license | Packaging note |
|---|---|---|---|
| `aiohttp` | Async HTTP client/server dependency used by the downloader runtime | Apache-2.0 | Baseline runtime candidate; verify compiled wheels and transitive notices in the final self-contained build. |
| `httpx` | HTTP client imported by storage/file manager code | BSD-3-Clause | Keep in sync with third-party `requirements.txt` and `pyproject.toml`. |
| `aiofiles` | Async file I/O | Apache-2.0 | Baseline runtime candidate. |
| `aiosqlite` | Async SQLite access for storage/deduplication paths | MIT | Baseline runtime candidate even if database use is later feature-gated. |
| `rich` | Console rendering used by the upstream CLI/runtime | MIT | Wrapper stdout must remain JSONL; Rich output must not leak into EasyGet stdout contracts. |
| `PyYAML` | YAML configuration parser | MIT | Verify `_yaml` native extension handling if bundled. |
| `python-dateutil` | Date parsing/filtering | Apache-2.0 / BSD | Include upstream license text when vendored into a real self-contained asset. |
| `gmssl` | SM2/SM3/SM4 signing helpers used by a-bogus code | BSD-style | Verify exact package metadata during the final dependency lock. |
| `certifi` | CA bundle, typically pulled transitively by HTTP clients | MPL-2.0 | Include Mozilla CA bundle notice when included in a binary artifact. |

Deferred optional/runtime-heavy packages:

- `imageio-ffmpeg==0.6.0`: listed by the third-party runtime for audio
  extraction before transcription upload, but it is large and must be validated
  before any release claims it is included.
- `playwright`: optional browser automation extra, excluded in stage 1.
- `openai-whisper`: optional transcribe extra, excluded in stage 1.
- `fastapi`, `uvicorn`, `pydantic`: optional server extra, excluded in stage 1.
- `pytest`, `pytest-asyncio`, `hypothesis`, `ruff`: dev/test only, excluded in
  stage 1.
