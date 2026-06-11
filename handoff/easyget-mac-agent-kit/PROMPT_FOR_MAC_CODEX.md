# 给 Mac 上 Codex 的任务提示

请在 Mac 本机根据这个交接包构建一个 `easyget-agent-mac` 本地工具。

## 目标

构建一个给 OpenClaw / Codex 调用的视频下载工具。它运行在 Mac 本机，调用 `yt-dlp` 和 `ffmpeg`，支持浏览器 Cookie / cookies.txt profile，但绝不把 Cookie 原文暴露给 AI 上下文。

## 必读文档

请先阅读：

1. `docs/MAC_CODEX_BUILD_GUIDE.md`
2. `docs/COOKIE_AND_SECURITY.md`
3. `docs/TOOL_CONTRACT.md`
4. `docs/REFERENCE_CODE_MAP.md`

参考代码在：

```text
reference/easyget-csharp/
```

这些 C# 文件来自 Windows WPF 版 EasyGet，只作为逻辑参考，不要求直接编译。

## 推荐实现

优先使用 TypeScript + Node.js。

先实现 CLI：

```bash
easyget-agent info URL
easyget-agent download URL --format mp4 --quality 1080 --cookie-profile auto
easyget-agent cookie-profiles
```

CLI 稳定后，再实现 MCP server，暴露：

- `get_video_info`
- `download_video`
- `batch_download`
- `get_task_status`
- `cancel_task`
- `list_cookie_profiles`

## 关键要求

- 默认下载目录：`~/Downloads/EasyGet`
- 配置目录：`~/Library/Application Support/EasyGetAgent`
- 支持 `--cookies-from-browser firefox/chrome/safari`
- 支持 file cookie profile
- Cookie 原文不能进入日志、stdout、MCP 返回值或模型上下文
- 最后一行 stdout 必须是 JSON，方便 agent 解析
- 下载进度可以输出到 stderr
- 取消任务必须杀掉 `yt-dlp` 进程树

## 验证

至少完成：

```bash
easyget-agent info "公开视频URL"
easyget-agent download "公开视频URL" --format mp4 --quality 720
easyget-agent cookie-profiles
```

如果要验证 Cookie，请使用你有权访问的测试 URL。
