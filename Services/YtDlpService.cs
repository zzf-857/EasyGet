using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyGet.Models;

namespace EasyGet.Services;

public class VideoInfo
{
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public double Duration { get; set; }
    public string Thumbnail { get; set; } = "";
    public long FileSize { get; set; }
    public string Url { get; set; } = "";
}

public class DownloadProgress
{
    public double Percent { get; set; }
    public double Speed { get; set; }
    public double Eta { get; set; }
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public string RawLine { get; set; } = "";
}

internal sealed record ProcessOutput(string StandardOutput, string StandardError, int ExitCode);

internal readonly record struct DownloadOutputLineHandling(
    DownloadProgress? Progress,
    string? OutputPath,
    bool ShouldLog);

public partial class YtDlpService
{
    private static readonly TimeSpan DefaultDownloadNoOutputTimeout = TimeSpan.FromMinutes(10);

    internal enum CookieStrategy
    {
        Default,
        BrowserChrome,
        BrowserEdge
    }

    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;

    public YtDlpService(ConfigService configService, EnvironmentService envService)
    {
        _configService = configService;
        _envService = envService;
    }

    private string GetYtDlpPath()
    {
        return !string.IsNullOrWhiteSpace(_envService.Status.YtDlpPath)
            ? _envService.Status.YtDlpPath
            : "yt-dlp";
    }

    private string? GetFfmpegDirectory()
    {
        if (string.IsNullOrWhiteSpace(_envService.Status.FfmpegPath))
            return null;

        return Path.GetDirectoryName(_envService.Status.FfmpegPath);
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(string url, CancellationToken ct = default)
    {
        if (M3u8DownloadService.IsM3u8Url(url))
        {
            var title = "M3U8_Video";
            try
            {
                var uri = new Uri(url);
                var filename = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(filename) && filename != "index" && filename != "playlist")
                {
                    title = filename;
                }
                else
                {
                    title = $"M3U8_{DateTime.Now:yyyyMMdd_HHmmss}";
                }
            }
            catch
            {
                title = $"M3U8_{DateTime.Now:yyyyMMdd_HHmmss}";
            }

            return new VideoInfo
            {
                Title = title,
                Platform = "M3U8",
                Duration = 0,
                Thumbnail = "",
                FileSize = 0,
                Url = url
            };
        }

        if (TelegramDownloadService.IsTelegramUrl(url))
        {
            var title = "Telegram_Message";
            try
            {
                var parsed = TelegramDownloadService.ParseTelegramLink(url);
                if (parsed != null)
                {
                    var (chatTarget, startId, endId) = parsed.Value;
                    title = endId != null ? $"TG_{chatTarget}_{startId}-{endId}" : $"TG_{chatTarget}_{startId}";
                }
            }
            catch
            {
                title = $"TG_Message_{DateTime.Now:yyyyMMdd_HHmmss}";
            }

            return new VideoInfo
            {
                Title = title,
                Platform = "Telegram",
                Duration = 0,
                Thumbnail = "",
                FileSize = 0,
                Url = url
            };
        }

        try
        {
            var strategies = GetCookieStrategies(url);
            for (var i = 0; i < strategies.Count; i++)
            {
                var args = BuildVideoInfoBaseArgs();

                AddSiteCompatibilityArgs(args, url);
                AddProxyArgs(args);
                AddCookieArgs(args, url, strategies[i]);
                args.Add(url);

                var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    var firstJson = EnumerateProcessLines(result.StandardOutput)
                        .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));

                    if (!string.IsNullOrWhiteSpace(firstJson))
                        return ParseVideoInfoJson(firstJson, url);
                }

                if (i < strategies.Count - 1
                    && ShouldRetryWithNextCookieStrategy(url, EnumerateProcessLines(result.StandardError)))
                {
                    continue;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetVideoInfo failed: {ex.Message}");
        }

        if (IsDouyinUrl(url))
            return BuildDouyinFallbackVideoInfo(url);

        if (IsXiaohongshuUrl(url))
        {
            try
            {
                var fallback = new XiaohongshuImageDownloadService(_configService);
                var info = await fallback.GetImageNoteInfoAsync(url, ct);
                if (info != null)
                    return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YtDlpService] Xiaohongshu image fallback failed: {ex.Message}");
            }
        }

        return null;
    }

    internal static VideoInfo? ParseVideoInfoJson(string json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = NormalizeMetadataTitle(GetOptionalString(root, "title"));
        var platform = GetOptionalString(root, "extractor_key");
        if (string.IsNullOrWhiteSpace(platform))
            platform = GetOptionalString(root, "extractor");

        return new VideoInfo
        {
            Title = title,
            Platform = platform,
            Duration = GetOptionalDouble(root, "duration"),
            Thumbnail = GetThumbnail(root),
            FileSize = GetFileSize(root),
            Url = url
        };
    }

    private static string NormalizeMetadataTitle(string title)
        => title.Replace("\r", "").Replace("\n", " ").Trim();

    private static string GetThumbnail(JsonElement root)
    {
        var thumbnail = GetOptionalString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(thumbnail)
            || !root.TryGetProperty("thumbnails", out var thumbnails)
            || thumbnails.ValueKind != JsonValueKind.Array)
        {
            return thumbnail;
        }

        foreach (var item in thumbnails.EnumerateArray())
        {
            var candidate = GetOptionalString(item, "url");
            if (!string.IsNullOrWhiteSpace(candidate))
                thumbnail = candidate;
        }

        return thumbnail;
    }

    private static long GetFileSize(JsonElement root)
    {
        var fileSize = GetOptionalInt64(root, "filesize_approx");
        return fileSize > 0 ? fileSize : GetOptionalInt64(root, "filesize");
    }

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static long GetOptionalInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            return 0;
        }

        return Math.Max(0, number);
    }

    private static double GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
        {
            return 0;
        }

        return Math.Max(0, number);
    }

    public async Task<List<string>> GetPlaylistUrlsAsync(string url, CancellationToken ct = default)
    {
        var urls = new List<string>();

        try
        {
            var strategies = GetCookieStrategies(url);
            for (var i = 0; i < strategies.Count; i++)
            {
                var args = BuildPlaylistBaseArgs();

                AddSiteCompatibilityArgs(args, url);
                AddProxyArgs(args);
                AddCookieArgs(args, url, strategies[i]);
                args.Add(url);

                var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    foreach (var line in EnumerateProcessLines(result.StandardOutput))
                    {
                        try
                        {
                            var videoUrl = ExtractPlaylistUrlFromJson(line);
                            if (!string.IsNullOrWhiteSpace(videoUrl))
                                urls.Add(videoUrl);
                        }
                        catch
                        {
                            // ignore non-json lines
                        }
                    }

                    return urls;
                }

                if (i < strategies.Count - 1
                    && ShouldRetryWithNextCookieStrategy(url, EnumerateProcessLines(result.StandardError)))
                {
                    continue;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetPlaylistUrls failed: {ex.Message}");
        }

        return urls;
    }

    internal static string ExtractPlaylistUrlFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var videoUrl = GetOptionalString(root, "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
            return GetOptionalString(root, "webpage_url");

        if (IsAbsoluteHttpUrl(videoUrl))
            return videoUrl;

        var extractorKey = GetOptionalString(root, "ie_key");
        if (string.IsNullOrWhiteSpace(extractorKey))
            extractorKey = GetOptionalString(root, "extractor_key");

        return string.Equals(extractorKey, "Youtube", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.youtube.com/watch?v={videoUrl}"
            : videoUrl;
    }

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        task.Status = DownloadStatus.Downloading;

        var strategies = GetCookieStrategies(task.Url);

        List<string> lastStderr = [];
        var allStderr = new List<string>();
        int lastExitCode = -1;

        for (var i = 0; i < strategies.Count; i++)
        {
            var strategy = strategies[i];
            var aria2cPath = _configService.Config.UseAria2c ? _envService.GetAria2cPath() : null;
            if (_configService.Config.UseAria2c && string.IsNullOrWhiteSpace(aria2cPath))
                logCallback?.Invoke("[yt-dlp] aria2c 已启用但未找到 aria2c.exe，已回退到 yt-dlp 内置下载器。");

            var args = BuildDownloadArgs(task, strategy, aria2cPath);
            var strategyTag = strategy switch
            {
                CookieStrategy.BrowserChrome => " (cookies-from-browser: chrome)",
                CookieStrategy.BrowserEdge => " (cookies-from-browser: edge)",
                _ => string.Empty
            };

            logCallback?.Invoke($"[yt-dlp] start{strategyTag}: {task.Url}");
            logCallback?.Invoke($"[yt-dlp] args: {string.Join(" ", args)}");

            var stderrLines = new List<string>();
            string? capturedOutputPath = null;
            var downloadStartTime = DateTime.Now;
            ProcessOutput processOutput;

            try
            {
                processOutput = await RunDownloadProcessAsync(
                    GetYtDlpPath(),
                    args,
                    DefaultDownloadNoOutputTimeout,
                    line =>
                    {
                        var handling = ClassifyDownloadOutputLine(line);
                        if (!string.IsNullOrWhiteSpace(handling.OutputPath))
                            capturedOutputPath = handling.OutputPath;

                        if (handling.Progress is not null)
                            progress?.Report(handling.Progress);

                        if (handling.ShouldLog)
                            logCallback?.Invoke(line);
                    },
                    line =>
                    {
                        stderrLines.Add(line);
                        logCallback?.Invoke($"[stderr] {line}");
                    },
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                return;
            }
            catch (TimeoutException ex)
            {
                var message = $"ERROR: {ex.Message}";
                stderrLines.Add(message);
                lastStderr = stderrLines;
                allStderr.AddRange(stderrLines);
                lastExitCode = -1;
                logCallback?.Invoke($"[yt-dlp] {ex.Message}");
                break;
            }

            var outputFile = ResolveOutputFile(capturedOutputPath, task, downloadStartTime);
            if (processOutput.ExitCode == 0)
            {
                task.Status = DownloadStatus.Completed;
                task.Progress = 100;

                if (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile))
                {
                    task.OutputFilePath = outputFile;
                    task.FileSize = new FileInfo(outputFile).Length;
                }

                logCallback?.Invoke($"[yt-dlp] completed: {task.Title}");
                return;
            }

            lastStderr = stderrLines;
            allStderr.AddRange(stderrLines);
            lastExitCode = processOutput.ExitCode;

            var canRetryWithNextCookieStrategy =
                i < strategies.Count - 1
                && ShouldRetryWithNextCookieStrategy(task.Url, stderrLines);

            if (canRetryWithNextCookieStrategy)
            {
                logCallback?.Invoke("[yt-dlp] retrying with next cookie strategy...");
                continue;
            }

            break;
        }

        if (IsDouyinUrl(task.Url) && task.Format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            logCallback?.Invoke("[yt-dlp] Douyin extractor failed; trying browser fallback...");
            var fallback = new DouyinBrowserDownloadService();
            if (await fallback.TryDownloadAsync(task, progress, logCallback, ct))
                return;
        }

        if (IsXiaohongshuUrl(task.Url))
        {
            logCallback?.Invoke("[yt-dlp] Xiaohongshu extractor failed; trying image fallback...");
            var fallback = new XiaohongshuImageDownloadService(_configService);
            if (await fallback.TryDownloadAsync(task, progress, logCallback, ct))
                return;
        }

        task.Status = DownloadStatus.Failed;
        task.ErrorMessage = BuildDownloadFailureMessage(task.Url, allStderr.Count > 0 ? allStderr : lastStderr, lastExitCode);

        logCallback?.Invoke($"[yt-dlp] failed (exit code: {lastExitCode})");
    }

    private List<string> BuildDownloadArgs(DownloadTask task, CookieStrategy cookieStrategy = CookieStrategy.Default, string? aria2cPath = null)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "-f",
            BuildFormatString(task.Format, task.Quality)
        };

        if (task.Format is "mp3" or "m4a")
        {
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(task.Format);
            args.Add("--audio-quality");
            args.Add("0");
        }
        else
        {
            args.Add("--merge-output-format");
            args.Add(task.Format);
        }

        args.Add("-o");
        args.Add(DownloadFileNameBuilder.BuildOutputTemplate(task.OutputDirectory, task.Title));

        args.Add("--windows-filenames");
        args.Add("--no-mtime");
        args.Add("--encoding");
        args.Add("utf-8");
        args.Add("--newline");
        args.Add("--progress-template");
        args.Add("download:%(progress._percent_str)s %(progress._speed_str)s ETA %(progress._eta_str)s");

        AddDownloadThroughputArgs(args);
        AddNetworkReliabilityArgs(args);

        var fragments = Math.Clamp(
            _configService.Config.ConcurrentFragments,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);
        if (fragments > 1)
        {
            args.Add("--concurrent-fragments");
            args.Add(fragments.ToString());
        }

        var ffmpegDir = GetFfmpegDirectory();
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }

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

        AddAria2cArgs(args, _configService.Config.UseAria2c, aria2cPath, fragments);

        AddSiteCompatibilityArgs(args, task.Url);
        AddProxyArgs(args);
        AddCookieArgs(args, task.Url, cookieStrategy);

        args.Add(task.Url);
        return args;
    }

    internal static void AddAria2cArgs(List<string> args, bool useAria2c, string? aria2cPath, int splitCount = 16)
    {
        if (!useAria2c || string.IsNullOrWhiteSpace(aria2cPath))
            return;

        var resolvedSplitCount = Math.Clamp(
            splitCount,
            AppConfig.MinConcurrentFragments,
            AppConfig.MaxConcurrentFragments);

        args.Add("--external-downloader");
        args.Add(aria2cPath);
        args.Add("--external-downloader-args");
        args.Add($"aria2c:--min-split-size=1M --max-connection-per-server={resolvedSplitCount} --split={resolvedSplitCount}");
    }

    internal static void AddNetworkReliabilityArgs(List<string> args)
    {
        args.Add("--retries");
        args.Add("20");
        args.Add("--fragment-retries");
        args.Add("30");
        args.Add("--socket-timeout");
        args.Add("30");
        args.Add("--retry-sleep");
        args.Add("linear=1:5:1");
        args.Add("--retry-sleep");
        args.Add("fragment:linear=1:5:1");
    }

    internal static void AddDownloadThroughputArgs(List<string> args)
    {
        args.Add("--buffer-size");
        args.Add("1M");
    }

    internal static void AddSiteCompatibilityArgs(List<string> args, string url)
    {
        if (!IsBilibiliUrl(url))
            return;

        args.Add("--user-agent");
        args.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        args.Add("--referer");
        args.Add("https://www.bilibili.com/");
        args.Add("--add-header");
        args.Add("Origin:https://www.bilibili.com");
    }

    internal static List<string> BuildVideoInfoBaseArgs()
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--dump-json",
            "--no-download",
            "--no-warnings"
        };

        AddNetworkReliabilityArgs(args);
        return args;
    }

    internal static List<string> BuildPlaylistBaseArgs()
    {
        var args = new List<string>
        {
            "--flat-playlist",
            "--dump-json",
            "--no-warnings"
        };

        AddNetworkReliabilityArgs(args);
        return args;
    }

    internal static List<CookieStrategy> BuildCookieStrategies(
        string url,
        bool chromeCookiesAvailable,
        bool edgeCookiesAvailable)
    {
        var strategies = new List<CookieStrategy> { CookieStrategy.Default };
        if (!IsDouyinUrl(url) && !IsYoutubeUrl(url))
            return strategies;

        if (chromeCookiesAvailable)
            strategies.Add(CookieStrategy.BrowserChrome);

        if (edgeCookiesAvailable)
            strategies.Add(CookieStrategy.BrowserEdge);

        return strategies;
    }

    private List<CookieStrategy> GetCookieStrategies(string url)
    {
        return BuildCookieStrategies(
            url,
            HasBrowserCookies("chrome"),
            HasBrowserCookies("edge"));
    }

    private static string BuildFormatString(string format, string quality)
    {
        if (format is "mp3" or "m4a")
            return "bestaudio/best";

        var qualityFilter = quality switch
        {
            "2160" => "[height<=2160]",
            "1080" => "[height<=1080]",
            "720" => "[height<=720]",
            "480" => "[height<=480]",
            _ => ""
        };

        var fallback = $"bv*{qualityFilter}+ba/b{qualityFilter}/b";
        if (format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            return $"bv*[ext=mp4]{qualityFilter}+ba[ext=m4a]/b[ext=mp4]{qualityFilter}/{fallback}";
        }

        return fallback;
    }

    private static DownloadProgress? ParseProgressLine(string line)
    {
        var match = UniversalProgressRegex().Match(line);
        if (match.Success)
        {
            var dp = new DownloadProgress { RawLine = line };
            if (double.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var percent))
            {
                dp.Percent = percent;
            }

            dp.Speed = ParseSpeed(match.Groups[2].Value);
            dp.Eta = ParseEta(match.Groups[3].Value);
            return dp;
        }

        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadProgress { Percent = 99, RawLine = line };
        }

        return null;
    }

    internal static bool ShouldLogDownloadOutputLine(string line)
        => !IsDownloadProgressTemplateLine(line);

    internal static DownloadOutputLineHandling ClassifyDownloadOutputLine(string line)
    {
        if (IsDownloadProgressTemplateLine(line))
            return new DownloadOutputLineHandling(ParseProgressLine(line), null, false);

        return new DownloadOutputLineHandling(
            ParseProgressLine(line),
            ParseOutputPath(line),
            true);
    }

    private static bool IsDownloadProgressTemplateLine(string line)
        => line.StartsWith("download:", StringComparison.OrdinalIgnoreCase);

    private static double ParseSpeed(string speedStr)
    {
        var speed = speedStr.AsSpan().Trim();
        if (speed.Equals("Unknown B/s", StringComparison.OrdinalIgnoreCase)
            || !speed.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        speed = speed[..^2];
        var unitStart = 0;
        while (unitStart < speed.Length
            && (char.IsDigit(speed[unitStart]) || speed[unitStart] == '.'))
        {
            unitStart++;
        }

        if (unitStart == 0 || unitStart >= speed.Length)
            return 0;

        var valueSpan = speed[..unitStart];
        if (!double.TryParse(
                valueSpan,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return 0;
        }

        var unit = speed[unitStart..];
        if (unit.Equals("KiB", StringComparison.OrdinalIgnoreCase))
            return value * 1024;

        if (unit.Equals("MiB", StringComparison.OrdinalIgnoreCase))
            return value * 1024 * 1024;

        if (unit.Equals("GiB", StringComparison.OrdinalIgnoreCase))
            return value * 1024 * 1024 * 1024;

        return value;
    }

    private static double ParseEta(string etaStr)
    {
        if (etaStr == "Unknown" || string.IsNullOrWhiteSpace(etaStr))
            return 0;

        var eta = etaStr.AsSpan().Trim();
        Span<int> parts = stackalloc int[3];
        var partCount = 0;

        while (true)
        {
            if (partCount == parts.Length)
                return 0;

            var separator = eta.IndexOf(':');
            var part = separator >= 0 ? eta[..separator] : eta;
            if (!TryParseEtaPart(part, out parts[partCount]))
                return 0;

            partCount++;

            if (separator < 0)
                break;

            eta = eta[(separator + 1)..];
        }

        return partCount switch
        {
            3 => parts[0] * 3600 + parts[1] * 60 + parts[2],
            2 => parts[0] * 60 + parts[1],
            1 => parts[0],
            _ => 0
        };
    }

    private static bool TryParseEtaPart(ReadOnlySpan<char> value, out int result)
        => int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);

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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyGet",
        "cookies.txt");

    private void AddCookieArgs(List<string> args, string url, CookieStrategy strategy)
    {
        if (strategy is CookieStrategy.BrowserChrome or CookieStrategy.BrowserEdge)
        {
            var browser = strategy == CookieStrategy.BrowserChrome ? "chrome" : "edge";
            if (HasBrowserCookies(browser))
            {
                args.Add("--cookies-from-browser");
                args.Add(browser);
            }
            return;
        }

        var config = _configService.Config;
        if (string.IsNullOrWhiteSpace(config.CookieContent))
            return;

        try
        {
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

    private static bool IsDouyinUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static VideoInfo BuildDouyinFallbackVideoInfo(string url)
    {
        var id = ExtractDouyinStableId(url);
        return new VideoInfo
        {
            Title = string.IsNullOrWhiteSpace(id) ? "Douyin_Video" : $"Douyin_{id}",
            Platform = "Douyin",
            Duration = 0,
            Thumbnail = "",
            FileSize = 0,
            Url = url
        };
    }

    private static string ExtractDouyinStableId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        var videoId = Regex.Match(url, @"/video/(\d+)", RegexOptions.IgnoreCase);
        if (videoId.Success)
            return videoId.Groups[1].Value;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var token = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(token))
                return SanitizeDouyinStableId(token);
        }

        var longNumber = Regex.Match(url, @"\d{12,}");
        return longNumber.Success ? longNumber.Value : "";
    }

    private static string SanitizeDouyinStableId(string value)
    {
        var chars = value
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray();

        return new string(chars);
    }

    private static bool IsYoutubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBilibiliUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsXiaohongshuUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("xiaohongshu.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetryWithNextCookieStrategy(string url, IEnumerable<string> stderrLines)
    {
        if (IsDouyinUrl(url))
        {
            return stderrLines.Any(line =>
                line.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase))
                || stderrLines.Any(IsBrowserCookieAccessError);
        }

        if (IsYoutubeUrl(url))
        {
            return stderrLines.Any(IsYoutubeBotOrForbiddenError)
                   || stderrLines.Any(IsBrowserCookieAccessError);
        }

        return false;
    }

    private static bool IsYoutubeBotOrForbiddenError(string line)
    {
        return line.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase)
               || line.Contains("This video may be inappropriate for some users", StringComparison.OrdinalIgnoreCase)
               || line.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
               || line.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserCookieAccessError(string line)
    {
        return line.Contains("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Could not copy cookie database", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildDownloadFailureMessage(string url, IEnumerable<string> stderrLines, int exitCode)
    {
        var lines = stderrLines.ToList();

        if (IsDouyinUrl(url) && (lines.Any(line => line.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase))
            || lines.Any(IsBrowserCookieAccessError)))
        {
            return "抖音需要最新 Cookie，但自动读取浏览器 Cookie 失败或 Cookie 已失效。请关闭 Chrome/Edge 后重试，或在设置中粘贴最新抖音 Cookie。";
        }

        if (IsYoutubeUrl(url) && lines.Any(IsYoutubeBotOrForbiddenError))
        {
            return "YouTube 下载被风控拦截（403/需要登录验证）。请在设置中粘贴最新 YouTube Cookie，或关闭浏览器后重试。";
        }

        if (IsBilibiliUrl(url) && lines.Any(IsBilibiliPreconditionFailedError))
        {
            return "B 站返回 412 Precondition Failed，通常是请求头或站点风控校验导致。EasyGet 已自动补充 B 站请求头；如果仍失败，请稍后重试或更新 yt-dlp。";
        }

        return lines.LastOrDefault(line =>
                   line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
               ?? $"yt-dlp exit code: {exitCode}";
    }

    private static bool IsBilibiliPreconditionFailedError(string line)
    {
        return line.Contains("BiliBili", StringComparison.OrdinalIgnoreCase)
               && line.Contains("HTTP Error 412", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBrowserCookies(string browser)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = browser.ToLowerInvariant() switch
        {
            "chrome" => new[] { Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default") },
            "edge" => new[] { Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default") },
            _ => Array.Empty<string>()
        };

        foreach (var root in roots)
        {
            if (File.Exists(Path.Combine(root, "Network", "Cookies"))
                || File.Exists(Path.Combine(root, "Cookies")))
            {
                return true;
            }
        }

        return false;
    }

    private static void SaveCookieFile(string cookieContent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CookieFilePath)!);
        File.WriteAllLines(CookieFilePath, BuildCookieFileLines(cookieContent));
    }

    internal static List<string> BuildCookieFileLines(string cookieContent)
    {
        var lines = new List<string>
        {
            "# Netscape HTTP Cookie File",
            "# Generated by EasyGet",
            ""
        };

        var trimmed = cookieContent.Trim();

        if (LooksLikeNetscapeCookieFile(trimmed))
        {
            foreach (var line in trimmed.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = line.Split('\t');
                if (line.StartsWith("#", StringComparison.Ordinal)
                    && !IsHttpOnlyNetscapeCookieLine(line, fields))
                    continue;

                if (fields.Length >= 7)
                    lines.Add(line);
            }

            return lines;
        }

        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var item in EnumerateCookieJsonItems(doc.RootElement))
                {
                    var domain = GetCookieDomain(item, out var domainImpliesHostOnly);
                    var name = GetOptionalString(item, "name");
                    var value = GetCookieValue(item);
                    var path = GetOptionalString(item, "path");
                    if (string.IsNullOrWhiteSpace(path))
                        path = "/";
                    var secure = GetOptionalBoolean(item, "secure");
                    var hostOnly = GetOptionalBoolean(item, "hostOnly") || domainImpliesHostOnly;
                    var expiry = GetCookieExpiry(item);

                    if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
                        continue;

                    var includeSubdomains = hostOnly ? "FALSE" : "TRUE";
                    var secureText = (secure || name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase)) ? "TRUE" : "FALSE";

                    lines.Add($"{domain}\t{includeSubdomains}\t{path}\t{secureText}\t{expiry}\t{name}\t{value}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YtDlpService] JSON cookie parse failed: {ex.Message}");
                ParsePlainTextCookies(trimmed, lines);
            }
        }
        else
        {
            ParsePlainTextCookies(trimmed, lines);
        }

        return lines;
    }

    private static IEnumerable<JsonElement> EnumerateCookieJsonItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "cookies", "data" })
        {
            if (root.TryGetProperty(propertyName, out var cookies)
                && cookies.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cookies.EnumerateArray())
                    yield return item;

                yield break;
            }
        }
    }

    private static void ParsePlainTextCookies(string cookieContent, List<string> lines)
    {
        string[] domains = [".youtube.com", ".x.com", ".twitter.com", ".instagram.com", ".bilibili.com", ".douyin.com", ".tiktok.com"];

        var headerValue = ExtractCookieHeaderValue(cookieContent);
        var pairs = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
                continue;

            var name = pair[..index].Trim();
            var value = pair[(index + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            foreach (var domain in domains)
                lines.Add($"{domain}\tTRUE\t/\tTRUE\t0\t{name}\t{value}");
        }
    }

    private static string GetCookieValue(JsonElement item)
    {
        var value = GetOptionalString(item, "value");
        if (!string.IsNullOrEmpty(value))
            return value;

        return GetOptionalString(item, "sessionValue");
    }

    private static string GetCookieDomain(JsonElement item, out bool domainImpliesHostOnly)
    {
        domainImpliesHostOnly = false;

        var domain = GetOptionalString(item, "domain").Trim();
        if (!string.IsNullOrWhiteSpace(domain))
            return domain;

        var host = GetOptionalString(item, "host").Trim();
        if (!string.IsNullOrWhiteSpace(host))
        {
            domainImpliesHostOnly = !host.StartsWith(".", StringComparison.Ordinal);
            return host;
        }

        var url = GetOptionalString(item, "url").Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            domainImpliesHostOnly = true;
            return uri.Host;
        }

        return "";
    }

    private static long GetCookieExpiry(JsonElement item)
    {
        foreach (var propertyName in new[] { "expirationDate", "expires", "expiry" })
        {
            var expiry = GetOptionalUnixTime(item, propertyName);
            if (expiry > 0)
                return expiry;
        }

        return 0;
    }

    private static bool GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static long GetOptionalUnixTime(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0
        };

        return Math.Max(0, (long)seconds);
    }

    private static bool LooksLikeNetscapeCookieFile(string cookieContent)
    {
        if (cookieContent.Contains("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
            return true;

        return cookieContent
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => !line.StartsWith("#") && line.Split('\t').Length >= 7);
    }

    private static bool IsHttpOnlyNetscapeCookieLine(string line, string[] fields)
        => line.StartsWith("#HttpOnly_", StringComparison.Ordinal)
           && fields.Length >= 7;

    private static string ExtractCookieHeaderValue(string cookieContent)
    {
        var lines = cookieContent.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cookieLine = lines.FirstOrDefault(line => line.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase));
        var value = cookieLine ?? cookieContent.Trim();

        if (value.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            value = value["cookie:".Length..].Trim();

        return value;
    }

    private static string? ParseOutputPath(string line)
    {
        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase))
        {
            var mergerMatch = MergerOutputRegex().Match(line);
            if (mergerMatch.Success)
                return mergerMatch.Groups[1].Value;
        }

        if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Destination:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = line.IndexOf("Destination:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var path = line[(idx + "Destination:".Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(path) && !TempFilePattern().IsMatch(path))
                    return path;
            }
        }

        if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
        {
            var start = line.IndexOf(']') + 1;
            var end = line.IndexOf(" has already been downloaded", StringComparison.OrdinalIgnoreCase);
            if (start > 0 && end > start)
                return line[start..end].Trim();
        }

        return null;
    }

    private static string? ResolveOutputFile(string? capturedPath, DownloadTask task, DateTime downloadStartTime)
    {
        if (!string.IsNullOrWhiteSpace(capturedPath) && File.Exists(capturedPath))
            return capturedPath;

        if (string.IsNullOrWhiteSpace(task.OutputDirectory) || !Directory.Exists(task.OutputDirectory))
            return null;

        var extensions = task.Format switch
        {
            "mp3" => new[] { ".mp3" },
            "m4a" => new[] { ".m4a" },
            "webm" => new[] { ".webm" },
            "mkv" => new[] { ".mkv", ".mp4", ".webm" },
            _ => new[] { ".mp4", ".mkv", ".webm" }
        };

        var minWriteTime = downloadStartTime.AddMinutes(-1);
        string? newestPath = null;
        var newestWriteTime = DateTime.MinValue;

        foreach (var path in Directory.EnumerateFiles(task.OutputDirectory))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            if (!extensions.Any(ext => extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            var lastWriteTime = File.GetLastWriteTime(path);
            if (lastWriteTime < minWriteTime || lastWriteTime <= newestWriteTime)
                continue;

            newestPath = path;
            newestWriteTime = lastWriteTime;
        }

        return newestPath is null ? null : Path.GetFullPath(newestPath);
    }

    private async Task<string> RunAsync(List<string> args, CancellationToken ct = default)
    {
        var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
        return result.StandardOutput;
    }

    private static IEnumerable<string> EnumerateProcessLines(string output)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                yield return trimmed;
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    internal static async Task<ProcessOutput> RunDownloadProcessAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan? noOutputTimeout = null,
        Action<string>? stdoutLineReceived = null,
        Action<string>? stderrLineReceived = null,
        CancellationToken ct = default)
    {
        using var process = Process.Start(CreateProcessStartInfo(fileName, args))
            ?? throw new InvalidOperationException($"无法启动命令: {fileName}");

        var lastOutputTicks = DateTime.UtcNow.Ticks;
        void TouchOutput() => Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);

        var stdoutTask = ReadProcessLinesAsync(process.StandardOutput, output: null, stdoutLineReceived, TouchOutput);
        var stderrTask = ReadProcessLinesAsync(process.StandardError, output: null, stderrLineReceived, TouchOutput);

        var idleTimeoutSource = new TaskCompletionSource<TimeoutException>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = MonitorProcessOutputIdleAsync(
            process,
            () => new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc),
            noOutputTimeout ?? DefaultDownloadNoOutputTimeout,
            idleTimeoutSource,
            monitorCts.Token);

        var readersDrained = false;
        try
        {
            var exitTask = process.WaitForExitAsync(ct);
            var completedTask = await Task.WhenAny(exitTask, monitorTask, idleTimeoutSource.Task);
            if (completedTask == idleTimeoutSource.Task)
                throw await idleTimeoutSource.Task;

            if (completedTask == monitorTask)
                await monitorTask;

            await exitTask;
            if (idleTimeoutSource.Task.IsCompletedSuccessfully)
                throw idleTimeoutSource.Task.Result;

            if (monitorTask.IsFaulted)
                await monitorTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            readersDrained = true;
            throw;
        }
        catch (TimeoutException)
        {
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            readersDrained = true;
            throw;
        }
        finally
        {
            monitorCts.Cancel();
        }

        if (!readersDrained)
            await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessOutput("", "", process.ExitCode);
    }

    internal static async Task<ProcessOutput> RunProcessAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var process = Process.Start(CreateProcessStartInfo(fileName, args))
            ?? throw new InvalidOperationException($"无法启动命令: {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainProcessOutputAsync(stdoutTask, stderrTask);
            throw new TimeoutException($"yt-dlp 命令执行超时: {fileName}");
        }

        return new ProcessOutput(
            await stdoutTask,
            await stderrTask,
            process.ExitCode);
    }

    private static async Task DrainProcessOutputAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(1000));
        }
        catch
        {
            // 进程超时后的输出清理是 best effort。
        }
    }

    private static async Task ReadProcessLinesAsync(
        StreamReader reader,
        StringBuilder? output,
        Action<string>? lineReceived,
        Action touchOutput)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                touchOutput();
                output?.AppendLine(line);
                lineReceived?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Debug.WriteLine($"[YtDlpService] process output read error: {ex.Message}");
        }
    }

    private static async Task MonitorProcessOutputIdleAsync(
        Process process,
        Func<DateTime> getLastOutputTimeUtc,
        TimeSpan noOutputTimeout,
        TaskCompletionSource<TimeoutException> idleTimeoutSource,
        CancellationToken ct)
    {
        if (noOutputTimeout <= TimeSpan.Zero)
            return;

        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(noOutputTimeout.TotalMilliseconds / 4, 100, 1000));

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (process.HasExited)
                return;

            if (DateTime.UtcNow - getLastOutputTimeUtc() < noOutputTimeout)
                continue;

            var ex = new TimeoutException($"yt-dlp 下载超过 {FormatDuration(noOutputTimeout)} 没有输出，已终止。");
            idleTimeoutSource.TrySetResult(ex);
            TryKill(process);
            throw ex;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} 分钟"
            : $"{duration.TotalSeconds:0.#} 秒";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略进程清理失败。
        }
    }

    [GeneratedRegex(@"([\d.]+)%.*?((?:[\d.]+[KMGT]?i?B|Unknown B)/s).*?ETA\s+(\S+)")]
    private static partial Regex UniversalProgressRegex();

    [GeneratedRegex(@"\.f\d+\.[a-zA-Z0-9]+$")]
    private static partial Regex TempFilePattern();

    [GeneratedRegex(@"\[Merger\].*?""(.+?)""")]
    private static partial Regex MergerOutputRegex();
}
