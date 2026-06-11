# Agent Tool Contract

这个文档定义 Mac 本地工具要暴露给 OpenClaw / Codex 的能力。

## CLI 合约

### info

```bash
easyget-agent info URL [--cookie-profile auto]
```

返回 JSON：

```json
{
  "ok": true,
  "video": {
    "url": "https://...",
    "title": "Video title",
    "platform": "YouTube",
    "duration": 123.4,
    "thumbnail": "https://...",
    "fileSize": 0
  }
}
```

### download

```bash
easyget-agent download URL \
  --format mp4 \
  --quality 1080 \
  --subtitle none \
  --output-dir "$HOME/Downloads/EasyGet" \
  --cookie-profile auto
```

最终返回 JSON：

```json
{
  "ok": true,
  "taskId": "abc123",
  "status": "completed",
  "title": "Video title",
  "platform": "YouTube",
  "outputFilePath": "/Users/me/Downloads/EasyGet/Video title.mp4",
  "fileSize": 12345678
}
```

下载过程中可以向 stderr 输出进度日志，但最后一行 stdout 必须是机器可读 JSON。

### status

```bash
easyget-agent status TASK_ID
```

### cancel

```bash
easyget-agent cancel TASK_ID
```

### cookie profiles

```bash
easyget-agent cookie-profiles
```

只返回 profile id 和类型，不返回 Cookie 内容：

```json
{
  "ok": true,
  "profiles": [
    { "id": "auto", "type": "auto" },
    { "id": "firefox", "type": "browser" },
    { "id": "youtube_private", "type": "file" }
  ]
}
```

## MCP tools

CLI 稳定后，再暴露 MCP tools。

### get_video_info

Input:

```json
{
  "url": "string",
  "cookieProfile": "auto"
}
```

Output:

```json
{
  "title": "string",
  "platform": "string",
  "duration": 0,
  "thumbnail": "string",
  "fileSize": 0,
  "url": "string"
}
```

### download_video

Input:

```json
{
  "url": "string",
  "format": "mp4",
  "quality": "best",
  "subtitle": "none",
  "outputDir": "~/Downloads/EasyGet",
  "cookieProfile": "auto"
}
```

Allowed values:

```text
format: mp4 | mkv | webm | mp3 | m4a
quality: best | 2160 | 1080 | 720 | 480
subtitle: none | auto | all
```

Output:

```json
{
  "taskId": "string",
  "status": "completed",
  "outputFilePath": "string",
  "fileSize": 0,
  "errorMessage": ""
}
```

### batch_download

Input:

```json
{
  "urls": ["string"],
  "format": "mp4",
  "quality": "best",
  "subtitle": "none",
  "outputDir": "~/Downloads/EasyGet",
  "cookieProfile": "auto",
  "maxConcurrent": 3
}
```

### get_task_status

Input:

```json
{
  "taskId": "string"
}
```

### cancel_task

Input:

```json
{
  "taskId": "string"
}
```

### list_cookie_profiles

Input:

```json
{}
```

Output must not include Cookie values or sensitive paths unless user explicitly requests local setup help.

## 状态枚举

```text
waiting
resolving
downloading
merging
completed
failed
cancelled
paused
```

## 错误格式

所有工具失败时统一返回：

```json
{
  "ok": false,
  "errorCode": "YTDLP_FAILED",
  "message": "Human readable error",
  "safeDetails": "Redacted diagnostic text"
}
```

`safeDetails` 必须脱敏。
