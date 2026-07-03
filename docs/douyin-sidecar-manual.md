# EasyGet Douyin Python Sidecar Manual

## 位置

- Sidecar 入口：`tools/douyin-sidecar/sidecar.py`
- 行为测试：`tools/douyin-sidecar/tests/test_sidecar.py`
- 参考第三方仓库：`F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax`

该原型不修改 EasyGet C# 调用路径，不实现 UI，也不复制第三方 downloader 主体代码。真实下载时，sidecar 会生成临时 JSON config，然后以子进程调用第三方仓库的 `run.py`。

## 依赖安装

Sidecar wrapper 本身只使用 Python 标准库。若只跑 `--dry-run` 或 `--emit-sample`，不需要安装第三方依赖。

真实下载需要先给第三方仓库安装依赖。sidecar 不显式传 `--python` 时，会优先使用第三方仓库下的 `.venv` / `venv`，所以推荐把依赖装在参考仓库自己的虚拟环境里：

```powershell
cd F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

如果未来要启用第三方项目的浏览器兜底或 Cookie 获取能力，还需要：

```powershell
.\.venv\Scripts\python.exe -m pip install playwright
.\.venv\Scripts\python.exe -m playwright install chromium
```

当前 sidecar 默认 `browser_fallback.enabled=false`，避免后台调用时弹出浏览器。

## 参数

```text
--url             抖音链接，必填
--output-dir      下载输出目录，必填
--cookie          Cookie header 字符串或 JSON object，可选；兼容旧调用，不推荐用于真实 Cookie
--cookie-env      从指定环境变量读取 Cookie；不与 --cookie-file 同用
--cookie-file     从指定文件读取 Cookie；不与 --cookie-env 同用
--proxy           HTTP/HTTPS 代理，可选
--mode            用户主页批量模式；支持 post、like、mix、music，可用逗号组合
--limit           每个 mode 的数量限制；0 表示不限，默认 1
--include-cover   下载封面；默认 false
--include-music   下载音乐/音频副产物；默认 false
--include-json    保存原始 JSON；默认 false
--format          C# runner 兼容参数，当前 sidecar 接受但不传给第三方配置
--quality         C# runner 兼容参数，当前 sidecar 接受但不传给第三方配置
--title           C# runner 兼容参数，当前 sidecar 接受但不传给第三方配置
--downloader-root 第三方仓库路径；也可用 DOUYIN_DOWNLOADER_PROMAX_ROOT
--python          运行第三方 run.py 的 Python；不传时优先使用第三方仓库 .venv/venv
--runner-mode     auto|subprocess|in-process；auto 默认优先 in-process，显式 --python/--timeout 时走 subprocess
--dry-run         只输出计划，不真实下载
--emit-sample     输出可测试 JSONL，并创建一个 sample 文件
--self-test-imports 检查当前 runtime 是否能定位并导入阶段一第三方模块
--output-format   jsonl 或 json，默认 jsonl
```

## 示例

可解析样例输出：

```powershell
python .\tools\douyin-sidecar\sidecar.py `
  --url "https://www.douyin.com/video/7604129988555574538" `
  --output-dir ".\Downloaded\Douyin" `
  --emit-sample
```

Dry run：

```powershell
python .\tools\douyin-sidecar\sidecar.py `
  --url "https://www.douyin.com/user/MS4wLjABAAAAxxxx" `
  --output-dir ".\Downloaded\Douyin" `
  --mode post `
  --limit 3 `
  --include-cover `
  --include-json `
  --dry-run
```

导入自检：

```powershell
.\venv\Scripts\python.exe .\tools\douyin-sidecar\sidecar.py `
  --url "https://www.douyin.com/video/7604129988555574538" `
  --output-dir "$env:TEMP\easyget-douyin-selftest" `
  --downloader-root "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax" `
  --self-test-imports
```

`--self-test-imports` 不访问抖音，也不下载文件。它会临时把第三方仓库加入 `sys.path`，导入阶段一需要的 `config`、`auth.cookie_manager`、`core.api_client`、`core.url_parser`、`storage.file_manager`、`storage.database`，并以 JSONL 返回 `success` 或 `failed`。这比 `--emit-sample` 更接近真实下载 runtime，但仍不等于真实下载验证。

真实单视频或图文下载：

```powershell
python .\tools\douyin-sidecar\sidecar.py `
  --url "https://www.douyin.com/video/7604129988555574538" `
  --output-dir ".\Downloaded\Douyin" `
  --cookie-env "EASYGET_DOUYIN_COOKIE" `
  --downloader-root "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax"
```

真实用户主页批量：

```powershell
python .\tools\douyin-sidecar\sidecar.py `
  --url "https://www.douyin.com/user/MS4wLjABAAAAxxxx" `
  --output-dir ".\Downloaded\Douyin" `
  --mode post `
  --limit 20 `
  --cookie-file "$env:APPDATA\EasyGet\douyin-cookie.txt" `
  --downloader-root "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax"
```

## 输出契约

默认输出 JSON Lines。每一行 stdout 都应被视为一个独立 JSON object；如果未来出现非 JSON 行，C# sidecar client 会把它当普通日志处理。当前 wrapper 会捕获第三方 downloader 的终端输出，stdout 只输出 JSONL。

顶层事件字段：

```json
{
  "event": "progress|success|failed|cancelled|log"
}
```

`progress` 事件字段：

```json
{
  "event": "progress",
  "percent": 5,
  "downloaded_bytes": 0,
  "total_bytes": 0,
  "speed_bytes_per_sec": 0,
  "eta_seconds": null,
  "message": "starting_downloader"
}
```

`success` 摘要字段：

```json
{
  "event": "success",
  "title": "作品标题",
  "platform": "douyin",
  "duration_seconds": 0,
  "thumbnail_url": null,
  "file_size_bytes": 123456,
  "output_file_path": "F:\\Downloads\\Douyin\\author\\post\\item\\video.mp4"
}
```

`failed` / `cancelled` 字段：

```json
{
  "event": "failed",
  "error": "错误信息",
  "message": "错误信息",
  "reason": "错误信息"
}
```

`error` 是主字段；`message` 和 `reason` 只是为 C# 兼容读取保留的同值字段。

`log` 事件用于 dry-run 和诊断信息。`--dry-run` 会输出 `event=log`，并在 `details.planned_command` 和 `details.config` 中提供计划命令和脱敏配置。真实下载完成后，sidecar 优先读取第三方生成的 `download_manifest.jsonl`，再兜底扫描本次新生成的输出文件。旧的 `total/success/failed/skipped` 计数不再作为顶层契约输出，真实下载时会放在 `details.counts`。

Cookie 输出规则：

- 推荐用 `--cookie-env ENV_NAME` 或 `--cookie-file PATH` 传入真实 Cookie，避免 Cookie 原文出现在进程命令行。
- `--cookie-env` 和 `--cookie-file` 只能选一个；环境变量缺失/为空、文件不存在/为空都会返回 JSONL `failed` 事件。
- dry-run 的 `details.cookie_source` 只显示来源类型、环境变量名或文件路径，以及 `redacted=true`；不会输出 Cookie 原文。
- sidecar 会把 Cookie 写入自己管理的临时 config 目录再交给第三方 runner；默认运行结束后删除。该目录不会被 manifest 或输出扫描当成下载文件。
- sidecar 捕获第三方 stdout/stderr 后会再次按 Cookie 值脱敏，再写入 JSONL 失败事件。

## 当前能力边界

真实调用：

- 调用第三方 `douyin-downloader-promax/run.py`
- 通过临时 config 传入 `url`、`output_dir`、`cookie`、`proxy`、`mode`、`limit`、`include-cover`、`include-music`、`include-json`
- 支持第三方已有能力：单视频 `/video`、图文 `/note`/`/gallery`、用户主页 `post`/`like`/`mix`/`music` 批量、合集/混剪/音乐单链接 `/collection`/`/mix`/`/music`
- 读取 `download_manifest.jsonl` 汇总 `output_files`
- C# runner 默认从 `AppContext.BaseDirectory` 向上优先寻找 `tools\douyin-sidecar\sidecar.py`，便于开发联调；发布包仍可放置 `sidecars\douyin\EasyGet.DouyinSidecar.exe` 或 `sidecars\douyin_sidecar.py`
- C# runner 仍会传 `--format`、`--quality`、`--title`；Python sidecar 接受这些兼容参数并在 `--dry-run` 的 `details.runner_options` 中记录非空值，但当前不改变第三方下载配置

占位或测试路径：

- `--emit-sample` 只创建 sample JSON 文件，不访问抖音
- `--dry-run` 只输出 `event=log` 的计划命令和配置，不访问抖音
- `--self-test-imports` 只验证当前 runtime 的第三方源码/依赖 import，不访问抖音，不证明真实链接可下载
- 细粒度下载进度暂未桥接，当前只有开始和结束两个 coarse event
- REST server 未实现；第一期只提供命令行入口
- 默认不下载封面、音乐副产物和原始 JSON；需要通过 `--include-cover`、`--include-music`、`--include-json` 显式开启

## 发布骨架

Task C 新增 `scripts\build-douyin-sidecar.ps1` 和 `tools\douyin-sidecar\EasyGet.DouyinSidecar.spec`，默认将 sidecar 打成 `artifacts\sidecar\win-x64\EasyGet.DouyinSidecar.exe`，并生成 `THIRD_PARTY_NOTICES.md`、`licenses\douyin-downloader-promax-LICENSE.txt`、`sidecar-version.json`。

`scripts\publish-win-x64.ps1` 默认把这些资产复制到发布目录 `sidecars\douyin\`，检查文件非空，运行 `EasyGet.DouyinSidecar.exe --emit-sample`，并在 zip smoke 中要求 sidecar 条目存在。`-SkipDouyinSidecar` 只用于本地开发跳过。

默认发布流程不要求真实 Cookie，也不会访问抖音。`sidecar-version.json` 中 `realDownloadVerified=false`、`realDownloadVerifiedAtUtc=null`、`realDownloadSmokeUrlHash=null`、`realDownloadSmokeUsedCookieSource=null`，且 `selfContainedRealDownload=false`。

发布脚本提供显式强化门禁：

```powershell
.\scripts\publish-win-x64.ps1 `
  -SkipTests `
  -SkipZip `
  -DouyinSidecarPython "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\venv\Scripts\python.exe" `
  -DouyinThirdPartyRepoRoot "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax" `
  -RunDouyinImportSelfTest
```

该门禁要求所选 Python 环境同时具备 PyInstaller 和第三方运行依赖。当前参考仓库的 `venv` 具备第三方下载依赖，但若未安装 PyInstaller，构建会在 PyInstaller 检查阶段失败；这是构建环境缺口，不代表 sidecar import 自检失败。

真实下载 smoke 是单独的可选门禁，必须显式传入 URL 和 Cookie 来源：

```powershell
.\scripts\publish-win-x64.ps1 `
  -DouyinSidecarPython "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax\venv\Scripts\python.exe" `
  -DouyinThirdPartyRepoRoot "F:\AI\AIMadeupTools\05_ThirdPartyRepos\GitRepositories\douyin-downloader-promax" `
  -RunDouyinImportSelfTest `
  -RunDouyinRealDownloadSmoke `
  -DouyinRealSmokeUrl "https://www.douyin.com/video/7604129988555574538" `
  -DouyinCookieEnvVar "EASYGET_DOUYIN_COOKIE"
```

也可以用 `-DouyinCookieFile "C:\secure\douyin-cookie.txt"`，但不能同时传 `-DouyinCookieEnvVar`。发布脚本会使用发布产物中的 `sidecars\douyin\EasyGet.DouyinSidecar.exe`，在隔离临时 cwd/output dir 中运行，并清除 `DOUYIN_DOWNLOADER_PROMAX_ROOT`。成功后 manifest 只记录 URL 的 SHA256 hash 和 Cookie 来源标签（`env:<name>` 或 `file:<basename>`），不会记录原始 URL 或 Cookie 内容。`selfContainedRealDownload` 只有在 bundled runtime、import self-test 和真实下载 smoke 都满足时才会变为 `true`。

## 风险与注意事项

- 抖音接口和风控会变化；无 Cookie 或 Cookie 过期时真实下载可能失败。
- 第三方依赖包含 `gmssl`、`aiohttp`、`httpx`、`imageio-ffmpeg` 等，打包时需要确认目标机器可安装 wheel。
- `--python` 可显式指向第三方仓库 venv；不传时 sidecar 会自动优先选择第三方仓库 `.venv` / `venv`。
- 默认不保留临时 config，避免 Cookie 落盘。只有调试时才使用 `--keep-temp-config`，它会把 Cookie 写入 `output-dir\.easyget-douyin-sidecar\last-config.json`。
- 当前 wrapper 不解析第三方 Rich 进度条，只解析最终计数字符串；若第三方输出格式变化，仍可通过 manifest 推断成功文件。

## License Attribution

真实下载能力来自 `douyin-downloader-promax`，该项目声明为 MIT License。当前 sidecar 未复制第三方项目主体代码，只通过子进程调用其 `run.py`，并根据其公开 README/config 字段生成运行配置。若后续复制任何第三方代码片段，应在本手册中补充具体文件、片段来源和 MIT attribution。
