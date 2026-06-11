# EasyGet Mac Agent Kit

这个文件夹是给 Mac 上的 Codex / OpenClaw 构建本地视频下载工具的交接包。

目标不是把 Windows WPF 版 EasyGet 原样移植到 macOS，而是复用它的核心下载思路，在 Mac 上做一个本地 agent tool：

- OpenClaw / Codex 负责理解用户意图
- 本地工具负责调用 `yt-dlp` / `ffmpeg`
- Cookie 留在 Mac 本机，不暴露给 AI 上下文
- 最终提供 CLI 或 MCP tools 给 agent 调用

## 推荐阅读顺序

1. `docs/MAC_CODEX_BUILD_GUIDE.md`
2. `docs/COOKIE_AND_SECURITY.md`
3. `docs/TOOL_CONTRACT.md`
4. `docs/REFERENCE_CODE_MAP.md`
5. `PROMPT_FOR_MAC_CODEX.md`

## 参考代码

从当前 EasyGet 项目复制的核心代码在：

```text
reference/easyget-csharp/
```

这些代码主要用于参考参数拼装、进度解析、Cookie 转换、文件名清理、队列管理。它们来自 Windows WPF 项目，不建议在 macOS 上直接照搬编译。

## Mac 上的最小目标

先构建一个本地 CLI：

```bash
easyget-agent info "https://..."
easyget-agent download "https://..." --format mp4 --quality 1080 --cookie-profile auto
```

然后再包成 MCP server，给 OpenClaw / Codex 使用。

## 安装依赖脚本

Mac 上可以先运行：

```bash
./scripts/bootstrap-macos.sh
```

它会检查并尝试安装 `yt-dlp` 和 `ffmpeg`。
