using System;
using System.IO;

namespace EasyGet.Models;

/// <summary>
/// 应用配置模型
/// </summary>
public class AppConfig
{
    public const int MinConcurrentFragments = 1;
    public const int MaxConcurrentFragments = 32;
    public const int MinConcurrentDownloadLimit = 1;
    public const int MaxConcurrentDownloadLimit = 8;
    public const string DefaultDouyinTemplate = "{date}_{title}_{id}";
    public const int MaxDouyinCommentPageSize = 20;
    public const int DefaultDouyinLiveChunkSize = 65536;
    public const int DefaultDouyinLiveIdleTimeoutSeconds = 30;

    /// <summary>默认下载目录</summary>
    public string DefaultDownloadPath { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "EasyGet");

    /// <summary>默认格式 (mp4, mkv, webm, mp3, m4a)</summary>
    public string DefaultFormat { get; set; } = "mp4";

    /// <summary>默认画质 (best, 2160, 1080, 720, 480)</summary>
    public string DefaultQuality { get; set; } = "best";

    /// <summary>默认字幕选项 (none, auto, all)</summary>
    public string DefaultSubtitle { get; set; } = "none";

    /// <summary>yt-dlp 并发分片数</summary>
    public int ConcurrentFragments { get; set; } = Math.Clamp(
        Environment.ProcessorCount,
        MinConcurrentFragments,
        MaxConcurrentFragments);

    /// <summary>批量下载同时任务数</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>是否启用代理</summary>
    public bool UseProxy { get; set; } = false;

    /// <summary>代理地址 (http://host:port 或 socks5://host:port)</summary>
    public string ProxyAddress { get; set; } = "";

    /// <summary>是否启用 aria2c 加速</summary>
    public bool UseAria2c { get; set; } = false;

    /// <summary>Cookie 原始内容（从浏览器复制 of cookie 字符串）</summary>
    public string CookieContent { get; set; } = "";

    /// <summary>是否启用抖音专项引擎配置（下载流程接线后生效）</summary>
    public bool EnableDouyinSpecialEngine { get; set; } = false;

    /// <summary>抖音用户主页批量模式（post, like, mix, music, collect, collectmix）</summary>
    public string DouyinMode { get; set; } = "post";

    /// <summary>抖音用户作品下载数量上限；0 表示不限制</summary>
    public int DouyinLimit { get; set; } = 0;

    /// <summary>抖音媒体文件命名模板；必须包含 {id}</summary>
    public string DouyinFilenameTemplate { get; set; } = DefaultDouyinTemplate;

    /// <summary>抖音单作品子文件夹命名模板；必须包含 {id}</summary>
    public string DouyinFolderTemplate { get; set; } = DefaultDouyinTemplate;

    /// <summary>抖音作者目录命名策略（nickname, sec_uid, nickname_uid, user_sec_uid）</summary>
    public string DouyinAuthorDirectoryMode { get; set; } = "nickname";

    /// <summary>抖音批量下载是否按 post/like/mix/music 模式分层目录</summary>
    public bool DouyinGroupByMode { get; set; } = true;

    /// <summary>抖音作品筛选开始时间；空字符串表示不限制</summary>
    public string DouyinStartTime { get; set; } = "";

    /// <summary>抖音作品筛选结束时间；空字符串表示不限制</summary>
    public string DouyinEndTime { get; set; } = "";

    /// <summary>抖音专项下载是否包含置顶作品</summary>
    public bool DouyinDownloadPinned { get; set; } = false;

    /// <summary>抖音专项下载是否包含封面；复用 CookieContent，不新增登录流程</summary>
    public bool DouyinDownloadCover { get; set; } = false;

    /// <summary>抖音专项下载是否下载作者头像；复用 CookieContent，不新增登录流程</summary>
    public bool DouyinDownloadAvatar { get; set; } = false;

    /// <summary>抖音专项下载是否包含音乐；复用 CookieContent，不新增登录流程</summary>
    public bool DouyinDownloadMusic { get; set; } = false;

    /// <summary>抖音专项下载是否包含评论；复用 CookieContent，不新增登录流程</summary>
    public bool DouyinDownloadComments { get; set; } = false;

    /// <summary>抖音评论采集是否包含二级回复；会增加额外请求量</summary>
    public bool DouyinCommentIncludeReplies { get; set; } = false;

    /// <summary>抖音评论采集数量上限；0 表示不限制</summary>
    public int DouyinMaxComments { get; set; } = 0;

    /// <summary>抖音评论采集分页大小；抖音通常上限为 20</summary>
    public int DouyinCommentPageSize { get; set; } = MaxDouyinCommentPageSize;

    /// <summary>抖音专项下载是否保存原始 JSON；复用 CookieContent，不新增登录流程</summary>
    public bool DouyinDownloadJson { get; set; } = false;

    /// <summary>抖音专项下载是否启用本地 SQLite 去重数据库</summary>
    public bool DouyinEnableDatabase { get; set; } = false;

    /// <summary>抖音专项下载是否启用批量模式增量下载；sidecar 会在需要时自动启用数据库</summary>
    public bool DouyinIncrementalDownload { get; set; } = false;

    /// <summary>抖音专项下载是否启用浏览器兜底；用于翻页受限或需要人工验证的批量下载</summary>
    public bool DouyinEnableBrowserFallback { get; set; } = false;

    /// <summary>抖音直播最大录制时长（秒）；0 表示录到主播下播</summary>
    public int DouyinLiveMaxDurationSeconds { get; set; } = 0;

    /// <summary>抖音直播流读取分块大小（字节）</summary>
    public int DouyinLiveChunkSize { get; set; } = DefaultDouyinLiveChunkSize;

    /// <summary>抖音直播流空闲超时（秒）</summary>
    public int DouyinLiveIdleTimeoutSeconds { get; set; } = DefaultDouyinLiveIdleTimeoutSeconds;

    /// <summary>是否按平台自动归类下载文件到子文件夹</summary>
    public bool AutoCategorizeByPlatform { get; set; } = true;

    /// <summary>主题配色名称 (Indigo, Teal, Rose, Amber, Blue)</summary>
    public string ThemeColor { get; set; } = "Indigo";

    /// <summary>Telegram API ID</summary>
    public string TgApiId { get; set; } = "";

    /// <summary>Telegram API Hash</summary>
    public string TgApiHash { get; set; } = "";

    /// <summary>Telegram 绑定手机号</summary>
    public string TgPhoneNumber { get; set; } = "";

    /// <summary>应用窗口位置和大小</summary>
    public WindowState Window { get; set; } = new();
}

/// <summary>
/// 窗口状态
/// </summary>
public class WindowState
{
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 800;
    public const double MinWidth = 960;
    public const double MinHeight = 600;

    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
}
