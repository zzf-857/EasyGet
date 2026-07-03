# EasyGet 抖音 Sidecar 发布打包与风险评估

日期：2026-07-03  
角色：子代理 F，发布打包与风险评估

## 结论

正式 Windows 发布包推荐采用 **PyInstaller onefile sidecar**，将抖音专项引擎作为独立可执行文件放入发布目录，例如 `sidecars\douyin\EasyGet.DouyinSidecar.exe`，由 EasyGet 进程启动、读取 stdout JSONL，并在取消时杀掉整棵进程树。

理由：

- 最符合 EasyGet 当前 win-x64 自包含发布模型：用户不需要预装 Python，zip 和 Inno Setup 安装包都可以递归包含 sidecar。
- 包内容可由 CI 固定，避免依赖用户机器的 Python、pip、PATH、第三方仓库绝对路径。
- C# 与 Python 的边界保持在 stdout JSONL，抖音高波动逻辑留在 sidecar 内，EasyGet 主程序不被第三方依赖污染。

但当前代码还不满足直接打包条件。现有 C# runner 默认调用 `python` 和 `sidecars\douyin_sidecar.py`，并传入 `--format`、`--quality`、`--title`；现有 wrapper 位于 `tools\douyin-sidecar\sidecar.py`，且 CLI 不接受这三个参数。发布前必须先由主代理对齐启动路径、参数和终态事件契约。

开发和联调阶段可以继续使用 **源码 + 第三方 venv**。不建议正式发布包依赖本机第三方仓库路径；该方式只适合开发者机器上的烟测。

Task C 已补充 PyInstaller onefile 发布骨架、第三方 notices/license 和 publish 脚本门禁。当前骨架只承诺 `--emit-sample` smoke 与发布资产落位；虽然 `sidecar.py` 已有 in-process runner，发布 exe 仍未内置第三方源码和依赖，不代表真实下载已自包含。

Task C.1 仅加强打包边界、manifest 和合规信息：

- PyInstaller spec 通过 `DOUYIN_DOWNLOADER_PROMAX_ROOT` 或 build 脚本参数接收可选第三方仓库根目录，不再把本机参考仓库路径写进 spec/build 脚本。
- `sidecar-version.json` 记录 `packagingMode`、`runtimeRequiresExternalPython`、`selfContainedRealDownload`、`excludedOptionalFeatures`、`artifactSizeBytes`、`licenseInventoryPath`、第三方 commit 和 dirty 状态。
- 发布资产补充 Python dependency license inventory、Apache-2.0 文件级 notice/license，并明确 optional extras 不包含在阶段一边界内。
- `sidecar.py` 提供 `--self-test-imports`，用于验证当前 runtime 能定位 `douyin-downloader-promax` 并导入阶段一关键模块；`publish-win-x64.ps1` 通过显式 `-RunDouyinImportSelfTest` 开关运行该门禁。
- `publish-win-x64.ps1` 提供显式 `-RunDouyinRealDownloadSmoke` 门禁；默认发布仍不要求 Cookie，只有传入 `-DouyinRealSmokeUrl` 和 `-DouyinCookieEnvVar` 或 `-DouyinCookieFile` 时才用发布产物 sidecar 做真实下载验证。

当前 `tools\douyin-sidecar\sidecar.py` 已具备 in-process runner 与 `--self-test-imports` 诊断入口，但发布包仍未把第三方源码和依赖闭环打进 onefile。只要 `runtimeRequiresExternalPython = true` 且 `selfContainedRealDownload = false`，发布包就只能被视为 smoke-only sidecar packaging skeleton。

## 阅读依据

- `EasyGet.csproj`
- `scripts\publish-win-x64.ps1`
- `scripts\build-installer.ps1`
- `scripts\EasyGet.iss`
- `README.md`
- `docs\release_and_updater_manual.md`
- `docs\douyin-special-engine-design.md`
- `docs\douyin-sidecar-manual.md`
- `tools\douyin-sidecar\sidecar.py`
- `Services\DouyinSpecialDownloadService.cs`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\pyproject.toml`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\requirements.txt`
- `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\LICENSE`

## 当前发布链路影响

EasyGet 当前发布链路是：

1. `scripts\publish-win-x64.ps1` 执行 `dotnet publish -r win-x64 --self-contained true`，输出到 `artifacts\publish\Release\win-x64`。
2. 发布脚本清理 pdb 和调试运行时文件，并检查 `EasyGet.exe` 与便携 zip 根目录。
3. `scripts\build-installer.ps1` 调用发布脚本，再用 Inno Setup 打包。
4. `scripts\EasyGet.iss` 会递归打包 `artifacts\publish\Release\win-x64\*` 到 `{app}`。

因此 sidecar 只要在 publish 阶段被复制到 `artifacts\publish\Release\win-x64` 下，zip 和安装包都会包含它。当前发布脚本尚未复制 Python sidecar、第三方项目、venv 或许可证文件。

当前本机体积锚点：

| 项目 | 体积 |
|---|---:|
| 现有 `artifacts\publish\Release\win-x64` | 约 138.18 MB |
| 第三方源码，不含 `.git` / `venv` / 下载缓存 | 约 4.97 MB |
| 本机第三方 `venv` | 约 180.14 MB |
| `imageio_ffmpeg` 包 | 约 83.66 MB |
| `imageio_ffmpeg` 内置 ffmpeg exe | 约 83.58 MB |

本机 venv 还包含 dev/server 依赖，例如 pytest、ruff、fastapi、uvicorn、pydantic；正式包应只保留运行时依赖，包体需要在 CI 上重新实测。

## 打包方案比较

### 方案 A：源码 + venv

做法：发布目录包含 `sidecars\douyin\sidecar.py`、第三方 `douyin-downloader-promax` 源码、一个预构建 Python venv 或便携 Python 运行时，EasyGet 启动 venv 中的 `python.exe`。

可行性：适合内部联调和验收，不推荐作为正式分发默认方案。

优点：

- 改造最少，当前 wrapper 本来就是调用第三方 `run.py`。
- 调试方便，现场可以直接看 Python 源码、配置和第三方输出。
- MIT attribution 容易处理，直接随源码附带 LICENSE。

主要风险：

- 普通 Windows venv 不是可靠的可重定位发布格式。本机 `pyvenv.cfg` 指向 `C:\Python314`，不能代表无 Python 环境的用户机器。
- 文件数量多、升级清理复杂，安装包和便携 zip 都会明显变大。
- 当前 wrapper 默认使用系统临时目录创建 `.easyget-douyin-sidecar-*` 配置目录，不再把包含 Cookie 的临时配置写入用户输出目录；如果进程被强杀，系统临时目录中仍可能残留，需要后续增加过期清理。
- 若把 venv 放在 `{app}`，安装覆盖升级时可能留下旧依赖；若放在 `%LocalAppData%`，又需要首次安装/修复逻辑。

体积估算：

- 源码约 5 MB。
- 当前本机 venv 约 180 MB；去掉 dev/server 依赖后仍可能在 120-150 MB 区间，主要由 Python 运行时和 `imageio-ffmpeg` 决定。
- 若后续启用 Playwright Chromium，预计再增加 150-250 MB 以上，不建议一期打包。

首次启动成本：

- 不需要 onefile 解压。
- Python 冷启动和依赖 import 预计 0.5-2 秒；真实下载还会叠加网络、Cookie 校验和抖音风控延迟。

适用场景：

- 开发者本地、CI 集成烟测、主代理确认 stdout JSONL 契约前的快速迭代。

### 方案 B：PyInstaller onefile

做法：CI 使用固定 Python 版本和 PyInstaller 构建 `EasyGet.DouyinSidecar.exe`，发布目录只包含 sidecar exe、必要许可证和 notices。EasyGet 启动 exe 而不是 `python sidecar.py`。

可行性：推荐作为正式发布路线，但需要先完成 sidecar 打包适配。

优点：

- 用户机器不需要 Python，不依赖 PATH，也不依赖第三方仓库绝对路径。
- 发布目录简单，zip 和安装包都容易验收。
- C# 取消时可以杀掉 sidecar exe 整棵进程树，进程边界清晰。
- 便于做版本锁定：`EasyGet.exe` 与 `EasyGet.DouyinSidecar.exe` 可以共同随 release tag 发布。

主要风险：

- 当前 wrapper 已有 in-process runner，但如果只把 wrapper 打成 onefile 而不收集第三方源码和依赖，它仍然需要外部第三方仓库路径，不能解决正式分发问题。显式 `--python` 或 `--timeout` 仍会回到 subprocess 模式。
- PyInstaller 需要补 spec 或重构 wrapper：要么把第三方项目作为包内模块直接 import，要么作为 data 解包到受控目录后调用，不能继续依赖 `F:\AI\...`。
- `aiohttp`、`PyYAML`、`Cryptodome`、`gmssl`、`imageio_ffmpeg` 等包含 native 或 data 文件，需要 hidden imports / datas 验证。
- onefile 每次启动会解压到 `%TEMP%\_MEI*`，冷启动会比源码 + venv 慢，且安全软件可能扫描大 exe。
- Python 打包 exe 有杀软误报风险，需要签名、固定 CI 环境、保留 SHA256，并做 Windows Defender/常见杀软 smoke。

体积估算：

- 包含 Python 运行时和运行时依赖后，sidecar exe 预计 100-160 MB；如果 `imageio-ffmpeg` 无法裁剪，单它就贡献约 84 MB。
- 如果确认一期不需要音频提取/转写并安全排除 `imageio-ffmpeg`，包体可能显著下降，但这属于后续实现任务，不能在未验证第三方 import 路径前直接裁剪。
- 不打包 Playwright、FastAPI server、dev/test 依赖。

首次启动成本：

- 冷启动需要解压 onefile，预计 2-8 秒；带 100 MB 以上 payload、机械盘或杀软扫描时可能更久。
- 建议 EasyGet 启动后不立即启动 sidecar，只在抖音专项任务首次执行时懒启动，并在 UI 文案中允许“正在启动抖音引擎”的短暂阶段。

适用场景：

- 正式 Windows 安装包和便携 zip。
- 无 Python 环境、普通终端用户。

### 方案 C：嵌入第三方仓库路径

做法：EasyGet 或 sidecar 固定使用 `F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax`，或者要求用户设置 `DOUYIN_DOWNLOADER_PROMAX_ROOT`。

可行性：只适合开发机，不适合发布。

优点：

- 零打包成本，方便并行子代理在同一工作区联调。
- 可以直接复用第三方仓库当前 venv 和调试文件。

主要风险：

- 用户机器不存在该路径，便携 zip 和安装包都会失效。
- 版本不可控，第三方仓库任何改动都可能让 EasyGet release 行为漂移。
- 许可证、依赖、Cookie、日志和数据库落点都不在 EasyGet 发布资产控制内。

适用场景：

- 本地 smoke、回归复现、对比第三方原始行为。

## 推荐发布分阶段

阶段 0：继续开发联调。

- 使用源码 + 第三方 venv。
- 通过 `--downloader-root` 或 `DOUYIN_DOWNLOADER_PROMAX_ROOT` 指向参考仓库。
- 只跑 `--dry-run`、`--emit-sample` 和少量真实链接。

阶段 1：正式发布前打包适配。

- 将 C# runner 的目标从 `python + .py` 改为可配置的 sidecar exe，或增加明确的发布模式。
- 对齐 wrapper CLI，至少处理 C# 当前可能传入的 `--url`、`--output-dir`、`--format`、`--quality`、`--title`、`--cookie`、`--proxy`、`--mode`、`--limit`。
- 确保 stdout 只输出 JSONL，终态必须是 `success`、`failed` 或 `cancelled`。
- 默认临时配置已从用户输出目录移到系统临时目录；后续可继续收敛到 `%LocalAppData%\EasyGet\tools\douyin-sidecar\tmp\` 并增加过期清理。

阶段 2：PyInstaller onefile 发布。

- CI 固定 Python 3.11 或 3.12；第三方声明 `requires-python >=3.9`，但不要用本机 Python 3.14 作为 release 基准。
- 构建 `sidecars\douyin\EasyGet.DouyinSidecar.exe`。
- 打包 `sidecars\douyin\THIRD_PARTY_NOTICES.md` 和 `sidecars\douyin\licenses\douyin-downloader-promax-LICENSE.txt`。
- `publish-win-x64.ps1` 增加 sidecar 文件存在性、`--emit-sample`、zip 内容和 SHA256 smoke。
- `publish-win-x64.ps1 -RunDouyinImportSelfTest` 增加 `--self-test-imports` 门禁；该门禁应在真实发布 CI 中打开，并指向具备 PyInstaller 与第三方运行依赖的 Python 环境。
- `publish-win-x64.ps1 -RunDouyinRealDownloadSmoke -DouyinRealSmokeUrl <url> -DouyinCookieEnvVar <env>` 或 `-DouyinCookieFile <path>` 增加真实下载门禁；脚本应清除 `DOUYIN_DOWNLOADER_PROMAX_ROOT`，使用发布目录 `sidecars\douyin\EasyGet.DouyinSidecar.exe`，并只在真实输出文件存在且非空时记录 `realDownloadVerified=true`。
- `build-installer.ps1` 继续复用 publish 目录；Inno Setup 会自动包含 sidecar 文件。

## 新增依赖

运行时必选依赖来自第三方 `requirements.txt`：

| 依赖 | 用途 | 发布注意 |
|---|---|---|
| `aiohttp` | 异步 HTTP 请求 | 含 native 扩展，PyInstaller 需验证 |
| `httpx` | HTTP 客户端，第三方 storage/file_manager 运行时导入 | 不能遗漏，否则 clean-room 启动失败 |
| `aiofiles` | 异步文件读写 | 文件取消和临时文件清理需验证 |
| `aiosqlite` | SQLite 去重/存储 | 若一期关闭 database，仍可能被模块导入 |
| `rich` | 第三方终端输出 | wrapper 不应把 Rich 进度条透传到 stdout |
| `pyyaml` | 配置读取 | 含 `_yaml` native 扩展 |
| `python-dateutil` | 日期过滤 | 运行时依赖 |
| `gmssl` | 抖音签名相关加密 | 依赖 `pycryptodomex`/`Cryptodome` |
| `imageio-ffmpeg==0.6.0` | 音频提取/转写前置 ffmpeg | 包体最大，约 84 MB；裁剪前必须验证 import |

不建议一期打包的 optional extra：

- `browser`：`playwright` 和 Chromium，体积大，安装/权限/验证码交互复杂。
- `server`：`fastapi`、`uvicorn`、`pydantic`。当前一期契约是 stdout JSONL，不需要常驻 REST server。
- `transcribe`：`openai-whisper`，体积和运行成本都不适合抖音专项一期。
- `dev`：`pytest`、`ruff`、`hypothesis` 等只留在 CI。

## 日志与数据位置

当前 EasyGet 已有位置：

| 数据 | 位置 |
|---|---|
| 配置文件 | `%LocalAppData%\EasyGet\config.json` |
| Cookie 转换文件 | `%LocalAppData%\EasyGet\cookies.txt` |
| 更新日志 | `%LocalAppData%\EasyGet\logs\update.log` |
| 崩溃日志 | `<应用目录>\logs\crash_*.txt` |

抖音 sidecar 推荐位置：

| 数据 | 推荐位置 | 说明 |
|---|---|---|
| sidecar exe | `<应用目录>\sidecars\douyin\EasyGet.DouyinSidecar.exe` | 随 zip/安装包发布 |
| sidecar 版本文件 | `<应用目录>\sidecars\douyin\sidecar-version.json` | 记录第三方 commit、构建 Python、依赖锁 |
| sidecar 日志 | `%LocalAppData%\EasyGet\logs\douyin-sidecar.log` | 必须脱敏 Cookie、msToken、ttwid、odin_tt 等 |
| sidecar 临时配置 | `%LocalAppData%\EasyGet\tools\douyin-sidecar\tmp\` | 不建议写在用户下载目录 |
| sidecar 数据库/去重 | `%LocalAppData%\EasyGet\tools\douyin-sidecar\data\` | 若启用第三方 database |
| 下载 manifest | `<输出目录>\download_manifest.jsonl` 或按 job 子目录保存 | 用于图文/批量多文件追溯 |
| PyInstaller 解压目录 | `%TEMP%\_MEI*` | 强杀后可能残留，启动时不应依赖它 |

当前 wrapper 的现实行为：

- stdout 输出 JSONL，由 C# 读取并映射到任务进度或日志。
- stderr 被 C# 收集，sidecar 非 0 退出时作为异常原因。
- 默认临时 config 在系统临时目录下的 `.easyget-douyin-sidecar-*`，正常退出会删除；如果被 `Kill(entireProcessTree: true)` 强杀，可能在系统临时目录残留，但不会混入用户下载输出目录。
- `--keep-temp-config` 会保留 `output-dir\.easyget-douyin-sidecar\last-config.json`，其中可能包含 Cookie，只能用于开发调试，正式包应禁用。

## 取消、杀进程与清理

当前 C# runner 在取消时调用 `process.Kill(entireProcessTree: true)`。这是正确方向，但发布前要验证以下行为：

- 源码 + venv 模式下，父进程是 Python wrapper，子进程是第三方 `run.py`，必须杀整棵进程树，否则真实下载可能继续。
- PyInstaller 模式如果仍然调用外部 `run.py`，说明打包没有闭环；正式模式应尽量避免再次依赖外部 Python。
- 强杀不会给 Python `TemporaryDirectory` 机会清理，Cookie 临时 config 可能在系统临时目录残留。后续仍建议改为受控 tmp 目录，并在 EasyGet 启动或 sidecar 启动时清理过期目录。
- 下载中的 `.tmp`、`.part`、空目录、未完成 manifest 行要么清理，要么在失败/取消日志中标记。
- 取消后 EasyGet 任务状态应为 `Cancelled`，不应因为进程退出码非 0 覆盖成 `Failed`。
- 取消后 5 秒内不应存在 `EasyGet.DouyinSidecar.exe`、`python.exe`、第三方 `run.py` 相关残留进程。

## 许可证与 attribution

第三方项目 `douyin-downloader-promax` 声明为 MIT License，版权行为：

```text
Copyright (c) 2026 jiji262
```

MIT 要求在软件副本或实质性部分中包含版权声明和许可声明。因此：

- 如果发布包复制第三方源码、打包进 PyInstaller exe，或分发其修改版，都必须随 EasyGet 发布资产包含第三方 LICENSE。
- 推荐新增 `sidecars\douyin\licenses\douyin-downloader-promax-LICENSE.txt`，内容来自第三方 `LICENSE`。
- 推荐新增 `sidecars\douyin\THIRD_PARTY_NOTICES.md`，说明 EasyGet 抖音专项能力基于 `douyin-downloader-promax`，许可证为 MIT，并记录第三方 commit/tag。
- 依赖包许可证还需要单独审计。PyInstaller 会把 Python 包并入 exe，不能只写第三方主项目的 MIT。
- README 或发布说明应补一行 attribution，避免用户只拿到安装包时看不到许可证来源。

## 平台接口变动风险

外部平台风险：

- 抖音 Web API、签名字段、`X-Bogus` / `a_bogus` / msToken、Cookie 名称和风控策略高波动。
- 短链解析、图文字段、无水印视频源、用户 post 分页字段可能随时变化。
- Cookie 缺失、Cookie 过期、账号风控和地区限制会成为高频失败，不应展示 Python traceback。

内部接口风险：

- C# sidecar client 与 Python wrapper 目前路径和参数不一致，是发布阻断项。
- stdout JSONL 字段必须版本化或至少稳定记录。新增字段可以兼容，不能改名或把机器可读内容换成 Rich 文本。
- 当前 wrapper 从第三方 stdout/stderr 解析 `Total: Success:` 计数字符串；第三方输出格式变化会影响 summary，但 manifest 仍可作为兜底。
- `success.output_file_path` 对单视频、图文、用户 post 的语义必须由主代理定稿，否则历史记录和打开文件位置会漂移。
- EasyGet 现有全局代理支持 HTTP / SOCKS5；第三方依赖未显式包含 SOCKS 支持包。SOCKS5 代理是否可用需要实施任务验证或增加依赖。

## 发布验证清单

### 1. 无 Python 环境

- 在干净 Windows 10/11 VM 上卸载 Python，确保 `python`、`py` 不在 PATH。
- 安装 `EasyGet-Setup-vX.Y.Z.exe`，或解压便携 zip。
- 运行抖音 sidecar `--emit-sample` 等价路径，确认不依赖系统 Python。
- 启动 EasyGet，执行一个抖音 sample/dry-run 任务，日志中不得出现 `python was not found`。
- 失败时应提示“抖音专项引擎不可用/启动失败”，EasyGet 主程序不得崩溃。

### 2. Cookie 缺失

- 清空 EasyGet Cookie 设置和 sidecar 临时 Cookie 文件。
- 单视频、图文、用户 post 各跑一次。
- 如果平台要求登录，失败应映射为可操作中文提示，例如“抖音 Cookie 缺失或登录态不足，请在设置中更新 Cookie 后重试”。
- 日志中不得包含 Cookie 原文，也不得写入保留的调试 config。

### 3. Cookie 失效

- 写入格式正确但已过期或伪造的 Cookie。
- 用户 post 应尽早失败，不应长时间重试到 UI 卡死。
- 错误摘要优先输出 `failed.error`，C# 应展示 `error` 而不是 traceback。
- 验证 `%LocalAppData%\EasyGet\logs\douyin-sidecar.log` 只含脱敏信息。

### 4. 用户取消

- 选择一个较大的用户 post 任务，例如 limit 50。
- 下载开始后立即取消，再在下载中段取消一次。
- 5 秒内确认无 sidecar、Python、第三方 runner 残留进程。
- 输出目录中没有含 Cookie 的 `.easyget-douyin-sidecar-*` 残留；未完成媒体文件要么被删除，要么以可识别临时后缀保留。
- EasyGet 任务状态显示 `Cancelled`，不写入成功历史。

### 5. 大批量 post

- 使用用户主页 post，分别验证 limit 20、100、500。
- 进度应按作品计数或下载字节单调前进，不应长期停在 0 或超过 100。
- UI 应保持可响应，内存不随作品数线性失控。
- `download_manifest.jsonl` 能追溯每个成功作品；失败/跳过计数可见。
- 必须验证数量上限提示，避免用户误下全量主页。

### 6. 代理

- HTTP 代理：设置 `http://127.0.0.1:端口`，确认 sidecar 真实走代理。
- HTTPS 代理：使用同一代理地址验证抖音 API 和媒体下载。
- SOCKS5 代理：如果 EasyGet UI 允许 SOCKS5，必须确认第三方 HTTP 栈支持；若不支持，应在 UI 或 sidecar 错误中明确“不支持 SOCKS5 代理”。
- 代理不可用时应快速失败，错误信息包含代理连接失败但不包含 Cookie。

### 7. 输出路径含中文/空格

- 输出目录使用类似 `D:\下载 测试\抖音 专项\`。
- 安装目录使用默认 `C:\Program Files\EasyGet`。
- 验证单视频、图文、用户 post 均能创建目录和文件。
- stdout JSONL 的 `output_file_path` 必须是 UTF-8 可解析的 Windows 绝对路径。
- EasyGet 历史记录、打开文件夹和后续清理均能处理中文与空格。

## 发布门禁

正式启用抖音专项引擎前，至少满足：

- `dotnet test EasyGet.Tests\EasyGet.Tests.csproj` 通过。
- `tools\douyin-sidecar\tests\test_sidecar.py` 或等价打包后契约测试通过。
- `sidecar.py --self-test-imports` 在第三方参考 venv 中通过；打包后的 sidecar 若声明自包含，也必须在无外部 Python/无外部第三方仓库路径时通过等价 import 自检。
- 打包后的 sidecar 在无 Python VM 上通过 `--emit-sample` 和一次 Cookie-backed 真实下载烟测；发布 manifest 只允许记录 URL hash 和 Cookie 来源标签，不得记录原始 URL 或 Cookie 内容。
- zip 根目录或安装目录包含 sidecar exe、第三方 LICENSE、notices。
- 取消测试确认无残留进程和 Cookie 临时文件。
- Cookie 缺失/失效、代理失败、路径含中文空格均有中文可操作错误。
- `publish-win-x64.ps1` 或 CI release job 记录 sidecar exe SHA256 和体积。

## 当前验证状态

截至 2026-07-03，本地候选 onefile 已经能在提供 `-ThirdPartyRepoRoot` 与具备 PyInstaller/运行依赖的第三方 venv 时，打包第三方源码和阶段一 hidden imports。发布后的 `EasyGet.DouyinSidecar.exe --self-test-imports` 在清空 `DOUYIN_DOWNLOADER_PROMAX_ROOT`、切换到临时 cwd 后通过，`downloader_root` 指向 PyInstaller 解包目录 `_MEI*\douyin-downloader-promax`。

真实单视频 smoke 已能启动 bundled 第三方 runner，但在无 Cookie 环境下返回 `Success 0 / Failed 1`，sidecar 将其映射为 `Douyin Cookie may be invalid or incomplete. Please update Cookie and retry.`。因此当前剩余闭环不是“exe 无法导入第三方 runtime”，而是需要有效 Cookie/登录态场景下的真实下载成功门禁，以及无 Python clean VM 的重复验证。

发布脚本现已具备真实下载 smoke gate：`-RunDouyinRealDownloadSmoke` 要求同时提供 `-DouyinRealSmokeUrl` 和且仅一个 Cookie 来源（`-DouyinCookieEnvVar` 或 `-DouyinCookieFile`）。成功后 `sidecar-version.json` 增加 `realDownloadVerified`、`realDownloadVerifiedAtUtc`、`realDownloadSmokeUrlHash`、`realDownloadSmokeUsedCookieSource`，并且只有 bundled runtime、import self-test、真实下载 smoke 同时满足时才将 `selfContainedRealDownload` 置为 `true`。该 gate 仍依赖 sidecar 实现 `--cookie-env` / `--cookie-file` CLI 合约。

## 开放问题

- 主代理需决定第一期 `success.output_file_path` 对图文和用户 post 的语义：作品目录、用户目录，还是 `download_manifest.jsonl`。
- 主代理需决定是否保留 `imageio-ffmpeg`。若裁剪，必须确认第三方模块 import 和一期能力不受影响。
- 主代理需决定 SOCKS5 代理策略：增加依赖并支持，还是仅对抖音专项声明支持 HTTP/HTTPS。
- 主代理需决定 sidecar 日志是否做 UI 入口，例如设置页“复制抖音诊断日志”。
- Task C 已补 PyInstaller spec、第三方 LICENSE/notices 打包和本机 `--emit-sample` smoke 门禁；CI 无 Python smoke、杀软误报检查和真实下载自包含仍是后续任务。
