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

    /// <summary>默认下载目录</summary>
    public string DefaultDownloadPath { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "EasyGet");

    /// <summary>默认格式 (mp4, mkv, webm, mp3)</summary>
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

    /// <summary>Cookie 原始内容（从浏览器复制的 cookie 字符串）</summary>
    public string CookieContent { get; set; } = "";

    /// <summary>是否按平台自动归类下载文件到子文件夹</summary>
    public bool AutoCategorizeByPlatform { get; set; } = true;

    /// <summary>应用窗口位置和大小</summary>
    public WindowState Window { get; set; } = new();
}

/// <summary>
/// 窗口状态
/// </summary>
public class WindowState
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
}
