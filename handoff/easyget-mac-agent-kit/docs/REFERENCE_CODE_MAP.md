# EasyGet 参考代码映射

这些文件来自 Windows 版 EasyGet，用于指导 Mac agent tool 的实现。

## `Services/YtDlpService.cs`

最重要的参考文件。

可复用思想：

- `GetVideoInfoAsync`
- `GetPlaylistUrlsAsync`
- `DownloadAsync`
- `BuildDownloadArgs`
- `BuildFormatString`
- `ParseProgressLine`
- `ParseOutputPath`
- `ResolveOutputFile`
- `BuildCookieFileLines`

Mac 实现注意：

- `GetYtDlpPath()` 应支持 `/opt/homebrew/bin/yt-dlp`、`/usr/local/bin/yt-dlp` 和 PATH。
- `GetFfmpegDirectory()` 在 Homebrew 下通常是 `/opt/homebrew/bin`。
- `CookieFilePath` 应改到 `~/Library/Application Support/EasyGetAgent/cookies/`。
- `HasBrowserCookies()` 不要照搬 Windows 路径。
- macOS 可增加 `safari` profile。

## `Services/DownloadFileNameBuilder.cs`

可移植价值很高。

保留：

- 标题清理
- 替换非法字符
- `%` 转义为 `%%`
- 空标题回退到 `video`

Mac 上可以简化编码检查，主要保留 Unicode 和路径安全处理。

## `Services/DownloadManager.cs`

作为任务队列参考。

可复用思想：

- 并发信号量
- 任务状态
- 取消 / 暂停 / 恢复 / 重试
- 下载完成后记录输出路径和文件大小

Mac 最小版可以先只做：

- 单任务下载
- 内存任务表
- 取消任务

批量和历史记录可以后加。

## `Services/ConfigService.cs`

参考配置模型和 JSON 持久化。

Mac 路径替换：

```text
Windows: %LocalAppData%/EasyGet/config.json
Mac:     ~/Library/Application Support/EasyGetAgent/config.json
```

## `Services/EnvironmentService.cs`

只参考环境检测，不要复用 Windows 下载逻辑。

Mac 推荐：

```bash
brew install yt-dlp ffmpeg
```

或者只提示用户安装。

## `Models/AppConfig.cs`

参考默认配置字段：

- `DefaultDownloadPath`
- `DefaultFormat`
- `DefaultQuality`
- `DefaultSubtitle`
- `ConcurrentFragments`
- `MaxConcurrentDownloads`
- `UseProxy`
- `ProxyAddress`
- `UseAria2c`
- `AutoCategorizeByPlatform`

Mac 版应移除：

- WPF window state
- 原始 `CookieContent` 字段，改为 `cookieProfiles`

## `Models/DownloadTask.cs`

参考状态和字段即可。

Mac 版不需要 `CommunityToolkit.Mvvm`。

## Tests

参考测试：

- `Tests/DownloadFileNameBuilderTests.cs`
- `Tests/YtDlpCookieTests.cs`

Mac 版应重写为项目本身的测试框架，例如 Vitest / Node test / pytest。
