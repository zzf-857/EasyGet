# 央视频识别与下载实现说明

## 背景

`yt-dlp 2026.03.17` 不能识别 `yangshipin.cn/video/home?vid=...`，直接执行会返回 `Unsupported URL`。央视频网页会在运行时调用播放器接口，并由站点脚本生成带时效签名的 MP4 地址。该地址是普通、未加密的 `video/mp4`，但不能长期保存或复用。

EasyGet 不复制站点内部的混淆签名算法。程序让本机 Edge/Chrome 在隐藏模式下正常加载页面，由网页生成当次有效地址，再把公开 MP4 交给应用自己的 HTTP 下载器。

## 数据流

1. `YangshipinUrlParser` 严格校验协议、`yangshipin.cn` 主机边界、`/video/home` 路径和 `vid`。
2. `ChromiumYangshipinPageCapture` 使用独立临时浏览器目录加载规范化页面，不读取用户正在使用的浏览器配置或 Cookie。
3. 页面完成播放器初始化后，浏览器输出动态 DOM。
4. 解析器只接受与当前 `vid` 匹配、且位于 `*.ysp.cctv.cn` 或 `*.yangshipin.cn` 的 HTTPS MP4；同时读取标题、可用封面和时长文本。
5. 元数据预览通过 HEAD/Range 请求读取文件大小。相同规范化页面的并发预览共享一次浏览器解析和大小探测；缓存只保存标题、时长、封面和大小，不保存签名媒体地址。
6. 真正取得下载并发位后，`DownloadAsync` 再次加载页面，获取新的临时地址，避免任务排队期间签名过期。
7. `YangshipinDirectDownloader` 使用 `.part` 文件、HTTP Range、Referer、Origin、浏览器 User-Agent 和 EasyGet 代理设置进行续传；自动重定向已禁用，每个重定向目标都会重新校验 HTTPS、央视频/CCTV 域名边界和 MP4 路径，完成后原子移动为最终 MP4。
8. 最终任务继续复用 EasyGet 的队列、进度、暂停/重试、自动平台归类和下载历史。

## 安全与隐私边界

- 不读取用户浏览器密码、Cookie、历史或现有标签页。
- 每次解析使用独立临时浏览器目录，结束后立即清理。
- 签名 MP4 地址只存在于当前进程内存和 HTTP 请求中，不进入日志、配置或历史数据库。
- 严格拒绝相似恶意域名、不属于央视频 CDN 的媒体地址及越界重定向。
- 不绕过登录、会员、付费、地区限制、DRM 或其他受保护媒体机制。

## 当前支持范围

- 页面：`https://www.yangshipin.cn/video/home?vid=<视频ID>`，包括合法的央视频子域和附加分享参数。
- 内容：页面公开提供的单文件 MP4（H.264/AAC 等编码由文件本身决定）。
- 输出：MP4。若选择固定画质，首版会明确改用网页当前提供的默认最高画质。
- 环境：Windows 10/11，需安装 Edge 或 Chrome；Windows 通常自带 Edge。

首版不支持央视频栏目/合集导入、直播、VIP/地区受限内容、DRM 内容、独立音视频轨合并和 MP3/WebM 转换。

## 验收样例

页面：

```text
https://www.yangshipin.cn/video/home?vid=b000045ctqj
```

2026-07-16 的真实站点验收结果：

- 标题：`2026年美加墨世界杯半决赛 法国VS西班牙`
- 平台：`央视频`
- 时长：`7,284` 秒（文件探测为 `7,284.680272` 秒）
- 文件大小：`2,455,784,251` 字节
- 媒体：H.264 1920×1080 25 fps + AAC 双声道

对应自动化覆盖位于：

- `EasyGet.Tests/YangshipinUrlParserTests.cs`
- `EasyGet.Tests/YangshipinDownloadServiceTests.cs`

测试覆盖 URL 欺骗防护、动态 DOM 解析、稳定元数据缓存、过期地址刷新、Range 续传请求头和 `YtDlpService` 总入口路由。
