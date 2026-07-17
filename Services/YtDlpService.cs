using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyGet.Models;
using EasyGet.Services.Cookies;

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

public class PlaylistInfo
{
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public List<string> Urls { get; set; } = [];
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

    private readonly ConfigService _configService;
    private readonly EnvironmentService _envService;
    private readonly CookieAcquisitionCoordinator _cookieCoordinator;
    private readonly IYangshipinDownloadService _yangshipinDownloadService;

    public YtDlpService(ConfigService configService, EnvironmentService envService)
        : this(
            configService,
            envService,
            CreateDefaultCookieCoordinator(configService),
            new YangshipinDownloadService(configService))
    {
    }

    public YtDlpService(
        ConfigService configService,
        EnvironmentService envService,
        CookieAcquisitionCoordinator cookieCoordinator)
        : this(
            configService,
            envService,
            cookieCoordinator,
            new YangshipinDownloadService(configService))
    {
    }

    public YtDlpService(
        ConfigService configService,
        EnvironmentService envService,
        CookieAcquisitionCoordinator cookieCoordinator,
        IYangshipinDownloadService yangshipinDownloadService)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(envService);
        ArgumentNullException.ThrowIfNull(cookieCoordinator);
        ArgumentNullException.ThrowIfNull(yangshipinDownloadService);
        _configService = configService;
        _envService = envService;
        _cookieCoordinator = cookieCoordinator;
        _yangshipinDownloadService = yangshipinDownloadService;
    }

    private static CookieAcquisitionCoordinator CreateDefaultCookieCoordinator(ConfigService configService)
    {
        ArgumentNullException.ThrowIfNull(configService);
        return new CookieAcquisitionCoordinator(
            configService,
            new PlatformCookieVault(configService.ConfigDirectory),
            new BrowserProfileDiscoveryService(),
            new CookieHealthStore(configService.ConfigDirectory),
            new EmptyManagedLoginSessionService(),
            CookieFileLease.DefaultTemporaryDirectory);
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
        if (YangshipinUrlParser.IsYangshipinVideoUrl(url))
            return await _yangshipinDownloadService.GetVideoInfoAsync(url, ct);

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
            var attempts = await _cookieCoordinator.BuildAttemptsAsync(url, ct);
            foreach (var attempt in attempts)
            {
                CookieArgumentLease cookieArguments;
                try
                {
                    cookieArguments = await _cookieCoordinator.AcquireArgumentsAsync(
                        attempt,
                        url,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var acquisitionFailure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                        attempt,
                        [$"ERROR: {ex.Message}"],
                        ct);
                    if (acquisitionFailure.ShouldTryNextCookieSource)
                        continue;
                    break;
                }

                await using var cookieArgumentLease = cookieArguments;
                var args = BuildVideoInfoBaseArgs();

                AddSiteCompatibilityArgs(args, url);
                AddProxyArgs(args);
                args.AddRange(cookieArguments.Arguments);
                args.Add(url);

                var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    var firstJson = EnumerateProcessLines(result.StandardOutput)
                        .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));

                    if (!string.IsNullOrWhiteSpace(firstJson))
                    {
                        var info = ParseVideoInfoJson(firstJson, url);
                        if (info is not null)
                        {
                            await _cookieCoordinator.RecordSuccessAsync(attempt, ct);
                            return info;
                        }
                    }
                }

                var failure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                    attempt,
                    EnumerateProcessLines(result.StandardError),
                    ct);
                if (!failure.ShouldTryNextCookieSource)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        => (await GetPlaylistInfoAsync(url, ct)).Urls;

    public async Task<PlaylistInfo> GetPlaylistInfoAsync(string url, CancellationToken ct = default)
    {
        var empty = new PlaylistInfo { SourceUrl = url };

        try
        {
            var attempts = await _cookieCoordinator.BuildAttemptsAsync(url, ct);
            foreach (var attempt in attempts)
            {
                CookieArgumentLease cookieArguments;
                try
                {
                    cookieArguments = await _cookieCoordinator.AcquireArgumentsAsync(
                        attempt,
                        url,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var acquisitionFailure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                        attempt,
                        [$"ERROR: {ex.Message}"],
                        ct);
                    if (acquisitionFailure.ShouldTryNextCookieSource)
                        continue;
                    break;
                }

                await using var cookieArgumentLease = cookieArguments;
                var args = BuildPlaylistInfoBaseArgs();

                AddSiteCompatibilityArgs(args, url);
                AddProxyArgs(args);
                args.AddRange(cookieArguments.Arguments);
                args.Add(url);

                var result = await RunProcessAsync(GetYtDlpPath(), args, TimeSpan.FromSeconds(60), ct);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    foreach (var line in EnumerateProcessLines(result.StandardOutput))
                    {
                        try
                        {
                            var playlist = ParsePlaylistInfoJson(line, url);
                            if (playlist.Urls.Count == 0)
                                continue;

                            await _cookieCoordinator.RecordSuccessAsync(attempt, ct);
                            return playlist;
                        }
                        catch
                        {
                            // ignore non-json lines
                        }
                    }
                }

                var failure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                    attempt,
                    EnumerateProcessLines(result.StandardError),
                    ct);
                if (!failure.ShouldTryNextCookieSource)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YtDlpService] GetPlaylistInfo failed: {ex.Message}");
        }

        return empty;
    }

    internal static PlaylistInfo ParsePlaylistInfoJson(string json, string sourceUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var urls = new List<string>();
        var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var videoUrl = ExtractPlaylistUrl(entry);
                if (!string.IsNullOrWhiteSpace(videoUrl) && knownUrls.Add(videoUrl))
                    urls.Add(videoUrl);
            }
        }

        return new PlaylistInfo
        {
            Title = NormalizeMetadataTitle(GetOptionalString(root, "title")),
            SourceUrl = sourceUrl,
            Urls = urls
        };
    }

    internal static string ExtractPlaylistUrlFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ExtractPlaylistUrl(doc.RootElement);
    }

    private static string ExtractPlaylistUrl(JsonElement root)
    {
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
        if (YangshipinUrlParser.IsYangshipinVideoUrl(task.Url))
        {
            await _yangshipinDownloadService.DownloadAsync(
                task,
                progress,
                logCallback,
                ct);
            return;
        }

        task.Status = DownloadStatus.Downloading;

        IReadOnlyList<CookieAttempt> attempts;
        try
        {
            attempts = await _cookieCoordinator.BuildAttemptsAsync(task.Url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.Status = DownloadStatus.Cancelled;
            return;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            logCallback?.Invoke($"[yt-dlp] Cookie strategy initialization failed: {ex.Message}");
            return;
        }

        List<string> lastStderr = [];
        var allStderr = new List<string>();
        int lastExitCode = -1;

        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            CookieArgumentLease cookieArguments;
            try
            {
                cookieArguments = await _cookieCoordinator.AcquireArgumentsAsync(
                    attempt,
                    task.Url,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Cancelled;
                return;
            }
            catch (Exception ex)
            {
                var errorLine = $"ERROR: {ex.Message}";
                lastStderr = [errorLine];
                allStderr.Add(errorLine);
                var acquisitionFailure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                    attempt,
                    lastStderr,
                    ct);
                if (i < attempts.Count - 1 && acquisitionFailure.ShouldTryNextCookieSource)
                    continue;
                break;
            }

            await using var cookieArgumentLease = cookieArguments;
            var aria2cPath = _configService.Config.UseAria2c ? _envService.GetAria2cPath() : null;
            if (_configService.Config.UseAria2c && string.IsNullOrWhiteSpace(aria2cPath))
                logCallback?.Invoke("[yt-dlp] aria2c 已启用但未找到 aria2c.exe，已回退到 yt-dlp 内置下载器。");

            var args = BuildDownloadArgs(task, cookieArguments.Arguments, aria2cPath);
            var strategyTag = attempt.Source switch
            {
                CookieSourceKind.Anonymous => "匿名访问",
                CookieSourceKind.LegacyScoped => "平台手动 Cookie",
                CookieSourceKind.Browser => $"浏览器 {attempt.BrowserProfile?.BrowserName}",
                CookieSourceKind.ManagedSession => "托管登录",
                _ => "未知策略"
            };

            logCallback?.Invoke($"[yt-dlp] start ({strategyTag}): {task.Url}");

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
                            logCallback?.Invoke(RedactCookieArgumentValues(
                                line,
                                cookieArguments.Arguments));
                    },
                    line =>
                    {
                        stderrLines.Add(RedactCookieArgumentValues(
                            line,
                            cookieArguments.Arguments));
                        logCallback?.Invoke($"[stderr] {RedactCookieArgumentValues(
                            line,
                            cookieArguments.Arguments)}");
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
                var message = RedactPotentialSensitiveText($"ERROR: {ex.Message}");
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
                await _cookieCoordinator.RecordSuccessAsync(attempt, ct);
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

            var failure = await _cookieCoordinator.ClassifyAndRecordFailureAsync(
                attempt,
                stderrLines,
                ct);
            var canRetryWithNextCookieStrategy =
                i < attempts.Count - 1
                && failure.ShouldTryNextCookieSource;

            if (canRetryWithNextCookieStrategy)
            {
                logCallback?.Invoke("[yt-dlp] retrying with next cookie strategy...");
                continue;
            }

            break;
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

    private List<string> BuildDownloadArgs(
        DownloadTask task,
        IReadOnlyList<string> cookieArguments,
        string? aria2cPath = null)
    {
        ArgumentNullException.ThrowIfNull(cookieArguments);
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

        var fragments = ResolveConcurrentFragments(
            _configService.Config.ConcurrentFragments,
            _configService.Config.MaxConcurrentDownloads);
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
        args.AddRange(cookieArguments);

        args.Add(task.Url);
        return args;
    }

    internal static int ResolveConcurrentFragments(
        int configuredFragments,
        int maxConcurrentDownloads)
        => DownloadConcurrencyPolicy.ResolvePerTaskConnections(
            configuredFragments,
            maxConcurrentDownloads);

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

    internal static List<string> BuildPlaylistInfoBaseArgs()
    {
        var args = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json",
            "--no-warnings"
        };

        AddNetworkReliabilityArgs(args);
        return args;
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

    internal static string RedactCookieArgumentValues(
        string text,
        IReadOnlyList<string> cookieArguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(cookieArguments);

        var redacted = text;
        for (var index = 0; index + 1 < cookieArguments.Count; index++)
        {
            var option = cookieArguments[index];
            if (option is not "--cookies" and not "--cookies-from-browser")
                continue;

            var sensitiveValue = cookieArguments[index + 1];
            if (!string.IsNullOrWhiteSpace(sensitiveValue))
            {
                redacted = redacted.Replace(
                    sensitiveValue,
                    "[已隐藏]",
                    StringComparison.OrdinalIgnoreCase);
                if (option == "--cookies-from-browser")
                {
                    var separatorIndex = sensitiveValue.IndexOf(':');
                    if (separatorIndex >= 0 && separatorIndex < sensitiveValue.Length - 1)
                    {
                        var profilePath = sensitiveValue[(separatorIndex + 1)..];
                        redacted = redacted.Replace(
                            profilePath,
                            "[已隐藏]",
                            StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            index++;
        }

        return RedactPotentialSensitiveText(redacted);
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

    internal static string BuildDownloadFailureMessage(string url, IEnumerable<string> stderrLines, int exitCode)
    {
        var platform = MediaPlatformResolver.Resolve(url);
        var failure = CookieFailureClassifier.Classify(platform.Id, stderrLines);
        var lastErrorLine = failure.LastErrorLine;

        if (platform.Id == "douyin"
            && failure.Category is CookieFailureCategory.AuthenticationRequired
                or CookieFailureCategory.CookieStoreLocked
                or CookieFailureCategory.CookieDecryptFailed
                or CookieFailureCategory.CookieExpired
                or CookieFailureCategory.BotChallenge)
        {
            return "抖音需要有效登录状态，但本机浏览器 Cookie 不可用或已失效。EasyGet 会继续尝试其他浏览器；如仍失败，请在智能登录设置中重新登录抖音。";
        }

        if (platform.Id == "youtube"
            && failure.Category is CookieFailureCategory.AuthenticationRequired
                or CookieFailureCategory.CookieExpired
                or CookieFailureCategory.BotChallenge)
        {
            return "YouTube 下载被风控拦截，或需要登录验证。EasyGet 已尝试本机浏览器登录状态；如仍失败，请在智能登录设置中重新登录 YouTube。";
        }

        if (platform.Id == "bilibili"
            && failure.Category == CookieFailureCategory.BotChallenge)
        {
            return "B 站返回 412 Precondition Failed，通常是请求头或站点风控校验导致。EasyGet 已自动补充 B 站请求头；如果仍失败，请稍后重试或更新 yt-dlp。";
        }

        return lastErrorLine is null
            ? $"yt-dlp exit code: {exitCode}"
            : RedactPotentialSensitiveText(lastErrorLine);
    }

    private static string RedactPotentialSensitiveText(string text)
    {
        var redacted = WindowsLocalPathRegex().Replace(text, "[已隐藏]");
        redacted = CredentialHeaderRegex().Replace(redacted, "[已隐藏]");
        return CredentialAssignmentRegex().Replace(redacted, "[已隐藏]");
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

    [GeneratedRegex(@"(?i)(?:[A-Z]:\\|\\\\)[^;\r\n""']+")]
    private static partial Regex WindowsLocalPathRegex();

    [GeneratedRegex(@"(?i)\b(?:cookie|authorization|auth_token|sessionid|sid)\s*[:=]\s*[^;\s,]+")]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?im)\b(?:cookie|authorization)\s*:\s*[^\r\n]+")]
    private static partial Regex CredentialHeaderRegex();
}
