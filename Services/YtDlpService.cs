using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyGet.Models;

namespace EasyGet.Services;

/// <summary>
/// 视频信息（yt-dlp --dump-json 解析结果）
/// </summary>
public class VideoInfo
{
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public double Duration { get; set; }
    public string Thumbnail { get; set; } = "";
    public long FileSize { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>
/// 下载进度信息
/// </summary>
public class DownloadProgress
{
    public double Percent { get; set; }
    public double Speed { get; set; }   // bytes/s
    public double Eta { get; set; }     // seconds
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public string RawLine { get; set; } = "";
}

/// <summary>
/// yt-dlp 命令封装服务
/// </summary>
public partial class YtDlpService
{
    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;

    public YtDlpService(ConfigService configService, EnvironmentService envService)
    {
        _configService = configService;
        _envService = envService;
    }

    /// <summary>
    /// 获取 yt-dlp 可执行文件路径
    /// </summary>
    private string GetYtDlpPath()
    {
        return !string.IsNullOrEmpty(_envService.Status.YtDlpPath) 
            ? _envService.Status.YtDlpPath 
            : "yt-dlp";
    }

    /// <summary>
    /// 获取 ffmpeg 所在目录 (用于 --ffmpeg-location)
    /// </summary>
    private string? GetFfmpegDirectory()
    {
        if (!string.IsNullOrEmpty(_envService.Status.FfmpegPath))
            return Path.GetDirectoryName(_envService.Status.FfmpegPath);
        return null;
    }

    /// <summary>
    /// 获取视频信息
    /// </summary>
    public async Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var args = new List<string>
            {
                "--dump-json",
                "--no-download",
                "--no-warnings",
                url
            };

            AddProxyArgs(args);
            AddCookieArgs(args);

            var output = await RunAsync(args, ct: ct);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            return new VideoInfo
            {
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Platform = root.TryGetProperty("extractor", out var p) ? p.GetString() ?? "" : "",
                Duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
                Thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() ?? "" : "",
                FileSize = root.TryGetProperty("filesize_approx", out var fs) ? fs.GetInt64() :
                           root.TryGetProperty("filesize", out var fs2) ? fs2.GetInt64() : 0,
                Url = url
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetVideoInfo failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取播放列表中的所有视频 URL
    /// </summary>
    public async Task<List<string>> GetPlaylistUrlsAsync(string url, CancellationToken ct = default)
    {
        var urls = new List<string>();
        try
        {
            var args = new List<string>
            {
                "--flat-playlist",
                "--dump-json",
                "--no-warnings",
                url
            };

            AddProxyArgs(args);
            AddCookieArgs(args);

            var output = await RunAsync(args, ct: ct);
            if (string.IsNullOrEmpty(output)) return urls;

            // Each line is a separate JSON object for each video in the playlist
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var videoUrl = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" :
                                   root.TryGetProperty("webpage_url", out var wu) ? wu.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(videoUrl))
                        urls.Add(videoUrl);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetPlaylistUrls failed: {ex.Message}");
        }
        return urls;
    }

    /// <summary>
    /// 下载视频
    /// </summary>
    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var args = BuildDownloadArgs(task);
        logCallback?.Invoke($"[yt-dlp] 开始下载: {task.Url}");
        logCallback?.Invoke($"[yt-dlp] 参数: {string.Join(" ", args)}");

        task.Status = DownloadStatus.Downloading;

        var psi = new ProcessStartInfo
        {
            FileName = GetYtDlpPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // 读取输出并解析进度
        var outputTask = Task.Run(async () =>
        {
            using var reader = process.StandardOutput;
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line == null) continue;

                logCallback?.Invoke(line);

                // 解析进度行
                var dp = ParseProgressLine(line);
                if (dp != null)
                {
                    progress?.Report(dp);
                }
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line != null)
                    logCallback?.Invoke($"[stderr] {line}");
            }
        }, ct);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(ct);

        if (ct.IsCancellationRequested)
        {
            task.Status = DownloadStatus.Cancelled;
            return;
        }

        if (process.ExitCode == 0)
        {
            task.Status = DownloadStatus.Completed;
            task.Progress = 100;

            // 查找输出文件
            var outputFile = FindOutputFile(task);
            if (outputFile != null)
            {
                task.OutputFilePath = outputFile;
                task.FileSize = new FileInfo(outputFile).Length;
            }

            logCallback?.Invoke($"[yt-dlp] 下载完成: {task.Title}");
        }
        else
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = $"yt-dlp 退出码: {process.ExitCode}";
            logCallback?.Invoke($"[yt-dlp] 下载失败 (exit code: {process.ExitCode})");
        }
    }

    /// <summary>
    /// 构建下载参数
    /// </summary>
    private List<string> BuildDownloadArgs(DownloadTask task)
    {
        var args = new List<string>();

        // 格式选择
        args.Add("-f");
        args.Add(BuildFormatString(task.Format, task.Quality));

        // 输出目录和文件名
        args.Add("-o");
        args.Add(Path.Combine(task.OutputDirectory, "%(title)s.%(ext)s"));

        // 进度输出格式
        args.Add("--newline");
        args.Add("--progress-template");
        args.Add("download:%(progress._percent_str)s %(progress._speed_str)s ETA %(progress._eta_str)s");

        // 多线程分片
        var fragments = _configService.Config.ConcurrentFragments;
        if (fragments > 1)
        {
            args.Add("--concurrent-fragments");
            args.Add(fragments.ToString());
        }

        // ffmpeg 路径
        var ffmpegDir = GetFfmpegDirectory();
        if (ffmpegDir != null)
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }

        // 字幕
        switch (task.Subtitle)
        {
            case "auto":
                args.Add("--write-auto-subs");
                args.Add("--sub-lang");
                args.Add("zh-Hans,zh,en");
                break;
            case "all":
                args.Add("--write-subs");
                args.Add("--all-subs");
                break;
        }

        // aria2c 加速
        if (_configService.Config.UseAria2c)
        {
            args.Add("--external-downloader");
            args.Add("aria2c");
            args.Add("--external-downloader-args");
            args.Add($"aria2c:--min-split-size=1M --max-connection-per-server=16 --split=16");
        }

        // 代理 & Cookie
        AddProxyArgs(args);
        AddCookieArgs(args);

        // URL
        args.Add(task.Url);

        return args;
    }

    /// <summary>
    /// 构建 yt-dlp 格式字符串
    /// </summary>
    private static string BuildFormatString(string format, string quality)
    {
        if (format is "mp3" or "m4a")
        {
            return "bestaudio/best";
        }

        var qualityFilter = quality switch
        {
            "2160" => "[height<=2160]",
            "1080" => "[height<=1080]",
            "720" => "[height<=720]",
            "480" => "[height<=480]",
            _ => "" // best
        };

        return $"bestvideo{qualityFilter}+bestaudio/best{qualityFilter}";
    }

    /// <summary>
    /// 解析进度输出行
    /// </summary>
    private static DownloadProgress? ParseProgressLine(string line)
    {
        // 匹配格式: download:  45.2% 12.5MiB/s ETA 00:30
        var match = ProgressRegex().Match(line);
        if (!match.Success) return null;

        var dp = new DownloadProgress
        {
            RawLine = line
        };

        if (double.TryParse(match.Groups[1].Value, out var percent))
            dp.Percent = percent;

        var speedStr = match.Groups[2].Value;
        dp.Speed = ParseSpeed(speedStr);

        var etaStr = match.Groups[3].Value;
        dp.Eta = ParseEta(etaStr);

        return dp;
    }

    private static double ParseSpeed(string speedStr)
    {
        var m = SpeedRegex().Match(speedStr);
        if (!m.Success) return 0;

        var value = double.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value;
        return unit switch
        {
            "KiB" => value * 1024,
            "MiB" => value * 1024 * 1024,
            "GiB" => value * 1024 * 1024 * 1024,
            _ => value
        };
    }

    private static double ParseEta(string etaStr)
    {
        if (etaStr == "Unknown" || string.IsNullOrWhiteSpace(etaStr)) return 0;
        var parts = etaStr.Split(':');
        return parts.Length switch
        {
            3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]),
            2 => int.Parse(parts[0]) * 60 + int.Parse(parts[1]),
            1 => int.Parse(parts[0]),
            _ => 0
        };
    }

    private void AddProxyArgs(List<string> args)
    {
        var config = _configService.Config;
        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            args.Add("--proxy");
            args.Add(config.ProxyAddress);
        }
    }

    private static readonly string CookieFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGet", "cookies.txt");

    private void AddCookieArgs(List<string> args)
    {
        var config = _configService.Config;
        if (string.IsNullOrWhiteSpace(config.CookieContent)) return;

        try
        {
            // 将 cookie 字符串转换为 Netscape cookies.txt 格式并保存
            SaveCookieFile(config.CookieContent);
            if (File.Exists(CookieFilePath))
            {
                args.Add("--cookies");
                args.Add(CookieFilePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] AddCookieArgs failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 将 "key=value; key2=value2" 格式的 cookie 字符串转为 Netscape cookies.txt 文件
    /// </summary>
    private static void SaveCookieFile(string cookieContent)
    {
        // 常见视频平台域名
        string[] domains = [".douyin.com", ".tiktok.com", ".bilibili.com", ".youtube.com", ".instagram.com"];

        var lines = new List<string> { "# Netscape HTTP Cookie File", "# Generated by EasyGet", "" };

        var pairs = cookieContent.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0) continue;
            var name = pair[..eqIdx].Trim();
            var value = pair[(eqIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            foreach (var domain in domains)
            {
                // domain \t include_subdomains \t path \t secure \t expiry \t name \t value
                lines.Add($"{domain}\tTRUE\t/\tFALSE\t0\t{name}\t{value}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CookieFilePath)!);
        File.WriteAllLines(CookieFilePath, lines);
    }

    private static string? FindOutputFile(DownloadTask task)
    {
        if (string.IsNullOrEmpty(task.OutputDirectory) || string.IsNullOrEmpty(task.Title))
            return null;

        try
        {
            // 查找最近修改的匹配文件
            var files = Directory.GetFiles(task.OutputDirectory)
                .Select(f => new FileInfo(f))
                .Where(f => f.Name.Contains(task.Title[..Math.Min(20, task.Title.Length)]))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            return files?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> RunAsync(List<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetYtDlpPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    [GeneratedRegex(@"download:\s*([\d.]+)%\s+(\S+)\s+ETA\s+(\S+)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"([\d.]+)([\w/]+)")]
    private static partial Regex SpeedRegex();
}
