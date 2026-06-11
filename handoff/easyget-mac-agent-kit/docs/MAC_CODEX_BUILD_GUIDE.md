# Mac Codex 构建指南

## 背景

当前 EasyGet 是 Windows WPF 桌面应用，核心下载能力基于 `yt-dlp` 和 `ffmpeg`。Mac 侧需要的是一个给 OpenClaw / Codex 调用的本地下载工具，不需要 UI。

推荐构建一个新的跨平台小项目，名字可以叫：

```text
easyget-agent-mac
```

## 推荐技术路线

优先做 TypeScript + Node.js：

- MCP server 生态更自然
- JSON schema / stdio 工具调用更方便
- 适合 OpenClaw / Codex 本地长期运行
- `child_process.spawn` 可以稳定读取 `yt-dlp` 进度

如果 Mac 上更偏好 Python，也可以实现同样接口，但请保持工具契约不变。

## 目标结构

```text
easyget-agent-mac/
  package.json
  tsconfig.json
  README.md
  src/
    index.ts              # MCP server 入口
    cli.ts                # CLI 入口，可选但推荐先做
    downloader.ts         # yt-dlp 调用、进度解析、输出路径解析
    cookies.ts            # cookie profile 选择和安全处理
    config.ts             # 配置读写
    filenames.ts          # 文件名清理和输出模板
    tasks.ts              # 内存任务队列、取消、状态查询
    types.ts              # 共享类型
  test/
    filenames.test.ts
    cookies.test.ts
    progress.test.ts
```

## 构建顺序

1. 先实现 CLI，不急着接 MCP。
2. CLI 能完成 `info` 和 `download` 后，再加 MCP server。
3. Cookie profile 只传 profile id，不让模型读取 Cookie 内容。
4. 最后加任务队列、批量下载、取消和状态查询。

## macOS 依赖

Mac 上不要使用 Windows 项目里的自动安装逻辑。优先使用 Homebrew：

```bash
brew install yt-dlp ffmpeg
```

也可以允许用户自定义路径：

```json
{
  "ytDlpPath": "/opt/homebrew/bin/yt-dlp",
  "ffmpegPath": "/opt/homebrew/bin/ffmpeg"
}
```

配置文件推荐放在：

```text
~/Library/Application Support/EasyGetAgent/config.json
```

下载默认目录推荐：

```text
~/Downloads/EasyGet
```

## 从 EasyGet 复用的关键逻辑

优先参考：

- `reference/easyget-csharp/Services/YtDlpService.cs`
- `reference/easyget-csharp/Services/DownloadFileNameBuilder.cs`
- `reference/easyget-csharp/Models/AppConfig.cs`
- `reference/easyget-csharp/Models/DownloadTask.cs`

需要移植的逻辑：

- `GetVideoInfoAsync`：调用 `yt-dlp --dump-json --no-download`
- `BuildDownloadArgs`：拼装格式、画质、字幕、代理、ffmpeg、cookie 参数
- `ParseProgressLine`：解析 `--progress-template` 输出
- `BuildCookieFileLines`：把浏览器 Cookie 字符串 / JSON 转 Netscape cookies.txt
- `SanitizeResolvedTitle`：清理非法文件名字符

不要直接移植的逻辑：

- WPF / MVVM 绑定代码
- Windows `.exe` 工具安装逻辑
- `%LocalAppData%` 路径
- Windows 专用文件名行为

## yt-dlp 参数基线

视频信息：

```bash
yt-dlp --no-playlist --dump-json --no-download --no-warnings URL
```

下载：

```bash
yt-dlp \
  --no-playlist \
  -f "bv*[height<=1080]+ba/b[height<=1080]/b" \
  --merge-output-format mp4 \
  -o "/Users/me/Downloads/EasyGet/%(title).150s.%(ext)s" \
  --no-mtime \
  --newline \
  --progress-template "download:%(progress._percent_str)s %(progress._speed_str)s ETA %(progress._eta_str)s" \
  --ffmpeg-location "/opt/homebrew/bin" \
  URL
```

音频：

```bash
yt-dlp \
  -f "bestaudio/best" \
  -x \
  --audio-format mp3 \
  --audio-quality 0 \
  -o "/Users/me/Downloads/EasyGet/%(title).150s.%(ext)s" \
  URL
```

## 验收标准

Mac Codex 构建完成后，至少验证：

1. `easyget-agent info URL` 能返回 title、platform、duration、thumbnail。
2. `easyget-agent download URL --format mp4 --quality 720` 能下载公开视频。
3. `--cookie-profile firefox` 能对需要登录态的网站生效。
4. 日志不会打印 Cookie 原文。
5. 取消任务能杀掉整个 `yt-dlp` 进程树。
6. 下载完成后返回真实文件路径和文件大小。
