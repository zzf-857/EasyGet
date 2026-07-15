using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using EasyGet.Models;

namespace EasyGet.Services;

public interface IYangshipinDownloadService
{
    Task<VideoInfo?> GetVideoInfoAsync(
        string url,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default);
}

internal sealed record YangshipinMediaCapture(
    string VideoId,
    string PageUrl,
    string VideoUrl,
    string Title,
    string ThumbnailUrl,
    double Duration);

internal interface IYangshipinPageCapture
{
    Task<YangshipinMediaCapture> CaptureAsync(
        YangshipinUrlInfo urlInfo,
        CancellationToken cancellationToken);
}

internal interface IYangshipinDirectDownloader
{
    Task<long> GetContentLengthAsync(
        string mediaUrl,
        string referer,
        CancellationToken cancellationToken);

    Task<long> DownloadAsync(
        string mediaUrl,
        string referer,
        string temporaryPath,
        string outputPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class YangshipinDownloadService : IYangshipinDownloadService
{
    private static readonly TimeSpan MetadataCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IYangshipinPageCapture _pageCapture;
    private readonly IYangshipinDirectDownloader _directDownloader;
    private readonly ConcurrentDictionary<string, CachedMetadata> _metadataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MetadataRequest> _metadataRequests =
        new(StringComparer.OrdinalIgnoreCase);

    public YangshipinDownloadService(ConfigService configService)
        : this(
            new ChromiumYangshipinPageCapture(configService),
            new YangshipinDirectDownloader(configService))
    {
    }

    internal YangshipinDownloadService(
        IYangshipinPageCapture pageCapture,
        IYangshipinDirectDownloader directDownloader)
    {
        ArgumentNullException.ThrowIfNull(pageCapture);
        ArgumentNullException.ThrowIfNull(directDownloader);
        _pageCapture = pageCapture;
        _directDownloader = directDownloader;
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!YangshipinUrlParser.TryParse(url, out var urlInfo))
            return null;

        if (_metadataCache.TryGetValue(urlInfo.PageUrl, out var cached)
            && DateTimeOffset.UtcNow - cached.CreatedAt <= MetadataCacheLifetime)
        {
            return CloneVideoInfo(cached.Info, url);
        }

        var request = GetMetadataRequest(urlInfo);
        try
        {
            var metadata = await request.Task.WaitAsync(cancellationToken);
            return CloneVideoInfo(metadata.Info, url);
        }
        finally
        {
            var shouldCancel = request.ReleaseWaiterAndShouldCancel();
            if (shouldCancel)
                RemoveMetadataRequest(urlInfo.PageUrl, request, cancel: true);
            else if (request.Task.IsCompleted)
                RemoveMetadataRequest(urlInfo.PageUrl, request);
        }
    }

    private MetadataRequest GetMetadataRequest(YangshipinUrlInfo urlInfo)
    {
        while (true)
        {
            var request = _metadataRequests.GetOrAdd(
                urlInfo.PageUrl,
                _ => new MetadataRequest(
                    cancellationToken => LoadMetadataAsync(urlInfo, cancellationToken)));
            if (request.TryAddWaiter())
                return request;

            RemoveMetadataRequest(urlInfo.PageUrl, request);
        }
    }

    private async Task<CachedMetadata> LoadMetadataAsync(
        YangshipinUrlInfo urlInfo,
        CancellationToken cancellationToken)
    {
        if (_metadataCache.TryGetValue(urlInfo.PageUrl, out var cached)
            && DateTimeOffset.UtcNow - cached.CreatedAt <= MetadataCacheLifetime)
        {
            return cached;
        }

        var capture = await _pageCapture.CaptureAsync(urlInfo, cancellationToken);
        var fileSize = await _directDownloader.GetContentLengthAsync(
            capture.VideoUrl,
            capture.PageUrl,
            cancellationToken);
        var info = new VideoInfo
        {
            Title = NormalizeTitle(capture.Title, capture.VideoId),
            Platform = "央视频",
            Duration = Math.Max(0, capture.Duration),
            Thumbnail = capture.ThumbnailUrl,
            FileSize = Math.Max(0, fileSize),
            Url = urlInfo.PageUrl
        };

        var metadata = new CachedMetadata(info, DateTimeOffset.UtcNow);
        _metadataCache[urlInfo.PageUrl] = metadata;
        return metadata;
    }

    private void RemoveMetadataRequest(
        string pageUrl,
        MetadataRequest expected,
        bool cancel = false)
    {
        if (!_metadataRequests.TryGetValue(pageUrl, out var current)
            || !ReferenceEquals(current, expected))
        {
            return;
        }

        if (((ICollection<KeyValuePair<string, MetadataRequest>>)_metadataRequests)
            .Remove(new KeyValuePair<string, MetadataRequest>(pageUrl, current))
            && cancel)
        {
            current.Cancel();
        }
    }

    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!YangshipinUrlParser.TryParse(task.Url, out var urlInfo))
            throw new ArgumentException("不是受支持的央视频页面链接。", nameof(task));

        if (!string.Equals(task.Format, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = "央视频当前仅支持 MP4 输出，请将格式改为 mp4 后重试。";
            logCallback?.Invoke("[央视频] 当前仅支持 MP4 输出。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(task.Quality)
            && !task.Quality.Equals("best", StringComparison.OrdinalIgnoreCase))
        {
            logCallback?.Invoke("[央视频] 当前使用网页提供的默认最高画质，已忽略固定画质选项。");
            task.Quality = "best";
        }

        task.Status = DownloadStatus.Downloading;
        task.Platform = "央视频";
        logCallback?.Invoke("[央视频] 正在获取当前有效的播放地址...");

        try
        {
            var outputPath = ResolveOutputPath(task, urlInfo.VideoId);
            var temporaryPath = $"{outputPath}.part";
            task.OutputFilePath = outputPath;

            long downloadedSize = 0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capture = await _pageCapture.CaptureAsync(urlInfo, cancellationToken);
                ApplyCaptureMetadata(task, capture);

                try
                {
                    downloadedSize = await _directDownloader.DownloadAsync(
                        capture.VideoUrl,
                        capture.PageUrl,
                        temporaryPath,
                        outputPath,
                        progress,
                        cancellationToken);
                    break;
                }
                catch (HttpRequestException ex) when (
                    attempt == 0
                    && ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    logCallback?.Invoke("[央视频] 临时播放地址已失效，正在刷新后续传...");
                }
            }

            if (!File.Exists(outputPath) || downloadedSize <= 0)
                throw new IOException("央视频文件下载完成后未生成有效输出。");

            task.FileSize = new FileInfo(outputPath).Length;
            task.DownloadedSize = task.FileSize;
            task.Progress = 100;
            task.Status = DownloadStatus.Completed;
            task.ErrorMessage = "";
            progress?.Report(new DownloadProgress
            {
                Percent = 100,
                Downloaded = task.FileSize,
                Total = task.FileSize,
                RawLine = "[央视频] 下载完成"
            });
            logCallback?.Invoke($"[央视频] 下载完成: {outputPath}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = BuildFriendlyErrorMessage(ex);
            logCallback?.Invoke($"[央视频] {task.ErrorMessage}");
        }
    }

    private static void ApplyCaptureMetadata(
        DownloadTask task,
        YangshipinMediaCapture capture)
    {
        var title = NormalizeTitle(capture.Title, capture.VideoId);
        if (!string.IsNullOrWhiteSpace(title))
            task.Title = title;
        if (capture.Duration > 0)
            task.Duration = capture.Duration;
        if (!string.IsNullOrWhiteSpace(capture.ThumbnailUrl))
            task.ThumbnailUrl = capture.ThumbnailUrl;
    }

    private static string ResolveOutputPath(DownloadTask task, string videoId)
    {
        Directory.CreateDirectory(task.OutputDirectory);
        if (IsReusableOutputPath(task.OutputDirectory, task.OutputFilePath))
            return Path.GetFullPath(task.OutputFilePath);

        var title = NormalizeTitle(task.Title, videoId);
        var stem = DownloadFileNameBuilder.SanitizeResolvedTitle(title);
        var candidate = Path.Combine(task.OutputDirectory, $"{stem}.mp4");
        if (!File.Exists(candidate))
            return Path.GetFullPath(candidate);

        for (var index = 1; index < 10_000; index++)
        {
            candidate = Path.Combine(task.OutputDirectory, $"{stem} ({index}).mp4");
            if (!File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return Path.GetFullPath(Path.Combine(
            task.OutputDirectory,
            $"{stem} ({Guid.NewGuid():N}).mp4"));
    }

    private static bool IsReusableOutputPath(string outputDirectory, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)
            || string.IsNullOrWhiteSpace(outputPath)
            || File.Exists(outputPath)
            || !Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var expectedDirectory = Path.GetFullPath(outputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var actualDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(
                expectedDirectory,
                actualDirectory,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static string NormalizeTitle(string? title, string videoId)
    {
        var normalized = Regex.Replace(
                (title ?? "").Replace('\r', ' ').Replace('\n', ' '),
                @"\s+",
                " ")
            .Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? $"央视频_{videoId}"
            : normalized;
    }

    private static VideoInfo CloneVideoInfo(VideoInfo info, string url)
        => new()
        {
            Title = info.Title,
            Platform = info.Platform,
            Duration = info.Duration,
            Thumbnail = info.Thumbnail,
            FileSize = info.FileSize,
            Url = url
        };

    private static string BuildFriendlyErrorMessage(Exception exception)
        => exception switch
        {
            YangshipinMediaUnavailableException => exception.Message,
            FileNotFoundException => "未找到 Chrome 或 Edge，无法解析央视频页面。",
            TimeoutException => "央视频页面解析超时，请检查网络后重试。",
            HttpRequestException => "央视频媒体请求失败，请稍后重试。",
            IOException => "央视频下载中断，已保留临时文件，可直接重试续传。",
            _ => "央视频解析或下载失败，请稍后重试。"
        };

    private sealed record CachedMetadata(VideoInfo Info, DateTimeOffset CreatedAt);

    private sealed class MetadataRequest
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task<CachedMetadata>> _task;
        private int _waiterCount;
        private bool _acceptingWaiters = true;

        public MetadataRequest(
            Func<CancellationToken, Task<CachedMetadata>> requestFactory)
        {
            ArgumentNullException.ThrowIfNull(requestFactory);
            _task = new Lazy<Task<CachedMetadata>>(
                () => requestFactory(_cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<CachedMetadata> Task => _task.Value;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (!_acceptingWaiters)
                    return false;

                _waiterCount++;
                return true;
            }
        }

        public bool ReleaseWaiterAndShouldCancel()
        {
            lock (_gate)
            {
                if (_waiterCount <= 0)
                    throw new InvalidOperationException("央视频元数据等待者计数不平衡。");

                _waiterCount--;
                if (_waiterCount != 0
                    || !_task.IsValueCreated
                    || _task.Value.IsCompleted)
                {
                    return false;
                }

                _acceptingWaiters = false;
                return true;
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

internal sealed partial class ChromiumYangshipinPageCapture : IYangshipinPageCapture
{
    private static readonly SemaphoreSlim BrowserSlots = new(2, 2);
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(35);

    private readonly ConfigService _configService;

    public ChromiumYangshipinPageCapture(ConfigService configService)
    {
        ArgumentNullException.ThrowIfNull(configService);
        _configService = configService;
    }

    public async Task<YangshipinMediaCapture> CaptureAsync(
        YangshipinUrlInfo urlInfo,
        CancellationToken cancellationToken)
    {
        await BrowserSlots.WaitAsync(cancellationToken);
        try
        {
            var browserPath = FindChromiumBrowserPath()
                ?? throw new FileNotFoundException("Chrome or Edge was not found.");
            var profileDirectory = Path.Combine(
                Path.GetTempPath(),
                $"easyget-yangshipin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDirectory);

            try
            {
                var startInfo = CreateBrowserStartInfo(
                    browserPath,
                    profileDirectory,
                    urlInfo.PageUrl,
                    _configService.Config);
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("无法启动 Chrome/Edge。");
                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                var standardErrorTask = process.StandardError.ReadToEndAsync();
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeoutSource.CancelAfter(BrowserTimeout);

                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    await DrainOutputAsync(standardOutputTask, standardErrorTask);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    await DrainOutputAsync(standardOutputTask, standardErrorTask);
                    throw new TimeoutException("央视频页面解析超时。");
                }

                var dom = await standardOutputTask;
                _ = await standardErrorTask;
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(dom))
                    throw new InvalidOperationException("Chrome/Edge 未能加载央视频页面。");

                return ParseDumpedDom(dom, urlInfo);
            }
            finally
            {
                TryDeleteDirectory(profileDirectory);
            }
        }
        finally
        {
            BrowserSlots.Release();
        }
    }

    internal static YangshipinMediaCapture ParseDumpedDom(
        string dom,
        YangshipinUrlInfo urlInfo)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(urlInfo);

        var videoUrl = VideoSourceRegex()
            .Matches(dom)
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .FirstOrDefault(candidate => IsAllowedVideoUrl(candidate, urlInfo.VideoId));
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            throw new YangshipinMediaUnavailableException(
                "央视频页面未提供可下载的公开 MP4；内容可能需要登录、受地区/会员限制，或使用了受保护媒体。");
        }

        var titleMatch = VideoTitleRegex().Match(dom);
        var title = titleMatch.Success
            ? NormalizeHtmlText(titleMatch.Groups["title"].Value)
            : "";
        var posterMatch = VideoPosterRegex().Match(dom);
        var thumbnailUrl = posterMatch.Success
            ? NormalizeHttpUrl(posterMatch.Groups["url"].Value)
            : "";
        var pageText = NormalizeHtmlText(dom);
        var durationMatch = PreviewDurationRegex().Match(pageText);
        var duration = durationMatch.Success
            ? ParseDuration(durationMatch)
            : 0;

        return new YangshipinMediaCapture(
            urlInfo.VideoId,
            urlInfo.PageUrl,
            videoUrl,
            YangshipinDownloadService.NormalizeTitle(title, urlInfo.VideoId),
            thumbnailUrl,
            duration);
    }

    internal static ProcessStartInfo CreateBrowserStartInfo(
        string browserPath,
        string profileDirectory,
        string pageUrl,
        AppConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = browserPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        string[] arguments =
        [
            "--headless=new",
            "--disable-gpu",
            "--disable-extensions",
            "--disable-sync",
            "--disable-default-apps",
            "--disable-component-update",
            "--disable-background-networking",
            "--disable-features=MediaRouter",
            "--no-default-browser-check",
            "--no-first-run",
            "--mute-audio",
            "--autoplay-policy=no-user-gesture-required",
            "--virtual-time-budget=15000",
            "--dump-dom",
            $"--user-data-dir={profileDirectory}"
        ];
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
            startInfo.ArgumentList.Add($"--proxy-server={config.ProxyAddress.Trim()}");

        startInfo.ArgumentList.Add(pageUrl);
        return startInfo;
    }

    private static double ParseDuration(Match match)
    {
        static int Parse(Group group)
            => group.Success && int.TryParse(group.Value, out var value) ? value : 0;

        return Parse(match.Groups["hours"]) * 3600d
               + Parse(match.Groups["minutes"]) * 60d
               + Parse(match.Groups["seconds"]);
    }

    private static string NormalizeHtmlText(string value)
        => WhitespaceRegex().Replace(
                WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " ")),
                " ")
            .Trim();

    private static string NormalizeHttpUrl(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Trim();
        return Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : "";
    }

    private static bool IsAllowedVideoUrl(string value, string videoId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Contains(videoId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HostMatches(uri.Host, "ysp.cctv.cn")
               || HostMatches(uri.Host, "yangshipin.cn");
    }

    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static string? FindChromiumBrowserPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task DrainOutputAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(2)));
        }
        catch
        {
            // Process cleanup is best effort after cancellation or timeout.
        }
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
            // Best effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Browser processes can briefly retain profile files after exit.
        }
    }

    [GeneratedRegex(
        """<video\b[^>]*\bsrc\s*=\s*["'](?<url>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VideoSourceRegex();

    [GeneratedRegex(
        """class\s*=\s*["'](?=[^"']*\btitle\b)(?=[^"']*\boverflow-1\b)[^"']*["'][^>]*>(?<title>.*?)</div>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VideoTitleRegex();

    [GeneratedRegex(
        """<video\b[^>]*\bposter\s*=\s*["'](?<url>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VideoPosterRegex();

    [GeneratedRegex(
        @"可试看\s*(?:(?<hours>\d+)\s*小时)?\s*(?:(?<minutes>\d+)\s*分钟)?\s*(?:(?<seconds>\d+)\s*秒)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreviewDurationRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed class YangshipinDirectDownloader : IYangshipinDirectDownloader
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";
    private const int DownloadBufferSize = 128 * 1024;
    private const int MaxInterruptedResumeAttempts = 3;
    private const int MaxRedirects = 5;
    private const long DownloadProgressByteInterval = 512 * 1024;
    private static readonly TimeSpan DownloadProgressReportInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly ConfigService _configService;

    public YangshipinDirectDownloader(ConfigService configService)
    {
        ArgumentNullException.ThrowIfNull(configService);
        _configService = configService;
    }

    public async Task<long> GetContentLengthAsync(
        string mediaUrl,
        string referer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateHttpClient();
            using var headResponse = await SendAsync(
                client,
                HttpMethod.Head,
                mediaUrl,
                referer,
                null,
                null,
                cancellationToken);
            if (headResponse.IsSuccessStatusCode
                && headResponse.Content.Headers.ContentLength is > 0)
            {
                return headResponse.Content.Headers.ContentLength.Value;
            }

            using var rangeResponse = await SendAsync(
                client,
                HttpMethod.Get,
                mediaUrl,
                referer,
                0,
                0,
                cancellationToken);
            if (!rangeResponse.IsSuccessStatusCode)
                return 0;

            return rangeResponse.Content.Headers.ContentRange?.Length
                   ?? rangeResponse.Content.Headers.ContentLength
                   ?? 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<long> DownloadAsync(
        string mediaUrl,
        string referer,
        string temporaryPath,
        string outputPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
                                  ?? throw new IOException("输出目录无效。"));
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        var downloaded = File.Exists(temporaryPath)
            ? new FileInfo(temporaryPath).Length
            : 0;
        long total = 0;
        var sessionStartDownloaded = downloaded;
        var started = DateTime.UtcNow;
        var lastProgressReport = DateTime.MinValue;
        var lastProgressDownloaded = downloaded;
        var interruptedResumeAttempts = 0;

        try
        {
            using var client = CreateHttpClient();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attemptStartDownloaded = downloaded;
                using var response = await SendAsync(
                    client,
                    HttpMethod.Get,
                    mediaUrl,
                    referer,
                    downloaded > 0 ? downloaded : null,
                    null,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                    && downloaded > 0
                    && response.Content.Headers.ContentRange?.Length == downloaded)
                {
                    total = downloaded;
                    break;
                }

                response.EnsureSuccessStatusCode();
                var fileMode = downloaded > 0 ? FileMode.Append : FileMode.Create;
                if (downloaded > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    downloaded = 0;
                    sessionStartDownloaded = 0;
                    fileMode = FileMode.Create;
                }

                total = response.Content.Headers.ContentRange?.Length
                        ?? Math.Max(total, response.Content.Headers.ContentLength ?? 0);
                if (response.StatusCode == HttpStatusCode.PartialContent
                    && response.Content.Headers.ContentRange?.From is long rangeStart
                    && rangeStart != downloaded)
                {
                    throw new IOException("央视频服务器返回了不匹配的续传范围。");
                }

                try
                {
                    await using var source = await response.Content.ReadAsStreamAsync(
                        cancellationToken);
                    await using var target = new FileStream(
                        temporaryPath,
                        fileMode,
                        FileAccess.Write,
                        FileShare.Read,
                        DownloadBufferSize,
                        useAsync: true);
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                            break;

                        await target.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                        downloaded += read;
                        var now = DateTime.UtcNow;
                        var completedKnownTotal = total > 0 && downloaded >= total;
                        if (progress is not null
                            && (lastProgressReport == DateTime.MinValue
                                || completedKnownTotal
                                || now - lastProgressReport >= DownloadProgressReportInterval
                                || downloaded - lastProgressDownloaded >= DownloadProgressByteInterval))
                        {
                            ReportProgress(
                                progress,
                                downloaded,
                                total,
                                sessionStartDownloaded,
                                started,
                                now);
                            lastProgressReport = now;
                            lastProgressDownloaded = downloaded;
                        }
                    }
                }
                catch (IOException) when (
                    downloaded > attemptStartDownloaded
                    && total > 0
                    && downloaded < total
                    && interruptedResumeAttempts < MaxInterruptedResumeAttempts
                    && !cancellationToken.IsCancellationRequested)
                {
                    interruptedResumeAttempts++;
                    continue;
                }

                if (total <= 0 || downloaded >= total)
                    break;

                if (interruptedResumeAttempts >= MaxInterruptedResumeAttempts)
                    throw new IOException("央视频下载多次中断。");

                interruptedResumeAttempts++;
            }

            if (downloaded <= 0 || (total > 0 && downloaded != total))
                throw new IOException($"央视频下载不完整：{downloaded} / {total} 字节。");

            if (progress is not null && lastProgressDownloaded != downloaded)
            {
                ReportProgress(
                    progress,
                    downloaded,
                    total,
                    sessionStartDownloaded,
                    started,
                    DateTime.UtcNow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        File.Move(temporaryPath, outputPath, overwrite: false);
        return downloaded;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        var config = _configService.Config;
        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(config.ProxyAddress.Trim());
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string mediaUrl,
        string referer,
        long? rangeStart,
        long? rangeEnd,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var currentUri))
            throw new HttpRequestException("央视频媒体地址无效。");

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = CreateRequest(method, currentUri.AbsoluteUri, referer);
            if (rangeStart.HasValue || rangeEnd.HasValue)
                request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode))
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("央视频媒体地址重定向次数过多。");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null
                || !Uri.TryCreate(currentUri, location, out var redirectUri)
                || !IsAllowedRedirectTarget(redirectUri))
            {
                throw new HttpRequestException("央视频媒体地址重定向到了不受信任的目标。");
            }

            currentUri = redirectUri;
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static bool IsAllowedRedirectTarget(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
           && (HostMatches(uri.Host, "ysp.cctv.cn")
               || HostMatches(uri.Host, "yangshipin.cn"));

    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string mediaUrl,
        string referer)
    {
        var request = new HttpRequestMessage(method, mediaUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.yangshipin.cn");
        request.Headers.TryAddWithoutValidation("Accept", "video/mp4,*/*;q=0.8");
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            request.Headers.Referrer = refererUri;
        return request;
    }

    private static void ReportProgress(
        IProgress<DownloadProgress> progress,
        long downloaded,
        long total,
        long sessionStartDownloaded,
        DateTime started,
        DateTime now)
    {
        var elapsed = Math.Max((now - started).TotalSeconds, 0.1);
        var sessionBytes = Math.Max(0, downloaded - sessionStartDownloaded);
        var speed = sessionBytes / elapsed;
        var percent = total > 0 ? downloaded * 100d / total : 0;
        var eta = total > 0 && speed > 0 ? (total - downloaded) / speed : 0;
        progress.Report(new DownloadProgress
        {
            Percent = percent,
            Downloaded = downloaded,
            Total = total,
            Speed = speed,
            Eta = eta,
            RawLine = "[央视频] 下载中"
        });
    }
}

internal sealed class YangshipinMediaUnavailableException(string message)
    : InvalidOperationException(message);
